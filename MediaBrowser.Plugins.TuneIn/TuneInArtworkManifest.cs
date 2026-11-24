using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInUploadManifest
    {
        internal const int MaxImages = 20000;
        internal const string LegacySchemaNotice = "This artwork pack uses schema 1, which is no longer supported. Please regenerate or download a newer pack using schemaVersion 2. Existing installed artwork is unchanged.";
        internal string Scope { get; private set; }
        internal string PackId { get; private set; }
        internal string Name { get; private set; }
        internal string Theme { get; private set; }
        internal string Version { get; private set; }
        internal IReadOnlyList<TuneInUploadImage> Images { get; private set; }
        internal Dictionary<string, string> SupplementalFiles { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        internal static TuneInUploadManifest Read(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using (var buffer = new MemoryStream())
            {
                var bytes = new byte[8192];
                int count;
                while ((count = stream.Read(bytes, 0, bytes.Length)) != 0)
                {
                    if (buffer.Length + count > TuneInCategoryArtwork.MaxPackManifestBytes)
                        throw new InvalidDataException("Artwork manifest exceeds 4 MiB.");
                    buffer.Write(bytes, 0, count);
                }
                var content = buffer.ToArray();
                string json;
                try { json = new UTF8Encoding(false, true).GetString(content); }
                catch (DecoderFallbackException error) { throw new InvalidDataException("Artwork manifest is not UTF-8.", error); }
                RejectLegacySchema(content);
                var parser = new ManifestParser(json);
                return parser.Parse();
            }
        }

        private static void RejectLegacySchema(byte[] content)
        {
            var legacy = false;
            try
            {
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(content, new XmlDictionaryReaderQuotas
                {
                    MaxDepth = 64, MaxStringContentLength = TuneInCategoryArtwork.MaxPackManifestBytes,
                    MaxArrayLength = TuneInCategoryArtwork.MaxPackManifestBytes,
                    MaxBytesPerRead = 8192, MaxNameTableCharCount = 16384
                }))
                {
                    var rootIsObject = reader.MoveToContent() == XmlNodeType.Element && reader.GetAttribute("type") == "object";
                    while (reader.Read())
                    {
                        // Escaped JSON property names are mapped to an item attribute.
                        if (rootIsObject && reader.NodeType == XmlNodeType.Element && reader.Depth == 1
                            && (reader.GetAttribute("item") ?? reader.LocalName) == "schemaVersion" && reader.GetAttribute("type") == "number"
                            && reader.ReadString() == "1")
                            legacy = true;
                    }
                }
            }
            catch (XmlException error) { throw new InvalidDataException("Malformed artwork manifest.", error); }
            if (legacy) throw new NotSupportedException(LegacySchemaNotice);
        }

        private sealed class ManifestParser
        {
            private readonly string text;
            private int position;

            internal ManifestParser(string text) { this.text = text; }

            internal TuneInUploadManifest Parse()
            {
                var root = Object(new[] { "schemaVersion", "scope", "packId", "packName", "theme", "version", "imageCount", "images" }, true);
                Space();
                if (position != text.Length) throw Invalid();
                var scope = (string)root["scope"];
                if (!TuneInCategoryArtwork.Scopes.Contains(scope, StringComparer.Ordinal))
                    throw new InvalidDataException("Unsupported artwork scope.");
                var name = (string)root["packName"];
                var theme = (string)root["theme"];
                if (!ValidDisplayName(name) || !ValidDisplayName(theme))
                    throw new InvalidDataException("Invalid artwork pack name or theme.");
                var packId = (string)root["packId"];
                var version = (string)root["version"];
                if (!Regex.IsMatch(packId, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")
                    || !Regex.IsMatch(version, @"\A[0-9A-Za-z][0-9A-Za-z.+_-]{0,63}\z"))
                    throw new InvalidDataException("Invalid artwork pack ID or version.");
                var images = (List<TuneInUploadImage>)root["images"];
                var imageCount = (int)root["imageCount"];
                if (imageCount < 1 || imageCount > MaxImages || imageCount != images.Count)
                    throw new InvalidDataException("Artwork image count does not match records.");
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var image in images)
                {
                    if (!TuneInCategoryArtwork.ValidId(scope, image.Id) || !ids.Add(image.Id))
                        throw new InvalidDataException("Invalid or duplicate artwork ID.");
                    if (!(image.File == image.Id + ".jpg" || image.File == image.Id + ".png") || !files.Add(image.File))
                        throw new InvalidDataException("Invalid or duplicate artwork filename.");
                    if (!Regex.IsMatch(image.Sha256, @"\A[0-9a-fA-F]{64}\z"))
                        throw new InvalidDataException("Invalid artwork SHA256.");
                }
                return new TuneInUploadManifest { Scope = scope, PackId = packId, Name = name, Theme = theme, Version = version, Images = images };
            }

            private static bool ValidDisplayName(string value) =>
                !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value == value.Trim()
                && !value.Any(char.IsControl) && !value.Contains(":") && !value.Contains("\\")
                && !value.Contains("/") && !value.Contains("@")
                && value.IndexOf("www.", StringComparison.OrdinalIgnoreCase) < 0;

            private Dictionary<string, object> Object(string[] required, bool root)
            {
                Take('{');
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                Space();
                if (Peek() != '}')
                {
                    while (true)
                    {
                        var key = String();
                        if (!required.Contains(key, StringComparer.Ordinal) || result.ContainsKey(key))
                            throw new InvalidDataException("Unexpected or duplicate artwork manifest field. Use a canonical schemaVersion 2 pack; regenerate or download a new pack.");
                        Take(':');
                        object value;
                        if (root && (key == "schemaVersion" || key == "imageCount")) value = Number();
                        else if (root && key == "images") value = Images();
                        else value = String();
                        if (root && key == "schemaVersion" && (int)value != 2)
                            throw new NotSupportedException("Artwork packs require schemaVersion 2. Legacy packs are unsupported; regenerate or download a canonical schema 2 pack.");
                        result.Add(key, value);
                        Space();
                        if (Peek() != ',') break;
                        position++;
                    }
                }
                Take('}');
                if (required.Any(key => !result.ContainsKey(key))) throw new InvalidDataException("Canonical schemaVersion 2 artwork manifest is missing a required field. Regenerate or download a complete pack.");
                return result;
            }

            private List<TuneInUploadImage> Images()
            {
                Take('[');
                var images = new List<TuneInUploadImage>();
                Space();
                if (Peek() != ']')
                {
                    while (true)
                    {
                        if (images.Count == MaxImages) throw new InvalidDataException("Too many artwork images.");
                        var item = Object(new[] { "id", "file", "sha256" }, false);
                        images.Add(new TuneInUploadImage { Id = (string)item["id"], File = (string)item["file"], Sha256 = (string)item["sha256"] });
                        Space();
                        if (Peek() != ',') break;
                        position++;
                    }
                }
                Take(']');
                return images;
            }

            private int Number()
            {
                Space();
                var start = position;
                while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++;
                if (start == position || position - start > 1 && text[start] == '0'
                    || !int.TryParse(text.Substring(start, position - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    throw Invalid();
                return value;
            }

            private string String()
            {
                Take('"');
                var result = new StringBuilder();
                while (position < text.Length)
                {
                    var c = text[position++];
                    if (c == '"')
                    {
                        var value = result.ToString();
                        for (var i = 0; i < value.Length; i++)
                        {
                            if (char.IsHighSurrogate(value[i]))
                            {
                                if (++i == value.Length || !char.IsLowSurrogate(value[i])) throw Invalid();
                            }
                            else if (char.IsLowSurrogate(value[i])) throw Invalid();
                        }
                        return value;
                    }
                    if (c < 0x20) throw Invalid();
                    if (c == '\\')
                    {
                        if (position == text.Length) throw Invalid();
                        c = text[position++];
                        switch (c)
                        {
                            case '"': case '\\': case '/': break;
                            case 'b': c = '\b'; break;
                            case 'f': c = '\f'; break;
                            case 'n': c = '\n'; break;
                            case 'r': c = '\r'; break;
                            case 't': c = '\t'; break;
                            case 'u':
                                if (position + 4 > text.Length || !ushort.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) throw Invalid();
                                position += 4;
                                c = (char)code;
                                break;
                            default: throw Invalid();
                        }
                    }
                    result.Append(c);
                }
                throw Invalid();
            }

            private void Space()
            {
                while (position < text.Length && (text[position] == ' ' || text[position] == '\r' || text[position] == '\n' || text[position] == '\t')) position++;
            }

            private char Peek() => position < text.Length ? text[position] : '\0';
            private void Take(char expected)
            {
                Space();
                if (Peek() != expected) throw Invalid();
                position++;
            }
            private static InvalidDataException Invalid() => new InvalidDataException("Malformed artwork manifest.");
        }
    }

    internal sealed class TuneInUploadImage
    {
        internal string Id { get; set; }
        internal string File { get; set; }
        internal string Sha256 { get; set; }
    }
}
