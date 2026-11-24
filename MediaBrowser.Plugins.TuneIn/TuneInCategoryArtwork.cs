using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
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

    [Route("/TuneIn/PackArtwork/{Scope}/{Id}", "GET")]
    [Authenticated(AllowLocalOnly = true)]
    public sealed class GetTuneInPackArtwork
    {
        public string Scope { get; set; }
        public string Id { get; set; }
    }

    // The settings page loads this from the administrator's browser, so it cannot be loopback-only.
    [Route("/TuneIn/LibraryArtwork", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInLibraryArtwork
    {
    }

    [Route("/TuneIn/PanelArtwork", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInPanelArtwork
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

        public object Get(GetTuneInCategoryArtwork request)
        {
            lock (TuneInCategoryArtwork.PackGate)
            {
                var path = TuneInCategoryArtwork.PackFile("categories", request.Category);
                return Respond(() => path == null ? TuneInCategoryArtwork.OpenBundled(request.Category) : File.OpenRead(path),
                    path != null && path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png");
            }
        }

        public object Get(GetTuneInGenreArtwork request)
        {
            lock (TuneInCategoryArtwork.PackGate)
            {
                var path = TuneInCategoryArtwork.LegacyGenreFile(request.GenreId)
                    ?? throw new ArgumentException("Unknown TuneIn genre artwork.", nameof(request));
                return Respond(() => File.OpenRead(path), path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            }
        }

        public object Get(GetTuneInPackArtwork request)
        {
            lock (TuneInCategoryArtwork.PackGate)
            {
                var path = TuneInCategoryArtwork.PackFile(request.Scope, request.Id)
                    ?? throw new ArgumentException("Unknown TuneIn pack artwork.", nameof(request));
                return Respond(() => File.OpenRead(path), path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            }
        }

        public object Get(GetTuneInLibraryArtwork request) =>
            Respond(() => TuneInCategoryArtwork.Open(TuneInCategoryArtwork.ChannelArtwork), "image/png", false);

        public object Get(GetTuneInPanelArtwork request) =>
            Respond(TuneInCategoryArtwork.OpenPanel, "image/png", false);

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
        internal const int MaxPackManifestBytes = 4 * 1024 * 1024;
        private static readonly string[] Categories = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };
        internal static readonly string[] Scopes = { "genres", "languages", "locations", "categories" };
        internal static readonly object PackGate = new object();
        private static readonly string[] Extensions = { ".jpg", ".png" };

        // Optional artwork packs (for example from oconnoradv/EmbyTuneInArt) are extracted here as <guide id>.jpg or .png.
        internal static string GenreFolder { get; set; }
        internal static string LanguageFolder { get; set; }
        internal static string LocationFolder { get; set; }
        internal static string CategoryFolder { get; set; }

        internal static string Folder(string scope) => scope == "genres" ? GenreFolder
            : scope == "languages" ? LanguageFolder : scope == "locations" ? LocationFolder
            : scope == "categories" ? CategoryFolder : null;

        internal static bool ValidId(string scope, string id) =>
            scope == "genres" ? ConfigurationSnapshot.ValidId(id, "music")
            : scope == "languages" ? ConfigurationSnapshot.ValidId(id, "language")
            : scope == "locations" ? ConfigurationSnapshot.ValidId(id, "location")
            : scope == "categories" && Categories.Contains(id, StringComparer.Ordinal);

        internal static string Url(string baseUrl, string category)
        {
            ResourceName(category);
            if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException("The local Emby API URL is unavailable.");
            var version = category == ChannelArtwork ? LibraryRevision : PackRevision("categories", category);
            if (version == "missing") version = Revision;
            return baseUrl.TrimEnd('/') + "/TuneIn/CategoryArtwork/" + category + "?v=" + version;
        }

        // Music genre folders, including music genres listed under a language, use pack artwork when installed.
        internal static string GenreId(NavigationEntry entry)
        {
            if (entry?.Request == null || entry.StationId != null) return null;
            if (entry.Category != "music" && entry.ContentCategory != "music" && entry.MusicSelectionId == null) return null;
            return entry.Request.Parameters.TryGetValue("id", out var id) && GenreFile(id) != null ? id : null;
        }

        internal static (string Scope, string Id) ArtworkFor(NavigationEntry entry)
        {
            if (entry?.Request == null || entry.StationId != null) return (null, null);
            var id = entry.Request.Parameters.GetValueOrDefault("id");
            string scope = null;
            if (entry.Category == "language" && entry.LanguageArtworkId != null)
            {
                scope = "languages";
                id = entry.LanguageArtworkId;
            }
            else if (entry.Category == "music" || entry.Category == "language"
                && (entry.ContentCategory == "music" || entry.MusicSelectionId != null))
                scope = "genres";
            else if (entry.Category == "location")
                scope = "locations";
            else if (entry.Category == "language" && entry.Request.Parameters.GetValueOrDefault("c") == "lang"
                && id == null && (entry.ContentCategory == null || entry.ContentCategory == "language"))
            {
                scope = "languages";
                id = entry.SelectionId ?? entry.Request.Parameters.GetValueOrDefault("filter");
            }
            return PackFile(scope, id) == null ? (null, null) : (scope, id);
        }

        internal static string GenreFile(string genreId) => PackFile("genres", genreId);

        internal static string LegacyGenreFile(string genreId)
        {
            lock (PackGate)
            {
                if (string.IsNullOrEmpty(GenreFolder) || genreId == null
                    || !Regex.IsMatch(genreId, @"\A[cg][0-9]{1,12}\z")) return null;
                return Extensions.Select(extension => Path.Combine(GenreFolder, genreId + extension)).FirstOrDefault(File.Exists);
            }
        }

        internal static string PackFile(string scope, string id)
        {
            lock (PackGate)
            {
                var folder = Folder(scope);
                if (string.IsNullOrEmpty(folder) || !ValidId(scope, id)) return null;
                return Extensions.Select(extension => Path.Combine(folder, id + extension)).FirstOrDefault(File.Exists);
            }
        }

        internal static string GenreRevision(string genreId) => PackRevision("genres", genreId);

        internal static string PackRevision(string scope, string id)
        {
            lock (PackGate)
            {
                var path = PackFile(scope, id);
                if (path == null) return "missing";
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant()
                        + Path.GetExtension(path).Substring(1);
            }
        }

        internal static int CountGenreFiles() => ReadPackStatus("genres").InstalledCount ?? 0;

        internal static TuneInArtworkPackStatus ReadPackManifest() => ReadPackStatus("genres");

        internal static TuneInArtworkPackStatus ReadPackStatus(string scope)
        {
            lock (PackGate) return ReadPackStatusLocked(scope);
        }

        private static TuneInArtworkPackStatus ReadPackStatusLocked(string scope)
        {
            if (!Scopes.Contains(scope, StringComparer.Ordinal)) throw new ArgumentException("Unknown artwork scope.", nameof(scope));
            var folder = Folder(scope);
            var result = new TuneInArtworkPackStatus { Scope = scope, Path = folder, Status = "unavailable" };
            if (string.IsNullOrEmpty(folder)) return result;
            try
            {
                var files = Directory.EnumerateFiles(folder).Where(path =>
                {
                    var name = Path.GetFileName(path);
                    var extension = Path.GetExtension(name);
                    return Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                        && ValidId(scope, Path.GetFileNameWithoutExtension(name));
                }).ToList();
                result.InstalledCount = files.Count;
                TuneInUploadManifest manifest;
                try
                {
                    using (var stream = File.OpenRead(Path.Combine(folder, "tunein-artwork.json")))
                    {
                        if (stream.Length > MaxPackManifestBytes) { result.Status = "invalid"; return result; }
                        manifest = TuneInUploadManifest.Read(stream);
                    }
                }
                catch (FileNotFoundException) { result.Status = files.Count == 0 ? "missing" : "unsupported"; return result; }
                if (manifest.Scope != scope) { result.Status = "invalid"; return result; }
                var entries = manifest.Images.ToDictionary(image => image.Id, StringComparer.OrdinalIgnoreCase);
                var matched = 0;
                var modified = 0;
                var added = 0;
                using (var sha = SHA256.Create())
                {
                    foreach (var file in files)
                    {
                        if (!entries.TryGetValue(Path.GetFileNameWithoutExtension(file), out var image)) { added++; continue; }
                        using (var stream = File.OpenRead(file))
                        {
                            var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                            if (string.Equals(Path.GetFileName(file), image.File, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(hash, image.Sha256, StringComparison.OrdinalIgnoreCase)) matched++;
                            else modified++;
                        }
                    }
                }
                result.MatchedCount = matched;
                result.ModifiedCount = modified;
                result.AddedCount = added;
                result.Status = "identified";
                result.Name = manifest.Name;
                result.PackId = manifest.PackId;
                result.Theme = manifest.Theme;
                result.Version = manifest.Version;
                result.ImageCount = manifest.Images.Count;
                return result;
            }
            catch (DirectoryNotFoundException) { result.Status = "missing"; result.InstalledCount = 0; return result; }
            catch (NotSupportedException) { result.Status = "unsupported"; return result; }
            catch (InvalidDataException) { result.Status = "invalid"; return result; }
            catch (IOException) { result.Status = "unavailable"; return result; }
            catch (UnauthorizedAccessException) { result.Status = "unavailable"; return result; }
        }

        internal static string PackUrl(string baseUrl, string scope, string id)
        {
            if (scope == "genres") return GenreUrl(baseUrl, id);
            if (PackFile(scope, id) == null) throw new ArgumentException("Unknown TuneIn pack artwork.", nameof(id));
            if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException("The local Emby API URL is unavailable.");
            return baseUrl.TrimEnd('/') + "/TuneIn/PackArtwork/" + scope + "/" + id + "?v=" + PackRevision(scope, id);
        }

        internal static string GenreUrl(string baseUrl, string genreId)
        {
            if (GenreFile(genreId) == null) throw new ArgumentException("Unknown TuneIn genre artwork.", nameof(genreId));
            if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException("The local Emby API URL is unavailable.");
            return baseUrl.TrimEnd('/') + "/TuneIn/GenreArtwork/" + genreId + "?v=" + GenreRevision(genreId);
        }
        internal static Stream Open(string category)
        {
            lock (PackGate)
            {
                var resource = ResourceName(category);
                var path = PackFile("categories", category);
                return path != null ? File.OpenRead(path) : OpenBundledResource(resource);
            }
        }

        internal static Stream OpenBundled(string category) => OpenBundledResource(ResourceName(category));

        private static Stream OpenBundledResource(string resource) =>
            typeof(TuneInCategoryArtwork).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Bundled TuneIn category artwork is missing.");

        internal static Stream OpenPanel() =>
            typeof(TuneInCategoryArtwork).Assembly.GetManifestResourceStream("MediaBrowser.Plugins.TuneIn.Images.tunein-panel.png")
            ?? throw new InvalidOperationException("Bundled TuneIn panel artwork is missing.");

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
