using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneIn/Refresh/Status", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInRefreshStatus : IReturn<TuneInRefreshStatus>
    {
        public bool CheckOptional { get; set; }
    }

    [Route("/TuneIn/OptionalChecks/Preference", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class SetTuneInOptionalChecksPreference : IReturn<TuneInOptionalChecksPreference>
    {
        public bool? Skip { get; set; }
    }

    [Route("/TuneIn/DiscoveryImport/Acknowledge", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class AcknowledgeTuneInDiscoveryRisk : IReturn<TuneInDiscoveryRiskToken>
    {
        public int Version { get; set; }
        public bool Accepted { get; set; }
    }

    public sealed class TuneInDiscoveryRiskToken
    {
        public string Token { get; set; }
    }

    public sealed class TuneInOptionalChecksPreference
    {
        public bool SkipOptionalStartupChecks { get; set; }
    }

    [Route("/TuneIn/Refresh/Log", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInRefreshFailureLog : IReturn<TuneInRefreshFailureLog> { }

    [Route("/TuneIn/Refresh/Start", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class StartTuneInRefresh : IReturn<TuneInRefreshStatus>
    {
        public string Category { get; set; }
        public string ExpectedRecoveryId { get; set; }
        public bool? ResumeFirst { get; set; }
    }

    [Route("/TuneIn/Refresh/Cancel", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class CancelTuneInRefresh : IReturn<TuneInRefreshStatus> { }

    [Route("/TuneIn/SettingsLock", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class SetTuneInSettingsLock : IReturn<TuneInSettingsLock>
    {
        public bool? Locked { get; set; }
    }

    [Route("/TuneIn/SettingsLock/OnOpen", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class OpenTuneInSettingsPanel : IReturn<TuneInSettingsLock> { }

    [Route("/TuneIn/Cleanup/Preview", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class PreviewTuneInCleanup : IReturn<TuneInCleanupPreview>
    {
        public string Category { get; set; }
        public string StationReference { get; set; }
    }

    [Route("/TuneIn/Cleanup/Stations", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class SearchTuneInCleanupStations : IReturn<TuneInCleanupStationSearch>
    {
        public string Name { get; set; }
    }

    [Route("/TuneIn/Cleanup/Start", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class StartTuneInCleanup : IReturn<TuneInRefreshStatus>
    {
        public string Token { get; set; }
    }

    [Route("/TuneIn/Cleanup/Cancel", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class CancelTuneInCleanup : IReturn<TuneInRefreshStatus> { }

    public sealed class TuneInRefreshService : IService, IRequiresRequest
    {
        private static readonly object SharedGate = new object();
        private static TuneInRefresh shared;
        internal static readonly TuneInArtworkPackStatusCache ArtworkStatusCache = new TuneInArtworkPackStatusCache();
        private readonly TuneInRefresh refresh;

        public IRequest Request { get; set; }

        public TuneInRefreshService(IHttpClient client, IApplicationHost host, IApplicationPaths paths,
            IChannelManager manager, ILogManager logManager, ILibraryManager library, ITaskManager tasks)
            : this(GetShared(client, host, paths, manager, logManager, library, tasks)) { }

        internal TuneInRefreshService(TuneInRefresh refresh)
        {
            this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        }

        public TuneInRefreshStatus Get(GetTuneInRefreshStatus request)
        {
            EnsureAdmin();
            var configuration = Plugin.Instance.Configuration;
            var deferred = configuration.SkipOptionalStartupChecks == true && !request.CheckOptional;
            var artworkPacks = TuneInCategoryArtwork.Scopes.Select(scope =>
            {
                TuneInArtworkPackStatusSnapshot snapshot;
                if (request.CheckOptional)
                    snapshot = ArtworkStatusCache.Read(scope, forceRescan: true);
                else if (!ArtworkStatusCache.TryRead(scope, scanIfNeeded: !deferred, out snapshot))
                    deferred = true;
                var pack = snapshot?.Status ?? new TuneInArtworkPackStatus
                    { Scope = scope, Path = TuneInCategoryArtwork.Folder(scope), Status = "deferred" };
                pack.CheckedUtc = snapshot?.CheckedAtUtc.UtcDateTime;
                return pack;
            }).ToArray();
            var status = refresh.Status();
            status.ChannelRegistered = refresh.ChannelRegistered();
            status.SettingsLocked = Plugin.Instance.SettingsLock.TryReadStatus();
            status.ArtworkChecksDeferred = deferred;
            status.ArtworkPacks = artworkPacks;
            var artworkPack = status.ArtworkPacks[0];
            status.ArtworkFolder = artworkPack.Path;
            status.ArtworkCards = artworkPack.InstalledCount;
            status.ArtworkPackName = artworkPack.Name;
            status.ArtworkPackImageCount = artworkPack.ImageCount;
            status.ArtworkPackStatus = artworkPack.Status;
            status.HasRefreshBeenStarted = Plugin.Instance.Configuration.HasStartedCatalogRefresh
                || status.State != "NeverRun"
                || (status.Categories ?? Array.Empty<TuneInCategoryRefreshStatus>())
                    .Any(category => category.State != "No recorded refresh");
            status.SkipOptionalStartupChecks = configuration.SkipOptionalStartupChecks;
            status.OptionalChecksEligible = configuration.SchemaVersion == 1 && configuration.ConfigurationCompleted
                && configuration.SettingsSaved && status.HasSuccessfulCategoryRefresh && !status.Busy
                && !status.MaintenanceBusy && status.Cleanup?.State != "Running";
            return status;
        }

        public object Post(SetTuneInOptionalChecksPreference request)
        {
            EnsureAdmin();
            if (!request.Skip.HasValue) throw new ArgumentException("Specify whether to skip optional startup checks.");
            Plugin.Instance.SetOptionalStartupChecks(request.Skip.Value, refresh);
            return new TuneInOptionalChecksPreference { SkipOptionalStartupChecks = request.Skip.Value };
        }

        public object Post(AcknowledgeTuneInDiscoveryRisk request)
        {
            EnsureAdmin();
            return new TuneInDiscoveryRiskToken
            {
                Token = Plugin.Instance.AcknowledgeDiscoveryRisk(request.Version, request.Accepted, refresh)
            };
        }

        public TuneInRefreshFailureLog Get(GetTuneInRefreshFailureLog request)
        {
            EnsureAdmin();
            return refresh.GetFailureLog();
        }

        public TuneInRefreshStatus Post(StartTuneInRefresh request)
        {
            EnsureAdmin();
            Plugin.Instance.MarkCatalogRefreshStarted();
            var status = refresh.Start(request.Category, request.ExpectedRecoveryId, request.ResumeFirst);
            status.HasRefreshBeenStarted = true;
            return status;
        }

        public TuneInRefreshStatus Post(CancelTuneInRefresh request)
        {
            EnsureAdmin();
            return refresh.Cancel();
        }

        public TuneInSettingsLock Post(SetTuneInSettingsLock request)
        {
            EnsureAdmin();
            if (!request.Locked.HasValue) throw new ArgumentException("Specify whether to lock TuneIn settings.");
            return refresh.SetSettingsLock(() => Plugin.Instance.SettingsLock.Set(request.Locked.Value));
        }

        public TuneInSettingsLock Post(OpenTuneInSettingsPanel request)
        {
            EnsureAdmin();
            return Plugin.Instance.LockSettingsOnPanelOpen();
        }

        public TuneInCleanupPreview Post(PreviewTuneInCleanup request)
        {
            EnsureAdmin();
            return refresh.PreviewCleanup(request.Category, request.StationReference);
        }

        public TuneInCleanupStationSearch Get(SearchTuneInCleanupStations request)
        {
            EnsureAdmin();
            return refresh.SearchCleanupStations(request.Name);
        }

        public TuneInRefreshStatus Post(StartTuneInCleanup request)
        {
            EnsureAdmin();
            return refresh.StartCleanup(request.Token);
        }

        public TuneInRefreshStatus Post(CancelTuneInCleanup request)
        {
            EnsureAdmin();
            return refresh.CancelCleanup();
        }

        private void EnsureAdmin()
        {
            if (Request == null) throw new UnauthorizedAccessException("This TuneIn operation requires administrator access.");
        }

        internal static void UpdateConfiguration(Action update)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            lock (SharedGate)
            {
                if (shared == null) update();
                else shared.UpdateConfiguration(update);
            }
        }

        internal static T InstallArtwork<T>(Func<T> install)
        {
            TuneInRefresh coordinator;
            lock (SharedGate) coordinator = shared;
            if (coordinator == null) throw new InvalidOperationException("TuneIn refresh status is unavailable; reload settings and try again.");
            return coordinator.InstallArtwork(install, Plugin.Instance.SettingsLock.LockForArtworkInstallation);
        }

        internal static Task RunMaintenance(Func<Task> run)
        {
            lock (SharedGate)
            {
                if (shared == null) throw new InvalidOperationException("TuneIn refresh status is unavailable; reload settings and try again.");
                return shared.RunMaintenance(run);
            }
        }

        internal static bool TryGetCommitted(ConfigurationSnapshot snapshot, string folder, out ChannelItemResult result)
        {
            TuneInRefresh coordinator;
            lock (SharedGate) coordinator = shared;
            if (coordinator == null)
            {
                result = null;
                return false;
            }
            return coordinator.TryGetCommitted(snapshot, folder, out result);
        }

        internal static void DisposeShared()
        {
            TuneInRefresh coordinator;
            lock (SharedGate)
            {
                coordinator = shared;
                shared = null;
            }
            coordinator?.Dispose();
        }

        internal static void EnsureNoCleanup()
        {
            TuneInRefresh coordinator;
            lock (SharedGate) coordinator = shared;
            coordinator?.EnsureNoCleanup();
        }

        internal static Task ImportChannel(IChannelManager manager, TuneInChannel channel,
            TuneInStagedImport staged, string restrictTopLevelFolderId, CancellationToken token) =>
            channel.ImportAsync(staged, token, () =>
                manager.RefreshChannelContent(channel, staged.ImportRefreshLevel, restrictTopLevelFolderId, token),
                allowChangedToken: channel.DiscoveryBridgeEnabled());

        internal static void LogDiscoveryRiskAcceptance()
        {
            lock (SharedGate) shared?.LogDiscoveryRiskAcceptance();
        }

        internal static TuneInRefresh GetShared(IHttpClient client, IApplicationHost host, IApplicationPaths paths,
            IChannelManager manager, ILogManager logManager, ILibraryManager library, ITaskManager tasks)
        {
            lock (SharedGate)
            {
                if (shared != null) return shared;
                var directory = TuneInRequests.Directory(client, host);
                var statePath = Path.Combine(paths.PluginConfigurationsPath, "TuneIn.refresh.xml");
                var logger = logManager.GetLogger(typeof(TuneInRefreshService).Name);
                var cleanup = new TuneInLibraryCleanup(library, () =>
                {
                    var source = manager.GetChannel<TuneInChannel>();
                    if (source == null) throw new InvalidOperationException("The TuneIn channel is not registered.");
                    return library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Channel) } },
                        CancellationToken.None).OfType<Channel>()
                        .SingleOrDefault(channel => string.Equals(channel.Name, source.Name, StringComparison.Ordinal));
                }, logger);
                shared = new TuneInRefresh(directory, new TuneInRefreshStore(statePath),
                    () => manager.GetChannel<TuneInChannel>(),
                    (channel, folder, session, strict, token) =>
                        channel.GetRefreshChannelItems(folder, session, strict, token),
                    (channel, staged, folder, token) => ImportChannel(manager, channel, staged, folder, token),
                    logger,
                    updateArtwork: (channel, folder, token) => channel.UpdateCategoryArtwork(folder, token),
                    libraryCleanup: cleanup, allowScopedImport: true,
                    lockSettings: () => Plugin.Instance.SettingsLock.EnsureLocked(),
                    backupBusy: () => TuneInBackup.IsActive(tasks));
                return shared;
            }
        }
    }
}
