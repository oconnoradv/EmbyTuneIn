using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class TuneInRefreshStatus
    {
        public TuneInCleanupStatus Cleanup { get; set; }
        public string State { get; set; } = "NeverRun";
        public bool Busy { get; set; }
        public string Category { get; set; }
        public string Phase { get; set; }
        public int CategoriesTotal { get; set; }
        public int CategoriesCompleted { get; set; }
        public int CategoriesSkipped { get; set; }
        public int SkippedBrowseLinks { get; set; }
        public string SkippedBrowseLinkDetails { get; set; }
        public int RequestsStarted { get; set; }
        public int RequestsCompleted { get; set; }
        public int CacheHits { get; set; }
        public int CatalogPagesCached { get; set; }
        public int CatalogPagesLimit { get; set; } = TuneInCatalogueSession.MaxRefreshPages;
        public int RateLimitRetries { get; set; }
        public int ActiveRequests { get; set; }
        public bool PartialImportAllowed { get; set; }
        public bool ReportAvailable { get; set; }
        public int? LastHttpStatus { get; set; }
        public int ImportFoldersTotal { get; set; }
        public int ImportFoldersServed { get; set; }
        public TuneInImportProgress ImportProgress { get; set; }
        public bool ImportMayHavePartiallyApplied { get; set; }
        public bool Resumed { get; set; }
        public int DiscoveryTimeouts { get; set; }
        public int ImportTimeouts { get; set; }
        public int? AutomaticRetry { get; set; }
        public int? AutomaticRetryLimit { get; set; }
        public string AutomaticRetryPhase { get; set; }
        public string AutomaticRetryReason { get; set; }
        public DateTime? DiscoveryCheckpointUtc { get; set; }
        public double Progress { get; set; }
        public string ErrorCode { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string DirtyCategory { get; set; }
        public string RecoveryCategory { get; set; }
        public string RecoveryId { get; set; }
        public bool RecoveryChoiceRequired { get; set; }
        public string ActiveRequest { get; set; }
        public bool Cancelling { get; set; }
        public string[] QueuedRequests { get; set; }
        public int QueueSlotsAvailable { get; set; }
        public TuneInCategoryRefreshStatus[] Categories { get; set; }
        public bool? SettingsLocked { get; set; }
        public bool HasRefreshBeenStarted { get; set; }
        public DateTime? LastProgressUtc { get; set; }
        public string Activity { get; set; }
        public string PluginVersion { get; set; } = PluginBuild.Display;
        public string ArtworkFolder { get; set; }
        public int ArtworkCards { get; set; }
        public string ArtworkPackName { get; set; }
        public int? ArtworkPackImageCount { get; set; }
        public string ArtworkPackStatus { get; set; }
        public TuneInArtworkPackStatus[] ArtworkPacks { get; set; }
        internal TuneInRefreshStatus Copy() => (TuneInRefreshStatus)MemberwiseClone();
    }

    public sealed class TuneInArtworkPackStatus
    {
        public string Scope { get; set; }
        public string Path { get; set; }
        public string Status { get; set; }
        public string Name { get; set; }
        public int? ImageCount { get; set; }
        public int? InstalledCount { get; set; }
        public int? MatchedCount { get; set; }
        public int? ModifiedCount { get; set; }
        public int? AddedCount { get; set; }
    }

    internal static class PluginBuild
    {
        internal static readonly string Display = Describe(typeof(PluginBuild).Assembly);

        // Every RC shares assembly version 1.2.8.0, so append the source commit to tell builds apart.
        internal static string Describe(System.Reflection.Assembly assembly)
        {
            var version = "v" + assembly.GetName().Version;
            var informational = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            var plus = informational?.IndexOf('+') ?? -1;
            if (plus < 0 || plus == informational.Length - 1) return version;
            var commit = informational.Substring(plus + 1);
            return version + " (" + (commit.Length > 7 ? commit.Substring(0, 7) : commit) + ")";
        }
    }

    public sealed class TuneInCategoryRefreshStatus
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public string LimitReason { get; set; }
        public TuneInStationCounts StationCounts { get; set; }
    }

    public sealed class TuneInRefreshFailureLog
    {
        public string Text { get; set; }
        public bool Truncated { get; set; }
        public DateTime? FinishedUtc { get; set; }
    }

    internal sealed class TuneInRefresh : IDisposable
    {
        private const int ChannelImportVersion = 2;
        private static readonly TimeSpan DiscoveryCheckpointInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DiscoveryCheckpointMaxAge = TimeSpan.FromHours(48);
        internal const int InterruptionRetryLimit = 3;
        private const string ImportDeadlineActivity = "Import deadline reached; waiting for active work to stop";
        private const int FailureReportLimit = 64 * 1024;
        private const int SkipReasonDisplayLimit = 5;
        private const int SkippedEntryReportLimit = 100;
        private const int SkippedEntryCharacterLimit = 48 * 1024;
        private readonly object gate = new object();
        private readonly TuneInDirectory directory;
        private readonly TuneInRefreshStore store;
        private readonly Func<TuneInChannel> getChannel;
        private readonly Func<TuneInChannel, string, TuneInCatalogueSession, bool, CancellationToken,
            Task<ChannelItemResult>> getItems;
        private readonly Func<TuneInChannel, TuneInStagedImport, string, CancellationToken, Task> import;
        private readonly ILogger logger;
        private readonly Action<TuneInChannel, string, CancellationToken> updateArtwork;
        private readonly Func<TuneInChannel, string, CancellationToken, int?> countStations;
        private readonly Func<DateTime> clock;
        private readonly TimeSpan? timeoutOverride;
        private TuneInRefreshStatus status = new TuneInRefreshStatus();
        private TuneInRefreshStatus lastStatus = new TuneInRefreshStatus();
        private ConfigurationSnapshot statusConfiguration;
        private TuneInRefreshTimings activeTimings;
        private TuneInRefreshFailureLog failureLog;
        private string recentPagesKey;
        private string recentPagesCategory;
        private readonly Dictionary<string, TuneInCachedPage> recentPages =
            new Dictionary<string, TuneInCachedPage>(StringComparer.Ordinal);
        private PendingImport pendingImport;
        private TuneInRefreshRecovery activeRecovery;
        private readonly Dictionary<string, (int IntervalHours, DateTime DueUtc)> scheduled =
            new Dictionary<string, (int, DateTime)>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> categoryStates = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TuneInStationCounts> categoryCounts = new Dictionary<string, TuneInStationCounts>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> categoryLimitReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        private string categoryStateKey;
        private string dirtyCategory;
        private CommittedCatalogue committed = new CommittedCatalogue(null,
            new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal));
        private CancellationTokenSource cancellation;
        private Task work;
        private readonly Queue<string> queue = new Queue<string>();
        private const int QueueCapacity = 2;
        private string activeRequest;
        private string recoveryId = Guid.NewGuid().ToString("N");
        private bool disposed;
        private enum RetryOutcome { None, Interruption }
        private readonly TuneInLibraryCleanup libraryCleanup;
        private readonly bool allowScopedImport;
        private TuneInLibraryCleanup.Plan cleanupPlan;
        private string cleanupToken;
        private DateTime cleanupExpires;
        private TuneInCleanupStatus cleanup = new TuneInCleanupStatus();
        private CancellationTokenSource cleanupCancellation;
        private Task cleanupWork;
        private bool Cleaning => cleanup.State == "Running";

        private sealed class PendingImport
        {
            internal string DiscoveryKey;
            internal string RequestedCategory;
            internal bool AllowPartial;
            internal DateTime DiscoveryStarted;
            internal TuneInCategorySnapshot[] Listings;
            internal HashSet<string> Partial;
            internal TuneInRefreshStatus DiscoveryStatus;
            internal List<string> SkippedEntries;
            internal int SkippedEntriesOmitted;
        }

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
            ILogger logger, Func<DateTime> clock = null, TimeSpan? timeoutOverride = null,
            Action<TuneInChannel, string, CancellationToken> updateArtwork = null,
            Func<TuneInChannel, string, CancellationToken, int?> countStations = null,
            TuneInLibraryCleanup libraryCleanup = null, bool allowScopedImport = false)
        {
            this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.getChannel = getChannel ?? throw new ArgumentNullException(nameof(getChannel));
            this.getItems = getItems ?? throw new ArgumentNullException(nameof(getItems));
            this.import = import ?? throw new ArgumentNullException(nameof(import));
            this.logger = logger;
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.timeoutOverride = timeoutOverride;
            this.updateArtwork = updateArtwork;
            this.libraryCleanup = libraryCleanup;
            this.allowScopedImport = allowScopedImport;
            this.countStations = countStations ?? ((channel, folder, token) => channel.CountCategoryStations(folder, token));
            statusConfiguration = directory.Snapshot();
            LoadCategoryStates(statusConfiguration);
        }

        internal TuneInRefreshStatus Status()
        {
            if (!Monitor.TryEnter(gate))
            {
                var previous = Volatile.Read(ref lastStatus).Copy();
                previous.Busy = true;
                previous.Activity = "Waiting for refresh state";
                return previous;
            }
            try
            {
                var snapshot = statusConfiguration;
                var copy = status.Copy();
                copy.HasRefreshBeenStarted = categoryStates.Count > 0;
                copy.Cleanup = cleanup.Copy();
                copy.Busy = work != null && !work.IsCompleted;
                copy.ReportAvailable = !copy.Busy && failureLog != null;
                if (!copy.Busy && copy.DiscoveryTimeouts > 0)
                    copy.Phase += copy.DiscoveryTimeouts == 1
                        ? " (1 discovery timeout)" : $" ({copy.DiscoveryTimeouts} discovery timeouts)";
                if (!copy.Busy && copy.ImportTimeouts > 0)
                    copy.Phase += copy.ImportTimeouts == 1
                        ? " (1 import timeout)" : $" ({copy.ImportTimeouts} import timeouts)";
                copy.DirtyCategory = dirtyCategory;
                copy.RecoveryCategory = copy.Busy ? null : (snapshot.Enabled(dirtyCategory) ? dirtyCategory : null) ??
                    (copy.State == "Error" && snapshot.Enabled(activeRequest ?? copy.Category) ? activeRequest ?? copy.Category : null);
                copy.RecoveryId = copy.RecoveryCategory == null ? null : recoveryId;
                copy.ActiveRequest = copy.Busy ? activeRequest ?? "all" : null;
                copy.Cancelling = copy.Busy && cancellation.IsCancellationRequested;
                if (!copy.Busy || copy.Cancelling)
                {
                    copy.AutomaticRetry = null;
                    copy.AutomaticRetryLimit = null;
                    copy.AutomaticRetryPhase = null;
                    copy.AutomaticRetryReason = null;
                }
                if (copy.Busy && !copy.Cancelling && copy.Activity == ImportDeadlineActivity)
                    copy.Phase = "Import deadline reached; draining";
                copy.ActiveRequests = activeTimings == null ? 0 : Volatile.Read(ref activeTimings.ActiveResponses);
                if (copy.Busy && copy.Activity == null && copy.Phase == "Discovering")
                    copy.Activity = activeTimings?.DescribeWait();
                copy.QueuedRequests = queue.Select(category => category ?? "all").ToArray();
                copy.QueueSlotsAvailable = QueueCapacity - queue.Count;
                copy.Categories = snapshot.Categories.Select(category => new TuneInCategoryRefreshStatus
                {
                    Category = category,
                    Name = TuneInBrowsing.CategoryName(category),
                    State = copy.Busy && copy.State == "Running" && copy.Category == category ? copy.Phase :
                        categoryStates.TryGetValue(category, out var state) ? state : "No recorded refresh",
                    LimitReason = categoryStates.TryGetValue(category, out var limitedState) && limitedState.StartsWith("Limited", StringComparison.Ordinal)
                        && categoryLimitReasons.TryGetValue(category, out var reason) ? reason : null,
                    StationCounts = categoryCounts.TryGetValue(category, out var counts) ? CountsForStatus(counts) : null
                }).ToArray();
                Volatile.Write(ref lastStatus, copy);
                return copy.Copy();
            }
            finally { Monitor.Exit(gate); }
        }

        internal TuneInRefreshStatus Start(string category = null, string expectedRecoveryId = null, bool? resumeFirst = null)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(TuneInRefresh));
                EnsureNoCleanup();
                cleanupPlan = null;
                var snapshot = directory.Snapshot();
                statusConfiguration = snapshot;
                LoadCategoryStates(snapshot);
                if (category != null && !snapshot.Enabled(category))
                    throw new ArgumentException("Choose a category enabled by the saved TuneIn configuration.", nameof(category));
                var current = Status();
                if (current.Busy)
                {
                    if (resumeFirst.HasValue || cancellation.IsCancellationRequested)
                        throw new InvalidOperationException("Refresh status changed. Check the current refresh before retrying.");
                    if (activeRequest == category || queue.Contains(category))
                        throw new InvalidOperationException("That TuneIn refresh is already active or queued.");
                    if (queue.Count >= QueueCapacity)
                        throw new InvalidOperationException("The TuneIn refresh queue is full.");
                    queue.Enqueue(category);
                    return Status();
                }
                if (category != null && current.RecoveryCategory != null && current.RecoveryCategory != category)
                {
                    if (!resumeFirst.HasValue)
                    {
                        current.RecoveryChoiceRequired = true;
                        return current;
                    }
                    if (current.RecoveryId != expectedRecoveryId)
                        throw new InvalidOperationException("The TuneIn recovery choice is no longer current.");
                    if (resumeFirst.Value)
                    {
                        queue.Enqueue(category);
                        category = current.RecoveryCategory;
                    }
                }
                else if (resumeFirst.HasValue)
                    throw new InvalidOperationException("The TuneIn recovery choice is no longer current.");
                Begin(snapshot, category, ResumeRecovery(snapshot, category));
                return Status();
            }
        }

        private void Begin(ConfigurationSnapshot snapshot, string category, TuneInRefreshRecovery recovery = null)
        {
            cleanupPlan = null;
            Prepare(snapshot, category, recovery);
            work = Task.Run(async () =>
            {
                var next = category;
                while (true)
                {
                    try
                    {
                        while (true)
                        {
                            var outcome = await ExecuteAsync(snapshot, next, cancellation.Token).ConfigureAwait(false);
                            if (outcome == RetryOutcome.None) break;
                            lock (gate)
                            {
                                if (cancellation.IsCancellationRequested || disposed) break;
                                activeRecovery.Retries++;
                                store.WriteRecovery(activeRecovery);
                                status = new TuneInRefreshStatus
                                {
                                    State = "Running", Phase = "Preparing", Category = next,
                                    StartedUtc = status.StartedUtc, DiscoveryTimeouts = status.DiscoveryTimeouts,
                                    ImportTimeouts = status.ImportTimeouts,
                                    AutomaticRetry = activeRecovery.Retries,
                                    AutomaticRetryLimit = InterruptionRetryLimit,
                                    AutomaticRetryPhase = activeRecovery.Phase,
                                    AutomaticRetryReason = "interruption"
                                };
                            }
                            logger?.Info("TuneIn refresh for {0}: starting interruption retry after active work drained.",
                                next ?? "all");
                        }
                    }
                    catch (Exception error)
                    {
                        RecordWorkerFailure(error);
                    }
                    try
                    {
                        lock (gate)
                        {
                            // Shutdown leaves the active marker for the next server instance.
                            if (!disposed) store.ClearRecovery();
                            if (disposed || cancellation.IsCancellationRequested || queue.Count == 0)
                            {
                                queue.Clear();
                                work = null;
                                return;
                            }
                            next = queue.Dequeue();
                            Prepare(snapshot, next);
                        }
                    }
                    catch (Exception error)
                    {
                        logger?.Error("TuneIn refresh recovery state could not be updated ({0}).", error.GetType().Name);
                        lock (gate)
                        {
                            status.State = "Error";
                            status.ErrorCode = error.GetType().Name;
                            status.FinishedUtc = clock();
                            failureLog = CreateFailureLog(error, status);
                            queue.Clear();
                            work = null;
                        }
                        return;
                    }
                }
            });
        }

        private void RecordWorkerFailure(Exception error)
        {
            logger?.Error("TuneIn refresh worker could not finish ({0}).", error.GetType().Name);
            lock (gate)
            {
                status.State = "Error";
                status.ErrorCode = error.GetType().Name;
                status.FinishedUtc = clock();
                failureLog = CreateFailureLog(error, status);
            }
        }

        private void Prepare(ConfigurationSnapshot snapshot, string category, TuneInRefreshRecovery recovery = null)
        {
            activeRecovery = recovery ?? new TuneInRefreshRecovery { ConfigurationKey = snapshot.Key, RequestedCategory = category };
            store.WriteRecovery(activeRecovery);
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            activeRequest = category;
            recoveryId = Guid.NewGuid().ToString("N");
            status = new TuneInRefreshStatus
            {
                State = "Running", Phase = "Preparing", Category = category, StartedUtc = clock(),
                AutomaticRetry = activeRecovery.Retries == 0 ? (int?)null : activeRecovery.Retries,
                AutomaticRetryLimit = activeRecovery.Retries == 0 ? (int?)null : InterruptionRetryLimit,
                AutomaticRetryPhase = activeRecovery.Retries == 0 ? null : activeRecovery.Phase,
                AutomaticRetryReason = activeRecovery.Retries == 0 ? null : "interruption"
            };
            failureLog = null;
        }

        private TuneInRefreshRecovery ResumeRecovery(ConfigurationSnapshot snapshot, string category)
        {
            var recovery = store.ReadRecovery();
            if (recovery == null) return null;
            if (recovery.ConfigurationKey == snapshot.Key && snapshot.Ready
                && recovery.RequestedCategory == category && recovery.Retries < InterruptionRetryLimit)
            {
                recovery.Retries++;
                logger?.Info("TuneIn refresh for {0}: resuming interrupted work on request (retry {1} of {2}).",
                    category ?? "all", recovery.Retries, InterruptionRetryLimit);
                return recovery;
            }
            store.ClearRecovery();
            return null;
        }

        internal TuneInRefreshStatus Cancel()
        {
            lock (gate)
            {
                if (work == null || work.IsCompleted) return Status();
                store.ClearRecovery();
                queue.Clear();
                cancellation.Cancel();
                status.Phase = "Cancelling and draining";
                return Status();
            }
        }

        internal TuneInCleanupStationSearch SearchCleanupStations(string name) =>
            libraryCleanup.SearchStations(directory.Snapshot(), name);

        internal TuneInCleanupPreview PreviewCleanup(string category = null, string stationReference = null)
        {
            lock (gate)
            {
                EnsureCleanupAvailable();
                cleanupPlan = null;
                if (category == "all") category = null;
                if (stationReference != null && category != null)
                    throw new ArgumentException("Choose either category cleanup or a station reference, not both.");
                var plan = stationReference == null ? libraryCleanup.Preview(directory.Snapshot(), category)
                    : libraryCleanup.PreviewStation(directory.Snapshot(), stationReference);
                if (plan.Targets.Length == 0)
                    return new TuneInCleanupPreview { Message = "No verified generated entries were found in the selected cleanup scope." };
                cleanupPlan = plan;
                cleanupToken = Guid.NewGuid().ToString("N");
                cleanupExpires = clock().AddMinutes(5);
                var folders = plan.Targets.Count(item => item.IsFolder);
                return new TuneInCleanupPreview
                {
                    Token = cleanupToken,
                    Scope = plan.SingleStation ? TuneInBrowsing.CategoryName(plan.Category)
                        : category == null ? "All categories" : TuneInBrowsing.CategoryName(category),
                    StationName = plan.StationName,
                    StationReference = plan.SingleStation ? plan.Targets[0].Id.ToString(CultureInfo.InvariantCulture) : null,
                    Folders = folders,
                    Stations = plan.Targets.Length - folders,
                    Message = plan.Unverified == 0 ? null : $"{plan.Unverified} unverified entries will be left alone."
                };
            }
        }

        internal TuneInRefreshStatus StartCleanup(string token)
        {
            lock (gate)
            {
                EnsureCleanupAvailable();
                if (cleanupPlan == null || token != cleanupToken || clock() >= cleanupExpires)
                    throw new InvalidOperationException("The TuneIn cleanup preview is no longer current. Preview again.");
                var plan = cleanupPlan;
                cleanupPlan = null;
                cleanup = new TuneInCleanupStatus { State = "Running", Total = plan.Targets.Length };
                cleanupCancellation?.Dispose();
                cleanupCancellation = new CancellationTokenSource();
                var cancellationToken = cleanupCancellation.Token;
                cleanupWork = Task.Run(() => RunCleanupAsync(plan, cancellationToken));
                return Status();
            }
        }

        internal TuneInRefreshStatus CancelCleanup()
        {
            lock (gate)
            {
                if (Cleaning && !cleanup.Cancelling)
                {
                    cleanup.Cancelling = true;
                    cleanupCancellation.Cancel();
                    cleanup.Message = "Stopping cleanup; current removals must finish.";
                }
                return Status();
            }
        }

        private async Task RunCleanupAsync(TuneInLibraryCleanup.Plan plan, CancellationToken token)
        {
            var stopped = false;
            var cancelled = false;
            var baselinePreserved = false;
            TuneInRefreshCheckpoint stationRollback = null;
            try
            {
                lock (gate) { recentPages.Clear(); recentPagesKey = null; recentPagesCategory = null; }
                store.ClearRecovery();
                store.ClearDiscovery();
                var snapshot = directory.Snapshot();
                var checkpoint = plan.Category == null ? null : store.Read();
                var preserve = checkpoint != null && checkpoint.ConfigurationKey == snapshot.Key;
                if (preserve)
                {
                    if (plan.SingleStation)
                    {
                        stationRollback = store.Read() ?? throw new InvalidOperationException("The retained snapshot changed during station cleanup.");
                        var parentId = directory.RebindOwned(snapshot, plan.Ancestors[0].ExternalId);
                        var stationId = directory.RebindOwned(snapshot, plan.Targets[0].ExternalId);
                        var saved = checkpoint.Categories.SingleOrDefault(category => category.Category == plan.Category);
                        if (saved != null && parentId != null && stationId != null)
                        {
                            var parentKey = directory.MembershipKey(snapshot, parentId);
                            var stationKey = directory.MembershipKey(snapshot, stationId);
                            var parents = saved.Folders.Where(folder => folder.Id == parentId).ToArray();
                            if (parents.Length == 0) parents = saved.Folders.Where(folder =>
                                directory.MembershipKey(snapshot, folder.Id) == parentKey).ToArray();
                            if (parents.Length > 1) throw new InvalidOperationException("The retained station parent is ambiguous.");
                            if (parents.Length == 1)
                            {
                                var index = parents[0].Items.FindIndex(item => item.Id == stationId);
                                if (index < 0) index = parents[0].Items.FindIndex(item =>
                                    directory.MembershipKey(snapshot, item.Id) == stationKey);
                                if (index >= 0) parents[0].Items.RemoveAt(index);
                            }
                            saved.Fingerprint = TuneInStagedImport.Fingerprint(saved.Folders);
                            saved.StationCounts = null;
                        }
                    }
                    if (!plan.SingleStation && snapshot.Enabled(plan.Category) && !checkpoint.CleanedCategories.Contains(plan.Category))
                        checkpoint.CleanedCategories.Add(plan.Category);
                    store.Write(checkpoint);
                    baselinePreserved = true;
                }
                else store.Clear();
                lock (gate)
                {
                    pendingImport = null;
                    activeRecovery = null;
                    status = new TuneInRefreshStatus();
                    failureLog = null;
                    recoveryId = Guid.NewGuid().ToString("N");
                    committed = new CommittedCatalogue(null, new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal));
                    categoryStateKey = null;
                    LoadCategoryStates(snapshot);
                }
                await libraryCleanup.RunAsync(plan, (removed, failed) =>
                {
                    lock (gate)
                    {
                        cleanup.Removed = removed;
                        cleanup.Failed = failed;
                    }
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception error)
            {
                stopped = true;
                logger?.Error("TuneIn library cleanup stopped ({0}).", error.GetType().Name);
            }
            finally
            {
                if (stationRollback != null && cleanup.Removed == 0)
                {
                    try { store.Write(stationRollback); }
                    catch (Exception error)
                    {
                        stopped = true;
                        logger?.Error("TuneIn station cleanup could not restore the retained snapshot ({0}).", error.GetType().Name);
                    }
                }
                lock (gate)
                {
                    cleanup.Cancelling = false;
                    cleanup.State = stopped ? "Failed" : cancelled ? "Stopped" : cleanup.Failed > 0 ? "Partial" : "Completed";
                    var recoveryMessage = plan.Category == null || !baselinePreserved
                        ? "Run Refresh all enabled categories to rebuild the catalog."
                        : "Other category snapshots were retained. A refresh can recreate enabled categories.";
                    cleanup.Message = stopped ? "Cleanup stopped before completing. Review the server log and preview again if needed."
                        : cancelled ? "Cleanup stopped. Removed entries cannot be restored by stopping. Preview again to remove remaining entries. " + recoveryMessage
                        : plan.Category == null ? "Run Refresh all enabled categories to rebuild the catalog."
                        : baselinePreserved ? "Other category snapshots were retained. A refresh can recreate enabled categories."
                        : "No compatible baseline was retained. Run Refresh all enabled categories to rebuild the catalog.";
                    if (plan.SingleStation)
                        cleanup.Message = stopped ? (cleanup.Removed == 1
                            ? "Station entry removed, but the retained snapshot could not be updated. Review the server log."
                            : "Station removal stopped. Review the server log.")
                            : cleanup.Removed == 1 ? "Only the referenced station entry was removed. Future discovery may add it again; no exclusion was saved."
                            : "Station entry was not removed; its retained snapshot is unchanged.";
                }
            }
        }

        private void EnsureCleanupAvailable()
        {
            if (disposed || libraryCleanup == null) throw new InvalidOperationException("TuneIn cleanup is unavailable.");
            EnsureNoCleanup();
            if (work != null && !work.IsCompleted)
                throw new InvalidOperationException("TuneIn cleanup cannot run while refresh work is active or draining.");
        }

        internal void EnsureNoCleanup()
        {
            lock (gate)
                if (Cleaning) throw new InvalidOperationException("TuneIn library cleanup is active.");
        }

        internal T SetSettingsLock<T>(Func<T> update)
        {
            lock (gate)
            {
                EnsureNoCleanup();
                if (work != null && !work.IsCompleted)
                    throw new InvalidOperationException("The TuneIn settings lock cannot change while refresh work is active or draining.");
                cleanupPlan = null;
                return update();
            }
        }

        internal void UpdateConfiguration(Action update)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            lock (gate)
            {
                EnsureNoCleanup();
                if (work != null && !work.IsCompleted)
                    throw new InvalidOperationException("TuneIn settings cannot change while refresh work is active or draining.");
                cleanupPlan = null;
                var previous = directory.Snapshot();
                var checkpoint = store.Read();
                update();
                var current = directory.Snapshot();
                statusConfiguration = current;
                pendingImport = null;
                store.ClearRecovery();
                if (current.Key == previous.Key) return;
                if (checkpoint?.ConfigurationKey == previous.Key)
                {
                    var retained = checkpoint.Categories.Where(category => current.Enabled(category.Category)
                        && (category.Category != "presets" || previous.Value.Username == current.Value.Username)
                        && (category.Category != "local" || previous.LocalSource != "coordinates"
                            && current.LocalSource != "coordinates"
                            || previous.CategoryScopeKey("local") == current.CategoryScopeKey("local"))).ToList();
                    foreach (var category in retained)
                    {
                        // Authenticate old entries, filter removed scopes, and re-sign the retained memberships.
                        var sign = directory.CategoryRebinder(previous, current, category.Category, filterSelections: true);
                        var rebound = new Dictionary<string, string>(StringComparer.Ordinal);
                        string Rebind(string id)
                        {
                            if (!rebound.TryGetValue(id, out var updated))
                                rebound[id] = updated = sign(id);
                            return updated;
                        }
                        category.RootId = Rebind(category.RootId);
                        category.Folders = category.Folders.Where(folder => Rebind(folder.Id) != null).ToList();
                        foreach (var folder in category.Folders)
                        {
                            folder.Id = Rebind(folder.Id);
                            folder.Items = folder.Items.Where(item => Rebind(item.Id) != null).ToList();
                            foreach (var item in folder.Items) item.Id = Rebind(item.Id);
                        }
                        category.Fingerprint = TuneInStagedImport.Fingerprint(category.Folders);
                    }
                    checkpoint.Categories = retained;
                    checkpoint.CleanedCategories.RemoveAll(category => !current.Enabled(category));
                    checkpoint.PendingCategories = current.Categories.Where(category =>
                        !retained.Any(saved => saved.Category == category) &&
                        checkpoint.DirtyCategory != category &&
                        (!previous.Enabled(category) || checkpoint.PendingCategories.Contains(category))).ToList();
                    checkpoint.ConfigurationKey = current.Key;
                    checkpoint.NeedsImport = true;
                    store.Write(checkpoint);
                }
                else store.Clear();
                categoryStateKey = null;
                LoadCategoryStates(current);
                status = new TuneInRefreshStatus();
                recoveryId = Guid.NewGuid().ToString("N");
                failureLog = null;
                committed = new CommittedCatalogue(null, new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal));
            }
        }

        internal void CheckSchedules()
        {
            lock (gate)
            {
                if (disposed || Cleaning) return;
                var snapshot = directory.Snapshot();
                statusConfiguration = snapshot;
                var now = clock();
                var enabled = (snapshot.Value.RefreshSchedules ?? new List<Configuration.CategoryRefreshSchedule>())
                    .Where(schedule => ConfigurationSnapshot.ValidSchedule(schedule) && snapshot.Enabled(schedule.Category))
                    .ToArray();
                var active = new HashSet<string>(enabled.Select(schedule => schedule.Category), StringComparer.Ordinal);
                foreach (var category in scheduled.Keys.Where(key => !active.Contains(key)).ToArray())
                    scheduled.Remove(category);
                foreach (var schedule in enabled)
                    if (!scheduled.TryGetValue(schedule.Category, out var slot) || slot.IntervalHours != schedule.IntervalHours)
                        scheduled[schedule.Category] = (schedule.IntervalHours, now.AddHours(schedule.IntervalHours));

                if (work != null && !work.IsCompleted || !scheduled.Values.Any(slot => slot.DueUtc <= now)) return;
                LoadCategoryStates(snapshot);
                var due = scheduled.Where(entry => entry.Value.DueUtc <= now &&
                        (dirtyCategory == null || dirtyCategory == entry.Key))
                    .OrderBy(entry => entry.Value.DueUtc).ThenBy(entry => entry.Key, StringComparer.Ordinal).FirstOrDefault();
                if (due.Key == null) return;
                pendingImport = null;
                Begin(snapshot, due.Key, ResumeRecovery(snapshot, due.Key));
                scheduled[due.Key] = (due.Value.IntervalHours, now.AddHours(due.Value.IntervalHours));
            }
        }

        internal TuneInRefreshFailureLog GetFailureLog()
        {
            lock (gate)
            {
                if (work != null && !work.IsCompleted || failureLog == null)
                    throw new InvalidOperationException("No recent TuneIn refresh diagnostic report is available.");
                return new TuneInRefreshFailureLog
                {
                    Text = failureLog.Text,
                    Truncated = failureLog.Truncated,
                    FinishedUtc = failureLog.FinishedUtc
                };
            }
        }

        // The checkpoint is bounded but can be large. Read it once per saved scope, not on every status poll.
        private void LoadCategoryStates(ConfigurationSnapshot snapshot)
        {
            if (categoryStateKey == snapshot.Key) return;
            categoryStates.Clear();
            categoryCounts.Clear();
            categoryLimitReasons.Clear();
            dirtyCategory = null;
            categoryStateKey = snapshot.Key;
            try
            {
                var checkpoint = store.Read();
                if (checkpoint?.ConfigurationKey != snapshot.Key) return;
                dirtyCategory = checkpoint.DirtyCategory;
                foreach (var category in checkpoint.Categories)
                {
                    categoryStates[category.Category] = category.DiscoveryPartial ? "Partial (applied)"
                        : category.DiscoveryLimitReason != null ? "Limited (imported)" : "Imported";
                    categoryLimitReasons[category.Category] = category.DiscoveryLimitReason;
                    if (category.StationCounts != null)
                        categoryCounts[category.Category] = category.StationCounts.Copy();
                }
                foreach (var category in checkpoint.CleanedCategories.Where(snapshot.Enabled))
                {
                    categoryStates[category] = "Incomplete (cleanup needs refresh)";
                    categoryCounts.Remove(category);
                }
            }
            catch (Exception error)
            {
                logger?.Error("TuneIn category refresh state could not be read ({0}).", error.GetType().Name);
                foreach (var category in snapshot.Categories) categoryStates[category] = "Status unavailable";
            }
        }

        private TuneInStationCounts CountsForStatus(TuneInStationCounts counts)
        {
            var copy = counts.Copy();
            if (dirtyCategory != null)
            {
                copy.EmbyCountError = "Import needs reconciliation";
            }
            return copy;
        }

        private void ReadEmbyStationCount(ConfigurationSnapshot snapshot, TuneInChannel channel, string category,
            string root, CancellationToken token)
        {
            int? count = null;
            string errorMessage = null;
            try
            {
                count = countStations(channel, root, token);
                if (count < 0 || count > TuneInStagedImport.MaxItems)
                    throw new InvalidDataException("Invalid TuneIn Emby inventory count.");
                if (!count.HasValue) errorMessage = "Emby inventory service unavailable";
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                count = null;
                errorMessage = "Emby inventory read failed; check server log";
                logger?.Warn("TuneIn station inventory could not be read for {0} ({1}).", category, error.GetType().Name);
            }
            directory.CheckCurrent(snapshot, token);
            lock (gate)
            {
                if (!categoryCounts.TryGetValue(category, out var counts))
                    categoryCounts[category] = counts = new TuneInStationCounts();
                if (count.HasValue)
                {
                    counts.EmbyStations = count;
                    counts.EmbyCheckedUtc = clock();
                }
                counts.EmbyCountError = errorMessage;
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
                logger?.Error("TuneIn committed catalog could not be read ({0}).", error.GetType().Name);
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
                if (checkpoint.CleanedCategories.Contains(category.Category)) continue;
                foreach (var folder in category.Folders)
                {
                    folders[folder.Id] = TuneInStagedImport.RestoreFolder(folder);
                }
            }
            return new CommittedCatalogue(key, folders);
        }

        private async Task<RetryOutcome> ExecuteAsync(ConfigurationSnapshot snapshot, string requestedCategory, CancellationToken serverToken)
        {
            TuneInCatalogueSession session = null;
            TuneInRefreshCheckpoint checkpoint = null;
            string currentCategory = null;
            var completed = new HashSet<string>(StringComparer.Ordinal);
            var skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);
            var skippedEntries = new List<string>();
            var skippedEntryCharacters = 0;
            var skippedEntriesOmitted = 0;
            DateTime started = clock();
            var timedOut = false;
            var discovering = false;
            var discoveryTimedOut = false;
            var importTimedOut = false;
            var importDeadlineReached = false;
            var retryInterrupted = false;
            string stoppedPhase = null;
            Exception failure = null;
            var timings = new TuneInRefreshTimings();
            var discoveryTime = new Stopwatch();
            var importTime = new Stopwatch();
            DateTime discoveryStarted = default, nextCheckpoint = default;
            var savedPageCount = 0;
            var discoveryDeadline = timeoutOverride ?? TimeSpan.FromHours(snapshot.Value.DiscoveryDeadlineHours);

            bool SaveDiscovery(CancellationToken token, bool force = false)
            {
                var savedAt = clock();
                if (session == null || (!force && savedAt < nextCheckpoint)
                    || savedAt < discoveryStarted || savedAt - discoveryStarted >= DiscoveryCheckpointMaxAge) return false;
                var pages = session.SnapshotPages();
                if (pages.Count <= savedPageCount) return false;
                directory.CheckCurrent(snapshot, token);
                store.WriteDiscovery(new TuneInDiscoveryCheckpoint
                {
                    ConfigurationKey = snapshot.DiscoveryKey, RequestedCategory = requestedCategory,
                    StartedUtc = discoveryStarted, SavedUtc = savedAt, Pages = pages
                });
                savedPageCount = pages.Count;
                nextCheckpoint = savedAt + DiscoveryCheckpointInterval;
                Update(() => status.DiscoveryCheckpointUtc = savedAt);
                return true;
            }

            using (var timeout = new CancellationTokenSource())
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, timeout.Token))
            using (timeout.Token.Register(() => Update(() =>
                status.Activity = "Attempt deadline reached; waiting for active work to stop")))
            {
                try
                {
                    Update(() => status.PartialImportAllowed = snapshot.Value.AllowPartialCatalogImport);
                    if (!snapshot.Ready) throw new InvalidOperationException("TuneIn settings are not complete.");
                    directory.CheckCurrent(snapshot, linked.Token);
                    var enabled = snapshot.Categories.ToArray();
                    timeout.CancelAfter(discoveryDeadline);

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
                    var listings = checkpoint.Categories.ToDictionary(category => category.Category, StringComparer.Ordinal);
                    var needsRebuild = checkpoint.ChannelImportVersion != ChannelImportVersion;
                    var now = clock();
                    var replay = pendingImport;
                    pendingImport = null;
                    if (replay != null && (dirtyAtStart == null || !compatible ||
                        replay.DiscoveryKey != snapshot.DiscoveryKey || replay.RequestedCategory != requestedCategory ||
                        replay.AllowPartial != snapshot.Value.AllowPartialCatalogImport ||
                        now < replay.DiscoveryStarted || now - replay.DiscoveryStarted >= DiscoveryCheckpointMaxAge))
                        replay = null;
                    if (replay == null && requestedCategory != null && (needsRebuild && enabled.Length > 1 ||
                        enabled.Any(category => category != requestedCategory &&
                            !listings.ContainsKey(category) &&
                            (!checkpoint.PendingCategories.Contains(category) || category == dirtyAtStart))))
                    {
                        lock (gate)
                        {
                            store.ClearRecovery();
                            status.State = "BaselineRequired";
                            status.Phase = "Select Refresh all enabled categories to build the catalog baseline first";
                        }
                        return RetryOutcome.None;
                    }
                    var categories = enabled
                        .Where(category => requestedCategory == null || category == requestedCategory)
                        .OrderBy(category => category == dirtyAtStart ? 0 : 1)
                        .ToArray();
                    lock (gate) status.CategoriesTotal = categories.Length;
                    if (requestedCategory != null && categories.Length == 0)
                        throw new InvalidOperationException("The selected TuneIn category is no longer enabled.");
                    var channel = categories.Length == 0 ? null
                        : getChannel() ?? throw new InvalidOperationException("TuneIn channel is not registered.");
                    var roots = enabled.Select(category => channel.GetCategoryRoot(snapshot, category)).ToArray();
                    var mayScope = allowScopedImport && compatible && !needsRebuild
                        && !checkpoint.NeedsImport && checkpoint.PendingCategories.Count == 0
                        && (dirtyAtStart == null || checkpoint.DirtyImportWasScoped
                            && (requestedCategory == null || dirtyAtStart == requestedCategory))
                        && checkpoint.Categories.Count == roots.Length
                        && enabled.Select((category, index) => listings.TryGetValue(category, out var saved)
                            && saved.RootId == roots[index].Id).All(matches => matches);
                    var progressive = mayScope && requestedCategory == null && replay == null && roots.Length > 0
                        && checkpoint.CleanedCategories.Count == 0;
                    if (progressive)
                        categories = categories.OrderBy(category => category == dirtyAtStart ? 0 : category == "music" ? 2 : 1)
                            .ToArray();
                    if (!mayScope && checkpoint.DirtyImportWasScoped && requestedCategory == null && activeRecovery.Retries > 0)
                        throw new InvalidOperationException("Cannot widen an interrupted scoped TuneIn import. "
                            + "Complete the interrupted category or explicitly refresh all enabled categories.");
                    var partial = replay?.Partial ?? new HashSet<string>(StringComparer.Ordinal);
                    var limited = new HashSet<string>(StringComparer.Ordinal);
                    var didImport = true;
                    var progressivelyApplied = new HashSet<string>(StringComparer.Ordinal);
                    if (replay != null)
                    {
                        foreach (var listing in replay.Listings) listings[listing.Category] = listing;
                        skippedEntries = replay.SkippedEntries;
                        skippedEntriesOmitted = replay.SkippedEntriesOmitted;
                        discoveryStarted = replay.DiscoveryStarted;
                        Update(() =>
                        {
                            status.CategoriesCompleted = replay.DiscoveryStatus.CategoriesCompleted;
                            status.SkippedBrowseLinks = replay.DiscoveryStatus.SkippedBrowseLinks;
                            status.SkippedBrowseLinkDetails = replay.DiscoveryStatus.SkippedBrowseLinkDetails;
                        });
                    }
                    else
                    {
                        var savedDiscovery = LoadDiscovery(snapshot.DiscoveryKey, requestedCategory, now);
                        List<TuneInCachedPage> reusablePages;
                        lock (gate)
                            reusablePages = savedDiscovery == null && requestedCategory != null
                                && checkpoint.CleanedCategories.Count == 0 && dirtyAtStart == null
                                && !needsRebuild && !checkpoint.NeedsImport && activeRecovery.Retries == 0
                                && recentPagesCategory != null && recentPagesCategory != requestedCategory
                                && recentPagesKey == snapshot.DiscoveryKey
                                ? recentPages.Values.ToList() : null;
                        discoveryStarted = savedDiscovery?.StartedUtc ?? now;
                        nextCheckpoint = now + DiscoveryCheckpointInterval;
                        savedPageCount = savedDiscovery?.Pages.Count ?? 0;
                        if (savedDiscovery != null)
                            Update(() =>
                            {
                                status.Resumed = true;
                                status.DiscoveryCheckpointUtc = savedDiscovery.SavedUtc;
                            });
                        // Admitted pages must stay cached for the entire attempt, including near the resume age limit.
                        session = new TuneInCatalogueSession(savedDiscovery?.Pages, DiscoveryCheckpointMaxAge + discoveryDeadline, clock,
                            hit => Update(() => { if (hit) status.CacheHits++; else status.RequestsStarted++; }),
                            () => Update(() =>
                            {
                                status.RequestsCompleted++;
                                status.CatalogPagesCached = session.PageCount;
                            }),
                            code => Update(() => status.LastHttpStatus = code), retryRateLimit: true,
                            skippedBrowseLink: (category, reason, details) => Update(() =>
                            {
                                status.SkippedBrowseLinks++;
                                var label = TuneInBrowsing.CategoryName(category) + ": " + reason.TrimEnd('.');
                                skipReasons.TryGetValue(label, out var count);
                                skipReasons[label] = count + 1;
                                var displayed = skipReasons.OrderByDescending(pair => pair.Value)
                                    .ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(SkipReasonDisplayLimit).ToArray();
                                status.SkippedBrowseLinkDetails = string.Join("; ", displayed.Select(pair => $"{pair.Value} x {pair.Key}"));
                                var otherCount = status.SkippedBrowseLinks - displayed.Sum(pair => pair.Value);
                                if (otherCount > 0) status.SkippedBrowseLinkDetails += $"; {otherCount} other";
                                var diagnostic = $"Entry {status.SkippedBrowseLinks} ({clock():O}): {label}"
                                    + Environment.NewLine + details;
                                if (skippedEntries.Count < SkippedEntryReportLimit
                                    && skippedEntryCharacters + diagnostic.Length <= SkippedEntryCharacterLimit)
                                {
                                    skippedEntries.Add(diagnostic);
                                    skippedEntryCharacters += diagnostic.Length;
                                }
                                else skippedEntriesOmitted++;
                            }), maxPages: TuneInCatalogueSession.MaxRefreshPages,
                            maxBytes: TuneInCatalogueSession.MaxRefreshBytes, timings: timings,
                            rateLimitRetry: () => Update(() => status.RateLimitRetries++), recentPages: reusablePages);
                        Update(() =>
                        {
                            activeTimings = timings;
                            status.CatalogPagesCached = session.PageCount;
                            status.LastProgressUtc = clock();
                        });

                        foreach (var category in categories)
                            ReadEmbyStationCount(snapshot, channel, category, roots[Array.IndexOf(enabled, category)].Id, linked.Token);
                        var importRequired = categories.Length != 0 && (needsRebuild || dirtyAtStart != null || checkpoint.NeedsImport
                            || categories.Any(checkpoint.CleanedCategories.Contains));
                        var changed = new HashSet<string>(StringComparer.Ordinal);
                        var cannotApply = false;
                        bool IsForwardPage(string parent, string child) => TuneInBrowsing.IsForwardPage(snapshot,
                            directory.Resolve(snapshot, parent), directory.Resolve(snapshot, child));
                        discovering = true;
                        discoveryTime.Start();
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
                                status.ImportProgress = null;
                                status.ImportFoldersTotal = 0;
                            });

                            var root = roots[Array.IndexOf(enabled, category)];
                            var checkpointStations = 0;
                            var staged = await TuneInStagedImport.DiscoverAsync(channel, root, session, linked.Token,
                                (folder, catalogue, strict, token) => getItems(channel, folder, catalogue, strict, token),
                                null,
                                () => directory.CheckCurrent(snapshot, linked.Token), IsForwardPage,
                                stations =>
                                {
                                    Update(() => status.LastProgressUtc = clock());
                                    if (SaveDiscovery(linked.Token, stations - checkpointStations >= 3000))
                                        checkpointStations = stations;
                                }, snapshot.Value.MaxStationCount, snapshot.Value.TraversalDepthLimit,
                                id => directory.Resolve(snapshot, id).StationId).ConfigureAwait(false);
                            directory.CheckCurrent(snapshot, linked.Token);
                            lock (gate)
                                if (status.RequestsCompleted + status.CacheHits == requestsBefore)
                                    throw new InvalidOperationException("TuneIn category discovery returned no successful provider fetch.");

                            listings.TryGetValue(category, out var previous);
                            if (checkpoint.CleanedCategories.Contains(category)) previous = null;
                            else previous = channel.ReadCategorySnapshot(snapshot, category, linked.Token, previous) ?? previous;
                            if (staged.DiscoveryLimitReason != null)
                            {
                                limited.Add(category);
                                logger?.Warn("TuneIn discovery for {0}: {1}. Retaining existing in-scope entries.",
                                    category, staged.DiscoveryLimitReason);
                            }
                            var availableStations = staged.CountStations(id =>
                            {
                                linked.Token.ThrowIfCancellationRequested();
                                return directory.Resolve(snapshot, id).StationId;
                            });
                            Update(() =>
                            {
                                var counts = categoryCounts[category];
                                counts.AvailableStations = availableStations;
                                counts.AvailablePartial = staged.DiscoveryIncomplete || staged.DiscoveryLimitReason != null;
                                counts.AvailableCheckedUtc = clock();
                            });
                            var allowExclusions = snapshot.Value.AllowPartialCatalogImport;
                            if (staged.DiscoveryIncomplete && checkpoint.CleanedCategories.Contains(category))
                            {
                                partial.Add(category);
                                Update(() => categoryStates[category] = "Incomplete (left unchanged)");
                                if (!progressive) cannotApply = true;
                                continue;
                            }
                            if (progressive && staged.DiscoveryIncomplete)
                            {
                                partial.Add(category);
                                Update(() => categoryStates[category] = "Incomplete (left unchanged)");
                                if (checkpoint.DirtyCategory == category) break;
                                continue;
                            }
                            var categorySnapshot = staged.MergeRetainedCategory(category, previous,
                                id => directory.MembershipKey(snapshot, id),
                                allowExclusions || category == "music" && !importRequired);
                            if (categorySnapshot == null)
                            {
                                Update(() => categoryStates[category] = "Incomplete (catalog links skipped)");
                                cannotApply = true;
                                continue;
                            }
                            lock (gate) categorySnapshot.StationCounts = categoryCounts[category].Copy();

                            if (snapshot.Value.EnableCatalogIdentityDiagnostics && logger != null)
                            {
                                var identities = TuneInIdentityDiagnostics.Compare(previous, categorySnapshot,
                                    id => directory.Resolve(snapshot, id).StationId, linked.Token);
                                logger.Info("TuneIn catalog identity comparison for {0}: {1}; saved discovery partial={2}; "
                                    + "fresh discovery partial={3}; prior import interrupted={4}. "
                                    + "Saved/fresh catalogs only, not Emby database deletions; change counters may overlap.",
                                    category, identities, previous?.DiscoveryPartial == true,
                                    categorySnapshot.DiscoveryPartial, dirtyAtStart == category);
                            }

                            if (previous == null || previous.Fingerprint != categorySnapshot.Fingerprint
                                || previous.DiscoveryPartial != categorySnapshot.DiscoveryPartial
                                || previous.DiscoveryLimitReason != categorySnapshot.DiscoveryLimitReason)
                                changed.Add(category);
                            listings[category] = categorySnapshot;
                            if (categorySnapshot.DiscoveryPartial)
                            {
                                partial.Add(category);
                                Update(() => categoryStates[category] = "Incomplete (catalog links skipped)");
                                continue;
                            }
                            completed.Add(category);
                            Update(() =>
                            {
                                status.CategoriesCompleted = completed.Count;
                                status.Progress = 99d * completed.Count / categories.Length;
                            });
                            if (progressive && (changed.Contains(category) || checkpoint.DirtyCategory == category
                                || checkpoint.CleanedCategories.Contains(category)))
                            {
                                discoveryTime.Stop();
                                await ApplyImport(categorySnapshot.RootId, progressiveCategory: category).ConfigureAwait(false);
                                progressivelyApplied.Add(category);
                                lock (gate)
                                {
                                    activeRecovery.Phase = "discovery";
                                    store.WriteRecovery(activeRecovery);
                                }
                                var remaining = discoveryDeadline - discoveryTime.Elapsed;
                                if (remaining <= TimeSpan.Zero) throw new TimeoutException("TuneIn discovery deadline reached.");
                                timeout.CancelAfter(remaining);
                                discoveryTime.Start();
                                discovering = true;
                            }
                        }

                        directory.CheckCurrent(snapshot, linked.Token);
                        discoveryTime.Stop();
                        if (cannotApply)
                        {
                            directory.CheckCurrent(snapshot, linked.Token);
                            store.ClearDiscovery();
                            store.ClearRecovery();
                            Update(() =>
                            {
                                foreach (var category in completed) categoryStates[category] = "Checked (not applied)";
                                status.State = "Partial";
                                status.Phase = "Discovery incomplete; existing library left unchanged";
                                status.Category = requestedCategory;
                            });
                            return RetryOutcome.None;
                        }

                        didImport = !progressive && (importRequired || changed.Count != 0);
                    }

                    async Task ApplyImport(string restriction, string progressiveCategory = null)
                    {
                        Update(() => status.Phase = "Preparing import");
                        // Only newly enabled, never-imported categories may wait outside the complete saved tree.
                        var importedCategories = enabled.Where(listings.ContainsKey).ToArray();
                        if (restriction == null && checkpoint.CleanedCategories.Any(category =>
                            enabled.Contains(category) && !completed.Contains(category)
                            && !(replay != null && categories.Contains(category) && !partial.Contains(category))))
                            throw new InvalidOperationException("A cleaned TuneIn category requires a complete refresh before a channel-wide import.");
                        var importedRoots = roots.Where(root => importedCategories.Any(category => listings[category].RootId == root.Id)).ToArray();
                        var importedListings = importedCategories.Select(category => listings[category]).ToArray();
                        var appliedCategories = restriction == null ? importedCategories : new[] { progressiveCategory ?? requestedCategory };
                        var appliedRoots = restriction == null ? importedRoots : importedRoots.Where(root => root.Id == restriction);
                        currentCategory = progressiveCategory ?? requestedCategory ?? dirtyAtStart ?? categories.First();
                        directory.CheckCurrent(snapshot, linked.Token);
                        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                        linked.Token.ThrowIfCancellationRequested();
                        discovering = false;
                        using (var importDeadline = new CancellationTokenSource())
                        using (var importCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverToken, importDeadline.Token))
                        using (importDeadline.Token.Register(() =>
                        {
                            int served = 0, total = 0;
                            TuneInImportProgress progress = null;
                            Update(() =>
                            {
                                status.Activity = ImportDeadlineActivity;
                                served = status.ImportFoldersServed;
                                total = status.ImportFoldersTotal;
                                progress = status.ImportProgress;
                            });
                            logger?.Warn("TuneIn import for {0}: deadline reached ({1}/{2} folders served); waiting for active work to stop. No automatic timeout retry.",
                                requestedCategory ?? "all", served, total);
                            if (snapshot.Value.EnableCatalogIdentityDiagnostics)
                                foreach (var line in TuneInImportStallDiagnostics.DescribePending(progress))
                                    logger?.Warn("{0}", line);
                        }))
                        {
                            var importToken = importCancellation.Token;
                            importDeadline.CancelAfter(timeoutOverride ?? TimeSpan.FromHours(snapshot.Value.ImportDeadlineHours));
                            try
                            {
                                directory.CheckCurrent(snapshot, importToken);
                                Update(() =>
                                {
                                    status.Category = currentCategory;
                                    status.Phase = "Preparing import";
                                    status.Activity = replay == null ? null : "Reusing staged catalog without discovery";
                                    status.ImportFoldersTotal = 0;
                                    status.ImportFoldersServed = 0;
                                    status.ImportProgress = null;
                                    status.LastProgressUtc = clock();
                                });
                                var staged = TuneInStagedImport.ForChannel(importedRoots, importedListings, importToken,
                                    (total, served) => Update(() =>
                                    {
                                        status.ImportFoldersTotal = total;
                                        status.ImportFoldersServed = served;
                                        status.LastProgressUtc = clock();
                                    }), () => directory.CheckCurrent(snapshot, importToken), restriction,
                                    progress => Update(() => status.ImportProgress = progress),
                                    snapshot.Value.EnableCatalogIdentityDiagnostics);
                                directory.CheckCurrent(snapshot, importToken);
                                checkpoint.DirtyCategory = checkpoint.DirtyCategory ?? currentCategory;
                                checkpoint.DirtyImportWasScoped = restriction != null;
                                Persist(snapshot, checkpoint, started);
                                if (progressiveCategory == null) store.ClearDiscovery();
                                pendingImport = progressiveCategory != null ? null : new PendingImport
                                {
                                    DiscoveryKey = snapshot.DiscoveryKey,
                                    RequestedCategory = requestedCategory,
                                    AllowPartial = snapshot.Value.AllowPartialCatalogImport,
                                    DiscoveryStarted = discoveryStarted,
                                    Listings = importedListings,
                                    Partial = partial,
                                    DiscoveryStatus = status.Copy(),
                                    SkippedEntries = skippedEntries,
                                    SkippedEntriesOmitted = skippedEntriesOmitted
                                };
                                lock (gate)
                                {
                                    importToken.ThrowIfCancellationRequested();
                                    activeRecovery.Phase = "import";
                                    store.WriteRecovery(activeRecovery);
                                }
                                Update(() =>
                                {
                                    status.Phase = "Importing";
                                    status.ImportFoldersTotal = staged.Count;
                                    if (restriction != null) status.Activity = "Importing only " + TuneInBrowsing.CategoryName(appliedCategories[0]);
                                });
                                logger?.Info("TuneIn import for {0}: starting Emby traversal ({1} folders, refresh level {2}).",
                                    currentCategory ?? "all", staged.Count, staged.ImportRefreshLevel);
                                importTime.Start();
                                try
                                {
                                    var importTask = import(channel, staged, restriction, importToken);
                                    await AwaitImportWithDiagnostics(importTask, importToken).ConfigureAwait(false);
                                }
                                finally
                                {
                                    importTime.Stop();
                                    if (snapshot.Value.EnableCatalogIdentityDiagnostics && importToken.IsCancellationRequested)
                                        foreach (var line in TuneInImportStallDiagnostics.DescribePending(Status().ImportProgress))
                                            logger?.Warn("{0}", line);
                                }
                                staged.Verify();
                                importToken.ThrowIfCancellationRequested();
                                foreach (var root in appliedRoots)
                                    updateArtwork?.Invoke(channel, root.Id, importToken);
                                foreach (var category in appliedCategories)
                                {
                                    ReadEmbyStationCount(snapshot, channel, category, listings[category].RootId, importToken);
                                    lock (gate) listings[category].StationCounts = categoryCounts[category].Copy();
                                }
                                directory.CheckCurrent(snapshot, importToken);
                                importToken.ThrowIfCancellationRequested();
                            }
                            catch (Exception error)
                            {
                                importDeadlineReached = importDeadline.IsCancellationRequested;
                                importTimedOut = error is TimeoutException
                                    || importDeadlineReached && error is OperationCanceledException;
                                if (importTimedOut && !serverToken.IsCancellationRequested)
                                    Update(() => status.ImportTimeouts++);
                                throw;
                            }
                        }
                        directory.CheckCurrent(snapshot, linked.Token);
                        checkpoint.Categories = importedCategories.Select(category => listings[category]).ToList();
                        foreach (var category in appliedCategories)
                            foreach (var item in listings[category].Folders.SelectMany(folder => folder.Items))
                                item.ForceUpdate = false;
                        checkpoint.PendingCategories.RemoveAll(listings.ContainsKey);
                        checkpoint.NeedsImport = false;
                        checkpoint.DirtyCategory = null;
                        checkpoint.DirtyImportWasScoped = false;
                        checkpoint.CleanedCategories.RemoveAll(appliedCategories.Contains);
                        checkpoint.ChannelImportVersion = ChannelImportVersion;
                        Persist(snapshot, checkpoint, started, BuildCommitted(checkpoint, snapshot.Key));
                        pendingImport = null;
                        lock (gate)
                            foreach (var category in appliedCategories)
                            {
                                categoryStates[category] = listings[category].DiscoveryPartial ? "Partial (applied)"
                                    : listings[category].DiscoveryLimitReason != null ? "Limited (imported)" : "Imported";
                                categoryLimitReasons[category] = listings[category].DiscoveryLimitReason;
                            }
                    }

                    if (didImport)
                    {
                        var restriction = mayScope && requestedCategory != null && !listings[requestedCategory].DiscoveryPartial
                            && !checkpoint.CleanedCategories.Contains(requestedCategory)
                            ? listings[requestedCategory].RootId : null;
                        if (restriction == null && checkpoint.DirtyImportWasScoped && requestedCategory == dirtyAtStart)
                            throw new InvalidOperationException("Cannot safely retry the interrupted scoped TuneIn import. "
                                + "Complete the interrupted category or explicitly refresh all enabled categories.");
                        await ApplyImport(restriction).ConfigureAwait(false);
                    }
                    else
                    {
                        discovering = false;
                        store.ClearDiscovery();
                        Update(() => status.Phase = "Finishing refresh");
                        foreach (var category in categories.Where(category => !progressive
                            || completed.Contains(category) && !progressivelyApplied.Contains(category)))
                        {
                            updateArtwork?.Invoke(channel, roots[Array.IndexOf(enabled, category)].Id, linked.Token);
                            lock (gate) listings[category].StationCounts = categoryCounts[category].Copy();
                        }
                        checkpoint.Categories = enabled.Where(listings.ContainsKey).Select(category => listings[category]).ToList();
                        Persist(snapshot, checkpoint, started);
                    }
                    lock (gate)
                    {
                        if (!didImport)
                            foreach (var category in categories.Where(category => !progressive
                                || completed.Contains(category) && !progressivelyApplied.Contains(category)))
                            {
                                categoryStates[category] = partial.Contains(category) ? "Partial (unchanged)"
                                    : listings[category].DiscoveryLimitReason != null ? "Limited (unchanged)" : "Unchanged";
                                categoryLimitReasons[category] = listings[category].DiscoveryLimitReason;
                            }
                        status.CategoriesSkipped = didImport ? 0 : completed.Count - progressivelyApplied.Count;
                    }

                    linked.Token.ThrowIfCancellationRequested();
                    directory.CheckCurrent(snapshot, linked.Token);
                    lock (gate)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        store.ClearRecovery();
                        foreach (var category in categories)
                            if (listings.TryGetValue(category, out var listing) && listing.DiscoveryLimitReason != null)
                                limited.Add(category);
                        status.State = partial.Count != 0 ? "Partial" : limited.Count != 0 ? "Limited" : "Completed";
                        status.Phase = partial.Count != 0
                            ? (progressive ? "Partial refresh completed; incomplete categories left unchanged" :
                                didImport ? "Partial refresh completed; valid updates applied, rejected entries excluded"
                                : "Partial refresh completed; merged library unchanged")
                            : limited.Count != 0 ? (didImport || progressivelyApplied.Count != 0
                                ? "Limited catalog imported; existing in-scope entries retained"
                                : "Limited discovery completed; compatible catalog retained")
                            : (categories.Length == 0 ? "No enabled categories" : "Completed");
                        if (partial.Count != 0) status.Category = requestedCategory ?? partial.OrderBy(category => category, StringComparer.Ordinal).First();
                        status.Progress = 100;
                        if (partial.Count == 0 && session != null && requestedCategory != null)
                        {
                            if (recentPagesKey != snapshot.DiscoveryKey || recentPagesCategory != requestedCategory)
                                recentPages.Clear();
                            recentPagesKey = snapshot.DiscoveryKey;
                            recentPagesCategory = requestedCategory;
                            foreach (var page in session.SnapshotFetchedPages())
                                recentPages[page.Key] = page;
                            var bytes = 0L;
                            var keep = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var page in recentPages.Values.OrderByDescending(page => page.SavedUtc))
                                if (page.SavedUtc <= clock() && clock() - page.SavedUtc < TimeSpan.FromMinutes(10)
                                    && keep.Count < TuneInCatalogueSession.MaxPages
                                    && bytes + page.Bytes.Length <= TuneInCatalogueSession.MaxBytes)
                                {
                                    keep.Add(page.Key);
                                    bytes += page.Bytes.Length;
                                }
                            foreach (var key in recentPages.Keys.Where(key => !keep.Contains(key)).ToArray())
                                recentPages.Remove(key);
                        }
                        else
                        {
                            recentPages.Clear();
                            recentPagesKey = null;
                            recentPagesCategory = null;
                        }
                    }
                }
                catch (Exception error)
                {
                    lock (gate) stoppedPhase = status.Phase;
                    var stalled = error is TuneInCatalogTimeoutException;
                    var cancelled = serverToken.IsCancellationRequested || directory.Snapshot().Key != snapshot.Key;
                    timedOut = (timeout.IsCancellationRequested || stalled || importTimedOut || error is TimeoutException) && !cancelled;
                    discoveryTimedOut = timedOut && discovering;
                    var interrupted = !cancelled && !timedOut && !importDeadlineReached
                        && (stoppedPhase == "Discovering" || stoppedPhase == "Importing") && IsInterruption(error);
                    if (!interrupted && error is OperationCanceledException) cancelled = true;
                    retryInterrupted = interrupted && activeRecovery.Retries < InterruptionRetryLimit;
                    if (interrupted) activeRecovery.Phase = discovering ? "discovery" : "import";
                    discoveryTime.Stop();
                    if (discoveryTimedOut || discovering && interrupted)
                    {
                        // The attempt token has expired; still honor user cancellation and scope changes.
                        try { SaveDiscovery(serverToken, force: true); }
                        catch (OperationCanceledException)
                        {
                            discoveryTimedOut = timedOut = retryInterrupted = false;
                            cancelled = true;
                            error = new OperationCanceledException();
                        }
                        catch (Exception saveError) when (saveError is IOException || saveError is UnauthorizedAccessException
                            || saveError is InvalidOperationException || saveError is InvalidDataException)
                        {
                            logger?.Warn("TuneIn could not save additional pages after discovery stopped ({0}); earlier checkpoints are unaffected.",
                                saveError.GetType().Name);
                        }
                        if (stalled && timedOut)
                            logger?.Warn("TuneIn catalog response deadline reached during {0}.", currentCategory ?? requestedCategory ?? "discovery");
                    }
                    lock (gate)
                    {
                        if (!retryInterrupted && (!disposed || timedOut || !cancelled) && !discoveryTimedOut)
                            store.ClearRecovery();
                        status.State = timedOut ? "TimedOut" : cancelled ? "Cancelled" : "Error";
                        status.Activity = stalled && timedOut ? "TuneIn catalog response deadline reached" : null;
                        if (discoveryTimedOut) status.DiscoveryTimeouts++;
                        failure = status.State == "Error" ? error : null;
                        if (currentCategory != null && categoryStateKey == snapshot.Key)
                            categoryStates[currentCategory] = timedOut ? "Timed out" : cancelled ? "Cancelled" : "Failed";
                        status.ErrorCode = timedOut || cancelled ? status.State : error.GetType().Name;
                        status.Phase = discoveryTimedOut
                            ? (status.DiscoveryCheckpointUtc.HasValue
                                ? "Discovery timed out; checkpoint saved"
                                : "Discovery timed out before a checkpoint was saved")
                            : timedOut ? (importTimedOut
                                ? "Import timed out; not retried automatically; refresh manually to reconcile"
                                : $"Timed out during {stoppedPhase}; not retried automatically")
                            : cancelled ? "Stopped and drained" : status.Phase;
                        if (interrupted && !retryInterrupted && !cancelled)
                            status.Phase += "; automatic interruption retry limit reached";
                        if (!cancelled && error is TuneInProviderResponseException provider && provider.StatusCode == 429)
                            status.Phase += " (TuneIn rate limit reached; wait before retrying or increase the API request interval)";
                    }
                    logger?.Error("TuneIn refresh stopped in {0}/{1} ({2}).",
                        status.Category ?? "preparation", status.Phase, status.ErrorCode);
                }
                finally
                {
                    if (session != null)
                    {
                        var wait = timings.PacerQueue + timings.Pacing + timings.Backoff;
                        var fetchMs = Math.Max(0, (timings.Fetch - wait).TotalMilliseconds);
                        logger?.Info("TuneIn refresh attempt for {0}: discovery {1:F0} ms wall time; summed request timings: HTTP fetch and parse excluding waits {2:F0} ms; "
                            + "request queue {3:F0} ms; pacing {4:F0} ms; provider backoff {5:F0} ms; cached page parsing {6:F0} ms; "
                            + "Emby import {7:F0} ms; effective catalog interval {8} s min / {9} s max; {10} requests started, {11} cache hits.",
                            requestedCategory ?? "all", discoveryTime.Elapsed.TotalMilliseconds, fetchMs,
                            timings.PacerQueue.TotalMilliseconds, timings.Pacing.TotalMilliseconds, timings.Backoff.TotalMilliseconds,
                            timings.CacheReplay.TotalMilliseconds, importTime.Elapsed.TotalMilliseconds,
                            timings.MinIntervalSeconds?.ToString() ?? "n/a", timings.MaxIntervalSeconds?.ToString() ?? "n/a",
                            status.RequestsStarted, status.CacheHits);
                    }
                    if (status.SkippedBrowseLinks > 0)
                        logger?.Warn("TuneIn refresh skipped {0} malformed catalog link(s). By reason: {1}",
                            status.SkippedBrowseLinks, status.SkippedBrowseLinkDetails);
                    lock (gate)
                    {
                        if (status.State != "Completed")
                        {
                            recentPages.Clear();
                            recentPagesKey = null;
                            recentPagesCategory = null;
                        }
                        if (serverToken.IsCancellationRequested || status.State == "Cancelled") pendingImport = null;
                        status.FinishedUtc = clock();
                        if (status.State == "Running")
                        {
                            status.State = "Completed";
                            status.Phase = "Completed";
                            status.Progress = 100;
                        }
                        failureLog = failure != null || timedOut || status.ImportTimeouts > 0
                            || activeRecovery.Retries > 0 || status.SkippedBrowseLinks > 0
                            ? CreateFailureLog(failure, status, skippedEntries, skippedEntriesOmitted, stoppedPhase) : null;
                    }
                }
            }
            return retryInterrupted ? RetryOutcome.Interruption : RetryOutcome.None;
        }

        private static bool IsInterruption(Exception error) =>
            error is OperationCanceledException
            || error is IOException && !(error is InvalidDataException)
                && (!(error is TuneInProviderResponseException response)
                    || response.StatusCode >= 500 && response.StatusCode != 504);

        private TuneInDiscoveryCheckpoint LoadDiscovery(string configurationKey, string requestedCategory, DateTime now)
        {
            try { return store.ReadDiscovery(configurationKey, requestedCategory, now, DiscoveryCheckpointMaxAge); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is InvalidOperationException || error is InvalidDataException || error is XmlException)
            {
                logger?.Warn("TuneIn discovery checkpoint could not be read; starting discovery again ({0}).", error.GetType().Name);
                return null;
            }
        }

        private static TuneInRefreshFailureLog CreateFailureLog(Exception error, TuneInRefreshStatus failedStatus,
            IEnumerable<string> skippedEntries = null, int skippedEntriesOmitted = 0, string stoppedPhase = null)
        {
            var lines = new List<string>
            {
                error == null ? "TuneIn Catalog refresh diagnostics." : "TuneIn Catalog refresh failed.",
                "Started (UTC): " + failedStatus.StartedUtc?.ToString("O"),
                "Finished (UTC): " + failedStatus.FinishedUtc?.ToString("O"),
                "State: " + failedStatus.State,
                "Category: " + (failedStatus.Category ?? "preparation"),
                "Phase: " + (failedStatus.Phase ?? "unknown"),
                "Discovery timeouts: " + failedStatus.DiscoveryTimeouts + " (no automatic timeout retries)",
                "Import timeouts: " + failedStatus.ImportTimeouts + " (no automatic timeout retries)",
                "Automatic interruption retries: " + (failedStatus.AutomaticRetryReason == "interruption" ? failedStatus.AutomaticRetry ?? 0 : 0)
                    + " (limit: " + InterruptionRetryLimit + ")",
                "Discovery checkpoint (UTC): " + (failedStatus.DiscoveryCheckpointUtc?.ToString("O") ?? "none"),
                "Import may have partially applied: " + failedStatus.ImportMayHavePartiallyApplied,
                "Import with exclusions allowed: " + failedStatus.PartialImportAllowed
            };
            if (stoppedPhase != null) lines.Add("Stopped during: " + stoppedPhase);
            if (failedStatus.LastHttpStatus.HasValue)
                lines.Add("TuneIn HTTP status: " + failedStatus.LastHttpStatus.Value);
            lines.AddRange(TuneInImportStallDiagnostics.DescribePending(failedStatus.ImportProgress));
            if (failedStatus.SkippedBrowseLinks != 0)
                lines.Add("Skipped malformed catalog links: " + failedStatus.SkippedBrowseLinks
                    + ". By reason: " + failedStatus.SkippedBrowseLinkDetails);
            if (skippedEntries != null)
            {
                lines.Add("Skipped-entry details (non-navigation query parameters omitted; review before sharing):");
                lines.AddRange(skippedEntries);
            }
            if (skippedEntriesOmitted > 0)
                lines.Add("Additional skipped-entry details omitted: " + skippedEntriesOmitted);
            if (error != null) lines.Add("Error: " + error);
            var report = string.Join(Environment.NewLine, lines);
            var truncated = report.Length > FailureReportLimit || skippedEntriesOmitted > 0
                || failedStatus.ImportProgress?.PendingFoldersOmitted > 0;
            if (report.Length > FailureReportLimit) report = report.Substring(0, FailureReportLimit);
            return new TuneInRefreshFailureLog
            {
                Text = report,
                Truncated = truncated,
                FinishedUtc = failedStatus.FinishedUtc
            };
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

        private async Task AwaitImportWithDiagnostics(Task importTask, CancellationToken token)
        {
            var diagnostics = new TuneInImportStallDiagnostics();
            try
            {
                while (!importTask.IsCompleted)
                {
                    using (var tick = new CancellationTokenSource())
                    {
                        var delay = Task.Delay(TuneInImportStallDiagnostics.Interval, tick.Token);
                        if (await Task.WhenAny(importTask, delay).ConfigureAwait(false) == importTask)
                        {
                            tick.Cancel();
                            break;
                        }
                    }

                    var current = Status();
                    var message = diagnostics.TryCreate(clock(), current.LastProgressUtc,
                        current.ImportFoldersTotal, current.ImportFoldersServed, token.IsCancellationRequested);
                    if (message != null)
                    {
                        logger?.Warn(message);
                        foreach (var line in TuneInImportStallDiagnostics.DescribePending(current.ImportProgress))
                            logger?.Warn("{0}", line);
                    }
                    if (diagnostics.LimitReached) break;
                }
            }
            // Diagnostic polling must never let a new attempt start before this import drains.
            finally { await importTask.ConfigureAwait(false); }
        }

        public void Dispose()
        {
            Task pending;
            Task pendingCleanup;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (work != null && !work.IsCompleted) cancellation.Cancel();
                cleanupCancellation?.Cancel();
                pending = work;
                pendingCleanup = cleanupWork;
            }
            if (pending != null)
            {
                try { pending.GetAwaiter().GetResult(); }
                catch (Exception error) { logger?.Error("TuneIn refresh drain failed ({0}).", error.GetType().Name); }
            }
            pendingCleanup?.GetAwaiter().GetResult();
            lock (gate)
            {
                cancellation?.Dispose();
                cancellation = null;
                cleanupCancellation?.Dispose();
            }
        }
    }
}
