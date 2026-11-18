using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
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
        public int CategoriesSkipped { get; set; }
        public int RequestsStarted { get; set; }
        public int RequestsCompleted { get; set; }
        public int CacheHits { get; set; }
        public int? LastHttpStatus { get; set; }
        public int ImportFoldersTotal { get; set; }
        public int ImportFoldersServed { get; set; }
        public bool ImportMayHavePartiallyApplied { get; set; }
        public bool Resumed { get; set; }
        public double Progress { get; set; }
        public string ErrorCode { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string DirtyCategory { get; set; }
        public TuneInCategoryRefreshStatus[] Categories { get; set; }
        internal TuneInRefreshStatus Copy() => (TuneInRefreshStatus)MemberwiseClone();
    }

    public sealed class TuneInCategoryRefreshStatus
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
    }

    internal sealed class TuneInRefresh : IDisposable
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan CatalogueCacheLifetime = TimeSpan.FromMinutes(15);
        private readonly object gate = new object();
        private readonly TuneInDirectory directory;
        private readonly TuneInRefreshStore store;
        private readonly Func<TuneInChannel> getChannel;
        private readonly Func<TuneInChannel, string, TuneInCatalogueSession, bool, CancellationToken,
            Task<ChannelItemResult>> getItems;
        private readonly Func<TuneInChannel, TuneInStagedImport, string, CancellationToken, Task> import;
        private readonly ILogger logger;
        private readonly Func<DateTime> clock;
        private readonly TimeSpan? timeoutOverride;
        private TuneInRefreshStatus status = new TuneInRefreshStatus();
        private readonly Dictionary<string, string> categoryStates = new Dictionary<string, string>(StringComparer.Ordinal);
        private string categoryStateKey;
        private string dirtyCategory;
        private CommittedCatalogue committed = new CommittedCatalogue(null,
            new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal));
        private CancellationTokenSource cancellation;
        private Task work;
        private bool disposed;

        private sealed class CommittedCatalogue
        {
            internal string ConfigurationKey { get; }
            internal Dictionary<string, ChannelItemResult> Folders { get; }

            internal CommittedCatalogue(string configurationKey, Dictionary<string, ChannelItemResult> folders)
            {
                ConfigurationKey = configurationKey;
                Folders = folders;
            }
        }

        internal TuneInRefresh(TuneInDirectory directory, TuneInRefreshStore store,
            Func<TuneInChannel> getChannel,
            Func<TuneInChannel, string, TuneInCatalogueSession, bool, CancellationToken, Task<ChannelItemResult>> getItems,
            Func<TuneInChannel, TuneInStagedImport, string, CancellationToken, Task> import,
            ILogger logger, Func<DateTime> clock = null, TimeSpan? timeoutOverride = null)
        {
            this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.getChannel = getChannel ?? throw new ArgumentNullException(nameof(getChannel));
            this.getItems = getItems ?? throw new ArgumentNullException(nameof(getItems));
            this.import = import ?? throw new ArgumentNullException(nameof(import));
            this.logger = logger;
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.timeoutOverride = timeoutOverride;
        }

        internal TuneInRefreshStatus Status()
        {
            lock (gate)
            {
                var snapshot = directory.Snapshot();
                LoadCategoryStates(snapshot);
                var copy = status.Copy();
                copy.Busy = work != null && !work.IsCompleted;
                copy.DirtyCategory = dirtyCategory;
                copy.Categories = snapshot.Categories.Select(category => new TuneInCategoryRefreshStatus
                {
                    Category = category,
                    Name = TuneInBrowsing.CategoryName(category),
                    State = copy.Busy && copy.State == "Running" && copy.Category == category ? copy.Phase :
                        categoryStates.TryGetValue(category, out var state) ? state : "No recorded refresh"
                }).ToArray();
                return copy;
            }
        }

        internal TuneInRefreshStatus Start(string category = null)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(TuneInRefresh));
                if (work != null && !work.IsCompleted)
                    throw new InvalidOperationException("TuneIn refresh is already running or draining.");
                var snapshot = directory.Snapshot();
                if (category != null && !snapshot.Enabled(category))
                    throw new ArgumentException("Choose a category enabled by the saved TuneIn configuration.", nameof(category));
                LoadCategoryStates(snapshot);
                if (category != null && dirtyCategory != null && category != dirtyCategory)
                    throw new InvalidOperationException("Reconcile the interrupted TuneIn category before refreshing another.");
                cancellation?.Dispose();
                cancellation = new CancellationTokenSource();
                status = new TuneInRefreshStatus
                {
                    State = "Running",
                    Phase = "Preparing",
                    Category = category,
                    StartedUtc = clock()
                };
                var ownedToken = cancellation.Token;
                work = Task.Run(() => ExecuteAsync(snapshot, category, ownedToken));
                return Status();
            }
        }

        internal TuneInRefreshStatus Cancel()
        {
            lock (gate)
            {
                if (work == null || work.IsCompleted) return Status();
                cancellation.Cancel();
                status.Phase = "Cancelling and draining";
                return Status();
            }
        }

        internal void UpdateConfiguration(Action update)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            lock (gate)
            {
                if (work != null && !work.IsCompleted)
                    throw new InvalidOperationException("TuneIn settings cannot change while refresh work is active or draining.");
                update();
                store.Clear();
                categoryStateKey = null;
                categoryStates.Clear();
                dirtyCategory = null;
                committed = new CommittedCatalogue(null, new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal));
            }
        }

        // The checkpoint is bounded but can be large. Read it once per saved scope, not on every status poll.
        private void LoadCategoryStates(ConfigurationSnapshot snapshot)
        {
            if (categoryStateKey == snapshot.Key) return;
            categoryStates.Clear();
            dirtyCategory = null;
            categoryStateKey = snapshot.Key;
            try
            {
                var checkpoint = store.Read();
                if (checkpoint?.ConfigurationKey != snapshot.Key) return;
                dirtyCategory = checkpoint.DirtyCategory;
                foreach (var category in checkpoint.Categories) categoryStates[category.Category] = "Imported";
            }
            catch (Exception error)
            {
                logger?.Error("TuneIn category refresh state could not be read ({0}).", error.GetType().Name);
                foreach (var category in snapshot.Categories) categoryStates[category] = "Status unavailable";
            }
        }

        internal bool TryGetCommitted(ConfigurationSnapshot snapshot, string folder, out ChannelItemResult result)
        {
            result = null;
            if (snapshot == null || string.IsNullOrEmpty(folder)) return false;
            Task activeRun;
            lock (gate)
            {
                if (work == null || work.IsCompleted) return false;
                if (committed.ConfigurationKey == snapshot.Key)
                    return CopyCommitted(committed, folder, out result);
                activeRun = work;
            }

            TuneInRefreshCheckpoint checkpoint = null;
            try { checkpoint = store.Read(); }
            catch (Exception error)
            {
                logger?.Error("TuneIn committed catalogue could not be read ({0}).", error.GetType().Name);
            }
            var loaded = BuildCommitted(checkpoint != null && checkpoint.ConfigurationKey == snapshot.Key
                ? checkpoint : null, snapshot.Key);
            lock (gate)
            {
                if (work != activeRun || work.IsCompleted) return false;
                if (committed.ConfigurationKey != snapshot.Key) committed = loaded;
                return CopyCommitted(committed, folder, out result);
            }
        }

        private static bool CopyCommitted(CommittedCatalogue source, string folder, out ChannelItemResult result)
        {
            if (!source.Folders.TryGetValue(folder, out var cached))
            {
                result = null;
                return false;
            }
            result = new ChannelItemResult { Items = cached.Items.Select(CopyItem).ToList() };
            return true;
        }

        private static ChannelItemInfo CopyItem(ChannelItemInfo item) => new ChannelItemInfo
        {
            Id = item.Id,
            Name = item.Name,
            Type = item.Type,
            ContentType = item.ContentType,
            MediaType = item.MediaType,
            ImageUrl = item.ImageUrl
        };

        private static CommittedCatalogue BuildCommitted(TuneInRefreshCheckpoint checkpoint, string key)
        {
            var folders = new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal);
            foreach (var category in checkpoint?.Categories ?? new List<TuneInCategorySnapshot>())
            {
                foreach (var folder in category.Folders)
                {
                    folders[folder.Id] = new ChannelItemResult
                    {
                        Items = folder.Items.Select(item => new ChannelItemInfo
                        {
                            Id = item.Id,
                            Name = item.Name,
                            Type = (ChannelItemType)Enum.Parse(typeof(ChannelItemType), item.Type),
                            ContentType = (ChannelMediaContentType)Enum.Parse(typeof(ChannelMediaContentType), item.ContentType),
                            MediaType = (ChannelMediaType)Enum.Parse(typeof(ChannelMediaType), item.MediaType),
                            ImageUrl = item.ImageUrl
                        }).ToList()
                    };
                }
            }
            return new CommittedCatalogue(key, folders);
        }

        private async Task ExecuteAsync(ConfigurationSnapshot snapshot, string requestedCategory, CancellationToken serverToken)
        {
            TuneInCatalogueSession session = null;
            TuneInRefreshCheckpoint checkpoint = null;
            string currentCategory = null;
            var completed = new HashSet<string>(StringComparer.Ordinal);
            DateTime started = clock();
            var timedOut = false;
            using (var timeout = new CancellationTokenSource())
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, timeout.Token))
            {
                try
                {
                    if (!snapshot.Ready) throw new InvalidOperationException("TuneIn settings are not complete.");
                    directory.CheckCurrent(snapshot, linked.Token);
                    timeout.CancelAfter(timeoutOverride ?? DefaultTimeout);

                    checkpoint = store.Read();
                    var compatible = checkpoint != null && checkpoint.ConfigurationKey == snapshot.Key;
                    if (!compatible)
                    {
                        checkpoint = new TuneInRefreshCheckpoint
                        {
                            ConfigurationKey = snapshot.Key,
                            SavedUtc = clock(),
                            StartedUtc = started
                        };
                    }
                    else
                    {
                        started = checkpoint.StartedUtc <= clock() ? checkpoint.StartedUtc : started;
                        lock (gate)
                        {
                            dirtyCategory = checkpoint.DirtyCategory;
                            status.Resumed = checkpoint.DirtyCategory != null;
                            status.ImportMayHavePartiallyApplied = checkpoint.DirtyCategory != null;
                        }
                    }

                    var dirtyAtStart = checkpoint.DirtyCategory;
                    var categories = snapshot.Categories
                        .Where(category => requestedCategory == null || category == requestedCategory)
                        .OrderBy(category => category == dirtyAtStart ? 0 : 1)
                        .ToArray();
                    lock (gate) status.CategoriesTotal = categories.Length;
                    if (dirtyAtStart != null && !categories.Contains(dirtyAtStart, StringComparer.Ordinal))
                        throw new InvalidOperationException("TuneIn refresh must include the category with an interrupted import.");
                    if (requestedCategory != null && categories.Length == 0)
                        throw new InvalidOperationException("The selected TuneIn category is no longer enabled.");
                    session = new TuneInCatalogueSession(null, CatalogueCacheLifetime, clock,
                        hit => Update(() => { if (hit) status.CacheHits++; else status.RequestsStarted++; }),
                        () => Update(() => status.RequestsCompleted++),
                        code => Update(() => status.LastHttpStatus = code));

                    var channel = categories.Length == 0 ? null
                        : getChannel() ?? throw new InvalidOperationException("TuneIn channel is not registered.");
                    foreach (var category in categories)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        directory.CheckCurrent(snapshot, linked.Token);
                        currentCategory = category;
                        int requestsBefore;
                        lock (gate) requestsBefore = status.RequestsCompleted + status.CacheHits;
                        Update(() =>
                        {
                            status.Category = category;
                            status.Phase = "Discovering";
                            status.ImportFoldersServed = 0;
                            status.ImportFoldersTotal = 0;
                        });

                        var root = new ChannelItemInfo
                        {
                            Id = directory.Register(snapshot, new NavigationEntry
                            { Category = category, Name = TuneInBrowsing.CategoryName(category) }),
                            Name = TuneInBrowsing.CategoryName(category),
                            Type = ChannelItemType.Folder
                        };
                        var staged = await TuneInStagedImport.DiscoverAsync(channel, root, session, linked.Token,
                            (folder, catalogue, strict, token) => getItems(channel, folder, catalogue, strict, token),
                            (total, served) => Update(() =>
                            {
                                status.ImportFoldersTotal = total;
                                status.ImportFoldersServed = served;
                            }),
                            () => directory.CheckCurrent(snapshot, linked.Token)).ConfigureAwait(false);
                        directory.CheckCurrent(snapshot, linked.Token);
                        lock (gate)
                            if (status.RequestsCompleted + status.CacheHits == requestsBefore)
                                throw new InvalidOperationException("TuneIn category discovery returned no successful provider fetch.");

                        var categorySnapshot = staged.CreateSnapshot(category);
                        var previous = checkpoint.Categories.FirstOrDefault(value => value.Category == category);
                        var mustReconcile = category == dirtyAtStart;
                        if (!mustReconcile && previous != null && previous.Fingerprint == categorySnapshot.Fingerprint)
                        {
                            completed.Add(category);
                            Update(() =>
                            {
                                categoryStates[category] = "Unchanged";
                                status.CategoriesSkipped++;
                                status.CategoriesCompleted = completed.Count;
                                status.Progress = categories.Length == 0 ? 99 : 99d * completed.Count / categories.Length;
                            });
                            currentCategory = null;
                            continue;
                        }

                        linked.Token.ThrowIfCancellationRequested();
                        directory.CheckCurrent(snapshot, linked.Token);
                        checkpoint.DirtyCategory = category;
                        Persist(snapshot, checkpoint, started);

                        Update(() =>
                        {
                            status.Phase = "Importing";
                            status.ImportFoldersTotal = staged.Count;
                            status.ImportFoldersServed = 0;
                        });
                        await import(channel, staged, root.Id, linked.Token).ConfigureAwait(false);
                        directory.CheckCurrent(snapshot, linked.Token);
                        linked.Token.ThrowIfCancellationRequested();

                        checkpoint.Categories.RemoveAll(value => value.Category == category);
                        checkpoint.Categories.Add(categorySnapshot);
                        checkpoint.DirtyCategory = null;
                        var published = BuildCommitted(checkpoint, snapshot.Key);
                        directory.CheckCurrent(snapshot, linked.Token);
                        Persist(snapshot, checkpoint, started, published);
                        completed.Add(category);
                        Update(() =>
                        {
                            categoryStates[category] = "Imported";
                            status.CategoriesCompleted = completed.Count;
                            status.Progress = categories.Length == 0 ? 99 : 99d * completed.Count / categories.Length;
                        });
                        currentCategory = null;
                        dirtyAtStart = null;
                    }

                    linked.Token.ThrowIfCancellationRequested();
                    directory.CheckCurrent(snapshot, linked.Token);
                    lock (gate)
                    {
                        status.State = "Completed";
                        status.Phase = categories.Length == 0 ? "No enabled categories" : "Completed";
                        status.Progress = 100;
                    }
                }
                catch (Exception error)
                {
                    timedOut = timeout.IsCancellationRequested && !serverToken.IsCancellationRequested;
                    var cancelled = serverToken.IsCancellationRequested || timeout.IsCancellationRequested
                        || error is OperationCanceledException;
                    lock (gate)
                    {
                        status.State = timedOut ? "TimedOut" : cancelled ? "Cancelled" : "Error";
                        if (currentCategory != null && categoryStateKey == snapshot.Key)
                            categoryStates[currentCategory] = timedOut ? "Timed out" : cancelled ? "Cancelled" : "Failed";
                        status.ErrorCode = cancelled ? status.State : error.GetType().Name;
                        status.Phase = cancelled ? "Stopped and drained" : status.Phase;
                    }
                    logger?.Error("TuneIn refresh stopped in {0}/{1} ({2}).",
                        status.Category ?? "preparation", status.Phase, status.ErrorCode);
                }
                finally
                {
                    lock (gate)
                    {
                        status.FinishedUtc = clock();
                        if (status.State == "Running")
                        {
                            status.State = "Completed";
                            status.Phase = "Completed";
                            status.Progress = 100;
                        }
                    }
                }
            }
        }

        private void Persist(ConfigurationSnapshot snapshot, TuneInRefreshCheckpoint checkpoint, DateTime started,
            CommittedCatalogue published = null)
        {
            lock (gate)
            {
                checkpoint.ConfigurationKey = snapshot.Key;
                checkpoint.SavedUtc = clock();
                checkpoint.StartedUtc = started;
                store.Write(checkpoint);
                dirtyCategory = checkpoint.DirtyCategory;
                status.ImportMayHavePartiallyApplied = checkpoint.DirtyCategory != null;
                if (published != null) committed = published;
            }
        }

        private void Update(Action update)
        {
            lock (gate) if (status.State == "Running") update();
        }

        public void Dispose()
        {
            Task pending;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (work != null && !work.IsCompleted) cancellation.Cancel();
                pending = work;
            }
            if (pending != null)
            {
                try { pending.GetAwaiter().GetResult(); }
                catch (Exception error) { logger?.Error("TuneIn refresh drain failed ({0}).", error.GetType().Name); }
            }
            lock (gate)
            {
                cancellation?.Dispose();
                cancellation = null;
            }
        }
    }
}
