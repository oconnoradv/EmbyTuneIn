using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;

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

        public object Get(GetTuneInCategoryArtwork request) =>
            Respond(() => TuneInCategoryArtwork.Open(request.Category), "image/png");

        public object Get(GetTuneInGenreArtwork request)
        {
            var path = TuneInCategoryArtwork.LegacyGenreFile(request.GenreId)
                ?? throw new ArgumentException("Unknown TuneIn genre artwork.", nameof(request));
            return Respond(() => File.OpenRead(path), path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
        }

        public object Get(GetTuneInPackArtwork request)
        {
            var path = TuneInCategoryArtwork.PackFile(request.Scope, request.Id)
                ?? throw new ArgumentException("Unknown TuneIn pack artwork.", nameof(request));
            return Respond(() => File.OpenRead(path), path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
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
        private static readonly string[] Categories = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };
        internal static readonly string[] Scopes = { "genres", "languages", "locations" };
        private static readonly string[] Extensions = { ".jpg", ".png" };

        // Optional artwork packs (for example from oconnoradv/EmbyTuneInArt) are extracted here as <guide id>.jpg or .png.
        internal static string GenreFolder { get; set; }
        internal static string LanguageFolder { get; set; }
        internal static string LocationFolder { get; set; }

        private static string Folder(string scope) => scope == "genres" ? GenreFolder
            : scope == "languages" ? LanguageFolder : scope == "locations" ? LocationFolder : null;

        private static bool ValidId(string scope, string id) =>
            scope == "genres" ? ConfigurationSnapshot.ValidId(id, "music")
            : scope == "languages" ? ConfigurationSnapshot.ValidId(id, "language")
            : scope == "locations" && ConfigurationSnapshot.ValidId(id, "location");

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
            if (string.IsNullOrEmpty(GenreFolder) || genreId == null
                || !Regex.IsMatch(genreId, @"\A[cg][0-9]{1,12}\z")) return null;
            return Extensions.Select(extension => Path.Combine(GenreFolder, genreId + extension)).FirstOrDefault(File.Exists);
        }

        internal static string PackFile(string scope, string id)
        {
            var folder = Folder(scope);
            if (string.IsNullOrEmpty(folder) || !ValidId(scope, id)) return null;
            return Extensions.Select(extension => Path.Combine(folder, id + extension)).FirstOrDefault(File.Exists);
        }

        internal static string GenreRevision(string genreId) => PackRevision("genres", genreId);

        internal static string PackRevision(string scope, string id)
        {
            var path = PackFile(scope, id);
            if (path == null) return "missing";
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant()
                    + Path.GetExtension(path).Substring(1);
        }

        internal static int CountGenreFiles() => ReadPackStatus("genres").InstalledCount ?? 0;

        [DataContract]
        private sealed class PackManifest
        {
            [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
            [DataMember(Name = "scope")] public string Scope { get; set; }
            [DataMember(Name = "packName")] public string PackName { get; set; }
            [DataMember(Name = "imageCount")] public int? ImageCount { get; set; }
            [DataMember(Name = "images")] public List<PackImage> Images { get; set; }
        }

        [DataContract]
        private sealed class PackImage
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "file")] public string File { get; set; }
            [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        }

        internal static TuneInArtworkPackStatus ReadPackManifest() => ReadPackStatus("genres");

        internal static TuneInArtworkPackStatus ReadPackStatus(string scope)
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
                PackManifest manifest;
                try
                {
                    using (var stream = File.OpenRead(Path.Combine(folder, "tunein-artwork.json")))
                    {
                        if (stream.Length > 1024 * 1024) { result.Status = "invalid"; return result; }
                        manifest = (PackManifest)new DataContractJsonSerializer(typeof(PackManifest)).ReadObject(stream);
                    }
                }
                catch (FileNotFoundException) { result.Status = "missing"; return result; }
                if (manifest == null || manifest.SchemaVersion != 1
                    || (manifest.Scope == null ? scope != "genres" : manifest.Scope != scope)
                    || string.IsNullOrWhiteSpace(manifest.PackName) || manifest.PackName.Length > 128
                    || manifest.ImageCount < 0 || manifest.ImageCount > 100000
                    || manifest.Scope != null && (manifest.Images == null || !manifest.ImageCount.HasValue))
                { result.Status = "invalid"; return result; }
                if (manifest.Images != null)
                {
                    var entries = new Dictionary<string, PackImage>(StringComparer.OrdinalIgnoreCase);
                    foreach (var image in manifest.Images)
                    {
                        if (image == null || !ValidId(scope, image.Id) || image.File == null
                            || !Extensions.Any(extension => string.Equals(image.File, image.Id + extension, StringComparison.Ordinal))
                            || image.Sha256 == null || !Regex.IsMatch(image.Sha256, @"\A[0-9a-fA-F]{64}\z")
                            || entries.ContainsKey(image.Id))
                        { result.Status = "invalid"; return result; }
                        entries.Add(image.Id, image);
                    }
                    if (manifest.ImageCount.HasValue && manifest.ImageCount.Value != entries.Count)
                    { result.Status = "invalid"; return result; }
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
                }
                result.Status = "identified";
                result.Name = manifest.PackName.Trim();
                result.ImageCount = manifest.ImageCount;
                return result;
            }
            catch (DirectoryNotFoundException) { result.Status = "missing"; result.InstalledCount = 0; return result; }
            catch (SerializationException) { result.Status = "invalid"; return result; }
            catch (XmlException) { result.Status = "invalid"; return result; }
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
        internal static Stream Open(string category) =>
            typeof(TuneInCategoryArtwork).Assembly.GetManifestResourceStream(ResourceName(category))
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
