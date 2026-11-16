using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using System.Linq;

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

    [Route("/TuneInFork/ChannelRefresh", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetTuneInChannelRefresh : IReturn<ChannelRefreshStatus> { }

    [Route("/TuneInFork/ChannelRefresh/Failure", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetTuneInChannelRefreshFailure : IReturn<ChannelRefreshFailure>
    {
        public string Attempt { get; set; }
    }

    public class TuneInService : IService, IRequiresRequest
    {
        private readonly ILogger logger;
        private readonly IHttpResultFactory resultFactory;
        private readonly TuneInDirectory directory;
        private readonly ITaskManager taskManager;
        private readonly IFfmpegManager ffmpegManager;
        public TuneInService(ILogManager logManager, IChannelManager channelManager, IHttpResultFactory resultFactory,
            ITaskManager taskManager, IFfmpegManager ffmpegManager)
            : this(logManager, channelManager, resultFactory, TuneInDirectory.Instance,
                ffmpegManager ?? throw new ArgumentNullException(nameof(ffmpegManager)))
        {
            this.taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
        }
        internal TuneInService(ILogManager logManager, IChannelManager channelManager, IHttpResultFactory resultFactory,
            TuneInDirectory directory, IFfmpegManager ffmpegManager = null)
        {
            logger = logManager.GetLogger(GetType().Name);
            this.resultFactory = resultFactory ?? throw new ArgumentNullException(nameof(resultFactory));
            this.directory = directory;
            this.ffmpegManager = ffmpegManager;
        }
        internal TuneInService() { directory = TuneInDirectory.Instance; }
        public IRequest Request { get; set; }

        public Task<SelectorResult> Get(GetTuneInSelectors request) =>
            TuneInDirectory.Instance.SelectAsync(request.Category, request.Parent, request.Path,
                request.Refresh, Request.CancellationToken);

        public ChannelRefreshStatus Get(GetTuneInChannelRefresh request) =>
            new TuneInChannelRefresh(taskManager).Status();
        public ChannelRefreshFailure Get(GetTuneInChannelRefreshFailure request) =>
            new TuneInChannelRefresh(taskManager).Failure(request.Attempt);

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
                    message => logger?.Warn(message),
                    DecodeAudio).ConfigureAwait(false);
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

        private System.IO.Stream DecodeAudio(SafeResponse source, CancellationToken token)
        {
            var configuration = ffmpegManager?.FfmpegConfiguration;
            return new PcmDecoderStream(configuration?.EncoderPath, source, token,
                configuration?.EnvironmentVariables?.ToDictionary(setting => setting.VariableName, setting => setting.Value));
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
            private bool disposed;
            public RelayStream(SafeResponse response, CancellationTokenSource operation)
            { this.response = response; this.operation = operation; }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(token, operation.Token))
                {
                    read.Token.ThrowIfCancellationRequested();
                    return await response.Body.ReadAsync(buffer, offset, count, read.Token).ConfigureAwait(false);
                }
            }
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, operation.Token).GetAwaiter().GetResult();
            protected override void Dispose(bool disposing)
            {
                if (disposing && !disposed)
                {
                    disposed = true;
                    operation.Cancel();
                    response.Dispose();
                    operation.Dispose();
                }
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
