using System;
using System.Threading;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class TuneInRefreshLifetime : IServerEntryPoint
    {
        private readonly TuneInRefresh refresh;
        private readonly ILogger logger;
        private Timer timer;

        public TuneInRefreshLifetime(IHttpClient client, IApplicationHost host, IApplicationPaths paths,
            IChannelManager manager, ILogManager logManager, ILibraryManager library)
        {
            refresh = TuneInRefreshService.GetShared(client, host, paths, manager, logManager, library);
            logger = logManager.GetLogger(nameof(TuneInRefreshLifetime));
        }

        public void Run() => timer = new Timer(_ =>
        {
            try { refresh.CheckSchedules(); }
            catch (Exception error) { logger.Error("TuneIn could not check refresh schedules ({0}).", error.GetType().Name); }
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1));

        public void Dispose()
        {
            timer?.Dispose();
            TuneInRefreshService.DisposeShared();
        }
    }
}
