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
        public string Outcome => State == "Failed" ? "Error" : State;
        public bool Resumable { get; set; }
        public int TimeoutMinutes { get; set; }
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
        public int ImportFoldersTotal { get; set; }
        public int ImportFoldersRequested { get; set; }
        public int ImportFoldersCompleted { get; set; }
        public int ImportItemsServed { get; set; }
        public bool ImportMayHavePartiallyApplied { get; set; }
        public int? EstimatedRequests { get; set; }
        public int? EstimatedCompletedRequests { get; set; }
        public double? EstimatedProgress { get; set; }
        public string ResumedFromId { get; set; }
        public bool ConfiguredTask { get; set; }
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
        public int ImportFoldersTotal { get; set; }
        public int ImportFoldersRequested { get; set; }
        public int ImportFoldersCompleted { get; set; }
        public int ImportItemsServed { get; set; }
        public int EstimatedRequests { get; set; }
        public int EstimatedCompletedRequests { get; set; }
        public double? EstimatedProgress { get; set; }
        public string Outcome { get; set; }
        public bool Resumable { get; set; }
        public int TimeoutMinutes { get; set; }
        public string EstimateWarning { get; set; }
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
        private Task lastConfiguredCompletion = Task.CompletedTask;
        private string lastConfiguredOutcome = "NeverRun";
        private string lastEstimateWarning;
        private readonly TimeSpan? timeoutOverride;

        internal TuneInRefresh(TuneInDirectory directory, TuneInRefreshStateStore history = null,
            TimeSpan? timeoutOverride = null)
        { this.directory = directory; this.history = history ?? new TuneInRefreshStateStore(); this.timeoutOverride = timeoutOverride; }

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
            lock (gate)
            {
                var snapshot = directory.Snapshot();
                return Categories.Select(category =>
                {
                    var status = jobs.TryGetValue(category, out var job) ? job.Status.Copy()
                        : TimedOutStatus(history.TimeoutCheckpoint, category, snapshot.SettingsKey)
                            ?? new RadioGroupRefreshStatus { Category = category };
                    status.Enabled = snapshot.Enabled(category);
                    var timeoutCheckpoint = history.TimeoutCheckpoint;
                    status.Resumable = status.State == "TimedOut" && timeoutCheckpoint != null
                        && timeoutCheckpoint.Category == category && timeoutCheckpoint.AttemptId == status.Id
                        && timeoutCheckpoint.ConfigurationKey == snapshot.SettingsKey && timeoutCheckpoint.PagesComplete;
                    var previous = history.History(category);
                    status.LastSuccessfulUtc = previous.LastSuccessfulUtc;
                    status.LastAttemptUtc = previous.LastAttemptUtc;
                    status.LastAttemptState = previous.LastAttemptState;
                    var baseline = history.Baseline(snapshot.SettingsKey);
                    var categoryBaseline = baseline?.Categories.SingleOrDefault(item => item.Category == category)?.Requests;
                    status.EstimatedRequests = categoryBaseline.HasValue
                        ? Math.Max(0, categoryBaseline.Value - status.CacheHits) : (int?)null;
                    status.EstimatedCompletedRequests = status.CompletedRequests;
                    status.EstimatedProgress = categoryBaseline > status.CacheHits
                        ? Math.Min(status.State == "Completed" ? 100d : 99d,
                            100d * status.CompletedRequests / (categoryBaseline.Value - status.CacheHits)) : (double?)null;
                    if (jobs.TryGetValue(category, out var currentJob))
                        status.TimeoutMinutes = currentJob.TimeoutMinutes;
                    else
                        status.TimeoutMinutes = snapshot.Value.RefreshTimeoutMinutes;
                    return status;
                }).ToArray();
            }
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
                    CacheHits = job?.CacheHits ?? 0,
                    ImportFoldersTotal = job?.ImportFoldersTotal ?? 0,
                    ImportFoldersRequested = job?.ImportFoldersRequested ?? 0,
                    ImportFoldersCompleted = job?.ImportFoldersCompleted ?? 0,
                    ImportItemsServed = job?.ImportItemsServed ?? 0,
                    EstimatedRequests = Math.Max(0, run.EstimatedRequests - run.CacheHitsBefore - (job?.CacheHits ?? 0)),
                    EstimatedCompletedRequests = run.CompletedRequestsBefore + (job?.CompletedRequests ?? 0),
                    EstimatedProgress = EstimateProgress(run, job),
                    Outcome = "Running",
                    Resumable = false,
                    TimeoutMinutes = run.TimeoutMinutes,
                    EstimateWarning = run.EstimateWarning
                };
            }
        }

        internal string LastConfiguredOutcome
        {
            get { lock (gate) return lastConfiguredOutcome; }
        }

        internal RadioGroupRefreshStatus LastTimedOutCategory()
        {
            lock (gate)
            {
                if (ReferenceEquals(directory, TuneInDirectory.Instance) && Plugin.Instance == null) return null;
                var key = directory.Snapshot().SettingsKey;
                var checkpoint = history.TimeoutCheckpoint;
                if (checkpoint?.ConfiguredTask != true) return null;
                var current = jobs.Values.FirstOrDefault(job => job.Status.Id == checkpoint.AttemptId
                    && job.Status.State == "TimedOut" && job.ConfigurationKey == key);
                var status = current?.Status.Copy() ?? TimedOutStatus(checkpoint, checkpoint.Category, key);
                if (status != null) status.Resumable = checkpoint.PagesComplete;
                return status;
            }
        }

        internal TuneInRequestBaseline Baseline(string configurationKey) => history.Baseline(configurationKey);

        internal TuneInRequestBaseline CurrentBaseline()
        {
            lock (gate)
            {
                if (ReferenceEquals(directory, TuneInDirectory.Instance) && Plugin.Instance == null) return null;
                return history.Baseline(directory.Snapshot().SettingsKey);
            }
        }

        internal int CurrentTimeoutMinutes
        {
            get
            {
                lock (gate)
                    return ReferenceEquals(directory, TuneInDirectory.Instance) && Plugin.Instance == null
                        ? Configuration.PluginConfiguration.DefaultRefreshTimeoutMinutes
                        : directory.Snapshot().Value.RefreshTimeoutMinutes;
            }
        }

        internal string CurrentEstimateWarning
        {
            get { lock (gate) return configuredRun?.EstimateWarning ?? lastEstimateWarning; }
        }

        internal bool IsRefreshActive
        {
            get
            {
                lock (gate)
                    return configuredRun != null || jobs.Values.Any(job => Active(job.Status.State) || job.AwaitingNativeExecution);
            }
        }

        internal bool IsConfiguredRunActive
        {
            get { lock (gate) return configuredRun != null; }
        }

        internal Task ConfiguredCompletion
        {
            get { lock (gate) return lastConfiguredCompletion; }
        }

        internal void UpdateConfiguration(Func<bool> isLocked, Action update)
        {
            lock (gate)
            {
                if (isLocked()) throw new InvalidOperationException("TuneIn settings are locked; unlock them before saving configuration.");
                if (configuredRun != null || jobs.Values.Any(job => Active(job.Status.State) || job.AwaitingNativeExecution))
                    throw new InvalidOperationException("TuneIn configuration cannot be saved while a TuneIn refresh is active.");
                update();
            }
        }

        internal TuneInSettingsLock ReadSettingsLock(TuneInSettingsLockStore store)
        {
            lock (gate) return store.Read(IsRefreshActiveLocked());
        }

        internal TuneInSettingsLock SetSettingsLock(TuneInSettingsLockStore store, bool locked)
        {
            lock (gate) return store.Set(locked, IsRefreshActiveLocked());
        }

        private bool IsRefreshActiveLocked() =>
            configuredRun != null || jobs.Values.Any(job => Active(job.Status.State) || job.AwaitingNativeExecution);

        public RadioGroupRefreshStatus Start(string category,
            Func<string, int, CancellationToken, Task> synchronize, ILogger logger = null)
        {
            var job = StartJob(category, synchronize, logger);
            lock (gate) return job.Status.Copy();
        }

        public RadioGroupRefreshStatus Start(string category, IChannelManager channelManager, ILogger logger) =>
            Start(category, Synchronizer(channelManager), logger);

        internal RadioGroupRefreshStatus StartApiCategory(string category, IChannelManager channelManager, ILogger logger)
        {
            if (!Categories.Contains(category)) throw new ArgumentException("Unknown TuneIn category.");
            lock (gate)
            {
                if (configuredRun != null || jobs.Values.Any(job => Active(job.Status.State) || job.AwaitingNativeExecution))
                    throw new InvalidOperationException("Another TuneIn refresh is already active.");
                var snapshot = directory.Snapshot();
                if (!snapshot.Enabled(category))
                    throw new InvalidOperationException("The TuneIn category is disabled or has no valid saved selections.");
                return StartJob(category, Synchronizer(channelManager), logger).Status.Copy();
            }
        }

        internal RadioGroupRefreshStatus CategoryStatus(string category)
        {
            if (!Categories.Contains(category)) throw new ArgumentException("Unknown TuneIn category.");
            lock (gate)
            {
                var snapshot = directory.Snapshot();
                var baseline = history.Baseline(snapshot.SettingsKey);
                var estimate = baseline?.Categories.SingleOrDefault(item => item.Category == category)?.Requests;
                if (jobs.TryGetValue(category, out var job))
                {
                    var status = job.Status.Copy();
                    status.Enabled = snapshot.Enabled(category);
                    var activeCheckpoint = history.TimeoutCheckpoint;
                    status.Resumable = status.State == "TimedOut" && job.ConfigurationKey == snapshot.SettingsKey
                        && activeCheckpoint?.Category == category && activeCheckpoint.AttemptId == status.Id
                        && activeCheckpoint.ConfigurationKey == snapshot.SettingsKey && activeCheckpoint.PagesComplete;
                    status.EstimatedRequests = estimate.HasValue
                        ? Math.Max(0, estimate.Value - status.CacheHits) : (int?)null;
                    status.EstimatedCompletedRequests = status.CompletedRequests;
                    status.EstimatedProgress = estimate > status.CacheHits
                        ? Math.Min(status.State == "Completed" ? 100d : 99d,
                            100d * status.CompletedRequests / (estimate.Value - status.CacheHits)) : (double?)null;
                    status.TimeoutMinutes = job.TimeoutMinutes;
                    return status;
                }
                var checkpoint = history.TimeoutCheckpoint;
                var timedOut = TimedOutStatus(checkpoint, category, snapshot.SettingsKey);
                if (timedOut != null)
                {
                    timedOut.Enabled = snapshot.Enabled(category);
                    timedOut.EstimatedRequests = estimate;
                    timedOut.EstimatedCompletedRequests = 0;
                    timedOut.TimeoutMinutes = checkpoint.TimeoutMinutes;
                    return timedOut;
                }
                return new RadioGroupRefreshStatus
                {
                    Category = category,
                    Enabled = snapshot.Enabled(category),
                    State = "Idle",
                    EstimatedRequests = estimate,
                    EstimatedCompletedRequests = 0,
                    TimeoutMinutes = snapshot.Value.RefreshTimeoutMinutes
                };
            }
        }

        internal RadioGroupRefreshStatus ResumeTimedOutCategory(string category, string attemptId,
            IChannelManager channelManager, ILogger logger) =>
            ResumeTimedOutCategoryCore(category, attemptId, Synchronizer(channelManager), channelManager, logger);

        internal RadioGroupRefreshStatus ResumeTimedOutCategory(string category, string attemptId,
            Func<string, int, CancellationToken, Task> synchronize, ILogger logger) =>
            ResumeTimedOutCategoryCore(category, attemptId, synchronize, null, logger);

        private RadioGroupRefreshStatus ResumeTimedOutCategoryCore(string category, string attemptId,
            Func<string, int, CancellationToken, Task> synchronize, IChannelManager channelManager, ILogger logger)
        {
            if (!Categories.Contains(category)) throw new ArgumentException("Unknown TuneIn category.");
            if (string.IsNullOrWhiteSpace(attemptId)) throw new ArgumentException("The timed-out attempt id is required.");
            TuneInTimedOutCheckpoint checkpoint;
            ConfigurationSnapshot snapshot;
            lock (gate)
            {
                if (configuredRun != null || jobs.Values.Any(job => Active(job.Status.State) || job.AwaitingNativeExecution))
                    throw new InvalidOperationException("Another TuneIn refresh is already active.");
                snapshot = directory.Snapshot();
                checkpoint = history.TimeoutCheckpoint;
                var matchingJob = jobs.TryGetValue(category, out var previous)
                    && previous.Status.Id == attemptId && previous.Status.State == "TimedOut"
                    && previous.ConfigurationKey == snapshot.SettingsKey;
                var matchingCheckpoint = checkpoint != null && checkpoint.Category == category
                    && checkpoint.AttemptId == attemptId;
                if (!matchingCheckpoint)
                    throw new InvalidOperationException("Only the current timed-out category attempt can be resumed.");
                if (!checkpoint.PagesComplete)
                    throw new InvalidOperationException("The timeout checkpoint exceeded the 128-page/8 MiB resume limit; this category must restart without resume.");
                if (checkpoint.ConfigurationKey != snapshot.SettingsKey && checkpoint.ConfigurationKey != snapshot.Key)
                    throw new InvalidOperationException("TuneIn settings changed after the timeout; the prior checkpoint cannot be resumed safely.");
                if (matchingJob && previous.ConfigurationKey != snapshot.SettingsKey
                    && previous.ConfigurationKey != snapshot.Key)
                    throw new InvalidOperationException("TuneIn settings changed after the timeout; the prior checkpoint cannot be resumed safely.");
                if (checkpoint.ConfigurationKey == snapshot.Key && checkpoint.ConfigurationKey != snapshot.SettingsKey)
                    checkpoint.ConfigurationKey = snapshot.SettingsKey;
            }
            if (checkpoint.ConfiguredTask)
            {
                var execution = ExecuteConfiguredAsync(channelManager, logger, CancellationToken.None,
                    new InlineProgress(), checkpoint, synchronize);
                _ = execution.ContinueWith(task =>
                {
                    if (task.IsFaulted) logger?.Error("Resumed Refresh TuneIn failed ({0}).",
                        task.Exception?.GetBaseException().GetType().Name ?? "unknown error");
                }, TaskContinuationOptions.OnlyOnFaulted);
                lock (gate) return jobs.TryGetValue(category, out var resumed) ? resumed.Status.Copy()
                    : TimedOutStatus(checkpoint, category, snapshot.SettingsKey);
            }
            return StartJob(category, synchronize, logger, false, attemptId, null, checkpoint).Status.Copy();
        }

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
            CancellationToken token, IProgress<double> progress, ConfiguredRun run,
            Func<string, int, CancellationToken, Task> synchronizeOverride = null)
        {
            token.ThrowIfCancellationRequested();
            var resumedFromId = run?.ResumeCategory == category ? run.ResumeAttemptId : null;
            var job = StartJob(category, synchronizeOverride ?? Synchronizer(manager), logger,
                false, resumedFromId, run);
            lock (gate)
            {
                if (run != null) run.Job = job;
                if (run != null) run.Progress = progress;
                job.AwaitingNativeExecution = false;
                job.NativeStart.TrySetResult(true);
            }
            using (token.Register(() =>
            {
                lock (gate)
                {
                    if (Active(job.Status.State)) { job.ExternallyCancelled = true; job.Cancellation.Cancel(); }
                }
            }))
            {
                if (run == null) progress.Report(0);
                await job.Completion.ConfigureAwait(false);
                RadioGroupRefreshStatus status;
                Exception failure;
                lock (gate)
                {
                    status = job.Status.Copy();
                    failure = job.Failure;
                }
                if (status.State == "TimedOut") throw new TimeoutException(status.Error, failure);
                if (status.State == "Cancelled") throw new OperationCanceledException(status.Error, token);
                if (status.State != "Completed")
                {
                    var linkDiagnostic = DirectoryRequest.LinkDiagnostic(failure);
                    throw new InvalidOperationException("TuneIn category '" + status.Category + "' failed. Phase: "
                        + status.Phase + ". Catalogue requests completed/started: " + status.CompletedRequests + "/"
                        + status.TotalRequests + ". Last HTTP status: " + (status.LastHttpStatus?.ToString() ?? "not recorded")
                        + ". " + status.Error + (linkDiagnostic == null ? "" : " " + linkDiagnostic), failure);
                }
                if (run == null) progress.Report(100);
                else
                {
                    lock (gate)
                    {
                        run.CompletedRequestsBefore += status.CompletedRequests;
                        run.CacheHitsBefore += status.CacheHits;
                        run.CategoryCounts.Add(new TuneInCategoryRequestCount
                        { Category = category, Requests = status.CompletedRequests });
                        run.Job = null;
                        run.Phase = "Finishing category";
                    }
                    ReportConfiguredProgress(run, null);
                }
            }
        }

        public Task ExecuteConfiguredAsync(IChannelManager manager, ILogger logger,
            CancellationToken token, IProgress<double> progress) =>
            ExecuteConfiguredAsync(manager, logger, token, progress, null, null);

        private Task ExecuteConfiguredAsync(IChannelManager manager, ILogger logger,
            CancellationToken token, IProgress<double> progress, TuneInTimedOutCheckpoint resume,
            Func<string, int, CancellationToken, Task> synchronizeOverride = null)
        {
            ConfiguredRun run;
            lock (gate)
            {
                if (configuredRun != null || jobs.Values.Any(job => Active(job.Status.State) || job.AwaitingNativeExecution))
                    throw new InvalidOperationException("A TuneIn refresh task is already running.");
                var snapshot = directory.Snapshot();
                var categories = snapshot.Categories.ToArray();
                if (categories.Length == 0)
                {
                    token.ThrowIfCancellationRequested();
                    progress.Report(100);
                    return Task.CompletedTask;
                }
                ValidateConfiguredCheckpoint(resume, snapshot, categories);
                var baseline = history.Baseline(snapshot.SettingsKey);
                run = new ConfiguredRun
                {
                    Categories = categories,
                    Count = categories.Length,
                    Phase = "Preparing selected categories",
                    TimeoutMinutes = snapshot.Value.RefreshTimeoutMinutes,
                    HasBaseline = baseline != null,
                    EstimatedRequests = baseline?.TotalRequests ?? 0,
                    ConfigurationKey = snapshot.SettingsKey,
                    StartedUtc = DateTime.UtcNow,
                    ResumeCategory = resume?.Category,
                    ResumeAttemptId = resume?.AttemptId
                };
                if (resume != null)
                {
                    run.CategoryCounts.AddRange(resume.CompletedRequestCounts.Select(item =>
                        new TuneInCategoryRequestCount { Category = item.Category, Requests = item.Requests }));
                    run.CompletedRequestsBefore = run.CategoryCounts.Sum(item => item.Requests);
                }
                configuredRun = run;
                lastConfiguredOutcome = "Running";
                lastEstimateWarning = null;
                run.TimeoutTimer = new Timer(_ => TimeoutConfiguredRun(run), null,
                    timeoutOverride ?? TimeSpan.FromMinutes(run.TimeoutMinutes), Timeout.InfiniteTimeSpan);
            }
            var execution = ExecuteConfiguredRunAsync(run, manager, logger, token, progress, resume, synchronizeOverride);
            lock (gate) lastConfiguredCompletion = execution;
            return execution;
        }

        private static void ValidateConfiguredCheckpoint(TuneInTimedOutCheckpoint checkpoint,
            ConfigurationSnapshot snapshot, string[] categories)
        {
            if (checkpoint == null) return;
            if (!checkpoint.ConfiguredTask || checkpoint.ConfigurationKey != snapshot.SettingsKey
            || !checkpoint.PagesComplete
                || checkpoint.Categories == null || !checkpoint.Categories.SequenceEqual(categories)
                || checkpoint.CompletedCategories == null || checkpoint.CompletedCategories.Count > categories.Length
                || !checkpoint.CompletedCategories.SequenceEqual(categories.Take(checkpoint.CompletedCategories.Count))
                || checkpoint.Category == null || !categories.Contains(checkpoint.Category)
                || checkpoint.CompletedImportFolders == null || checkpoint.CompletedImportFolders.Count > 1000
                || checkpoint.CompletedImportFolders.Any(folder => string.IsNullOrEmpty(folder) || folder.Length > 6000)
                || checkpoint.CompletedImportFolders.Distinct(StringComparer.Ordinal).Count() != checkpoint.CompletedImportFolders.Count
                || checkpoint.CompletedRequestCounts == null
                || checkpoint.CompletedRequestCounts.Select(item => item.Category).Distinct(StringComparer.Ordinal).Count()
                    != checkpoint.CompletedRequestCounts.Count
                || checkpoint.CompletedRequestCounts.Any(item => item.Requests < 0))
                throw new InvalidOperationException("The saved full-refresh checkpoint is invalid or no longer matches TuneIn settings.");
            if (!checkpoint.CompletedRequestCounts.Select(item => item.Category)
                .OrderBy(item => Array.IndexOf(categories, item))
                .SequenceEqual(checkpoint.CompletedCategories.OrderBy(item => Array.IndexOf(categories, item))))
                throw new InvalidOperationException("The saved request counts do not cover the completed categories.");
            var nextIndex = checkpoint.CompletedCategories.Count;
            if (nextIndex < categories.Length && categories[nextIndex] != checkpoint.Category)
                throw new InvalidOperationException("The saved full-refresh checkpoint does not match the next configured category.");
            if (nextIndex == categories.Length && categories[categories.Length - 1] != checkpoint.Category)
                throw new InvalidOperationException("The saved full-refresh checkpoint has no valid remaining category.");
            if (checkpoint.CompletedRequestCounts.Any(item => !checkpoint.CompletedCategories.Contains(item.Category)))
                throw new InvalidOperationException("The saved request counts do not match completed categories.");
        }

        private async Task ExecuteConfiguredRunAsync(ConfiguredRun run, IChannelManager manager, ILogger logger,
            CancellationToken token, IProgress<double> progress, TuneInTimedOutCheckpoint resume,
            Func<string, int, CancellationToken, Task> synchronizeOverride)
        {
            try
            {
                await directory.WithRefreshRunAsync(async () =>
                {
                    progress.Report(0);
                    for (var index = 0; index < run.Categories.Length; index++)
                    {
                        var category = run.Categories[index];
                        if (run.CategoryCounts.Any(item => item.Category == category)) continue;
                        token.ThrowIfCancellationRequested();
                        lock (gate)
                        {
                            run.Category = category;
                            run.Index = index + 1;
                            run.Job = null;
                            run.Phase = "Preparing category";
                        }
                        EnsureConfiguredRunNotTimedOut(run, category);
                        await ExecuteScheduledCoreAsync(category, manager, logger, token, progress, run,
                            synchronizeOverride).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        EnsureConfiguredRunNotTimedOut(run, category);
                        directory.CommitRefreshRun();
                        run.ResumeCategory = null;
                        run.ResumeAttemptId = null;
                    }
                }, resume?.Pages).ConfigureAwait(false);
                EnsureConfiguredRunNotTimedOut(run, run.Category ?? run.Categories.Last());
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    run.Finished = true;
                    run.TimeoutTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                }
                try { history.RecordBaseline(run.ConfigurationKey, run.CompletedRequestsBefore, run.CategoryCounts); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
                {
                    run.EstimateWarning = "The successful refresh could not save its query-count baseline.";
                    lock (gate) lastEstimateWarning = run.EstimateWarning;
                    logger?.Error("TuneIn refresh query baseline persistence failed ({0}).", error.GetType().Name);
                }
                lock (gate) lastConfiguredOutcome = "Completed";
                progress.Report(100);
            }
            catch (TimeoutException)
            {
                RecordConfiguredTimeoutCheckpoint(run, run.Category);
                lock (gate) lastConfiguredOutcome = "TimedOut";
                throw;
            }
            catch (OperationCanceledException)
            {
                lock (gate) lastConfiguredOutcome = "Cancelled";
                throw;
            }
            catch
            {
                lock (gate) lastConfiguredOutcome = "Error";
                throw;
            }
            finally
            {
                lock (gate)
                {
                    run.Finished = true;
                    run.TimeoutTimer?.Dispose();
                    if (configuredRun == run) configuredRun = null;
                }
            }
        }

        private Job StartJob(string category, Func<string, int, CancellationToken, Task> synchronize, ILogger logger,
            bool waitForNative = false, string resumedFromId = null, ConfiguredRun run = null,
            TuneInTimedOutCheckpoint resumeCheckpoint = null)
        {
            if (!Categories.Contains(category)) throw new ArgumentException("Unknown radio group.");
            lock (gate)
            {
                if (jobs.TryGetValue(category, out var previous) && (Active(previous.Status.State) || previous.AwaitingNativeExecution))
                    return previous;
                if (configuredRun != null && configuredRun != run)
                    throw new InvalidOperationException("A Refresh TuneIn run is already active.");
                if (jobs.Values.Any(activeJob => Active(activeJob.Status.State) || activeJob.AwaitingNativeExecution))
                    throw new InvalidOperationException("Another TuneIn refresh is already active.");
                var snapshot = directory.Snapshot();
                if (!snapshot.Enabled(category))
                    throw new InvalidOperationException("The TuneIn category is disabled or has no valid saved selections.");
                var entries = TuneInChannel.StartingEntries(snapshot, category);
                var baseline = history.Baseline(snapshot.SettingsKey);
                var baselineRequests = baseline?.Categories.SingleOrDefault(item => item.Category == category)?.Requests;
                var job = new Job
                {
                    Cancellation = directory.Operation(snapshot, CancellationToken.None),
                    AwaitingNativeExecution = waitForNative,
                    ConfiguredRun = run,
                    Status = new RadioGroupRefreshStatus
                    {
                        Category = category,
                        Id = Guid.NewGuid().ToString("N"),
                        ResumedFromId = resumedFromId,
                        ConfiguredTask = run != null,
                        Enabled = true,
                        State = "Queued",
                        Phase = "Waiting to populate the configured section",
                        TotalRequests = category == "language" ? 1 : 0,
                        EstimatedRequests = baselineRequests,
                        EstimatedCompletedRequests = 0,
                        TimeoutMinutes = snapshot.Value.RefreshTimeoutMinutes,
                        StartedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    }
                };
                if (!waitForNative) job.NativeStart.TrySetResult(true);
                jobs[category] = job;
                job.TimeoutMinutes = snapshot.Value.RefreshTimeoutMinutes;
                job.TimeoutDuration = run == null
                    ? timeoutOverride ?? TimeSpan.FromMinutes(job.TimeoutMinutes)
                    : Timeout.InfiniteTimeSpan;
                job.TimeoutTimer = new Timer(_ => TimeoutJob(job), null,
                    job.TimeoutDuration, Timeout.InfiniteTimeSpan);
                job.ConfigurationKey = snapshot.SettingsKey;
                var existingRun = directory.HasRefreshRun;
                job.Completion = Task.Run(() =>
                {
                    Func<Task> execute = () => RunAsync(job, snapshot, entries, synchronize, logger);
                    return existingRun ? execute() : directory.WithRefreshRunAsync(execute, resumeCheckpoint?.Pages);
                });
                return job;
            }
        }

        public RadioGroupRefreshStatus Cancel(string category, string id)
        {
            lock (gate)
            {
                if (category == null || !jobs.TryGetValue(category, out var job) || job.Status.Id != id)
                    throw new ArgumentException("Refresh is no longer current.");
                if (Active(job.Status.State)) { job.ExplicitlyCancelled = true; job.Cancellation.Cancel(); }
                return job.Status.Copy();
            }
        }

        internal Task Completion(string category)
        {
            lock (gate) return jobs[category].Completion;
        }

        private static bool Active(string state) => state == "Queued" || state == "Running" || state == "Synchronizing";

        private void TimeoutJob(Job job)
        {
            var cancel = false;
            lock (gate)
            {
                if (!job.ExplicitlyCancelled && !job.ExternallyCancelled && Active(job.Status.State))
                {
                    job.TimedOut = true;
                    cancel = true;
                }
            }
            if (cancel) job.Cancellation.Cancel();
        }

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
                using (job.Cancellation.Token.Register(() => job.NativeStart.TrySetCanceled()))
                {
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
                        Action<DirectoryRequest> started = request =>
                        {
                            Update(job, status =>
                            {
                                status.TotalRequests++;
                                status.State = "Running";
                                status.Phase = "Discovering selected catalogue pages";
                                status.LastHttpStatus = null;
                                status.LastApiResult = "Waiting for API response";
                            });
                            ReportConfiguredProgress(job);
                        };
                        Action<XDocument> completed = document =>
                        {
                            Update(job, status =>
                            {
                                status.CompletedRequests++;
                                status.EstimatedCompletedRequests = status.CompletedRequests;
                                status.EstimatedProgress = status.EstimatedRequests > status.CacheHits
                                    ? Math.Min(99d, 100d * status.CompletedRequests / (status.EstimatedRequests.Value - status.CacheHits)) : (double?)null;
                                status.LastApiResult = "Validated catalogue";
                                status.StationRows += document.Descendants("outline").Count(row => TuneInDirectory.Attribute(row, "type") == "audio");
                                status.FolderRows += document.Descendants("outline").Count(row => TuneInDirectory.Attribute(row, "type") == "link");
                            });
                            ReportConfiguredProgress(job);
                        };
                        operation.Token.ThrowIfCancellationRequested();
                        Update(job, status => { status.State = "Running"; status.Phase = "Discovering selected catalogue pages"; });
                        var folderId = directory.Register(snapshot, TuneInChannel.CategoryEntry(category));
                        // Emby 4.9.1.90's Refresh Internet Channels uses eight refresh levels.
                        const int depth = 8;
                        await directory.SynchronizeCategoryAsync(category, entries.Count,
                            () => synchronize(folderId, depth, operation.Token), started, received, completed,
                            entries.Select(entry => TuneInDirectory.RequestKey(snapshot, category, TuneInChannel.CatalogueRequest(snapshot, entry))),
                            () =>
                            {
                                Update(job, status => status.CacheHits++);
                                ReportConfiguredProgress(job);
                            },
                            () => Update(job, status =>
                            {
                                status.DiscoveryComplete = true;
                                status.DiscoveryMilliseconds = elapsed.ElapsedMilliseconds;
                                status.State = "Synchronizing";
                                status.Phase = "Importing staged section into Emby";
                            }),
                            phase => Update(job, status => { status.State = "Synchronizing"; status.Phase = phase; }),
                            (total, requested, items, imported) =>
                            {
                                Update(job, status =>
                                {
                                    status.State = "Synchronizing";
                                    status.ImportFoldersTotal = total;
                                    status.ImportFoldersRequested = requested;
                                    status.ImportFoldersCompleted = imported;
                                    status.ImportItemsServed = items;
                                    status.Phase = "Importing staged section into Emby: " + imported + "/" + total
                                        + " staged folders completed; " + requested + " requested; " + items + " items served";
                                });
                                ReportConfiguredProgress(job);
                            }).ConfigureAwait(false);
                        operation.Token.ThrowIfCancellationRequested();
                        Update(job, status =>
                        {
                            status.DiscoveryComplete = true;
                            status.Phase = "Configured section and station metadata populated in Emby";
                        });
                    }
                    directory.CommitRefreshRun();
                }
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                terminalState = job.TimedOut ? "TimedOut" : "Cancelled";
                Update(job, status =>
                {
                    status.Error = job.TimedOut
                        ? "Refresh exceeded its configured " + job.TimeoutMinutes + "-minute timeout. Resume reuses saved catalogue pages and restarts the interrupted category import."
                        : job.ExplicitlyCancelled ? "Cancelled by administrator." : "Refresh was externally cancelled.";
                });
            }
            catch (OperationCanceledException)
            {
                terminalState = "Cancelled";
                Update(job, status => status.Error = "A catalogue operation was cancelled by configuration change or an external cancellation.");
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
                var timeoutPagesComplete = false;
                var timeoutPages = terminalState == "TimedOut"
                    ? directory.CaptureRefreshPages(out timeoutPagesComplete) : null;
                lock (gate)
                {
                    if (beganDiscovery)
                    {
                        if (job.Status.DiscoveryMilliseconds.HasValue)
                            job.Status.ImportMilliseconds = elapsed.ElapsedMilliseconds - job.Status.DiscoveryMilliseconds.Value;
                        else
                            job.Status.DiscoveryMilliseconds = elapsed.ElapsedMilliseconds;
                    }
                    job.Status.ImportMayHavePartiallyApplied = terminalState != "Completed"
                        && job.Status.Phase?.StartsWith("Importing staged section into Emby", StringComparison.Ordinal) == true;
                    if (terminalState == "TimedOut")
                    {
                        job.Status.Resumable = timeoutPagesComplete;
                        if (!timeoutPagesComplete)
                            job.Status.Error = "Refresh timed out after the saved catalogue checkpoint exceeded the 128-page/8 MiB limit. This category cannot be safely resumed and must restart.";
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
                    try
                    {
                        if (job.Status.State == "TimedOut")
                        {
                            var run = job.ConfiguredRun;
                            history.RecordTimeoutCheckpoint(job.Status.Category, job.Status.Id, job.ConfigurationKey,
                                job.TimeoutMinutes, job.Status.ConfiguredTask, job.Status.FinishedUtc.Value,
                                run?.Categories, run?.CategoryCounts.Select(item => item.Category),
                                timeoutPages, Array.Empty<string>(),
                                run?.CategoryCounts, job.Status.Phase?.StartsWith("Importing staged section into Emby", StringComparison.Ordinal) == true,
                                timeoutPagesComplete);
                        }
                        else if (job.Status.ConfiguredTask)
                            history.ClearTimeoutCheckpoint();
                        else
                            history.ClearTimeoutCheckpoint(job.Status.Category);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
                    {
                        job.Status.HistoryError = "Timeout recovery state could not be saved; this timed-out refresh may not be resumable after a server restart.";
                        logger?.Error("TuneIn timeout recovery state persistence failed ({0}).", error.GetType().Name);
                    }
                    result = job.Status.Copy();
                    job.TimeoutTimer?.Dispose();
                    job.Cancellation.Dispose();
                }
                lock (gate)
                {
                    if (configuredRun != null && configuredRun.Job == job)
                    {
                        configuredRun.Phase = result.Phase;
                    }
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

        private void TimeoutConfiguredRun(ConfiguredRun run)
        {
            Job activeJob = null;
            lock (gate)
            {
                if (configuredRun != run || run.Finished) return;
                run.TimedOut = true;
                if (run.Job != null && Active(run.Job.Status.State))
                {
                    activeJob = run.Job;
                    activeJob.TimedOut = true;
                }
            }
            if (activeJob != null) activeJob.Cancellation.Cancel();
        }

        private void EnsureConfiguredRunNotTimedOut(ConfiguredRun run, string category)
        {
            bool timedOut;
            lock (gate) timedOut = run.TimedOut;
            if (!timedOut) return;
            RecordConfiguredTimeoutCheckpoint(run, category);
            throw new TimeoutException("Refresh TuneIn exceeded its configured " + run.TimeoutMinutes
                + "-minute timeout. The last category checkpoint can be resumed from its root.");
        }

        private void RecordConfiguredTimeoutCheckpoint(ConfiguredRun run, string category)
        {
            var completedCount = run.CategoryCounts.Count;
            if (completedCount < run.Categories.Length) category = run.Categories[completedCount];
            else if (run.Categories.Length > 0) category = run.Categories[run.Categories.Length - 1];
            var pages = directory.CaptureRefreshPages(out var pagesComplete);
            Job checkpointJob;
            lock (gate)
            {
                if (run.Job != null && run.Job.Status.State == "TimedOut"
                    && run.Job.ConfigurationKey == run.ConfigurationKey)
                    checkpointJob = run.Job;
                else if (jobs.TryGetValue(category, out var existing) && existing.Status.State == "TimedOut"
                    && existing.ConfigurationKey == run.ConfigurationKey)
                    checkpointJob = existing;
                else
                {
                    var now = DateTime.UtcNow;
                    checkpointJob = new Job
                    {
                        TimeoutMinutes = run.TimeoutMinutes,
                        TimeoutDuration = Timeout.InfiniteTimeSpan,
                        ConfigurationKey = run.ConfigurationKey,
                        Status = new RadioGroupRefreshStatus
                        {
                            Category = category,
                            Id = Guid.NewGuid().ToString("N"),
                            ConfiguredTask = true,
                            Enabled = true,
                            State = "TimedOut",
                            Phase = "Timed out between category checkpoints",
                            TimeoutMinutes = run.TimeoutMinutes,
                            Error = "Refresh TuneIn exceeded its configured " + run.TimeoutMinutes
                                + "-minute timeout. " + (pagesComplete
                                    ? "Resume restarts the interrupted category import from its root."
                                    : "The saved catalogue checkpoint exceeded the 128-page/8 MiB limit; resume is unavailable."),
                            Resumable = pagesComplete,
                            StartedUtc = run.StartedUtc,
                            FinishedUtc = now,
                            UpdatedUtc = now
                        },
                        Completion = Task.CompletedTask,
                        ConfiguredRun = run,
                    };
                    jobs[category] = checkpointJob;
                }
                run.Job = checkpointJob;
                run.Category = category;
            }
            var savedCheckpoint = history.TimeoutCheckpoint;
            if (pages.Count == 0 && savedCheckpoint != null
                && savedCheckpoint.AttemptId == checkpointJob.Status.Id
                && savedCheckpoint.ConfigurationKey == run.ConfigurationKey)
            {
                pages = savedCheckpoint.Pages;
                pagesComplete = savedCheckpoint.PagesComplete;
            }
            checkpointJob.Status.Resumable = pagesComplete;
            checkpointJob.Status.Error = "Refresh TuneIn exceeded its configured " + run.TimeoutMinutes
                + "-minute timeout. " + (pagesComplete
                    ? "Resume restarts the interrupted category import from its root."
                    : "The saved catalogue checkpoint exceeded the 128-page/8 MiB limit; resume is unavailable.");
            try
            {
                history.Record(category, "TimedOut", checkpointJob.Status.FinishedUtc.Value);
                history.RecordTimeoutCheckpoint(category, checkpointJob.Status.Id, run.ConfigurationKey,
                    run.TimeoutMinutes, true, checkpointJob.Status.FinishedUtc.Value,
                    run.Categories, run.CategoryCounts.Select(item => item.Category), pages,
                    Array.Empty<string>(), run.CategoryCounts,
                    checkpointJob.Status.Phase?.StartsWith("Importing staged section into Emby", StringComparison.Ordinal) == true,
                    pagesComplete);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
            {
                checkpointJob.Status.HistoryError = "Timeout recovery state could not be saved; this timed-out refresh may not be resumable after a server restart.";
            }
        }

        private static double? EstimateProgress(ConfiguredRun run, RadioGroupRefreshStatus status)
        {
            if (run == null) return null;
            var count = Math.Max(1, run.Count);
            var completedCategories = Math.Min(run.CategoryCounts.Count, count);
            var currentCompleted = status?.State == "Completed";
            var importing = status?.Phase != null
                && status.Phase.StartsWith("Importing staged section into Emby", StringComparison.Ordinal);
            double queryFraction;
            if (run.HasBaseline)
            {
                var estimatedRequests = Math.Max(0,
                    run.EstimatedRequests - run.CacheHitsBefore - (status?.CacheHits ?? 0));
                var completedRequests = run.CompletedRequestsBefore + (status?.CompletedRequests ?? 0);
                queryFraction = estimatedRequests > 0
                    ? Math.Min(1d, (double)completedRequests / estimatedRequests)
                    : Math.Min(1d, (double)(completedCategories + (currentCompleted ? 1 : 0)) / count);
            }
            else
            {
                var currentDiscovery = currentCompleted || importing ? 1d
                    : status == null || status.TotalRequests == 0 ? 0d
                    : Math.Min(1d, (double)status.CompletedRequests / status.TotalRequests);
                queryFraction = Math.Min(1d,
                    (completedCategories + (currentCompleted ? 0d : currentDiscovery)) / count);
            }
            var currentImport = currentCompleted ? 1d
                : importing && status.ImportFoldersTotal > 0
                    ? Math.Min(1d, (double)status.ImportFoldersRequested / status.ImportFoldersTotal) : 0d;
            var importFraction = Math.Min(1d, (completedCategories + (currentCompleted ? 0d : currentImport)) / count);
            return Math.Min(99d, 90d * queryFraction + 10d * importFraction);
        }

        private static RadioGroupRefreshStatus TimedOutStatus(TuneInTimedOutCheckpoint checkpoint,
            string category, string configurationKey)
        {
            if (checkpoint == null || checkpoint.Category != category || checkpoint.ConfigurationKey != configurationKey)
                return null;
            return new RadioGroupRefreshStatus
            {
                Category = checkpoint.Category,
                Id = checkpoint.AttemptId,
                State = "TimedOut",
                Resumable = checkpoint.PagesComplete,
                ConfiguredTask = checkpoint.ConfiguredTask,
                TimeoutMinutes = checkpoint.TimeoutMinutes,
                ImportFoldersCompleted = 0,
                ImportMayHavePartiallyApplied = checkpoint.ImportMayHavePartiallyApplied,
                Error = "Refresh exceeded its configured " + checkpoint.TimeoutMinutes
                    + "-minute timeout. " + (checkpoint.PagesComplete
                        ? "Resume restarts the interrupted category import from its root."
                        : "The saved catalogue checkpoint exceeded the 128-page/8 MiB limit; resume is unavailable."),
                StartedUtc = checkpoint.TimedOutUtc,
                FinishedUtc = checkpoint.TimedOutUtc
            };
        }

        private void ReportConfiguredProgress(Job job)
        {
            ConfiguredRun run;
            RadioGroupRefreshStatus status;
            IProgress<double> progress;
            double? value;
            lock (gate)
            {
                run = configuredRun;
                if (run == null || run.Job != job) return;
                status = job.Status;
                progress = run.Progress;
                value = EstimateProgress(run, status);
                if (value.HasValue)
                {
                    value = Math.Max(run.LastProgress, value.Value);
                    run.LastProgress = value.Value;
                }
            }
            if (value.HasValue) progress?.Report(value.Value);
        }

        private void ReportConfiguredProgress(ConfiguredRun run, Job job)
        {
            IProgress<double> progress;
            double? value;
            lock (gate)
            {
                progress = run.Progress;
                var status = job?.Status;
                value = EstimateProgress(run, status);
                if (value.HasValue)
                {
                    run.LastProgress = Math.Max(run.LastProgress, value.Value);
                    value = run.LastProgress;
                }
            }
            if (value.HasValue) progress?.Report(value.Value);
        }

        private sealed class Job
        {
            public bool AwaitingNativeExecution { get; set; }
            public bool NativeLaunchRequested { get; set; }
            public Task NativeExecution { get; set; }
            public TaskCompletionSource<bool> NativeStart { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public RadioGroupRefreshStatus Status { get; set; }
            public CancellationTokenSource Cancellation { get; set; }
            public bool ExplicitlyCancelled { get; set; }
            public bool ExternallyCancelled { get; set; }
            public Task Completion { get; set; }
            public Exception Failure { get; set; }
            public bool TimedOut { get; set; }
            public int TimeoutMinutes { get; set; }
            public TimeSpan TimeoutDuration { get; set; }
            public Timer TimeoutTimer { get; set; }
            public string ConfigurationKey { get; set; }
            public ConfiguredRun ConfiguredRun { get; set; }
        }

        private sealed class ConfiguredRun
        {
            public string[] Categories { get; set; } = Array.Empty<string>();
            public string Category { get; set; }
            public string Phase { get; set; }
            public int Index { get; set; }
            public int Count { get; set; }
            public Job Job { get; set; }
            public int TimeoutMinutes { get; set; }
            public int EstimatedRequests { get; set; }
            public bool HasBaseline { get; set; }
            public string EstimateWarning { get; set; }
            public int CompletedRequestsBefore { get; set; }
            public int CacheHitsBefore { get; set; }
            public List<TuneInCategoryRequestCount> CategoryCounts { get; } = new List<TuneInCategoryRequestCount>();
            public IProgress<double> Progress { get; set; }
            public double LastProgress { get; set; }
            public string ConfigurationKey { get; set; }
            public string ResumeCategory { get; set; }
            public string ResumeAttemptId { get; set; }
            public DateTime StartedUtc { get; set; }
            public Timer TimeoutTimer { get; set; }
            public bool TimedOut { get; set; }
            public bool Finished { get; set; }
        }

        private sealed class InlineProgress : IProgress<double>
        {
            public void Report(double value) { }
        }

    }
}
