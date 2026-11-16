using MediaBrowser.Controller.Channels;
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
        internal const string TaskKey = "TuneInForkRefresh";
        private readonly IChannelManager channelManager;
        private readonly ILogger logger;
        private readonly TuneInRefresh refresh;

        public RefreshTuneInTask(IChannelManager channelManager, ILogManager logs)
            : this(channelManager, logs.GetLogger(nameof(RefreshTuneInTask)), TuneInRefresh.Instance) { }

        internal RefreshTuneInTask(IChannelManager channelManager, ILogger logger, TuneInRefresh refresh)
        {
            this.channelManager = channelManager ?? throw new ArgumentNullException(nameof(channelManager));
            this.logger = logger;
            this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        }

        public string Name => "Refresh TuneIn";
        public string Key => TaskKey;
        public string Category => "Internet Channels";
        public string Description => "Refreshes only enabled TuneIn categories and their saved sub-category selections. Other channel providers are not refreshed.";
        public bool IsHidden => false;
        public bool IsEnabled => true;
        public bool IsLogged => true;
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) =>
            refresh.ExecuteConfiguredAsync(channelManager, logger, cancellationToken, progress);
    }
}
