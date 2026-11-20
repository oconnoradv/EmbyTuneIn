using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneIn/Playback/{Id}", "GET")]
    [Route("/TuneIn/Playback/{Id}/{Attempt}", "GET")]
    [Authenticated(AllowLocalOnly = true)]
    public sealed class GetTuneInPlayback
    {
        public string Id { get; set; }
        public string Attempt { get; set; }
    }

    public sealed class TuneInPlaybackService : IService, IRequiresRequest
    {
        private readonly TuneInPlayback playback;
        private readonly IHttpResultFactory resultFactory;
        private readonly IFfmpegManager ffmpeg;
        private readonly ILogger logger;

        public TuneInPlaybackService(IHttpClient client, IApplicationHost host, IHttpResultFactory resultFactory,
            IFfmpegManager ffmpeg, ILogManager logManager)
            : this(new TuneInPlayback(TuneInRequests.Directory(client, host), null,
                message => logManager.GetLogger(nameof(TuneInPlaybackService)).Warn(message)),
                resultFactory, ffmpeg, logManager.GetLogger(nameof(TuneInPlaybackService))) { }

        internal TuneInPlaybackService(TuneInPlayback playback, IHttpResultFactory resultFactory,
            IFfmpegManager ffmpeg, ILogger logger)
        {
            this.playback = playback;
            this.resultFactory = resultFactory ?? throw new ArgumentNullException(nameof(resultFactory));
            this.ffmpeg = ffmpeg;
            this.logger = logger;
        }

        public IRequest Request { get; set; }

        public async Task<object> Get(GetTuneInPlayback request)
        {
            var address = Request.RemoteIp;
            if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
            if (!Request.IsSocketConnectionLocal || address == null || !IPAddress.IsLoopback(address))
                throw new UnauthorizedAccessException("TuneIn playback handoff is restricted to this Emby server.");
            TuneInAudioSource source = null;
            try
            {
                source = await playback.OpenAsync(request.Id, Decode, Request.CancellationToken, request.Attempt).ConfigureAwait(false);
                Request.CancellationToken.ThrowIfCancellationRequested();
                return resultFactory.GetResult(Request, source.Body, source.ContentType, null);
            }
            catch (Exception error)
            {
                source?.Dispose();
                if (!(error is OperationCanceledException))
                    logger.Warn("TuneIn playback handoff could not open ({0}).", TuneInPlayback.DescribeFailure(error));
                throw;
            }
        }

        private Stream Decode(TuneInAudioSource source, CancellationToken token)
        {
            var configuration = ffmpeg?.FfmpegConfiguration;
            return new PcmDecoderStream(configuration?.EncoderPath, source, token,
                configuration?.EnvironmentVariables?.ToDictionary(setting => setting.VariableName, setting => setting.Value));
        }
    }
}
