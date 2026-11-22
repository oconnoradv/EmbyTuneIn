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

    [Route("/TuneIn/GenreArtwork/{GenreId}", "GET")]
    [Authenticated(AllowLocalOnly = true)]
    public sealed class GetTuneInGenreArtwork
    {
        public string GenreId { get; set; }
    }

    public sealed class TuneInCategoryArtworkService : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory resultFactory;
        public IRequest Request { get; set; }

        public TuneInCategoryArtworkService(IHttpResultFactory resultFactory)
        {
            this.resultFactory = resultFactory ?? throw new ArgumentNullException(nameof(resultFactory));
        }

        public object Get(GetTuneInCategoryArtwork request) =>
            Respond(() => TuneInCategoryArtwork.Open(request.Category), "image/png");

        public object Get(GetTuneInGenreArtwork request) =>
            Respond(() => TuneInCategoryArtwork.OpenGenre(request.GenreId), "image/jpeg");

        private object Respond(Func<Stream> open, string contentType)
        {
            var remote = Request?.RemoteIp;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            if (Request == null || !Request.IsSocketConnectionLocal || remote == null || !IPAddress.IsLoopback(remote))
                throw new UnauthorizedAccessException("TuneIn category artwork is available only to this Emby server.");
            Request.CancellationToken.ThrowIfCancellationRequested();
            using (var stream = open())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                Request.CancellationToken.ThrowIfCancellationRequested();
                return resultFactory.GetResult(Request, new ReadOnlyMemory<byte>(buffer.ToArray()), contentType, null);
            }
        }
    }

    internal static class TuneInCategoryArtwork
    {
        internal const string Revision = "1";
        internal const string GenreRevision = "3";
        internal const string ChannelArtwork = "channel";
        private const string GenrePrefix = "MediaBrowser.Plugins.TuneIn.Images.genres.";
        private static readonly string[] Categories = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };
        // Original genre cards are built offline by Build-GenreArtwork.ps1 (oconnoradv/EmbyLogoTool) and bundled here.
        private static readonly HashSet<string> Genres = new HashSet<string>(
            typeof(TuneInCategoryArtwork).Assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(GenrePrefix, StringComparison.Ordinal) && name.EndsWith(".jpg", StringComparison.Ordinal))
                .Select(name => name.Substring(GenrePrefix.Length, name.Length - GenrePrefix.Length - 4)),
            StringComparer.Ordinal);

        internal static string Url(string baseUrl, string category)
        {
            ResourceName(category);
            if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException("The local Emby API URL is unavailable.");
            return baseUrl.TrimEnd('/') + "/TuneIn/CategoryArtwork/" + category + "?v=" + Revision;
        }

        // Music genre folders, including music genres listed under a language, use bundled genre artwork when available.
        internal static string GenreId(NavigationEntry entry)
        {
            if (entry?.Request == null || entry.StationId != null) return null;
            if (entry.Category != "music" && entry.ContentCategory != "music" && entry.MusicSelectionId == null) return null;
            return entry.Request.Parameters.TryGetValue("id", out var id) && id != null && Genres.Contains(id) ? id : null;
        }

        internal static string GenreUrl(string baseUrl, string genreId)
        {
            if (genreId == null || !Genres.Contains(genreId)) throw new ArgumentException("Unknown TuneIn genre artwork.", nameof(genreId));
            if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException("The local Emby API URL is unavailable.");
            return baseUrl.TrimEnd('/') + "/TuneIn/GenreArtwork/" + genreId + "?v=" + GenreRevision;
        }

        internal static Stream OpenGenre(string genreId)
        {
            if (genreId == null || !Genres.Contains(genreId)) throw new ArgumentException("Unknown TuneIn genre artwork.", nameof(genreId));
            return typeof(TuneInCategoryArtwork).Assembly.GetManifestResourceStream(GenrePrefix + genreId + ".jpg")
                ?? throw new InvalidOperationException("Bundled TuneIn genre artwork is missing.");
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
