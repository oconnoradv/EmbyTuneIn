using System;
using System.Threading;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class TuneInRefreshLifetime : IServerEntryPoint
    {
        private readonly TuneInRefresh refresh;
        private readonly ILogger logger;
        private readonly IChannelManager manager;
        private bool libraryArtworkCurrent;
        private Timer timer;

        public TuneInRefreshLifetime(IHttpClient client, IApplicationHost host, IApplicationPaths paths,
            IChannelManager manager, ILogManager logManager, ILibraryManager library, ITaskManager tasks)
        {
            refresh = TuneInRefreshService.GetShared(client, host, paths, manager, logManager, library, tasks);
            logger = logManager.GetLogger(nameof(TuneInRefreshLifetime));
            this.manager = manager;
        }

        public void Run()
        {
            TuneInArtworkStorage.InitializeDirectories(Plugin.Instance?.ArtworkRoot,
                (path, error) => logger.Warn("TuneIn could not initialize artwork directory '{0}': {1}", path, error.Message));
            timer = new Timer(_ =>
            {
                UpdateLibraryArtwork();
                try { refresh.CheckSchedules(); }
                catch (Exception error) { logger.Error("TuneIn could not check refresh schedules ({0}).", error.GetType().Name); }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1));
        }

        // Plugin updates replace the library image without waiting for a catalog import.
        private void UpdateLibraryArtwork()
        {
            if (libraryArtworkCurrent) return;
            try { libraryArtworkCurrent = manager.GetChannel<TuneInChannel>()?.UpdateLibraryArtwork(CancellationToken.None) == true; }
            catch (Exception error) { logger.Error("TuneIn could not update its library artwork ({0}).", error.GetType().Name); }
        }

        public void Dispose()
        {
            timer?.Dispose();
            TuneInRefreshService.DisposeShared();
        }
    }
}
