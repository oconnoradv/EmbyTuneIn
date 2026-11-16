using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneInFork/Selectors", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetTuneInSelectors : IReturn<SelectorResult>
    {
        public string Category { get; set; }
        public string Parent { get; set; }
        public string Path { get; set; }
        public bool Refresh { get; set; }
    }

    [Route("/TuneInFork/Relay/{Id}", "GET")]
    [Authenticated(AllowLocalOnly = true)]
    public class GetTuneInRelay
    {
        public string Id { get; set; }
    }

    [Route("/TuneInFork/Artwork/{Id}", "GET")]
    [Authenticated(AllowLocalOnly = true)]
    public class GetTuneInArtwork
    {
        public string Id { get; set; }
    }

    [Route("/TuneInFork/Branding", "GET")]
    [Authenticated(AllowLocalOnly = true)]
    public class GetTuneInBranding { }

    [Route("/TuneInFork/Refresh", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetTuneInRefresh : IReturn<RadioGroupRefreshStatus[]> { }

    [Route("/TuneInFork/Schedule", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetTuneInSchedule : IReturn<TuneInScheduleSettings> { }

    [Route("/TuneInFork/Schedule", "POST")]
    [Authenticated(Roles = "Admin")]
    public class SetTuneInSchedule : TuneInScheduleSettings, IReturn<TuneInScheduleSettings> { }

    [Route("/TuneInFork/Refresh/{Category}", "POST")]
    [Authenticated(Roles = "Admin")]
    public class StartTuneInRefresh : IReturn<RadioGroupRefreshStatus>
    {
        public string Category { get; set; }
    }

    [Route("/TuneInFork/Refresh/{Category}/Cancel", "POST")]
    [Authenticated(Roles = "Admin")]
    public class CancelTuneInRefresh : IReturn<RadioGroupRefreshStatus>
    {
        public string Category { get; set; }
        public string Id { get; set; }
    }

    public class TuneInService : IService, IRequiresRequest
    {
        private readonly ILogger logger;
        private readonly IChannelManager channelManager;
        private readonly IHttpResultFactory resultFactory;
        private readonly TuneInDirectory directory;
        private readonly ITaskManager taskManager;
        public TuneInService(ILogManager logManager, IChannelManager channelManager, IHttpResultFactory resultFactory, ITaskManager taskManager)
            : this(logManager, channelManager, resultFactory, TuneInDirectory.Instance)
        { this.taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager)); }
        internal TuneInService(ILogManager logManager, IChannelManager channelManager, IHttpResultFactory resultFactory, TuneInDirectory directory)
        {
            logger = logManager.GetLogger(GetType().Name);
            this.channelManager = channelManager;
            this.resultFactory = resultFactory ?? throw new ArgumentNullException(nameof(resultFactory));
            this.directory = directory;
        }
        internal TuneInService() { directory = TuneInDirectory.Instance; }
        public IRequest Request { get; set; }

        public Task<SelectorResult> Get(GetTuneInSelectors request) =>
            TuneInDirectory.Instance.SelectAsync(request.Category, request.Parent, request.Path,
                request.Refresh, Request.CancellationToken);

        public RadioGroupRefreshStatus[] Get(GetTuneInRefresh request) => TuneInRefresh.Instance.Status();
        public TuneInScheduleSettings Get(GetTuneInSchedule request) =>
            new TuneInSchedules(taskManager, Plugin.Instance.RefreshState).Read();
        public TuneInScheduleSettings Post(SetTuneInSchedule request) =>
            new TuneInSchedules(taskManager, Plugin.Instance.RefreshState).Apply(request);
        public RadioGroupRefreshStatus Post(StartTuneInRefresh request) =>
            TuneInRefresh.Instance.StartNative(request.Category, taskManager, channelManager, logger);
        public RadioGroupRefreshStatus Post(CancelTuneInRefresh request) =>
            TuneInRefresh.Instance.Cancel(request.Category, request.Id);

        public async Task<object> Get(GetTuneInRelay request)
        {
            RequireLoopback();
            var snapshot = directory.Snapshot();
            var entry = directory.Resolve(snapshot, request.Id);
            if (entry.StationId == null) throw new ArgumentException("Invalid radio relay item.");
            var operation = directory.Operation(snapshot, Request.CancellationToken);
            RelayStream relay = null;
            try
            {
                var response = await directory.OpenStreamAsync(snapshot, entry, operation.Token,
                    message => logger.Warn(message)).ConfigureAwait(false);
                relay = new RelayStream(response, operation);
                return resultFactory.GetResult(Request, relay, response.ContentType, null);
            }
            catch
            {
                if (relay != null) relay.Dispose();
                else operation.Dispose();
                throw;
            }
        }

        public async Task<object> Get(GetTuneInArtwork request)
        {
            RequireLoopback();
            var snapshot = directory.Snapshot();
            var entry = directory.Resolve(snapshot, request.Id);
            using (var operation = directory.Operation(snapshot, Request.CancellationToken))
            {
                var artwork = await directory.OpenArtworkAsync(entry, operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                return resultFactory.GetResult(Request, new ReadOnlyMemory<byte>(artwork.Bytes), artwork.ContentType, null);
            }
        }

        public async Task<object> Get(GetTuneInBranding request)
        {
            RequireLoopback();
            var artwork = await TuneInArtwork.BrandingAsync(Request.CancellationToken).ConfigureAwait(false);
            Request.CancellationToken.ThrowIfCancellationRequested();
            return resultFactory.GetResult(Request, new ReadOnlyMemory<byte>(artwork.Bytes), artwork.ContentType, null);
        }

        private void RequireLoopback()
        {
            var remote = Request.RemoteIp;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            if (!Request.IsSocketConnectionLocal || remote == null || !System.Net.IPAddress.IsLoopback(remote))
                throw new UnauthorizedAccessException("TuneIn media endpoints are available only to this Emby server.");
        }

        private sealed class RelayStream : System.IO.Stream
        {
            private readonly SafeResponse response;
            private readonly CancellationTokenSource operation;
            public RelayStream(SafeResponse response, CancellationTokenSource operation)
            { this.response = response; this.operation = operation; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                response.Body.ReadAsync(buffer, offset, count, operation.Token);
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, operation.Token).GetAwaiter().GetResult();
            protected override void Dispose(bool disposing)
            {
                if (disposing) { response.Dispose(); operation.Dispose(); }
                base.Dispose(disposing);
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
