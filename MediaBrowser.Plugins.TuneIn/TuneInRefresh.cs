using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class TuneInRefreshStatus
    {
        public string State { get; set; } = "NeverRun";
        public bool Busy { get; set; }
        public string Category { get; set; }
        public string Phase { get; set; }
        public int CategoriesTotal { get; set; }
        public int CategoriesCompleted { get; set; }
        public int RequestsStarted { get; set; }
        public int RequestsCompleted { get; set; }
        public int CacheHits { get; set; }
        public int? LastHttpStatus { get; set; }
        public int ImportFoldersTotal { get; set; }
        public int ImportFoldersServed { get; set; }
        public bool ImportMayHavePartiallyApplied { get; set; }
        public bool Resumed { get; set; }
        public bool Resumable { get; set; }
        public double Progress { get; set; }
        public string ErrorCode { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        internal TuneInRefreshStatus Copy() => (TuneInRefreshStatus)MemberwiseClone();
    }

    internal sealed class TuneInRefresh
    {
        private readonly object gate = new object();
        private readonly TuneInDirectory directory;
        private readonly TuneInRefreshStore store;
        private readonly Func<DateTime> clock;
        private readonly TimeSpan? timeoutOverride;
        private TuneInRefreshStatus status = new TuneInRefreshStatus();
        private TuneInRefreshCheckpoint checkpoint;
        private List<TuneInCachedPage> committed = new List<TuneInCachedPage>();
        private string committedKey;
        private bool busy;

        internal TuneInRefresh(TuneInDirectory directory, TuneInRefreshStore store = null,
            Func<DateTime> clock = null, TimeSpan? timeoutOverride = null)
        {
            this.directory = directory;
            this.store = store ?? new TuneInRefreshStore();
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.timeoutOverride = timeoutOverride;
        }

        internal TuneInRefreshStatus Status()
        {
            lock (gate)
            {
                var copy = status.Copy();
                copy.Busy = busy;
                var snapshot = directory.Snapshot();
                if (!busy && checkpoint == null && status.State == "NeverRun")
                {
                    try { checkpoint = store.Read(); }
                    catch (Exception error) when (error is System.IO.IOException || error is InvalidOperationException
                        || error is UnauthorizedAccessException || error is System.Xml.XmlException)
                    {
                        throw new InvalidOperationException("TuneIn refresh checkpoint could not be read. Check its permissions and integrity; run Refresh TuneIn to record a sanitized task outcome.");
                    }
                }
                copy.Resumable = !busy && Compatible(checkpoint, snapshot);
                if (status.State == "NeverRun" && checkpoint != null)
                {
                    copy.State = "Interrupted";
                    copy.Phase = copy.Resumable ? "Compatible checkpoint retained" : "Stale checkpoint; next run restarts";
                    copy.CategoriesTotal = snapshot.Categories.Count();
                    copy.ImportMayHavePartiallyApplied = checkpoint.ImportMayHavePartiallyApplied;
                }
                return copy;
            }
        }

        internal void UpdateConfiguration(Action update)
        {
            lock (gate)
            {
                if (busy) throw new InvalidOperationException("TuneIn settings cannot be saved while refresh work is active or draining.");
                update();
                committed.Clear();
                committedKey = null;
                // A failed state-file removal must be visible to the administrator.
                store.Clear();
                checkpoint = null;
            }
        }

        internal Task ExecuteConfiguredAsync(IChannelManager manager, ILogger logger, CancellationToken token, IProgress<double> progress)
        {
            TuneInChannel channel = null;
            return ExecuteAsync(() => channel = manager.GetChannel<TuneInChannel>(),
                (folder, cancellation) => manager.RefreshChannelContent(channel, 8, folder, cancellation),
                logger, token, progress);
        }

        internal async Task ExecuteAsync(Func<TuneInChannel> channel, Func<string, CancellationToken, Task> import,
            ILogger logger, CancellationToken token, IProgress<double> progress)
        {
            ConfigurationSnapshot snapshot = null;
            lock (gate)
            {
                if (busy) throw new InvalidOperationException("TuneIn refresh work is still active or draining.");
                busy = true;
                status = new TuneInRefreshStatus { State = "Running", Phase = "Preparing", StartedUtc = clock() };
            }
            Task work = null;
            TuneInCatalogueSession session = null;
            var completed = new List<string>();
            var started = clock();
            using (var timeout = new CancellationTokenSource())
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token))
            {
                try
                {
                    snapshot = directory.Snapshot();
                    ConfigurationSnapshot.Validate(snapshot.Value);
                    lock (gate) status.CategoriesTotal = snapshot.Categories.Count();
                    timeout.CancelAfter(timeoutOverride ?? TimeSpan.FromMinutes(snapshot.Value.RefreshTimeoutMinutes));
                    token.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        checkpoint = store.Read() ?? checkpoint;
                        if (Compatible(checkpoint, snapshot))
                        {
                            if (checkpoint.StartedUtc <= clock()
                                && clock() - checkpoint.StartedUtc < TimeSpan.FromMinutes(snapshot.Value.CatalogueCacheMinutes))
                            {
                                completed.AddRange(checkpoint.CompletedCategories);
                                started = checkpoint.StartedUtc;
                            }
                            status.Resumed = true;
                            status.CategoriesCompleted = completed.Count;
                        }
                        else
                        {
                            checkpoint = null;
                            store.Clear();
                        }
                    }
                    var pages = checkpoint?.Pages ?? (committedKey == snapshot.Key ? committed : null);
                    session = new TuneInCatalogueSession(pages, TimeSpan.FromMinutes(snapshot.Value.CatalogueCacheMinutes), clock,
                        hit => Update(() => { if (hit) status.CacheHits++; else status.RequestsStarted++; }),
                        () =>
                        {
                            Update(() => status.RequestsCompleted++);
                            SaveCheckpoint(snapshot, session, completed, started);
                        }, received => Update(() => status.LastHttpStatus = received));
                    SaveCheckpoint(snapshot, session, completed, started);
                    progress?.Report(0);
                    work = Task.Run(() => directory.WithRefreshSessionAsync(session,
                        () => RunAsync(snapshot, channel, import, completed, session, started, cancellation.Token, progress)));
                    await AwaitOrCancel(work, cancellation.Token).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        store.Clear();
                        checkpoint = null;
                        committed = session.Capture();
                        committedKey = snapshot.Key;
                        status.State = "Completed";
                        status.Phase = status.CategoriesTotal == 0 ? "No configured scopes" : "Completed native import";
                        status.Progress = 100;
                    }
                    progress?.Report(100);
                }
                catch (Exception error)
                {
                    var interrupted = cancellation.IsCancellationRequested;
                    lock (gate)
                    {
                        status.State = interrupted ? token.IsCancellationRequested ? "Cancelled" : "TimedOut"
                            : error is OperationCanceledException ? "Cancelled" : "Error";
                        status.ErrorCode = interrupted ? status.State
                            : error is OperationCanceledException ? "OperationCancelled" : error.GetType().Name;
                    }
                    try
                    {
                        if (interrupted && session != null) SaveCheckpoint(snapshot, session, completed, started, true);
                        else { store.Clear(); checkpoint = null; }
                    }
                    catch (Exception persistenceError) when (persistenceError is System.IO.IOException
                        || persistenceError is UnauthorizedAccessException || persistenceError is InvalidOperationException)
                    {
                        lock (gate) { status.State = "Error"; status.ErrorCode = "CheckpointPersistenceFailure"; checkpoint = null; }
                        logger?.Error("TuneIn checkpoint persistence failed ({0}).", persistenceError.GetType().Name);
                        throw new InvalidOperationException("TuneIn refresh could not persist its interruption checkpoint.");
                    }
                    logger?.Error("TuneIn refresh stopped in {0}/{1} ({2}).", status.Category ?? "none", status.Phase, status.ErrorCode);
                    if (status.State == "TimedOut") throw new TimeoutException("TuneIn refresh exceeded its configured timeout; compatible checkpoint pages are retained.");
                    if (status.State == "Cancelled") throw new OperationCanceledException("TuneIn refresh was cancelled.", token);
                    throw new InvalidOperationException("TuneIn refresh failed in " + (status.Category ?? "preparation")
                        + " / " + status.Phase + " (" + status.ErrorCode + "). Native import is not complete.");
                }
                finally
                {
                    lock (gate)
                    {
                        status.FinishedUtc = clock();
                        if (work == null || work.IsCompleted) busy = false;
                        else
                        {
                            // Cancellation ends the native task promptly, but never abandons ownership.
                            _ = work.ContinueWith(task =>
                            {
                                try
                                {
                                    var failure = task.Exception?.GetBaseException();
                                    if (failure != null) logger?.Error("Draining TuneIn work stopped ({0}).", failure.GetType().Name);
                                }
                                finally { lock (gate) busy = false; }
                            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                    }
                }
            }
        }

        private async Task RunAsync(ConfigurationSnapshot snapshot, Func<TuneInChannel> getChannel,
            Func<string, CancellationToken, Task> import, List<string> completed, TuneInCatalogueSession session,
            DateTime started, CancellationToken token, IProgress<double> progress)
        {
            var categories = snapshot.Categories.ToArray();
            if (categories.Length == 0) return;
            var channel = getChannel() ?? throw new InvalidOperationException("TuneIn channel is not registered.");
            foreach (var category in categories)
            {
                if (completed.Contains(category)) continue;
                directory.CheckCurrent(snapshot, token);
                Update(() =>
                {
                    status.Category = category;
                    status.Phase = "Discovering";
                    status.ImportFoldersServed = status.ImportFoldersTotal = 0;
                });
                var root = new ChannelItemInfo { Id = directory.Register(snapshot, new NavigationEntry
                    { Category = category, Name = TuneInBrowsing.CategoryName(category) }),
                    Name = TuneInBrowsing.CategoryName(category), Type = ChannelItemType.Folder };
                var staged = await TuneInStagedImport.DiscoverAsync(channel, root, token, (total, served) =>
                {
                    Update(() => { status.ImportFoldersTotal = total; status.ImportFoldersServed = served; });
                    Report(progress, categories.Length, completed.Count, .5 + .49 * served / total);
                }, () => directory.CheckCurrent(snapshot, token)).ConfigureAwait(false);
                directory.CheckCurrent(snapshot, token);
                Update(() =>
                {
                    status.Phase = "Importing";
                    status.ImportFoldersTotal = staged.Count;
                    status.ImportMayHavePartiallyApplied = true;
                });
                SaveCheckpoint(snapshot, session, completed, started);
                Report(progress, categories.Length, completed.Count, .5);
                await channel.ImportAsync(staged, () => import(root.Id, token)).ConfigureAwait(false);
                directory.CheckCurrent(snapshot, token);
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    completed.Add(category);
                    status.CategoriesCompleted = completed.Count;
                    status.ImportMayHavePartiallyApplied = false;
                }
                SaveCheckpoint(snapshot, session, completed, started);
                Report(progress, categories.Length, completed.Count, 0);
            }
        }

        private void Report(IProgress<double> progress, int total, int completed, double fraction)
        {
            lock (gate)
            {
                if (status.State != "Running") return;
                status.Progress = Math.Min(99, 100d * (completed + fraction) / total);
                progress?.Report(status.Progress);
            }
        }

        private void Update(Action update)
        {
            lock (gate) if (status.State == "Running") update();
        }

        private void SaveCheckpoint(ConfigurationSnapshot snapshot, TuneInCatalogueSession session,
            List<string> completed, DateTime started, bool interrupted = false)
        {
            lock (gate)
            {
                if (!interrupted && status.State != "Running") return;
                checkpoint = new TuneInRefreshCheckpoint { ConfigurationKey = snapshot.Key, SavedUtc = clock(), StartedUtc = started,
                    CompletedCategories = completed.ToList(), Pages = session.Capture(),
                    ImportMayHavePartiallyApplied = status.ImportMayHavePartiallyApplied };
                store.Write(checkpoint);
            }
        }

        private bool Compatible(TuneInRefreshCheckpoint value, ConfigurationSnapshot snapshot) =>
            value != null && value.ConfigurationKey == snapshot.Key && value.SavedUtc <= clock()
                && clock() - value.SavedUtc < TimeSpan.FromMinutes(snapshot.Value.CatalogueCacheMinutes)
                && value.CompletedCategories.SequenceEqual(snapshot.Categories.Take(value.CompletedCategories.Count));

        private static async Task AwaitOrCancel(Task work, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(work, cancelled.Task).ConfigureAwait(false) != work)
                    token.ThrowIfCancellationRequested();
                await work.ConfigureAwait(false);
            }
        }
    }
}
