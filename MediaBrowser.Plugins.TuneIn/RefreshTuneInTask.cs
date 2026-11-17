using MediaBrowser.Controller.Channels;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class RefreshTuneInTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly IChannelManager channels;
        private readonly TuneInRefresh refresh;
        private readonly ILogger logger;

        public RefreshTuneInTask(IChannelManager channels, IHttpClient client, IApplicationHost host, ILogManager logs)
            : this(channels, TuneInRequests.Refresh(client, host), logs.GetLogger(nameof(RefreshTuneInTask))) { }

        internal RefreshTuneInTask(IChannelManager channels, TuneInRefresh refresh, ILogger logger = null)
        {
            this.channels = channels;
            this.refresh = refresh;
            this.logger = logger;
        }

        public string Name => "Refresh TuneIn";
        public string Key => "TuneInRefresh";
        public string Category => "Internet Channels";
        public string Description => "Refreshes enabled TuneIn categories and saved selections only.";
        public bool IsHidden => false;
        public bool IsEnabled => true;
        public bool IsLogged => true;
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            return refresh.ExecuteConfiguredAsync(channels, logger, cancellationToken, progress);
        }
    }
}
