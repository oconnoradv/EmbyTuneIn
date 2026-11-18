using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.IO;
using System.Linq;
using System.Net;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneIn/CategoryArtwork/{Category}", "GET")]
    [Authenticated(AllowLocalOnly = true)]
    public sealed class GetTuneInCategoryArtwork
    {
        public string Category { get; set; }
    }

    public sealed class TuneInCategoryArtworkService : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory resultFactory;
        public IRequest Request { get; set; }

        public TuneInCategoryArtworkService(IHttpResultFactory resultFactory)
        {
            this.resultFactory = resultFactory ?? throw new ArgumentNullException(nameof(resultFactory));
        }

        public object Get(GetTuneInCategoryArtwork request)
        {
            var remote = Request?.RemoteIp;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            if (Request == null || !Request.IsSocketConnectionLocal || remote == null || !IPAddress.IsLoopback(remote))
                throw new UnauthorizedAccessException("TuneIn category artwork is available only to this Emby server.");
            Request.CancellationToken.ThrowIfCancellationRequested();
            using (var stream = TuneInCategoryArtwork.Open(request.Category))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                Request.CancellationToken.ThrowIfCancellationRequested();
                return resultFactory.GetResult(Request, new ReadOnlyMemory<byte>(buffer.ToArray()), "image/png", null);
            }
        }
    }

    internal static class TuneInCategoryArtwork
    {
        internal const string Revision = "1";
        internal const string ChannelArtwork = "channel";
        private static readonly string[] Categories = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };

        internal static string Url(string baseUrl, string category)
        {
            ResourceName(category);
            if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException("The local Emby API URL is unavailable.");
            return baseUrl.TrimEnd('/') + "/TuneIn/CategoryArtwork/" + category + "?v=" + Revision;
        }

        internal static Stream Open(string category) =>
            typeof(TuneInCategoryArtwork).Assembly.GetManifestResourceStream(ResourceName(category))
            ?? throw new InvalidOperationException("Bundled TuneIn category artwork is missing.");

        private static string ResourceName(string category)
        {
            if (category == ChannelArtwork) return "MediaBrowser.Plugins.TuneIn.Images.tunein-poweredby.png";
            if (!Categories.Contains(category, StringComparer.Ordinal))
                throw new ArgumentException("Unknown TuneIn artwork category.", nameof(category));
            var name = category == "local" ? "localradio" : category == "language" ? "bylanguage"
                : category == "location" ? "bylocation" : category == "presets" ? "myfavs"
                : category == "podcast" ? "podcasts" : category;
            return "MediaBrowser.Plugins.TuneIn.Images.tunein-" + name + ".png";
        }
    }
}
