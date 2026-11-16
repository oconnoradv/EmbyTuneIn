using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class ArtworkResult
    {
        public byte[] Bytes { get; set; }
        public string ContentType { get; set; }
    }

    internal static class TuneInArtwork
    {
        private static readonly string[] Hosts = { "cdn-profiles.tunein.com", "cdn-radiotime-logos.tunein.com" };

        public static bool TryReference(string value, out string reference)
        {
            reference = null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https") || !uri.IsDefaultPort
                || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.PathAndQuery.Length > 1024)
                return false;
            var index = Array.FindIndex(Hosts, host => host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            reference = index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + uri.PathAndQuery;
            return true;
        }

        private static Uri Destination(string reference)
        {
            if (reference == null || reference.Length > 1026 || reference.Length < 4
                || reference[1] != ':' || reference[2] != '/' || reference[0] < '0' || reference[0] > '1')
                throw new InvalidDataException("Invalid TuneIn artwork reference.");
            var uri = PublicAddressPolicy.ValidateUri("https://" + Hosts[reference[0] - '0'] + reference.Substring(2), false);
            ValidateDestination(uri);
            return uri;
        }

        private static void ValidateDestination(Uri uri)
        {
            if (uri.Scheme != "https" || uri.Port != 443
                || !Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Untrusted TuneIn artwork destination rejected.");
        }

        public static void ValidateReference(string reference) => Destination(reference);

        internal static string Category(NavigationEntry entry) =>
            entry.ContentCategory == "music" || entry.ContentCategory == "talk" || entry.ContentCategory == "sports"
                ? entry.ContentCategory : entry.Category;

        internal static async Task<ArtworkResult> BrandingAsync(CancellationToken token)
        {
            using (var stream = typeof(TuneInChannel).Assembly.GetManifestResourceStream(
                "MediaBrowser.Plugins.TuneIn.Images.primary.png")
                ?? throw new InvalidOperationException("Bundled TuneIn library artwork is missing."))
                return new ArtworkResult
                {
                    Bytes = await TuneInDirectory.ReadBoundedAsync(stream, 2 * 1024 * 1024, token).ConfigureAwait(false),
                    ContentType = "image/png"
                };
        }

        public static async Task<ArtworkResult> OpenAsync(NavigationEntry entry, SafeHttpTransport transport, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (entry.ArtworkReference == null)
            {
                var category = Category(entry);
                var name = category == "local" ? "localradio" : category == "language" ? "bylanguage"
                    : category == "location" ? "bylocation" : category == "presets" ? "myfavs"
                    : category == "podcast" ? "podcasts" : category;
                if (!new[] { "localradio", "bylanguage", "bylocation", "myfavs", "podcasts", "music", "talk", "sports" }.Contains(name))
                    throw new InvalidDataException("Unknown artwork category.");
                using (var stream = typeof(TuneInChannel).Assembly.GetManifestResourceStream(
                    "MediaBrowser.Plugins.TuneIn.Images.tunein-" + name + ".png")
                    ?? throw new InvalidOperationException("Bundled TuneIn group artwork is missing."))
                    return new ArtworkResult
                    {
                        Bytes = await TuneInDirectory.ReadBoundedAsync(stream, 2 * 1024 * 1024, token).ConfigureAwait(false),
                        ContentType = "image/png"
                    };
            }
            if (entry.StationId == null) throw new InvalidDataException("Artwork reference requires a station.");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using (var response = await transport.OpenAsync(Destination(entry.ArtworkReference).AbsoluteUri,
                    false, timeout.Token, validateDestination: ValidateDestination).ConfigureAwait(false))
                {
                    var bytes = await TuneInDirectory.ReadBoundedAsync(response.Body, 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
                    ValidateRaster(bytes, response.ContentType);
                    return new ArtworkResult { Bytes = bytes, ContentType = response.ContentType };
                }
            }
        }

        internal static void ValidateRaster(byte[] bytes, string contentType)
        {
            if (contentType == "image/png" && bytes.Length >= 33
                && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                && bytes.Skip(12).Take(4).SequenceEqual(new byte[] { 73, 72, 68, 82 }))
            {
                var width = (long)bytes[16] << 24 | (long)bytes[17] << 16 | (long)bytes[18] << 8 | bytes[19];
                var height = (long)bytes[20] << 24 | (long)bytes[21] << 16 | (long)bytes[22] << 8 | bytes[23];
                ValidateDimensions(width, height);
                return;
            }
            if (contentType == "image/jpeg" && bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216)
            {
                var offset = 2;
                while (offset + 4 <= bytes.Length)
                {
                    if (bytes[offset++] != 255) break;
                    while (offset < bytes.Length && bytes[offset] == 255) offset++;
                    if (offset + 3 > bytes.Length) break;
                    var marker = bytes[offset++];
                    if (marker == 217 || marker == 218) break;
                    var length = bytes[offset] * 256 + bytes[offset + 1];
                    if (length < 2 || offset + length > bytes.Length) break;
                    if (new[] { 192, 193, 194 }.Contains(marker) && length >= 8)
                    {
                        ValidateDimensions(bytes[offset + 5] * 256 + bytes[offset + 6],
                            bytes[offset + 3] * 256 + bytes[offset + 4]);
                        return;
                    }
                    offset += length;
                }
            }
            throw new InvalidDataException("Unsupported or malformed TuneIn artwork; only bounded PNG/JPEG is accepted.");
        }

        private static void ValidateDimensions(long width, long height)
        {
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || width * height > 16 * 1024 * 1024)
                throw new InvalidDataException("TuneIn artwork dimensions exceed limits.");
        }
    }
}
