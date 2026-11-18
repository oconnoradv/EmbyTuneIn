using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.IO;

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

    public sealed class TuneInRefreshService : IService, IRequiresRequest
    {
        private static readonly object SharedGate = new object();
        private static TuneInRefresh shared;
        private readonly TuneInRefresh refresh;

        public IRequest Request { get; set; }

        public TuneInRefreshService(IHttpClient client, IApplicationHost host, IApplicationPaths paths,
            IChannelManager manager, ILogManager logManager)
            : this(GetShared(client, host, paths, manager, logManager)) { }

        internal TuneInRefreshService(TuneInRefresh refresh)
        {
            this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        }

        public TuneInRefreshStatus Get(GetTuneInRefreshStatus request)
        {
            EnsureAdmin();
            var status = refresh.Status();
            status.SettingsLocked = Plugin.Instance.SettingsLock.IsLocked;
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
            return refresh.Start(request.Category);
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
            return Plugin.Instance.SettingsLock.Set(request.Locked.Value);
        }

        private void EnsureAdmin()
        {
            if (Request == null) throw new UnauthorizedAccessException("TuneIn refresh requires administrator access.");
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

        internal static TuneInRefresh GetShared(IHttpClient client, IApplicationHost host, IApplicationPaths paths,
            IChannelManager manager, ILogManager logManager)
        {
            lock (SharedGate)
            {
                if (shared != null) return shared;
                var directory = TuneInRequests.Directory(client, host);
                var statePath = Path.Combine(paths.PluginConfigurationsPath, "TuneIn.refresh.xml");
                shared = new TuneInRefresh(directory, new TuneInRefreshStore(statePath),
                    () => manager.GetChannel<TuneInChannel>(),
                    (channel, folder, session, strict, token) =>
                        channel.GetRefreshChannelItems(folder, session, strict, token),
                    (channel, staged, folder, token) =>
                        channel.ImportAsync(staged,
                            () => manager.RefreshChannelContent(channel, 9, null, token)),
                    logManager.GetLogger(typeof(TuneInRefreshService).Name),
                    updateArtwork: (channel, folder, token) => channel.UpdateCategoryArtwork(folder, token));
                return shared;
            }
        }
    }
}
