using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MediaBrowser.Plugins.TuneIn
{
    internal static class TuneInArtworkArchive
    {
        internal const long MaxUploadBytes = 768L * 1024 * 1024;
        internal const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
        internal const long MaxImageBytes = 16L * 1024 * 1024;
        private const string ManifestName = "tunein-artwork.json";

        internal static TuneInUploadManifest ValidateAndExtract(string archivePath, string destination,
            Action<string, CancellationToken> validateImage, CancellationToken token)
        {
            if (validateImage == null) throw new ArgumentNullException(nameof(validateImage));
            using (var stream = File.OpenRead(archivePath))
            {
                var directory = ReadDirectory(stream, token);
                stream.Position = 0;
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
                {
                    if (zip.Entries.Count != directory.Count) throw BadArchive();
                    var entries = zip.Entries.ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
                    if (!entries.TryGetValue(ManifestName, out var manifestEntry))
                        throw new InvalidDataException("Artwork manifest is missing.");
                    if (manifestEntry.Length > TuneInCategoryArtwork.MaxPackManifestBytes)
                        throw new InvalidDataException("Artwork manifest exceeds 4 MiB.");
                    TuneInUploadManifest manifest;
                    using (var manifestStream = manifestEntry.Open()) manifest = TuneInUploadManifest.Read(manifestStream);
                    if (entries.Count != manifest.Images.Count + 1)
                        throw new InvalidDataException("Artwork archive inventory does not match the manifest.");
                    long expanded = manifestEntry.Length;
                    foreach (var image in manifest.Images)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!entries.TryGetValue(image.File, out var entry))
                            throw new InvalidDataException("Artwork archive is missing a declared image.");
                        if (entry.Length < 1 || entry.Length > MaxImageBytes || expanded > MaxExpandedBytes - entry.Length)
                            throw new InvalidDataException("Artwork archive exceeds an expanded size limit.");
                        expanded += entry.Length;
                        var path = Path.Combine(destination, image.File);
                        using (var input = entry.Open())
                        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        using (var hash = SHA256.Create())
                        {
                            var buffer = new byte[81920];
                            long actual = 0;
                            int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                token.ThrowIfCancellationRequested();
                                actual += count;
                                if (actual > entry.Length || actual > MaxImageBytes)
                                    throw new InvalidDataException("Artwork image exceeds its declared size.");
                                hash.TransformBlock(buffer, 0, count, buffer, 0);
                                output.Write(buffer, 0, count);
                            }
                            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            if (actual != entry.Length || !string.Equals(
                                BitConverter.ToString(hash.Hash).Replace("-", ""), image.Sha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Artwork image hash or size does not match the manifest.");
                        }
                        validateImage(path, token);
                    }
                    var manifestPath = Path.Combine(destination, ManifestName);
                    using (var input = manifestEntry.Open())
                    using (var output = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                    return manifest;
                }
            }
        }

        private static List<Record> ReadDirectory(Stream stream, CancellationToken token)
        {
            if (stream.Length < 22 || stream.Length > MaxUploadBytes) throw BadArchive();
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                stream.Position = stream.Length - 22;
                if (reader.ReadUInt32() != 0x06054b50 || reader.ReadUInt16() != 0 || reader.ReadUInt16() != 0)
                    throw BadArchive();
                var diskCount = reader.ReadUInt16();
                var total = reader.ReadUInt16();
                var centralSize = reader.ReadUInt32();
                var centralOffset = reader.ReadUInt32();
                var comment = reader.ReadUInt16();
                if (comment != 0 || diskCount != total || total == 0 || total > TuneInUploadManifest.MaxImages + 1
                    || (long)centralOffset + centralSize != stream.Length - 22) throw BadArchive();
                stream.Position = centralOffset;
                var records = new List<Record>(total);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.ReadUInt32() != 0x02014b50) throw BadArchive();
                    var madeBy = reader.ReadUInt16();
                    reader.ReadUInt16();
                    var flags = reader.ReadUInt16();
                    var method = reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    var crc = reader.ReadUInt32();
                    var compressed = reader.ReadUInt32();
                    var expanded = reader.ReadUInt32();
                    var nameLength = reader.ReadUInt16();
                    var extraLength = reader.ReadUInt16();
                    var commentLength = reader.ReadUInt16();
                    var disk = reader.ReadUInt16();
                    reader.ReadUInt16();
                    var attributes = reader.ReadUInt32();
                    var offset = reader.ReadUInt32();
                    if ((flags & ~0x808) != 0 || (method != 0 && method != 8) || disk != 0
                        || extraLength != 0 || commentLength != 0 || nameLength == 0 || nameLength > 128
                        || (attributes & 0x410) != 0) throw BadArchive();
                    var unixType = (attributes >> 16) & 0xf000;
                    if ((madeBy >> 8) == 3 && unixType != 0 && unixType != 0x8000) throw BadArchive();
                    var nameBytes = reader.ReadBytes(nameLength);
                    if (nameBytes.Length != nameLength || nameBytes.Any(value => value < 0x21 || value > 0x7e))
                        throw BadArchive();
                    var name = Encoding.ASCII.GetString(nameBytes);
                    if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !names.Add(name))
                        throw BadArchive();
                    records.Add(new Record { Name = name, Offset = offset, Compressed = compressed,
                        Expanded = expanded, Flags = flags, Method = method, Crc = crc });
                }
                if (stream.Position != centralOffset + centralSize) throw BadArchive();
                long next = 0;
                foreach (var record in records.OrderBy(value => value.Offset))
                {
                    token.ThrowIfCancellationRequested();
                    if (record.Offset != next) throw BadArchive();
                    stream.Position = record.Offset;
                    if (reader.ReadUInt32() != 0x04034b50) throw BadArchive();
                    reader.ReadUInt16();
                    var flags = reader.ReadUInt16();
                    var method = reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    var crc = reader.ReadUInt32();
                    var compressed = reader.ReadUInt32();
                    var expanded = reader.ReadUInt32();
                    var nameLength = reader.ReadUInt16();
                    var extraLength = reader.ReadUInt16();
                    if (flags != record.Flags || method != record.Method || extraLength != 0
                        || nameLength != record.Name.Length
                        || Encoding.ASCII.GetString(reader.ReadBytes(nameLength)) != record.Name) throw BadArchive();
                    if ((flags & 8) == 0 && (crc != record.Crc || compressed != record.Compressed || expanded != record.Expanded))
                        throw BadArchive();
                    next = stream.Position + record.Compressed;
                    if (next > centralOffset) throw BadArchive();
                    if ((flags & 8) != 0)
                    {
                        stream.Position = next;
                        var first = reader.ReadUInt32();
                        if (first == 0x08074b50) { first = reader.ReadUInt32(); next += 4; }
                        if (first != record.Crc || reader.ReadUInt32() != record.Compressed || reader.ReadUInt32() != record.Expanded)
                            throw BadArchive();
                        next += 12;
                    }
                }
                if (next != centralOffset) throw BadArchive();
                return records;
            }
        }

        private static InvalidDataException BadArchive() => new InvalidDataException("Unsafe or unsupported artwork ZIP structure.");

        private sealed class Record
        {
            internal string Name;
            internal long Offset;
            internal uint Compressed;
            internal uint Expanded;
            internal ushort Flags;
            internal ushort Method;
            internal uint Crc;
        }
    }
}
