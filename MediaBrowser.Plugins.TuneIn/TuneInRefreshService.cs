using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneIn/Refresh/Status", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInRefreshStatus : IReturn<TuneInRefreshStatus> { }

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

    [Route("/TuneIn/Cleanup/Preview", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class PreviewTuneInCleanup : IReturn<TuneInCleanupPreview>
    {
        public string Category { get; set; }
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
        private readonly TuneInRefresh refresh;

        public IRequest Request { get; set; }

        public TuneInRefreshService(IHttpClient client, IApplicationHost host, IApplicationPaths paths,
            IChannelManager manager, ILogManager logManager, ILibraryManager library)
            : this(GetShared(client, host, paths, manager, logManager, library)) { }

        internal TuneInRefreshService(TuneInRefresh refresh)
        {
            this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        }

        public TuneInRefreshStatus Get(GetTuneInRefreshStatus request)
        {
            EnsureAdmin();
            var status = refresh.Status();
            status.SettingsLocked = Plugin.Instance.SettingsLock.TryReadStatus();
            status.HasRefreshBeenStarted = Plugin.Instance.Configuration.HasStartedCatalogRefresh
                || status.State != "NeverRun"
                || (status.Categories ?? Array.Empty<TuneInCategoryRefreshStatus>())
                    .Any(category => category.State != "No recorded refresh");
            return status;
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

        public TuneInCleanupPreview Post(PreviewTuneInCleanup request)
        {
            EnsureAdmin();
            return refresh.PreviewCleanup(request.Category);
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
            channel.ImportAsync(staged, () =>
                manager.RefreshChannelContent(channel, staged.ImportRefreshLevel, restrictTopLevelFolderId, token));

        internal static TuneInRefresh GetShared(IHttpClient client, IApplicationHost host, IApplicationPaths paths,
            IChannelManager manager, ILogManager logManager, ILibraryManager library)
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
                    libraryCleanup: cleanup, allowScopedImport: true);
                return shared;
            }
        }
    }
}
