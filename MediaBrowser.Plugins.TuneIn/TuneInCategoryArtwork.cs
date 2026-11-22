using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
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
        internal const string GenreRevision = "2";
        internal const string ChannelArtwork = "channel";
        private static readonly string[] Categories = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };
        // Music genres for which TuneIn publishes a genre logo; other genres return a generic or station image.
        private static readonly HashSet<string> Genres = new HashSet<string>(new[]
        {
            "c100000948", "c100000949", "c100000952", "c100001676", "c100001870", "c100002494", "c100002506", "c100002533", "c100003819", "c100006348", "c10635888", "c1367173", "c481372", "c530749", "c57935", "c57939", "c57940", "c57941", "c57942", "c57943", "c57944", "c57950", "c57951", "g115", "g22", "g2762", "g2771", "g2773", "g2774", "g2792", "g2804", "g393", "g4152", "g4154", "g4156", "g54", "g63"
        }, StringComparer.Ordinal);

        internal static string Url(string baseUrl, string category)
        {
            ResourceName(category);
            if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException("The local Emby API URL is unavailable.");
            return baseUrl.TrimEnd('/') + "/TuneIn/CategoryArtwork/" + category + "?v=" + Revision;
        }

        // Music genre folders, including music genres listed under a language, use TuneIn's genre logo when one is published.
        internal static string GenreId(NavigationEntry entry)
        {
            if (entry?.Request == null || entry.StationId != null) return null;
            if (entry.Category != "music" && entry.ContentCategory != "music" && entry.MusicSelectionId == null) return null;
            return entry.Request.Parameters.TryGetValue("id", out var id) && id != null && Genres.Contains(id) ? id : null;
        }

        internal static string GenreUrl(string genreId)
        {
            if (genreId == null || !Genres.Contains(genreId)) throw new ArgumentException("Unknown TuneIn genre artwork.", nameof(genreId));
            return "https://cdn-profiles.tunein.com/" + genreId + "/images/logoq.png";
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
