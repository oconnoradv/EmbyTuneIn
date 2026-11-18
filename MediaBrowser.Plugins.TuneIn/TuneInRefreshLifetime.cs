using MediaBrowser.Controller.Plugins;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class TuneInRefreshLifetime : IServerEntryPoint
    {
        public void Run() { }

        public void Dispose() => TuneInRefreshService.DisposeShared();
    }
}
