using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

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

    // The settings page loads this from the administrator's browser, so it cannot be loopback-only.
    [Route("/TuneIn/LibraryArtwork", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInLibraryArtwork
    {
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

        public object Get(GetTuneInGenreArtwork request)
        {
            var path = TuneInCategoryArtwork.GenreFile(request.GenreId)
                ?? throw new ArgumentException("Unknown TuneIn genre artwork.", nameof(request));
            return Respond(() => File.OpenRead(path), path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
        }

        public object Get(GetTuneInLibraryArtwork request) =>
            Respond(() => TuneInCategoryArtwork.Open(TuneInCategoryArtwork.ChannelArtwork), "image/png", false);

        private object Respond(Func<Stream> open, string contentType, bool loopbackOnly = true)
        {
            if (Request == null) throw new UnauthorizedAccessException("TuneIn artwork requires an Emby request.");
            var remote = Request?.RemoteIp;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            if (loopbackOnly && (!Request.IsSocketConnectionLocal || remote == null || !IPAddress.IsLoopback(remote)))
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
        internal const string LibraryRevision = "2";
        internal const string ChannelArtwork = "channel";
        private static readonly string[] Categories = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };
        private static readonly Regex GenrePattern = new Regex("^[cg][0-9]{1,12}$", RegexOptions.CultureInvariant);
        private static readonly string[] GenreExtensions = { ".jpg", ".png" };

        // Optional artwork packs (for example from oconnoradv/EmbyTuneInArt) are extracted here as <guide id>.jpg or .png.
        internal static string GenreFolder { get; set; }

        internal static string Url(string baseUrl, string category)
        {
            ResourceName(category);
            if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException("The local Emby API URL is unavailable.");
            return baseUrl.TrimEnd('/') + "/TuneIn/CategoryArtwork/" + category + "?v=" + (category == ChannelArtwork ? LibraryRevision : Revision);
        }

        // Music genre folders, including music genres listed under a language, use pack artwork when installed.
        internal static string GenreId(NavigationEntry entry)
        {
            if (entry?.Request == null || entry.StationId != null) return null;
            if (entry.Category != "music" && entry.ContentCategory != "music" && entry.MusicSelectionId == null) return null;
            return entry.Request.Parameters.TryGetValue("id", out var id) && GenreFile(id) != null ? id : null;
        }

        internal static string GenreFile(string genreId)
        {
            var folder = GenreFolder;
            if (string.IsNullOrEmpty(folder) || genreId == null || !GenrePattern.IsMatch(genreId)) return null;
            return GenreExtensions.Select(extension => Path.Combine(folder, genreId + extension)).FirstOrDefault(File.Exists);
        }

        // Replacing or removing a pack image changes the revision, so the next refresh updates Emby's cached folder image.
        internal static string GenreRevision(string genreId)
        {
            var path = GenreFile(genreId);
            if (path == null) return "missing";
            var file = new FileInfo(path);
            return file.LastWriteTimeUtc.Ticks.ToString("x") + file.Length.ToString("x") + Path.GetExtension(path).Substring(1);
        }

        internal static int CountGenreFiles()
        {
            var folder = GenreFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return 0;
            try
            {
                return Directory.EnumerateFiles(folder).Select(Path.GetFileName)
                    .Count(name => GenreExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                        && GenrePattern.IsMatch(name.Substring(0, name.Length - extension.Length))));
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        internal static string GenreUrl(string baseUrl, string genreId)
        {
            if (GenreFile(genreId) == null) throw new ArgumentException("Unknown TuneIn genre artwork.", nameof(genreId));
            if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException("The local Emby API URL is unavailable.");
            return baseUrl.TrimEnd('/') + "/TuneIn/GenreArtwork/" + genreId + "?v=" + GenreRevision(genreId);
        }
        internal static Stream Open(string category) =>
            typeof(TuneInCategoryArtwork).Assembly.GetManifestResourceStream(ResourceName(category))
            ?? throw new InvalidOperationException("Bundled TuneIn category artwork is missing.");

        private static string ResourceName(string category)
        {
            if (category == ChannelArtwork) return "MediaBrowser.Plugins.TuneIn.Images.tunein-library.png";
            if (!Categories.Contains(category, StringComparer.Ordinal))
                throw new ArgumentException("Unknown TuneIn artwork category.", nameof(category));
            var name = category == "local" ? "localradio" : category == "language" ? "bylanguage"
                : category == "location" ? "bylocation" : category == "presets" ? "myfavs"
                : category == "podcast" ? "podcasts" : category;
            return "MediaBrowser.Plugins.TuneIn.Images.tunein-" + name + ".png";
        }
    }
}
