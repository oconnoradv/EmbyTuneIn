using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Tasks;
using System.Xml.Linq;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class RadioGroupRefreshStatus
    {
        public string Category { get; set; }
        public string Id { get; set; }
        public bool Enabled { get; set; }
        public string State { get; set; } = "Idle";
        public string Phase { get; set; }
        public int TotalRequests { get; set; }
        public int CompletedRequests { get; set; }
        public int ApiPercent => TotalRequests == 0 ? 0 : 100 * CompletedRequests / TotalRequests;
        public int StationRows { get; set; }
        public int FolderRows { get; set; }
        public int? LastHttpStatus { get; set; }
        public bool DiscoveryComplete { get; set; }
        public string LastApiResult { get; set; }
        public string Error { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? UpdatedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public DateTime? LastSuccessfulUtc { get; set; }
        public DateTime? LastAttemptUtc { get; set; }
        public string LastAttemptState { get; set; }
        public string HistoryError { get; set; }
        internal RadioGroupRefreshStatus Copy() => (RadioGroupRefreshStatus)MemberwiseClone();
    }

    internal sealed class TuneInRefresh
    {
        internal static readonly string[] Categories = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };
        public static TuneInRefresh Instance { get; } = new TuneInRefresh(TuneInDirectory.Instance);
        private readonly object gate = new object();
        private readonly TuneInDirectory directory;
        private readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();
        private TuneInRefreshStateStore history;

        internal TuneInRefresh(TuneInDirectory directory, TuneInRefreshStateStore history = null)
        { this.directory = directory; this.history = history ?? new TuneInRefreshStateStore(); }

        internal void UseHistoryStore(TuneInRefreshStateStore value)
        {
            lock (gate)
            {
                if (jobs.Values.Any(job => Active(job.Status.State)))
                    throw new InvalidOperationException("Cannot replace TuneIn history during a refresh.");
                history = value;
            }
        }

        internal string HistorySummary(string category)
        {
            var previous = history.History(category);
            return "Last successful group refresh (UTC): " + (previous.LastSuccessfulUtc?.ToString("u") ?? "never recorded")
                + ". Last attempt (UTC): " + (previous.LastAttemptUtc?.ToString("u") ?? "never recorded")
                + (previous.LastAttemptState == null ? "." : " (" + previous.LastAttemptState + ").");
        }

        public RadioGroupRefreshStatus[] Status()
        {
            var snapshot = directory.Snapshot();
            lock (gate)
                return Categories.Select(category =>
                {
                    var status = jobs.TryGetValue(category, out var job) ? job.Status.Copy()
                        : new RadioGroupRefreshStatus { Category = category };
                    status.Enabled = snapshot.Enabled(category);
                    var previous = history.History(category);
                    status.LastSuccessfulUtc = previous.LastSuccessfulUtc;
                    status.LastAttemptUtc = previous.LastAttemptUtc;
                    status.LastAttemptState = previous.LastAttemptState;
                    return status;
                }).ToArray();
        }

        public RadioGroupRefreshStatus Start(string category,
            Func<string, int, CancellationToken, Task> synchronize, ILogger logger = null)
        {
            var job = StartJob(category, synchronize, logger);
            lock (gate) return job.Status.Copy();
        }

        public RadioGroupRefreshStatus Start(string category, IChannelManager channelManager, ILogger logger) =>
            Start(category, Synchronizer(channelManager), logger);

        public RadioGroupRefreshStatus StartNative(string category, ITaskManager tasks, IChannelManager manager, ILogger logger)
        {
            if (!Categories.Contains(category)) throw new ArgumentException("Unknown radio group.");
            var workers = tasks.ScheduledTasks.Where(item => item.ScheduledTask.Key == "TuneInForkRefresh_" + category).ToArray();
            if (workers.Length != 1) throw new InvalidOperationException("The TuneIn scheduled task is unavailable or ambiguous.");
            lock (gate)
            {
                var job = StartJob(category, Synchronizer(manager), logger, true);
                if (job.AwaitingNativeExecution && !job.NativeLaunchRequested)
                {
                    job.NativeLaunchRequested = true;
                    job.NativeExecution = ExecuteNativeAsync(job, workers[0], tasks, logger);
                }
                return job.Status.Copy();
            }
        }

        private async Task ExecuteNativeAsync(Job job, IScheduledTaskWorker worker, ITaskManager tasks, ILogger logger)
        {
            try
            {
                await tasks.Execute(worker, new TaskOptions
                { HasManualInteraction = true, MaxRuntimeTicks = TimeSpan.FromMinutes(20).Ticks }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                logger?.Error("TuneIn native scheduled execution failed ({0}).", error.GetType().Name);
                lock (gate)
                {
                    if (job.AwaitingNativeExecution)
                        job.NativeStart.TrySetException(new InvalidOperationException("Emby could not start the TuneIn scheduled task."));
                }
            }
            finally
            {
                lock (gate)
                {
                    if (job.AwaitingNativeExecution)
                    {
                        job.AwaitingNativeExecution = false;
                        job.NativeStart.TrySetException(new InvalidOperationException("Emby did not execute the TuneIn scheduled task."));
                    }
                }
            }
        }

        private static Func<string, int, CancellationToken, Task> Synchronizer(IChannelManager manager) =>
            async (folder, depth, token) =>
            {
                var channel = manager.GetChannel<TuneInChannel>();
                if (channel == null) throw new InvalidOperationException("TuneIn channel is not registered.");
                await manager.RefreshChannelContent(channel, depth, folder, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                channel.ReplaceFolderArtwork(folder, token);
            };

        public async Task ExecuteScheduledAsync(string category, IChannelManager manager, ILogger logger,
            CancellationToken token, IProgress<double> progress)
        {
            token.ThrowIfCancellationRequested();
            var job = StartJob(category, Synchronizer(manager), logger);
            lock (gate)
            {
                job.AwaitingNativeExecution = false;
                job.NativeStart.TrySetResult(true);
            }
            using (token.Register(() =>
            {
                lock (gate)
                {
                    if (Active(job.Status.State)) { job.CancelledByAdmin = true; job.Cancellation.Cancel(); }
                }
            }))
            {
                progress.Report(0);
                await job.Completion.ConfigureAwait(false);
                RadioGroupRefreshStatus status;
                Exception failure;
                lock (gate)
                {
                    status = job.Status.Copy();
                    failure = job.Failure;
                }
                if (status.State == "Cancelled") throw new OperationCanceledException(status.Error, token);
                if (status.State != "Completed")
                {
                    var parameterDiagnostic = DirectoryRequest.ParameterDiagnostic(failure);
                    throw new InvalidOperationException("TuneIn category '" + status.Category + "' failed. Phase: "
                        + status.Phase + ". Catalogue requests completed/started: " + status.CompletedRequests + "/"
                        + status.TotalRequests + ". Last HTTP status: " + (status.LastHttpStatus?.ToString() ?? "not recorded")
                        + ". " + status.Error + (parameterDiagnostic == null ? "" : " " + parameterDiagnostic), failure);
                }
                progress.Report(100);
            }
        }

        public async Task ExecuteConfiguredAsync(IChannelManager manager, ILogger logger,
            CancellationToken token, IProgress<double> progress)
        {
            var categories = directory.Snapshot().Categories.ToArray();
            if (categories.Length == 0)
            {
                token.ThrowIfCancellationRequested();
                progress.Report(100);
                return;
            }

            progress.Report(0);
            for (var index = 0; index < categories.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                await ExecuteScheduledAsync(categories[index], manager, logger, token,
                    new CategoryProgress(progress, index, categories.Length)).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            progress.Report(100);
        }

        private Job StartJob(string category, Func<string, int, CancellationToken, Task> synchronize, ILogger logger,
            bool waitForNative = false)
        {
            if (!Categories.Contains(category)) throw new ArgumentException("Unknown radio group.");
            var snapshot = directory.Snapshot();
            var entries = TuneInChannel.StartingEntries(snapshot, category);
            lock (gate)
            {
                if (jobs.TryGetValue(category, out var previous) && (Active(previous.Status.State) || previous.AwaitingNativeExecution))
                    return previous;
                var job = new Job
                {
                    AwaitingNativeExecution = waitForNative,
                    Status = new RadioGroupRefreshStatus
                    {
                        Category = category,
                        Id = Guid.NewGuid().ToString("N"),
                        Enabled = true,
                        State = "Queued",
                        Phase = "Waiting to populate the configured section",
                        TotalRequests = category == "language" ? 1 : 0,
                        StartedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    }
                };
                if (!waitForNative) job.NativeStart.TrySetResult(true);
                jobs[category] = job;
                job.Completion = Task.Run(() => RunAsync(job, snapshot, entries, synchronize, logger));
                return job;
            }
        }

        public RadioGroupRefreshStatus Cancel(string category, string id)
        {
            lock (gate)
            {
                if (category == null || !jobs.TryGetValue(category, out var job) || job.Status.Id != id)
                    throw new ArgumentException("Refresh is no longer current.");
                if (Active(job.Status.State)) { job.CancelledByAdmin = true; job.Cancellation.Cancel(); }
                return job.Status.Copy();
            }
        }

        internal Task Completion(string category)
        {
            lock (gate) return jobs[category].Completion;
        }

        private static bool Active(string state) => state == "Queued" || state == "Running" || state == "Synchronizing";

        private void Update(Job job, Action<RadioGroupRefreshStatus> update)
        {
            lock (gate) { update(job.Status); job.Status.UpdatedUtc = DateTime.UtcNow; }
        }

        private async Task RunAsync(Job job, ConfigurationSnapshot snapshot, List<NavigationEntry> entries,
            Func<string, int, CancellationToken, Task> synchronize, ILogger logger)
        {
            var terminalState = "Completed";
            try
            {
                job.Cancellation.CancelAfter(TimeSpan.FromMinutes(20));
                using (job.Cancellation.Token.Register(() => job.NativeStart.TrySetCanceled()))
                    await job.NativeStart.Task.ConfigureAwait(false);
                using (var operation = directory.Operation(snapshot, job.Cancellation.Token))
                {
                    var category = job.Status.Category;
                    directory.InvalidateCategory(category);
                    Action<int> received = status => Update(job, value => value.LastHttpStatus = status);
                    if (category == "language")
                    {
                        BeginRequest(job, "Validating selected languages");
                        var languages = await directory.SelectAsync("language", null, null, true, operation.Token, received).ConfigureAwait(false);
                        Update(job, status => { status.CompletedRequests++; status.LastApiResult = "Validated language catalogue"; });
                        if (entries.Any(entry => !languages.Items.Any(item => item.Id == entry.SelectionId)))
                            throw new InvalidOperationException("A selected language is no longer available.");
                    }
                    Action<DirectoryRequest> started = request => Update(job, status =>
                    {
                        status.TotalRequests++;
                        status.State = "Running";
                        status.Phase = "Populating section; discovering nested pages";
                        status.LastHttpStatus = null;
                        status.LastApiResult = "Waiting for API response";
                    });
                    Action<XDocument> completed = document => Update(job, status =>
                    {
                        status.CompletedRequests++;
                        status.LastApiResult = "Validated catalogue";
                        status.StationRows += document.Descendants("outline").Count(row => TuneInDirectory.Attribute(row, "type") == "audio");
                        status.FolderRows += document.Descendants("outline").Count(row => TuneInDirectory.Attribute(row, "type") == "link");
                    });
                    operation.Token.ThrowIfCancellationRequested();
                    Update(job, status => { status.State = "Synchronizing"; status.Phase = "Populating the configured section in Emby; page total is discovered during traversal"; });
                    var folderId = directory.Register(snapshot, TuneInChannel.CategoryEntry(category));
                    // Emby 4.9.1.90's Refresh Internet Channels uses eight refresh levels.
                    const int depth = 8;
                    await directory.SynchronizeCategoryAsync(category, entries.Count,
                        () => synchronize(folderId, depth, operation.Token), started, received, completed,
                        entries.Select(entry => TuneInDirectory.RequestKey(snapshot, category, TuneInChannel.CatalogueRequest(snapshot, entry)))).ConfigureAwait(false);
                    operation.Token.ThrowIfCancellationRequested();
                    Update(job, status =>
                    {
                        status.DiscoveryComplete = true;
                        status.Phase = "Configured section and station metadata populated in Emby";
                    });
                }
            }
            catch (OperationCanceledException)
            {
                terminalState = "Cancelled";
                Update(job, status =>
                {
                    status.Error = job.CancelledByAdmin ? "Cancelled by administrator."
                        : job.Cancellation.IsCancellationRequested ? "Refresh exceeded its 20-minute time limit."
                        : "Configuration changed, a request was cancelled, or the API request timed out.";
                });
            }
            catch (Exception error)
            {
                terminalState = "Failed";
                lock (gate) job.Failure = error;
                Update(job, status =>
                {
                    status.Error = error is InvalidDataException || error is System.Xml.XmlException
                            ? "Provider response was rejected: HTTP failure, API fault, or malformed/unsafe catalogue."
                            : "Section refresh did not complete. Check saved selections, connectivity, request/time limits and Emby server logs.";
                    status.LastApiResult = status.LastApiResult == "Waiting for API response" ? "Request failed" : status.LastApiResult;
                });
                logger?.Error("TuneIn group refresh failed in {0} ({1}).", job.Status.Phase, error.GetType().Name);
            }
            finally
            {
                lock (gate)
                {
                    job.Status.State = terminalState;
                    job.Status.FinishedUtc = DateTime.UtcNow;
                    job.Status.UpdatedUtc = job.Status.FinishedUtc;
                    try { history.Record(job.Status.Category, job.Status.State, job.Status.FinishedUtc.Value); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
                    {
                        job.Status.HistoryError = "Refresh history could not be saved; timestamps may not survive a restart. Check server logs and data-directory permissions.";
                        logger?.Error("TuneIn refresh history persistence failed ({0}).", error.GetType().Name);
                    }
                    job.Cancellation.Dispose();
                }
            }
        }

        private void BeginRequest(Job job, string phase) => Update(job, status =>
        {
            status.State = "Running";
            status.Phase = phase + " " + (status.CompletedRequests + 1) + "/" + status.TotalRequests;
            status.LastHttpStatus = null;
            status.LastApiResult = "Waiting for API response";
        });

        private sealed class Job
        {
            public bool AwaitingNativeExecution { get; set; }
            public bool NativeLaunchRequested { get; set; }
            public Task NativeExecution { get; set; }
            public TaskCompletionSource<bool> NativeStart { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public RadioGroupRefreshStatus Status { get; set; }
            public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
            public bool CancelledByAdmin { get; set; }
            public Task Completion { get; set; }
            public Exception Failure { get; set; }
        }

        private sealed class CategoryProgress : IProgress<double>
        {
            private readonly IProgress<double> progress;
            private readonly int index;
            private readonly int count;

            public CategoryProgress(IProgress<double> progress, int index, int count)
            { this.progress = progress; this.index = index; this.count = count; }

            public void Report(double value) =>
                progress.Report(100d * (index + Math.Max(0d, Math.Min(100d, value)) / 100d) / count);
        }
    }
}
