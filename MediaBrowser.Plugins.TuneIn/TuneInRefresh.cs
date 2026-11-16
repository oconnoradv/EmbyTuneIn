using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Plugins.TuneIn.Security;
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
        public int CacheHits { get; set; }
        public int HttpResponses { get; set; }
        public long? DiscoveryMilliseconds { get; set; }
        public long? ImportMilliseconds { get; set; }
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

    public sealed class ConfiguredRefreshStatus
    {
        public string Category { get; set; }
        public string Phase { get; set; }
        public int CategoryIndex { get; set; }
        public int CategoryCount { get; set; }
        public int StartedRequests { get; set; }
        public int CompletedRequests { get; set; }
        public int CacheHits { get; set; }
    }

    internal sealed class TuneInRefresh
    {
        internal static readonly string[] Categories = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };
        public static TuneInRefresh Instance { get; } = new TuneInRefresh(TuneInDirectory.Instance);
        private readonly object gate = new object();
        private readonly TuneInDirectory directory;
        private readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();
        private TuneInRefreshStateStore history;
        private ConfiguredRun configuredRun;

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

        internal ConfiguredRefreshStatus CurrentConfiguredStatus()
        {
            lock (gate)
            {
                var run = configuredRun;
                if (run == null) return null;
                var job = run.Job?.Status;
                return new ConfiguredRefreshStatus
                {
                    Category = run.Category,
                    Phase = job?.Phase ?? run.Phase,
                    CategoryIndex = run.Index,
                    CategoryCount = run.Count,
                    StartedRequests = job?.TotalRequests ?? 0,
                    CompletedRequests = job?.CompletedRequests ?? 0,
                    CacheHits = job?.CacheHits ?? 0
                };
            }
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

        private Func<string, int, CancellationToken, Task> Synchronizer(IChannelManager manager) =>
            async (folder, depth, token) =>
            {
                var channel = manager.GetChannel<TuneInChannel>();
                if (channel == null) throw new InvalidOperationException("TuneIn channel is not registered.");
                await channel.SynchronizeStagedAsync(folder, depth,
                    () => manager.RefreshChannelContent(channel, depth, folder, token), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                directory.VerifyRefreshDiscovery();
                directory.ReportRefreshPhase("Updating category artwork");
                channel.ReplaceFolderArtwork(folder, token);
            };

        public async Task ExecuteScheduledAsync(string category, IChannelManager manager, ILogger logger,
            CancellationToken token, IProgress<double> progress) =>
            await ExecuteScheduledCoreAsync(category, manager, logger, token, progress, null).ConfigureAwait(false);

        private async Task ExecuteScheduledCoreAsync(string category, IChannelManager manager, ILogger logger,
            CancellationToken token, IProgress<double> progress, ConfiguredRun run)
        {
            token.ThrowIfCancellationRequested();
            var job = StartJob(category, Synchronizer(manager), logger);
            lock (gate)
            {
                if (run != null) run.Job = job;
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

            var run = new ConfiguredRun { Count = categories.Length, Phase = "Preparing selected categories" };
            lock (gate)
            {
                if (configuredRun != null) throw new InvalidOperationException("A TuneIn refresh task is already running.");
                configuredRun = run;
            }
            try
            {
                await directory.WithRefreshRunAsync(async () =>
                {
                    progress.Report(0);
                    for (var index = 0; index < categories.Length; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        lock (gate)
                        {
                            run.Category = categories[index];
                            run.Index = index + 1;
                            run.Job = null;
                            run.Phase = "Preparing category";
                        }
                        await ExecuteScheduledCoreAsync(categories[index], manager, logger, token,
                            new CategoryProgress(progress, index, categories.Length), run).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        lock (gate) { run.Job = null; run.Phase = "Finishing category"; }
                        directory.CommitRefreshRun();
                    }
                }).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                progress.Report(100);
            }
            finally { lock (gate) { if (configuredRun == run) configuredRun = null; } }
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
            var elapsed = Stopwatch.StartNew();
            var beganDiscovery = false;
            try
            {
                job.Cancellation.CancelAfter(TimeSpan.FromMinutes(20));
                using (job.Cancellation.Token.Register(() => job.NativeStart.TrySetCanceled()))
                    await job.NativeStart.Task.ConfigureAwait(false);
                elapsed.Restart();
                beganDiscovery = true;
                using (var operation = directory.Operation(snapshot, job.Cancellation.Token))
                {
                    var category = job.Status.Category;
                    directory.InvalidateCategory(category);
                    Action<int> received = status => Update(job, value => { value.LastHttpStatus = status; value.HttpResponses++; });
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
                        status.Phase = "Discovering selected catalogue pages";
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
                    Update(job, status => { status.State = "Running"; status.Phase = "Discovering selected catalogue pages"; });
                    var folderId = directory.Register(snapshot, TuneInChannel.CategoryEntry(category));
                    // Emby 4.9.1.90's Refresh Internet Channels uses eight refresh levels.
                    const int depth = 8;
                    await directory.SynchronizeCategoryAsync(category, entries.Count,
                        () => synchronize(folderId, depth, operation.Token), started, received, completed,
                        entries.Select(entry => TuneInDirectory.RequestKey(snapshot, category, TuneInChannel.CatalogueRequest(snapshot, entry))),
                        () => Update(job, status => status.CacheHits++),
                        () => Update(job, status =>
                        {
                            status.DiscoveryComplete = true;
                            status.DiscoveryMilliseconds = elapsed.ElapsedMilliseconds;
                            status.State = "Synchronizing";
                            status.Phase = "Importing staged section into Emby";
                        }),
                        phase => Update(job, status => { status.State = "Synchronizing"; status.Phase = phase; })).ConfigureAwait(false);
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
                    status.Error = error is CatalogueRateLimitException
                            ? "TuneIn rate limited catalogue requests; retry after the provider's requested delay."
                        : error is InvalidDataException || error is System.Xml.XmlException
                            ? "Provider response was rejected: HTTP failure, API fault, or malformed/unsafe catalogue."
                            : "Section refresh did not complete. Check saved selections, connectivity, request/time limits and Emby server logs.";
                    status.LastApiResult = status.LastApiResult == "Waiting for API response" ? "Request failed" : status.LastApiResult;
                });
                logger?.Error("TuneIn group refresh failed in {0} ({1}).", job.Status.Phase, error.GetType().Name);
            }
            finally
            {
                RadioGroupRefreshStatus result;
                lock (gate)
                {
                    if (beganDiscovery)
                    {
                        if (job.Status.DiscoveryMilliseconds.HasValue)
                            job.Status.ImportMilliseconds = elapsed.ElapsedMilliseconds - job.Status.DiscoveryMilliseconds.Value;
                        else
                            job.Status.DiscoveryMilliseconds = elapsed.ElapsedMilliseconds;
                    }
                    job.Status.State = terminalState;
                    job.Status.FinishedUtc = DateTime.UtcNow;
                    job.Status.UpdatedUtc = job.Status.FinishedUtc;
                    try { history.Record(job.Status.Category, job.Status.State, job.Status.FinishedUtc.Value); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
                    {
                        job.Status.HistoryError = "Refresh history could not be saved; timestamps may not survive a restart. Check server logs and data-directory permissions.";
                        logger?.Error("TuneIn refresh history persistence failed ({0}).", error.GetType().Name);
                    }
                    result = job.Status.Copy();
                    job.Cancellation.Dispose();
                }
                logger?.Debug("TuneIn refresh {0} ({1}): {2}/{3} catalogue pages completed, {4} HTTP responses, {5} cache hits, discovery {6} ms, Emby import {7} ms.",
                    result.Category, result.State, result.CompletedRequests, result.TotalRequests, result.HttpResponses,
                    result.CacheHits, result.DiscoveryMilliseconds, result.ImportMilliseconds);
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

        private sealed class ConfiguredRun
        {
            public string Category { get; set; }
            public string Phase { get; set; }
            public int Index { get; set; }
            public int Count { get; set; }
            public Job Job { get; set; }
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
