using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    public abstract class TuneInRadioGroupRefreshTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly IChannelManager manager;
        private readonly ILogger logger;
        protected TuneInRadioGroupRefreshTask(IChannelManager manager, ILogManager logs)
        { this.manager = manager; logger = logs.GetLogger(GetType().Name); }
        internal abstract string Group { get; }
        public string Name => "Refresh TuneIn - " + TuneInChannel.CategoryName(Group);
        public string Key => "TuneInForkRefresh_" + Group;
        public string Description => "Populate the configured " + TuneInChannel.CategoryName(Group) + " station section only. "
            + TuneInRefresh.Instance.HistorySummary(Group);
        public string Category => "Internet Channels";
        public bool IsHidden => false;
        public bool IsEnabled => true;
        public bool IsLogged => true;
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) =>
            TuneInRefresh.Instance.ExecuteScheduledAsync(Group, manager, logger, cancellationToken, progress);
    }

    public sealed class RefreshTuneInLocalRadio : TuneInRadioGroupRefreshTask
    {
        public RefreshTuneInLocalRadio(IChannelManager manager, ILogManager logs) : base(manager, logs) { }
        internal override string Group => "local";
    }
    public sealed class RefreshTuneInMusic : TuneInRadioGroupRefreshTask
    {
        public RefreshTuneInMusic(IChannelManager manager, ILogManager logs) : base(manager, logs) { }
        internal override string Group => "music";
    }
    public sealed class RefreshTuneInLanguage : TuneInRadioGroupRefreshTask
    {
        public RefreshTuneInLanguage(IChannelManager manager, ILogManager logs) : base(manager, logs) { }
        internal override string Group => "language";
    }
    public sealed class RefreshTuneInLocation : TuneInRadioGroupRefreshTask
    {
        public RefreshTuneInLocation(IChannelManager manager, ILogManager logs) : base(manager, logs) { }
        internal override string Group => "location";
    }
    public sealed class RefreshTuneInTalk : TuneInRadioGroupRefreshTask
    {
        public RefreshTuneInTalk(IChannelManager manager, ILogManager logs) : base(manager, logs) { }
        internal override string Group => "talk";
    }
    public sealed class RefreshTuneInSports : TuneInRadioGroupRefreshTask
    {
        public RefreshTuneInSports(IChannelManager manager, ILogManager logs) : base(manager, logs) { }
        internal override string Group => "sports";
    }
    public sealed class RefreshTuneInPodcasts : TuneInRadioGroupRefreshTask
    {
        public RefreshTuneInPodcasts(IChannelManager manager, ILogManager logs) : base(manager, logs) { }
        internal override string Group => "podcast";
    }
    public sealed class RefreshTuneInFavorites : TuneInRadioGroupRefreshTask
    {
        public RefreshTuneInFavorites(IChannelManager manager, ILogManager logs) : base(manager, logs) { }
        internal override string Group => "presets";
    }
}
