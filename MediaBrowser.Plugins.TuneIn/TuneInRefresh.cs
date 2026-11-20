using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
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
        public bool ImportMayHavePartiallyApplied { get; set; }
        public bool Resumed { get; set; }
        public int DiscoveryTimeouts { get; set; }
        public int ImportTimeouts { get; set; }
        public int? AutomaticRetry { get; set; }
        public int? AutomaticRetryLimit { get; set; }
        public string AutomaticRetryPhase { get; set; }
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
        public DateTime? LastProgressUtc { get; set; }
        public string Activity { get; set; }
        internal TuneInRefreshStatus Copy() => (TuneInRefreshStatus)MemberwiseClone();
    }

    public sealed class TuneInCategoryRefreshStatus
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
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
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(1);
        private static readonly TimeSpan DiscoveryCheckpointInterval = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan DiscoveryCheckpointMaxAge = TimeSpan.FromHours(48);
        private const int DiscoveryResumeLimit = 10;
        private const int ImportRetryLimit = 3;
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
        private PendingImport pendingImport;
        private readonly Dictionary<string, (int IntervalHours, DateTime DueUtc)> scheduled =
            new Dictionary<string, (int, DateTime)>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> categoryStates = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TuneInStationCounts> categoryCounts = new Dictionary<string, TuneInStationCounts>(StringComparer.Ordinal);
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
            Func<TuneInChannel, string, CancellationToken, int?> countStations = null)
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
                copy.ActiveRequests = activeTimings == null ? 0 : Volatile.Read(ref activeTimings.ActiveResponses);
                if (copy.Busy && copy.Activity == null && copy.Phase == "Discovering")
                    copy.Activity = activeTimings?.DescribeWait();
                if (copy.Busy && copy.State == "Running" && !copy.Cancelling
                    && (copy.DiscoveryTimeouts > 0 || copy.ImportTimeouts > 0))
                {
                    var importing = copy.ImportTimeouts > 0;
                    copy.AutomaticRetry = importing ? copy.ImportTimeouts : copy.DiscoveryTimeouts;
                    copy.AutomaticRetryLimit = importing ? ImportRetryLimit : DiscoveryResumeLimit;
                    copy.AutomaticRetryPhase = importing ? "import" : "discovery";
                }
                copy.QueuedRequests = queue.Select(category => category ?? "all").ToArray();
                copy.QueueSlotsAvailable = QueueCapacity - queue.Count;
                copy.Categories = snapshot.Categories.Select(category => new TuneInCategoryRefreshStatus
                {
                    Category = category,
                    Name = TuneInBrowsing.CategoryName(category),
                    State = copy.Busy && copy.State == "Running" && copy.Category == category ? copy.Phase :
                        categoryStates.TryGetValue(category, out var state) ? state : "No recorded refresh",
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
                Begin(snapshot, category);
                return Status();
            }
        }

        private void Begin(ConfigurationSnapshot snapshot, string category)
        {
            Prepare(category);
            work = Task.Run(async () =>
            {
                var next = category;
                while (true)
                {
                    try
                    {
                        for (var attempt = 0; ; attempt++)
                        {
                            if (!await ExecuteAsync(snapshot, next, cancellation.Token).ConfigureAwait(false)
                                || attempt >= DiscoveryResumeLimit) break;
                            lock (gate)
                            {
                                if (cancellation.IsCancellationRequested || disposed) break;
                                status = new TuneInRefreshStatus
                                {
                                    State = "Running", Phase = "Preparing", Category = next,
                                    StartedUtc = status.StartedUtc, DiscoveryTimeouts = status.DiscoveryTimeouts
                                };
                            }
                            logger?.Info("TuneIn refresh for {0}: starting automatic retry {1} of {2} after discovery timed out.",
                                next ?? "all", attempt + 1, DiscoveryResumeLimit);
                        }
                    }
                    catch (Exception error)
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
                    lock (gate)
                    {
                        if (disposed || cancellation.IsCancellationRequested || queue.Count == 0)
                        {
                            queue.Clear();
                            work = null;
                            return;
                        }
                        next = queue.Dequeue();
                        Prepare(next);
                    }
                }
            });
        }

        private void Prepare(string category)
        {
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            activeRequest = category;
            recoveryId = Guid.NewGuid().ToString("N");
            status = new TuneInRefreshStatus { State = "Running", Phase = "Preparing", Category = category, StartedUtc = clock() };
            failureLog = null;
        }

        internal TuneInRefreshStatus Cancel()
        {
            lock (gate)
            {
                if (work == null || work.IsCompleted) return Status();
                queue.Clear();
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
                var previous = directory.Snapshot();
                var checkpoint = store.Read();
                update();
                var current = directory.Snapshot();
                statusConfiguration = current;
                pendingImport = null;
                if (current.Key == previous.Key) return;
                if (checkpoint?.ConfigurationKey == previous.Key)
                {
                    var retained = checkpoint.Categories.Where(category => current.Enabled(category.Category)
                        && previous.CategoryScopeKey(category.Category) == current.CategoryScopeKey(category.Category)).ToList();
                    foreach (var category in retained)
                    {
                        // IDs include the whole configuration key; authenticate and re-sign unchanged scopes only.
                        var sign = directory.CategoryRebinder(previous, current, category.Category);
                        var rebound = new Dictionary<string, string>(StringComparer.Ordinal);
                        string Rebind(string id)
                        {
                            if (!rebound.TryGetValue(id, out var updated))
                                rebound[id] = updated = sign(id);
                            return updated;
                        }
                        category.RootId = Rebind(category.RootId);
                        foreach (var folder in category.Folders)
                        {
                            folder.Id = Rebind(folder.Id);
                            foreach (var item in folder.Items) item.Id = Rebind(item.Id);
                        }
                        category.Fingerprint = TuneInStagedImport.Fingerprint(category.Folders);
                    }
                    checkpoint.Categories = retained;
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
                if (disposed) return;
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
                Begin(snapshot, due.Key);
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
            dirtyCategory = null;
            categoryStateKey = snapshot.Key;
            try
            {
                var checkpoint = store.Read();
                if (checkpoint?.ConfigurationKey != snapshot.Key) return;
                dirtyCategory = checkpoint.DirtyCategory;
                foreach (var category in checkpoint.Categories)
                {
                    categoryStates[category.Category] = category.DiscoveryPartial ? "Partial (applied)" : "Imported";
                    if (category.StationCounts != null)
                        categoryCounts[category.Category] = category.StationCounts.Copy();
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
                foreach (var folder in category.Folders)
                {
                    folders[folder.Id] = TuneInStagedImport.RestoreFolder(folder);
                }
            }
            return new CommittedCatalogue(key, folders);
        }

        private async Task<bool> ExecuteAsync(ConfigurationSnapshot snapshot, string requestedCategory, CancellationToken serverToken)
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
            string stoppedPhase = null;
            Exception failure = null;
            var timings = new TuneInRefreshTimings();
            var discoveryTime = new Stopwatch();
            var importTime = new Stopwatch();
            DateTime discoveryStarted = default, nextCheckpoint = default;
            var savedPageCount = 0;

            void SaveDiscovery(CancellationToken token, bool dueToTimeout = false)
            {
                var savedAt = clock();
                if (session == null || (!dueToTimeout && savedAt < nextCheckpoint)
                    || savedAt < discoveryStarted || savedAt - discoveryStarted >= DiscoveryCheckpointMaxAge) return;
                var pages = session.SnapshotPages();
                if (pages.Count <= savedPageCount) return;
                directory.CheckCurrent(snapshot, token);
                store.WriteDiscovery(new TuneInDiscoveryCheckpoint
                {
                    ConfigurationKey = snapshot.DiscoveryKey, RequestedCategory = requestedCategory,
                    StartedUtc = discoveryStarted, SavedUtc = savedAt, Pages = pages
                });
                savedPageCount = pages.Count;
                nextCheckpoint = savedAt + DiscoveryCheckpointInterval;
                Update(() => status.DiscoveryCheckpointUtc = savedAt);
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
                            status.State = "BaselineRequired";
                            status.Phase = "Select Refresh all enabled categories to build the catalog baseline first";
                        }
                        return false;
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
                    var partial = replay?.Partial ?? new HashSet<string>(StringComparer.Ordinal);
                    var didImport = true;
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
                        session = new TuneInCatalogueSession(savedDiscovery?.Pages, DiscoveryCheckpointMaxAge + DefaultTimeout, clock,
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
                            rateLimitRetry: () => Update(() => status.RateLimitRetries++));
                        Update(() =>
                        {
                            activeTimings = timings;
                            status.CatalogPagesCached = session.PageCount;
                            status.LastProgressUtc = clock();
                        });

                        foreach (var category in categories)
                            ReadEmbyStationCount(snapshot, channel, category, roots[Array.IndexOf(enabled, category)].Id, linked.Token);
                        var importRequired = categories.Length != 0 && (needsRebuild || dirtyAtStart != null || checkpoint.NeedsImport);
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
                                status.ImportFoldersTotal = 0;
                            });

                            var root = roots[Array.IndexOf(enabled, category)];
                            var staged = await TuneInStagedImport.DiscoverAsync(channel, root, session, linked.Token,
                                (folder, catalogue, strict, token) => getItems(channel, folder, catalogue, strict, token),
                                null,
                                () => directory.CheckCurrent(snapshot, linked.Token), IsForwardPage,
                                () =>
                                {
                                    Update(() => status.LastProgressUtc = clock());
                                    SaveDiscovery(linked.Token);
                                }).ConfigureAwait(false);
                            directory.CheckCurrent(snapshot, linked.Token);
                            lock (gate)
                                if (status.RequestsCompleted + status.CacheHits == requestsBefore)
                                    throw new InvalidOperationException("TuneIn category discovery returned no successful provider fetch.");

                            listings.TryGetValue(category, out var previous);
                            var availableStations = staged.CountStations(id =>
                            {
                                linked.Token.ThrowIfCancellationRequested();
                                return directory.Resolve(snapshot, id).StationId;
                            });
                            Update(() =>
                            {
                                var counts = categoryCounts[category];
                                counts.AvailableStations = availableStations;
                                counts.AvailablePartial = staged.DiscoveryIncomplete;
                                counts.AvailableCheckedUtc = clock();
                            });
                            var allowExclusions = snapshot.Value.AllowPartialCatalogImport;
                            var categorySnapshot = staged.DiscoveryIncomplete
                                ? (allowExclusions || category == "music" && !importRequired
                                    ? staged.MergePartialCategory(category, previous,
                                        id => directory.Resolve(snapshot, id).StationId, IsForwardPage, allowExclusions) : null)
                                : staged.CreateSnapshot(category);
                            if (categorySnapshot == null)
                            {
                                Update(() => categoryStates[category] = "Incomplete (catalog links skipped)");
                                cannotApply = true;
                                continue;
                            }
                            lock (gate) categorySnapshot.StationCounts = categoryCounts[category].Copy();

                            if (previous == null || previous.Fingerprint != categorySnapshot.Fingerprint
                                || previous.DiscoveryPartial != categorySnapshot.DiscoveryPartial)
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
                        }

                        directory.CheckCurrent(snapshot, linked.Token);
                        discoveryTime.Stop();
                        if (cannotApply)
                        {
                            directory.CheckCurrent(snapshot, linked.Token);
                            store.ClearDiscovery();
                            Update(() =>
                            {
                                foreach (var category in completed) categoryStates[category] = "Checked (not applied)";
                                status.State = "Partial";
                                status.Phase = "Discovery incomplete; existing library left unchanged";
                                status.Category = requestedCategory;
                            });
                            return false;
                        }

                        didImport = importRequired || changed.Count != 0;
                    }
                    if (didImport)
                    {
                        Update(() => status.Phase = "Preparing import");
                        // Only newly enabled, never-imported categories may wait outside the complete saved tree.
                        var importedCategories = enabled.Where(listings.ContainsKey).ToArray();
                        var importedRoots = roots.Where(root => importedCategories.Any(category => listings[category].RootId == root.Id)).ToArray();
                        var importedListings = importedCategories.Select(category => listings[category]).ToArray();
                        currentCategory = requestedCategory ?? dirtyAtStart ?? categories.First();
                        directory.CheckCurrent(snapshot, linked.Token);
                        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                        linked.Token.ThrowIfCancellationRequested();
                        discovering = false;
                        var importPrepared = false;
                        for (var attempt = 0; ; attempt++)
                        {
                            using (var importDeadline = new CancellationTokenSource())
                            using (var importCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverToken, importDeadline.Token))
                            using (importDeadline.Token.Register(() => Update(() =>
                                status.Activity = "Import deadline reached; waiting for active work to stop")))
                            {
                                var importToken = importCancellation.Token;
                                importDeadline.CancelAfter(timeoutOverride ?? DefaultTimeout);
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
                                        status.LastProgressUtc = clock();
                                    });
                                    // Each attempt needs its own token, served-folder tracking and failure state.
                                    var full = TuneInStagedImport.ForChannel(importedRoots, importedListings, importToken,
                                        (total, served) => Update(() =>
                                        {
                                            status.ImportFoldersTotal = total;
                                            status.ImportFoldersServed = served;
                                            status.LastProgressUtc = clock();
                                        }), () => directory.CheckCurrent(snapshot, importToken));
                                    directory.CheckCurrent(snapshot, importToken);
                                    if (!importPrepared)
                                    {
                                        // Keep an earlier interruption recorded until the complete repair succeeds.
                                        checkpoint.DirtyCategory = dirtyAtStart ?? currentCategory;
                                        Persist(snapshot, checkpoint, started);
                                        store.ClearDiscovery();
                                        importPrepared = true;
                                        pendingImport = new PendingImport
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
                                    }
                                    importToken.ThrowIfCancellationRequested();
                                    Update(() =>
                                    {
                                        status.Phase = "Importing";
                                        status.ImportFoldersTotal = full.Count;
                                    });
                                    importTime.Start();
                                    try
                                    {
                                        var importTask = import(channel, full, null, importToken);
                                        await AwaitImportWithDiagnostics(importTask, importToken).ConfigureAwait(false);
                                    }
                                    finally { importTime.Stop(); }
                                    full.Verify();
                                    importToken.ThrowIfCancellationRequested();
                                    foreach (var root in importedRoots)
                                        updateArtwork?.Invoke(channel, root.Id, importToken);
                                    foreach (var category in importedCategories)
                                    {
                                        ReadEmbyStationCount(snapshot, channel, category, listings[category].RootId, importToken);
                                        lock (gate) listings[category].StationCounts = categoryCounts[category].Copy();
                                    }
                                    directory.CheckCurrent(snapshot, importToken);
                                    importToken.ThrowIfCancellationRequested();
                                    break;
                                }
                                catch (Exception error) when (importDeadline.IsCancellationRequested
                                    && !serverToken.IsCancellationRequested
                                    && (error is OperationCanceledException || error is TimeoutException))
                                {
                                    Update(() => status.ImportTimeouts++);
                                    directory.CheckCurrent(snapshot, serverToken);
                                    if (attempt >= ImportRetryLimit)
                                    {
                                        importTimedOut = true;
                                        throw;
                                    }
                                    logger?.Info("TuneIn import for {0}: starting automatic retry {1} of {2} after the timed-out attempt drained.",
                                        requestedCategory ?? "all", attempt + 1, ImportRetryLimit);
                                }
                            }
                        }
                        if (status.ImportTimeouts > 0)
                            logger?.Info("TuneIn import for {0} completed after {1} import timeout(s).",
                                requestedCategory ?? "all", status.ImportTimeouts);
                        directory.CheckCurrent(snapshot, linked.Token);
                        checkpoint.Categories = importedCategories.Select(category => listings[category]).ToList();
                        checkpoint.PendingCategories.RemoveAll(listings.ContainsKey);
                        checkpoint.NeedsImport = false;
                        checkpoint.DirtyCategory = null;
                        checkpoint.ChannelImportVersion = ChannelImportVersion;
                        Persist(snapshot, checkpoint, started, BuildCommitted(checkpoint, snapshot.Key));
                        pendingImport = null;
                    }
                    else
                    {
                        discovering = false;
                        store.ClearDiscovery();
                        Update(() => status.Phase = "Finishing refresh");
                        foreach (var category in categories)
                        {
                            updateArtwork?.Invoke(channel, roots[Array.IndexOf(enabled, category)].Id, linked.Token);
                            lock (gate) listings[category].StationCounts = categoryCounts[category].Copy();
                        }
                        checkpoint.Categories = enabled.Where(listings.ContainsKey).Select(category => listings[category]).ToList();
                        Persist(snapshot, checkpoint, started);
                    }
                    lock (gate)
                    {
                        if (didImport)
                            foreach (var category in enabled.Where(listings.ContainsKey))
                                categoryStates[category] = listings[category].DiscoveryPartial ? "Partial (applied)" : "Imported";
                        else
                            foreach (var category in categories)
                                categoryStates[category] = partial.Contains(category) ? "Partial (unchanged)" : "Unchanged";
                        status.CategoriesSkipped = didImport ? 0 : completed.Count;
                    }

                    linked.Token.ThrowIfCancellationRequested();
                    directory.CheckCurrent(snapshot, linked.Token);
                    lock (gate)
                    {
                        status.State = partial.Count == 0 ? "Completed" : "Partial";
                        status.Phase = partial.Count != 0
                            ? (didImport ? "Partial refresh completed; valid updates applied, rejected entries excluded"
                                : "Partial refresh completed; merged library unchanged")
                            : (categories.Length == 0 ? "No enabled categories" : "Completed");
                        if (partial.Count != 0) status.Category = requestedCategory ?? partial.OrderBy(category => category, StringComparer.Ordinal).First();
                        status.Progress = 100;
                    }
                }
                catch (Exception error)
                {
                    lock (gate) stoppedPhase = status.Phase;
                    var stalled = error is TuneInCatalogTimeoutException;
                    timedOut = (timeout.IsCancellationRequested || stalled || importTimedOut) && !serverToken.IsCancellationRequested;
                    discoveryTimedOut = timedOut && discovering;
                    discoveryTime.Stop();
                    if (discoveryTimedOut)
                    {
                        // The attempt token has expired; still honor user cancellation and scope changes.
                        try { SaveDiscovery(serverToken, dueToTimeout: true); }
                        catch (OperationCanceledException) { discoveryTimedOut = timedOut = false; error = new OperationCanceledException(); }
                        catch (Exception saveError) when (saveError is IOException || saveError is UnauthorizedAccessException
                            || saveError is InvalidOperationException || saveError is InvalidDataException)
                        {
                            logger?.Warn("TuneIn could not save additional pages at discovery timeout ({0}); earlier checkpoints are unaffected.",
                                saveError.GetType().Name);
                        }
                        if (stalled && timedOut)
                            logger?.Warn("TuneIn catalog response deadline reached during {0}.", currentCategory ?? requestedCategory ?? "discovery");
                    }
                    var cancelled = serverToken.IsCancellationRequested || timeout.IsCancellationRequested
                        || error is OperationCanceledException;
                    lock (gate)
                    {
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
                                ? "Import timed out; automatic retry limit reached; refresh again to reconcile"
                                : $"Timed out during {stoppedPhase}; not retried automatically")
                            : cancelled ? "Stopped and drained" : status.Phase;
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
                        if (serverToken.IsCancellationRequested || status.State == "Cancelled") pendingImport = null;
                        status.FinishedUtc = clock();
                        if (status.State == "Running")
                        {
                            status.State = "Completed";
                            status.Phase = "Completed";
                            status.Progress = 100;
                        }
                        failureLog = failure != null || timedOut || status.ImportTimeouts > 0 || status.SkippedBrowseLinks > 0
                            ? CreateFailureLog(failure, status, skippedEntries, skippedEntriesOmitted, stoppedPhase) : null;
                    }
                }
            }
            return discoveryTimedOut;
        }

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
                "Discovery timeouts: " + failedStatus.DiscoveryTimeouts + " (automatic retry limit: " + DiscoveryResumeLimit + ")",
                "Import timeouts: " + failedStatus.ImportTimeouts + " (automatic retry limit: " + ImportRetryLimit + ")",
                "Discovery checkpoint (UTC): " + (failedStatus.DiscoveryCheckpointUtc?.ToString("O") ?? "none"),
                "Import may have partially applied: " + failedStatus.ImportMayHavePartiallyApplied,
                "Import with exclusions allowed: " + failedStatus.PartialImportAllowed
            };
            if (stoppedPhase != null) lines.Add("Stopped during: " + stoppedPhase);
            if (failedStatus.LastHttpStatus.HasValue)
                lines.Add("TuneIn HTTP status: " + failedStatus.LastHttpStatus.Value);
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
            var truncated = report.Length > FailureReportLimit || skippedEntriesOmitted > 0;
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
                    if (message != null) logger?.Warn(message);
                    if (diagnostics.LimitReached) break;
                }
            }
            // Diagnostic polling must never let a new attempt start before this import drains.
            finally { await importTask.ConfigureAwait(false); }
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
