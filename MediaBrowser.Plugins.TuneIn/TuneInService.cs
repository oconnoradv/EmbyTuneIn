using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Controller.Channels;

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

    [Route("/TuneInFork/Refresh", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetTuneInRefresh : IReturn<RadioGroupRefreshStatus[]> { }

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

    public class TuneInService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly ILogger logger;
        private readonly IChannelManager channelManager;
        public TuneInService(ILogManager logManager, IChannelManager channelManager)
        { logger = logManager.GetLogger(GetType().Name); this.channelManager = channelManager; }
        internal TuneInService() { }
        public IRequest Request { get; set; }
        public IHttpResultFactory ResultFactory { get; set; }

        public Task<SelectorResult> Get(GetTuneInSelectors request) =>
            TuneInDirectory.Instance.SelectAsync(request.Category, request.Parent, request.Path,
                request.Refresh, Request.CancellationToken);

        public RadioGroupRefreshStatus[] Get(GetTuneInRefresh request) => TuneInRefresh.Instance.Status();
        public RadioGroupRefreshStatus Post(StartTuneInRefresh request) =>
            TuneInRefresh.Instance.Start(request.Category, (folder, depth, token) =>
            {
                var channel = channelManager.GetChannel<TuneInChannel>();
                if (channel == null) throw new InvalidOperationException("TuneIn channel is not registered.");
                return channelManager.RefreshChannelContent(channel, depth, folder, token);
            }, logger);
        public RadioGroupRefreshStatus Post(CancelTuneInRefresh request) =>
            TuneInRefresh.Instance.Cancel(request.Category, request.Id);

        public async Task<object> Get(GetTuneInRelay request)
        {
            var remote = Request.RemoteIp;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            if (!Request.IsSocketConnectionLocal || remote == null || !System.Net.IPAddress.IsLoopback(remote))
                throw new UnauthorizedAccessException("TuneIn relay is available only to this Emby server.");
            var directory = TuneInDirectory.Instance;
            var snapshot = directory.Snapshot();
            var entry = directory.Resolve(snapshot, request.Id);
            if (entry.StationId == null) throw new ArgumentException("Invalid radio relay item.");
            var operation = directory.Operation(snapshot, Request.CancellationToken);
            try
            {
                var response = await directory.OpenStreamAsync(snapshot, entry, operation.Token,
                    message => logger.Warn(message)).ConfigureAwait(false);
                return ResultFactory.GetResult(Request, new RelayStream(response, operation), response.ContentType, null);
            }
            catch
            {
                operation.Dispose();
                throw;
            }
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
