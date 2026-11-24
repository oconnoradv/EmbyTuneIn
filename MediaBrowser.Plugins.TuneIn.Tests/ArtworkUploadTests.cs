using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    [Collection("Artwork globals")]
    public class ArtworkUploadTests
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string Valid = "{\"schemaVersion\":2,\"scope\":\"categories\",\"packId\":\"test-pack\",\"packName\":\"Test\",\"theme\":\"Test\",\"version\":\"1\",\"imageCount\":1,\"images\":[{\"id\":\"talk\",\"file\":\"talk.jpg\",\"sha256\":\"" + Hash + "\"}]}";

        [Fact]
        public void StrictManifestAcceptsExactRecords()
        {
            var manifest = Read(Valid);
            Assert.Equal("categories", manifest.Scope);
            Assert.Equal("Test", manifest.Name);
            Assert.Equal("test-pack", manifest.PackId);
            Assert.Equal("Test", manifest.Theme);
            Assert.Equal("1", manifest.Version);
            Assert.Equal("talk.jpg", Assert.Single(manifest.Images).File);
        }

        [PublishedPackFact]
        public async Task PublishedPackPassesUploadInstallAndManifestChecks()
        {
            var destination = Path.Combine(Path.GetTempPath(), "tunein-published-pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destination);
            var previous = (TuneInCategoryArtwork.GenreFolder, TuneInCategoryArtwork.LanguageFolder,
                TuneInCategoryArtwork.LocationFolder, TuneInCategoryArtwork.CategoryFolder);
            try
            {
                var decoded = 0;
                var coordinator = new TuneInArtworkUploadCoordinator(destination, (path, token) =>
                {
                    var ffmpeg = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
                    if (!string.IsNullOrEmpty(ffmpeg)) TuneInArtworkImage.Validate(path, ffmpeg, null, token);
                    decoded++;
                }, (_, __) => TuneInArtworkScanner.Result.NotScanned, action => action(), () => { });
                using var input = File.OpenRead(Environment.GetEnvironmentVariable("TUNEIN_TEST_ARTWORK_PACK"));
                var pending = await coordinator.Prepare(input, CancellationToken.None);
                Assert.True(pending.ImageCount > 0);
                Assert.Equal(pending.ImageCount, decoded);
                await coordinator.Install(pending.Token, true, CancellationToken.None);
                TuneInCategoryArtwork.GenreFolder = Path.Combine(destination, "genres");
                TuneInCategoryArtwork.LanguageFolder = Path.Combine(destination, "languages");
                TuneInCategoryArtwork.LocationFolder = Path.Combine(destination, "locations");
                TuneInCategoryArtwork.CategoryFolder = Path.Combine(destination, "categories");
                var status = TuneInCategoryArtwork.ReadPackStatus(pending.Scope);
                Assert.Equal("identified", status.Status);
                Assert.Equal(pending.Name, status.Name);
                Assert.Equal(pending.PackId, status.PackId);
                Assert.Equal(pending.Theme, status.Theme);
                Assert.Equal(pending.Version, status.Version);
                Assert.Equal(pending.ImageCount, status.ImageCount);
                Assert.Equal(pending.ImageCount, status.MatchedCount);
                Assert.Equal(0, status.ModifiedCount);
                Assert.Equal(0, status.AddedCount);
            }
            finally
            {
                (TuneInCategoryArtwork.GenreFolder, TuneInCategoryArtwork.LanguageFolder,
                    TuneInCategoryArtwork.LocationFolder, TuneInCategoryArtwork.CategoryFolder) = previous;
                Directory.Delete(destination, true);
            }
        }

        private sealed class PublishedPackFactAttribute : FactAttribute
        {
            public PublishedPackFactAttribute()
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TUNEIN_TEST_ARTWORK_PACK")))
                    Skip = "Set TUNEIN_TEST_ARTWORK_PACK to check a published pack's upload, installation and manifest; optionally set TUNEIN_TEST_FFMPEG for decoding.";
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        public void UnsupportedSchemasExplainTheRequiredMigration(int schema)
        {
            var error = Assert.Throws<NotSupportedException>(() => Read(Valid.Replace("\"schemaVersion\":2", "\"schemaVersion\":" + schema)));
            Assert.Contains("schemaVersion 2", error.Message);
            Assert.Contains("regenerate or download", error.Message);
        }

        [Theory]
        [InlineData("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2")]
        [InlineData("\"scope\":\"categories\"", "\"scope\":\"other\"")]
        [InlineData("\"packName\":\"Test\"", "\"packName\":\"https://example.com\"")]
        [InlineData("\"packName\":\"Test\"", "\"packName\":\"www.example.com\"")]
        [InlineData("\"packName\":\"Test\"", "\"packName\":\"\\uD800\"")]
        [InlineData("\"imageCount\":1", "\"imageCount\":2")]
        [InlineData("\"file\":\"talk.jpg\"", "\"file\":\"../talk.jpg\"")]
        [InlineData("\"id\":\"talk\"", "\"id\":\"channel\"")]
        [InlineData("\"id\":\"talk\"", "\"id\":\"talk\",\"url\":\"https://example.com\"")]
        [InlineData("\"images\":", "\"unexpected\":1,\"images\":")]
        [InlineData("\"schemaVersion\":2", "\"schemaVersion\":null")]
        [InlineData("\"schemaVersion\":2", "\"schemaVersion\":\"1\"")]
        [InlineData("\"schemaVersion\":2", "\"SchemaVersion\":1")]
        [InlineData("\"scope\":\"categories\"", "\"scope\":null")]
        [InlineData("\"packName\":\"Test\"", "\"packName\":null")]
        [InlineData("\"imageCount\":1", "\"imageCount\":null")]
        [InlineData("\"images\":[", "\"images\":null,\"unused\":[")]
        [InlineData("\"id\":\"talk\"", "\"id\":null")]
        [InlineData("\"file\":\"talk.jpg\"", "\"file\":null")]
        [InlineData("\"sha256\":", "\"bytes\":null,\"sha256\":")]
        [InlineData("\"sha256\":", "\"sceneryShared\":null,\"sha256\":")]
        [InlineData("\"sha256\":\"" + Hash + "\"", "\"sha256\":null")]
        [InlineData("\"packId\":\"test-pack\"", "\"packId\":null")]
        [InlineData("\"packId\":\"test-pack\"", "\"packId\":\"../pack\"")]
        [InlineData("\"packId\":\"test-pack\"", "\"packId\":\"test pack\"")]
        [InlineData("\"version\":\"1\"", "\"version\":1")]
        [InlineData("\"version\":\"1\"", "\"version\":\"\"")]
        [InlineData("\"theme\":\"Test\"", "\"theme\":null")]
        [InlineData("\"theme\":\"Test\"", "\"theme\":\" Test\"")]
        [InlineData("\"scope\":\"categories\",", "")]
        [InlineData("\"packId\":\"test-pack\",", "")]
        [InlineData("\"theme\":\"Test\",", "")]
        [InlineData("\"version\":\"1\",", "")]
        [InlineData("\"packName\":", "\"toolCommit\":null,\"packName\":")]
        [InlineData("\"packName\":", "\"notes\":{\"schemaVersion\":1},\"packName\":")]
        [InlineData("\"packName\":", "\"notes\":\"{\\\"schemaVersion\\\":1}\",\"packName\":")]
        [InlineData("\"packName\":", "\"sampleImageUrl\":\"https://example.com/\",\"packName\":")]
        [InlineData("\"sha256\":", "\"representativeFlag\":\"US\",\"sha256\":")]
        [InlineData("\"sha256\":", "\"flagAsset\":\"us.png\",\"sha256\":")]
        public void StrictManifestRejectsInvalidOrUnexpectedFields(string oldValue, string newValue)
        {
            Assert.Throws<InvalidDataException>(() => Read(Valid.Replace(oldValue, newValue)));
        }

        [Fact]
        public void StrictManifestRejectsDuplicateRecordsAndMalformedJson()
        {
            var record = "{\"id\":\"talk\",\"file\":\"talk.jpg\",\"sha256\":\"" + Hash + "\"}";
            Assert.Throws<InvalidDataException>(() => Read(Valid.Replace("\"imageCount\":1", "\"imageCount\":2").Replace(record, record + "," + record)));
            Assert.Throws<InvalidDataException>(() => Read(Valid.Substring(0, Valid.Length - 1)));
            Assert.Throws<InvalidDataException>(() => Read(Valid + "{}"));
            Assert.Throws<InvalidDataException>(() => Read(Valid.Replace("\"imageCount\":1", "\"imageCount\":01")));
            Assert.Throws<InvalidDataException>(() => Read(Valid.Replace("\"images\":[", "\"images\":[, ")));
        }

        private static TuneInUploadManifest Read(string json) =>
            TuneInUploadManifest.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        [Theory]
        [InlineData("{\"schemaVersion\":1,\"packName\":\"Legacy\",\"imageCount\":1}")]
        [InlineData("{\"packName\":\"Legacy\",\"sampleImageUrl\":\"https://example.com/card.jpg\",\"images\":[{\"id\":\"g19\",\"file\":\"g19.jpg\",\"bytes\":3}],\"schemaVersion\":1}")]
        [InlineData("{\"scope\":\"languages\",\"images\":[{\"id\":\"l100\",\"representativeFlag\":\"US\",\"flagAsset\":\"us.png\"}],\"toolCommit\":null,\"schemaVersion\":1}")]
        [InlineData("{\"packName\":\"Legacy\",\"imageCount\":1,\"\\u0073chemaVersion\":1}")]
        public async Task RejectedLegacyUploadPreservesInstalledFilesUntilExplicitCanonicalInstallation(string oldManifest)
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-migrate-artwork-" + Guid.NewGuid().ToString("N"));
            var installed = Path.Combine(root, "genres");
            Directory.CreateDirectory(installed);
            var oldImage = new byte[] { 9, 8, 7 };
            File.WriteAllBytes(Path.Combine(installed, "g19.jpg"), oldImage);
            File.WriteAllText(Path.Combine(installed, "tunein-artwork.json"), oldManifest);
            try
            {
                var decoded = 0;
                var scanned = 0;
                var coordinator = new TuneInArtworkUploadCoordinator(root, (_, __) => decoded++,
                    (_, __) => { scanned++; return TuneInArtworkScanner.Result.Clean; }, action => action(), () => { });
                using var legacy = new MemoryStream();
                using (var zip = new ZipArchive(legacy, ZipArchiveMode.Create, true))
                    Add(zip, "tunein-artwork.json", Encoding.UTF8.GetBytes(oldManifest));
                legacy.Position = 0;
                var error = await Assert.ThrowsAsync<NotSupportedException>(() => coordinator.Prepare(legacy, CancellationToken.None));
                Assert.Contains("schema 1", error.Message);
                Assert.Contains("newer pack using schemaVersion 2", error.Message);
                Assert.Contains("Existing installed artwork is unchanged", error.Message);
                Assert.Equal("error", coordinator.Status().State);
                Assert.Equal(error.Message, coordinator.Status().Message);
                Assert.Equal(0, decoded);
                Assert.Equal(0, scanned);
                Assert.Equal(oldImage, File.ReadAllBytes(Path.Combine(installed, "g19.jpg")));
                Assert.Equal(oldManifest, File.ReadAllText(Path.Combine(installed, "tunein-artwork.json")));

                var replacement = new byte[] { 1, 2, 3 };
                var canonical = Valid.Replace("\"categories\"", "\"genres\"")
                    .Replace("\"talk\"", "\"g19\"").Replace("talk.jpg", "g19.jpg")
                    .Replace(Hash, Convert.ToHexString(SHA256.HashData(replacement)));
                using var archive = new MemoryStream();
                using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
                {
                    Add(zip, "tunein-artwork.json", Encoding.UTF8.GetBytes(canonical));
                    Add(zip, "g19.jpg", replacement);
                }
                archive.Position = 0;
                var pending = await coordinator.Prepare(archive, CancellationToken.None);
                Assert.Equal(oldImage, File.ReadAllBytes(Path.Combine(installed, "g19.jpg")));
                await coordinator.Install(pending.Token, false, CancellationToken.None);
                Assert.Equal(replacement, File.ReadAllBytes(Path.Combine(installed, "g19.jpg")));
                Assert.Equal(canonical, File.ReadAllText(Path.Combine(installed, "tunein-artwork.json")));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("categories", "talk", "Classic", false)]
        [InlineData("genres", "c100000948", "FuturisticSpotlight", false)]
        [InlineData("languages", "l122", "Classic", true)]
        [InlineData("languages", "l122", "CartoonSpotlight", true)]
        [InlineData("languages", "l122", "FuturisticSpotlight", true)]
        [InlineData("languages", "l122", "Elegant", false)]
        [InlineData("locations", "r100343", "Classic", false)]
        [InlineData("locations", "r100343", "Cartoon", false)]
        [InlineData("locations", "r100343", "Futuristic", false)]
        [InlineData("locations", "r100343", "Neon", false)]
        public async Task CanonicalPacksRoundTripWithoutScopeOrThemeSpecificManifestBranches(
            string scope, string id, string theme, bool flags)
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-generated-pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var previous = (TuneInCategoryArtwork.GenreFolder, TuneInCategoryArtwork.LanguageFolder,
                TuneInCategoryArtwork.LocationFolder, TuneInCategoryArtwork.CategoryFolder);
            try
            {
                var image = new byte[] { 1, 2, 3 };
                var json = Valid.Replace(Hash, Convert.ToHexString(SHA256.HashData(image)))
                    .Replace("\"categories\"", "\"" + scope + "\"").Replace("\"Test\"", "\"" + theme + "\"")
                    .Replace("\"talk\"", "\"" + id + "\"").Replace("talk.jpg", id + ".jpg")
                    .Replace("\"test-pack\"", "\"Custom." + theme + "_" + scope + "\"")
                    .Replace("\"version\":\"1\"", "\"version\":\"3.1-rc.2+local\"");
                using var archive = new MemoryStream();
                using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
                {
                    Add(zip, "tunein-artwork.json", Encoding.UTF8.GetBytes(json));
                    Add(zip, id + ".jpg", image);
                    Add(zip, "LICENSE.txt", Encoding.UTF8.GetBytes("Pack license"));
                    Add(zip, "Noto-OFL.txt", Encoding.UTF8.GetBytes("Font license"));
                    Add(zip, "README.txt", Encoding.UTF8.GetBytes("Pack instructions"));
                    if (flags)
                    {
                        Add(zip, "FLAGS-SOURCES.txt", Encoding.UTF8.GetBytes("Flag provenance"));
                        Add(zip, "flag-sources.json", Encoding.UTF8.GetBytes("{\"license\":\"test\"}"));
                    }
                }
                archive.Position = 0;
                var validated = 0;
                var scanned = false;
                var coordinator = new TuneInArtworkUploadCoordinator(root, (path, _) =>
                {
                    Assert.Equal(image, File.ReadAllBytes(path));
                    validated++;
                }, (content, _) =>
                {
                    scanned = true;
                    Assert.Equal(flags, File.Exists(Path.Combine(content, "FLAGS-SOURCES.txt")));
                    Assert.Equal(flags, File.Exists(Path.Combine(content, "flag-sources.json")));
                    Assert.False(File.Exists(Path.Combine(content, "us.png")));
                    return TuneInArtworkScanner.Result.Clean;
                }, action => action(), () => { });
                var pending = await coordinator.Prepare(archive, CancellationToken.None);
                Assert.Equal(scope, pending.Scope);
                Assert.Equal("Custom." + theme + "_" + scope, pending.PackId);
                Assert.Equal("3.1-rc.2+local", pending.Version);
                Assert.Equal(theme, pending.Theme);
                Assert.Equal(1, validated);
                Assert.True(scanned);
                await coordinator.Install(pending.Token, false, CancellationToken.None);
                TuneInCategoryArtwork.GenreFolder = Path.Combine(root, "genres");
                TuneInCategoryArtwork.LanguageFolder = Path.Combine(root, "languages");
                TuneInCategoryArtwork.LocationFolder = Path.Combine(root, "locations");
                TuneInCategoryArtwork.CategoryFolder = Path.Combine(root, "categories");
                var status = TuneInCategoryArtwork.ReadPackStatus(scope);
                Assert.Equal("identified", status.Status);
                Assert.Equal(theme, status.Name);
                Assert.Equal(pending.PackId, status.PackId);
                Assert.Equal(pending.Theme, status.Theme);
                Assert.Equal(pending.Version, status.Version);
                Assert.Equal(1, status.MatchedCount);
                Assert.Equal(0, status.ModifiedCount);
                Assert.Equal(0, status.AddedCount);
                Assert.Equal(image, File.ReadAllBytes(TuneInCategoryArtwork.PackFile(scope, id)));
                if (flags)
                {
                    Assert.Equal("Flag provenance", File.ReadAllText(Path.Combine(root, scope, "FLAGS-SOURCES.txt")));
                    Assert.Equal("{\"license\":\"test\"}", File.ReadAllText(Path.Combine(root, scope, "flag-sources.json")));
                }
            }
            finally
            {
                (TuneInCategoryArtwork.GenreFolder, TuneInCategoryArtwork.LanguageFolder,
                    TuneInCategoryArtwork.LocationFolder, TuneInCategoryArtwork.CategoryFolder) = previous;
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData("valid")]
        [InlineData("unlisted")]
        [InlineData("missing")]
        [InlineData("hash")]
        [InlineData("path")]
        [InlineData("collision")]
        public void ArchiveRequiresExactSafeInventoryAndHashes(string scenario)
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-upload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var zipPath = Path.Combine(root, "pack.zip");
                var stage = Path.Combine(root, "stage");
                Directory.CreateDirectory(stage);
                var image = new byte[] { 1, 2, 3 };
                var hash = Convert.ToHexString(SHA256.HashData(image));
                var manifest = Valid.Replace(Hash, scenario == "hash" ? new string('0', 64) : hash);
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    Add(zip, "tunein-artwork.json", Encoding.UTF8.GetBytes(manifest));
                    if (scenario != "missing") Add(zip, scenario == "path" ? "../talk.jpg" : "talk.jpg", image);
                    if (scenario == "unlisted") Add(zip, "extra.txt", image);
                    if (scenario == "collision") Add(zip, "TALK.jpg", image);
                }
                var decoded = 0;
                void Validate(string path, CancellationToken token) { Assert.Equal(image, File.ReadAllBytes(path)); decoded++; }
                if (scenario == "valid")
                {
                    var result = TuneInArtworkArchive.ValidateAndExtract(zipPath, stage, Validate, CancellationToken.None);
                    Assert.Equal("categories", result.Scope);
                    Assert.Equal(1, decoded);
                    Assert.Equal(new[] { "talk.jpg", "tunein-artwork.json" }, Directory.GetFiles(stage).Select(Path.GetFileName).OrderBy(x => x));
                }
                else Assert.Throws<InvalidDataException>(() =>
                    TuneInArtworkArchive.ValidateAndExtract(zipPath, stage, Validate, CancellationToken.None));
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Add(ZipArchive zip, string name, byte[] bytes)
        {
            using (var entry = zip.CreateEntry(name).Open()) entry.Write(bytes, 0, bytes.Length);
        }

        [Theory]
        [InlineData("valid", "LICENSE.txt")]
        [InlineData("empty", "LICENSE.txt")]
        [InlineData("changed", "LICENSE.txt")]
        [InlineData("binary", "LICENSE.txt")]
        [InlineData("utf16", "LICENSE.txt")]
        [InlineData("oversized", "LICENSE.txt")]
        [InlineData("valid", "FLAGS-SOURCES.txt")]
        [InlineData("empty", "FLAGS-SOURCES.txt")]
        [InlineData("changed", "FLAGS-SOURCES.txt")]
        [InlineData("binary", "FLAGS-SOURCES.txt")]
        [InlineData("utf16", "FLAGS-SOURCES.txt")]
        [InlineData("oversized", "FLAGS-SOURCES.txt")]
        [InlineData("valid", "flag-sources.json")]
        [InlineData("empty", "flag-sources.json")]
        [InlineData("changed", "flag-sources.json")]
        [InlineData("binary", "flag-sources.json")]
        [InlineData("utf16", "flag-sources.json")]
        [InlineData("oversized", "flag-sources.json")]
        public async Task PublishedPackNoticesAreBoundedScannedAndProtected(string scenario, string target)
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-notices-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var image = new byte[] { 1, 2, 3 };
                var hash = Convert.ToHexString(SHA256.HashData(image));
                using var archive = new MemoryStream();
                using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
                {
                    Add(zip, "tunein-artwork.json", Encoding.UTF8.GetBytes(Valid.Replace(Hash, hash)));
                    Add(zip, "talk.jpg", image);
                    Add(zip, "LICENSE.txt", Notice("LICENSE.txt", "License notice"));
                    Add(zip, "Noto-OFL.txt", Encoding.UTF8.GetBytes("Font notice"));
                    Add(zip, "README.txt", Encoding.UTF8.GetBytes("Pack notice"));
                    Add(zip, "FLAGS-SOURCES.txt", Notice("FLAGS-SOURCES.txt", "Flag notice"));
                    Add(zip, "flag-sources.json", Notice("flag-sources.json", "{\"license\":\"test\"}"));
                }
                byte[] Notice(string name, string text) => name == target && scenario == "binary" ? new byte[] { 0, 1, 2 }
                    : name == target && scenario == "empty" ? Array.Empty<byte>()
                    : name == target && scenario == "utf16" ? Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()
                    : name == target && scenario == "oversized" ? new byte[TuneInArtworkArchive.MaxSupplementalBytes + 1]
                    : Encoding.UTF8.GetBytes(text);
                archive.Position = 0;
                var scanCalled = false;
                var coordinator = new TuneInArtworkUploadCoordinator(root, (_, __) => { }, (content, _) =>
                {
                    scanCalled = true;
                    Assert.True(File.Exists(Path.Combine(content, "LICENSE.txt")));
                    Assert.True(File.Exists(Path.Combine(content, "FLAGS-SOURCES.txt")));
                    Assert.True(File.Exists(Path.Combine(content, "flag-sources.json")));
                    if (scenario == "changed") File.WriteAllText(Path.Combine(content, target), "altered");
                    return TuneInArtworkScanner.Result.Clean;
                }, action => action(), () => { });
                if (scenario != "valid")
                {
                    await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.Prepare(archive, CancellationToken.None));
                    Assert.Equal(scenario == "changed", scanCalled);
                }
                else
                {
                    var pending = await coordinator.Prepare(archive, CancellationToken.None);
                    Assert.True(scanCalled);
                    await coordinator.Install(pending.Token, false, CancellationToken.None);
                    Assert.Equal("License notice", File.ReadAllText(Path.Combine(root, "categories", "LICENSE.txt")));
                    Assert.Equal("Font notice", File.ReadAllText(Path.Combine(root, "categories", "Noto-OFL.txt")));
                    Assert.Equal("Flag notice", File.ReadAllText(Path.Combine(root, "categories", "FLAGS-SOURCES.txt")));
                    Assert.Equal("{\"license\":\"test\"}", File.ReadAllText(Path.Combine(root, "categories", "flag-sources.json")));
                }
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("encrypted")]
        [InlineData("symlink")]
        [InlineData("overlap")]
        [InlineData("trailing")]
        public void ArchiveRejectsUnsafeStructures(string scenario)
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-unsafe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var zipPath = Path.Combine(root, "pack.zip");
                var stage = Path.Combine(root, "stage");
                Directory.CreateDirectory(stage);
                var image = new byte[] { 1, 2, 3 };
                var hash = Convert.ToHexString(SHA256.HashData(image));
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    Add(zip, "tunein-artwork.json", Encoding.UTF8.GetBytes(Valid.Replace(Hash, hash)));
                    Add(zip, "talk.jpg", image);
                }
                var bytes = File.ReadAllBytes(zipPath);
                var central = BitConverter.ToInt32(bytes, bytes.Length - 6);
                if (scenario == "encrypted") bytes[central + 8] |= 1;
                if (scenario == "symlink")
                {
                    bytes[central + 5] = 3;
                    BitConverter.GetBytes(0xa1ff0000u).CopyTo(bytes, central + 38);
                }
                if (scenario == "overlap") BitConverter.GetBytes(1u).CopyTo(bytes, central + 42);
                if (scenario == "trailing") Array.Resize(ref bytes, bytes.Length + 1);
                File.WriteAllBytes(zipPath, bytes);
                Assert.Throws<InvalidDataException>(() =>
                    TuneInArtworkArchive.ValidateAndExtract(zipPath, stage, (_, __) => { }, CancellationToken.None));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void ArchiveRejectsOversizedImageBeforeDecoding()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-large-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var zipPath = Path.Combine(root, "pack.zip");
                var stage = Path.Combine(root, "stage");
                Directory.CreateDirectory(stage);
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    Add(zip, "tunein-artwork.json", Encoding.UTF8.GetBytes(Valid));
                    using (var image = zip.CreateEntry("talk.jpg").Open())
                    {
                        var block = new byte[65536];
                        for (var i = 0; i < 256; i++) image.Write(block, 0, block.Length);
                        image.WriteByte(0);
                    }
                }
                Assert.Throws<InvalidDataException>(() =>
                    TuneInArtworkArchive.ValidateAndExtract(zipPath, stage, (_, __) => throw new Exception("Decoder must not run."), CancellationToken.None));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void ImageValidationRejectsInvalidContentAndDimensionsBeforeDecoder()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-image-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var image = Path.Combine(root, "talk.png");
                File.WriteAllBytes(image, new byte[] { 1, 2, 3 });
                Assert.Throws<InvalidDataException>(() => TuneInArtworkImage.Validate(image, "unused", null, CancellationToken.None));
                using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream("MediaBrowser.Plugins.TuneIn.Images.tunein-talk.png"))
                using (var output = File.Create(image)) stream.CopyTo(output);
                var png = File.ReadAllBytes(image);
                png[16] = 0x7f;
                File.WriteAllBytes(image, png);
                Assert.Throws<InvalidDataException>(() => TuneInArtworkImage.Validate(image, "unused", null, CancellationToken.None));
            }
            finally { Directory.Delete(root, true); }
        }

        [FfmpegFact]
        public void RealDecoderAcceptsValidPngAndRejectsTruncatedImage()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-real-image-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var image = Path.Combine(root, "talk.png");
                using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream("MediaBrowser.Plugins.TuneIn.Images.tunein-talk.png"))
                using (var output = File.Create(image)) stream.CopyTo(output);
                var ffmpeg = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
                TuneInArtworkImage.Validate(image, ffmpeg, null, CancellationToken.None);
                var jpeg = Path.Combine(root, "talk.jpg");
                using (var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -y -i \"" + image + "\" -frames:v 1 \"" + jpeg + "\"")
                { UseShellExecute = false, CreateNoWindow = true }))
                {
                    process.WaitForExit();
                    Assert.Equal(0, process.ExitCode);
                }
                TuneInArtworkImage.Validate(jpeg, ffmpeg, null, CancellationToken.None);
                File.Copy(jpeg, image, true);
                Assert.Throws<InvalidDataException>(() => TuneInArtworkImage.Validate(image, ffmpeg, null, CancellationToken.None));
                using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream("MediaBrowser.Plugins.TuneIn.Images.tunein-talk.png"))
                using (var output = File.Create(image)) stream.CopyTo(output);
                File.WriteAllBytes(image, File.ReadAllBytes(image).Take(100).ToArray());
                Assert.Throws<InvalidDataException>(() => TuneInArtworkImage.Validate(image, ffmpeg, null, CancellationToken.None));
            }
            finally { Directory.Delete(root, true); }
        }

        [FfmpegFact]
        public async Task ValidPackIsDecodedStagedAndInstalled()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-real-pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                byte[] image;
                using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream("MediaBrowser.Plugins.TuneIn.Images.tunein-talk.png"))
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    image = buffer.ToArray();
                }
                var hash = Convert.ToHexString(SHA256.HashData(image));
                using var archive = new MemoryStream();
                using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
                {
                    Add(zip, "tunein-artwork.json", Encoding.UTF8.GetBytes(Valid.Replace("talk.jpg", "talk.png").Replace(Hash, hash)));
                    Add(zip, "talk.png", image);
                }
                archive.Position = 0;
                var ffmpeg = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
                var coordinator = new TuneInArtworkUploadCoordinator(root,
                    (path, token) => TuneInArtworkImage.Validate(path, ffmpeg, null, token),
                    (_, __) => TuneInArtworkScanner.Result.NotScanned, action => action(), () => { });
                var pending = await coordinator.Prepare(archive, CancellationToken.None);
                var installed = await coordinator.Install(pending.Token, true, CancellationToken.None);
                Assert.Equal(new[] { "talk" }, installed.AffectedCategories);
                Assert.Equal(image, File.ReadAllBytes(Path.Combine(root, "categories", "talk.png")));
            }
            finally { Directory.Delete(root, true); }
        }

        private sealed class FfmpegFactAttribute : FactAttribute
        {
            public FfmpegFactAttribute()
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG")))
                    Skip = "Set TUNEIN_TEST_FFMPEG to run the real artwork decoder regression.";
            }
        }

        [DefenderFact]
        public void RealDefenderScansEveryStagedFile()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-defender-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "first.txt"), "A harmless scanner test.");
                File.WriteAllText(Path.Combine(root, "second.txt"), "Another harmless scanner test.");
                Assert.Equal(TuneInArtworkScanner.Result.Clean, TuneInArtworkScanner.Scan(root, CancellationToken.None));
            }
            finally { Directory.Delete(root, true); }
        }

        private sealed class DefenderFactAttribute : FactAttribute
        {
            public DefenderFactAttribute()
            {
                if (Environment.GetEnvironmentVariable("TUNEIN_TEST_DEFENDER") != "1")
                    Skip = "Set TUNEIN_TEST_DEFENDER=1 to run the real Defender scanner regression.";
            }
        }

        [DefenderBlockedFact]
        public void RealDefenderRejectsUnverifiedCloudPolicy()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-defender-policy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "first.txt"), "A harmless scanner test.");
                var error = Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.Scan(root, CancellationToken.None));
                Assert.Contains("cloud reporting", error.Message);
            }
            finally { Directory.Delete(root, true); }
        }

        private sealed class DefenderBlockedFactAttribute : FactAttribute
        {
            public DefenderBlockedFactAttribute()
            {
                if (Environment.GetEnvironmentVariable("TUNEIN_TEST_DEFENDER_POLICY_BLOCK") != "1")
                    Skip = "Set TUNEIN_TEST_DEFENDER_POLICY_BLOCK=1 on a server with cloud reporting enabled.";
            }
        }

        [Fact]
        public void ScannerDoesNotReportThreatsErrorsOrPartialScansAsClean()
        {
            TuneInArtworkScanner.CheckDefenderPolicy(0, "TUNEIN_POLICY:0:2:True:Normal\r\n");
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefenderPolicy(0, "TUNEIN_POLICY:2:2:True:Normal"));
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefenderPolicy(0, "TUNEIN_POLICY:0:1:True:Normal"));
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefenderPolicy(0, "TUNEIN_POLICY:0:2:False:Normal"));
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefenderPolicy(1, "TUNEIN_POLICY:0:2:True:Normal"));
            TuneInArtworkScanner.CheckDefenderExclusion(1, "file [device] is not excluded.", "file");
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefenderExclusion(0, "file [device] is excluded.", "file"));
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefenderExclusion(2, "Access denied.", "file"));
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefenderExclusion(1, "other [device] is not excluded.", "file"));
            TuneInArtworkScanner.CheckClam(0, "Infected files: 0\nScanned files: 2\n", 2);
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckClam(1, "Infected files: 1\nScanned files: 2\n", 2));
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckClam(0, "Infected files: 0\nScanned files: 1\n", 2));
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckClam(0, "WARNING: skipped\nInfected files: 0\nScanned files: 2\n", 2));
            TuneInArtworkScanner.CheckDefender(0, "Scan finished.\nScanning file found no threats.", "file");
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefender(2, "Scan finished.\nScanning file found no threats.", "file"));
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefender(0, "Scan skipped.", "file"));
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefender(0, "Error: Scan finished.\nScanning file found no threats.", "file"));
            Assert.Throws<InvalidOperationException>(() => TuneInArtworkScanner.CheckDefender(0, "Scan finished.\nScanning other found no threats.", "file"));
        }

        [Fact]
        public void FailedReplacementRestoresThePreviousPack()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var previous = Path.Combine(root, "categories");
                var stage = Path.Combine(root, "stage");
                Directory.CreateDirectory(previous);
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(previous, "talk.jpg"), "old");
                File.WriteAllText(Path.Combine(stage, "talk.jpg"), "new");
                var moves = 0;
                void Move(string source, string destination)
                {
                    if (++moves == 2) throw new IOException("Simulated activation failure");
                    Directory.Move(source, destination);
                }
                Assert.Throws<IOException>(() => TuneInArtworkInstall.Install(root, "categories", stage, Move));
                Assert.Equal("old", File.ReadAllText(Path.Combine(previous, "talk.jpg")));
                Assert.Equal("new", File.ReadAllText(Path.Combine(stage, "talk.jpg")));
                Assert.False(Directory.Exists(Path.Combine(root, ".artwork-backup-categories")));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task ArtworkReadersWaitForReplacementToFinish()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-swap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var previous = TuneInCategoryArtwork.CategoryFolder;
            using var moved = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            try
            {
                var destination = Path.Combine(root, "categories");
                var stage = Path.Combine(root, "stage");
                Directory.CreateDirectory(destination);
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(destination, "talk.jpg"), "old");
                File.WriteAllText(Path.Combine(stage, "talk.jpg"), "new");
                TuneInCategoryArtwork.CategoryFolder = destination;
                var moves = 0;
                var installation = Task.Run(() => TuneInArtworkInstall.Install(root, "categories", stage, (from, to) =>
                {
                    Directory.Move(from, to);
                    if (++moves == 1) { moved.Set(); resume.Wait(); }
                }));
                Assert.True(moved.Wait(TimeSpan.FromSeconds(10)));
                var reader = Task.Run(() => TuneInCategoryArtwork.PackRevision("categories", "talk"));
                Assert.NotSame(reader, await Task.WhenAny(reader, Task.Delay(150)));
                resume.Set();
                await installation;
                Assert.NotEqual("missing", await reader);
            }
            finally
            {
                resume.Set();
                TuneInCategoryArtwork.CategoryFolder = previous;
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void InterruptedReplacementRecoversOrFinalizes(bool committed)
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var current = Path.Combine(root, "categories");
                var backup = Path.Combine(root, ".artwork-backup-categories");
                Directory.CreateDirectory(current);
                Directory.CreateDirectory(backup);
                File.WriteAllText(Path.Combine(current, "talk.jpg"), "new");
                File.WriteAllText(Path.Combine(backup, "talk.jpg"), "old");
                var journal = Path.Combine(root, ".artwork-install-categories");
                File.WriteAllText(journal, "old");
                if (committed) File.WriteAllText(journal + ".committed", "committed");
                TuneInArtworkInstall.Recover(root, "categories");
                Assert.Equal(committed ? "new" : "old", File.ReadAllText(Path.Combine(current, "talk.jpg")));
                Assert.False(Directory.Exists(backup));
                Assert.False(File.Exists(journal));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task LockedSettingsRejectUploadWithoutReplacingStagedPack()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-locked-upload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var locked = false;
                var coordinator = new TuneInArtworkUploadCoordinator(root, (_, __) => { },
                    (_, __) => TuneInArtworkScanner.Result.NotScanned, action => action(),
                    () => { if (locked) throw new InvalidOperationException("Settings are locked."); });
                using var first = CreateArchive();
                var pending = await coordinator.Prepare(first, CancellationToken.None);
                locked = true;
                using var second = CreateArchive();
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.Prepare(second, CancellationToken.None));
                Assert.Contains("locked", error.Message);
                Assert.Equal(0, second.Position);
                Assert.Single(Directory.GetDirectories(root, ".tunein-upload-*"));
                await coordinator.Discard(pending.Token, CancellationToken.None);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task UnscannedPackRequiresExplicitApprovalBeforeInstallation()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-confirm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var previous = Path.Combine(root, "categories");
                Directory.CreateDirectory(previous);
                File.WriteAllText(Path.Combine(previous, "talk.jpg"), "old");
                var coordinator = new TuneInArtworkUploadCoordinator(root, (_, __) => { },
                    (_, __) => TuneInArtworkScanner.Result.NotScanned, action => action(), () => { });
                using var archive = CreateArchive();
                var pending = await coordinator.Prepare(archive, CancellationToken.None);
                Assert.Equal("Not scanned", pending.ScanStatus);
                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.Install(pending.Token, false, CancellationToken.None));
                Assert.Equal("old", File.ReadAllText(Path.Combine(previous, "talk.jpg")));
                var installed = await coordinator.Install(pending.Token, true, CancellationToken.None);
                Assert.Equal("categories", installed.Scope);
                Assert.Null(installed.Token);
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(previous, "talk.jpg")));
                Assert.Equal("installed", coordinator.Status().State);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task ScannerFailureAndChangedStageNeverReplacePreviousPack()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-scan-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var previous = Path.Combine(root, "categories");
                Directory.CreateDirectory(previous);
                File.WriteAllText(Path.Combine(previous, "talk.jpg"), "old");
                var failing = new TuneInArtworkUploadCoordinator(root, (_, __) => { },
                    (_, __) => throw new InvalidOperationException("Scanner failed"), action => action(), () => { });
                using (var archive = CreateArchive())
                    await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Prepare(archive, CancellationToken.None));
                Assert.Empty(Directory.GetDirectories(root, ".tunein-upload-*"));
                Assert.Equal("old", File.ReadAllText(Path.Combine(previous, "talk.jpg")));

                var coordinator = new TuneInArtworkUploadCoordinator(root, (_, __) => { },
                    (_, __) => TuneInArtworkScanner.Result.Clean, action => action(), () => { });
                using var second = CreateArchive();
                var pending = await coordinator.Prepare(second, CancellationToken.None);
                var stage = Assert.Single(Directory.GetDirectories(root, ".tunein-upload-*"));
                File.WriteAllBytes(Path.Combine(stage, "content", "talk.jpg"), new byte[] { 9 });
                await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.Install(pending.Token, false, CancellationToken.None));
                Assert.Equal("old", File.ReadAllText(Path.Combine(previous, "talk.jpg")));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task CategoryReplacementIncludesRemovedAndAddedCardsInAffectedCategories()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-affected-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var previous = Path.Combine(root, "categories");
                Directory.CreateDirectory(previous);
                File.WriteAllText(Path.Combine(previous, "sports.jpg"), "old");
                var coordinator = new TuneInArtworkUploadCoordinator(root, (_, __) => { },
                    (_, __) => TuneInArtworkScanner.Result.Clean, action => action(), () => { });
                using var archive = CreateArchive();
                var pending = await coordinator.Prepare(archive, CancellationToken.None);
                var installed = await coordinator.Install(pending.Token, false, CancellationToken.None);
                Assert.Equal(new[] { "sports", "talk" }, installed.AffectedCategories.OrderBy(value => value));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void ArtworkEndpointsRequireAdministratorAuthentication()
        {
            foreach (var type in new[] { typeof(UploadTuneInArtwork), typeof(GetTuneInArtworkUploadStatus),
                typeof(InstallTuneInArtwork), typeof(DiscardTuneInArtwork) })
            {
                var attribute = Assert.IsType<AuthenticatedAttribute>(Assert.Single(type.GetCustomAttributes(typeof(AuthenticatedAttribute), false)));
                Assert.Equal("Admin", attribute.Roles);
            }
        }

        private static MemoryStream CreateArchive()
        {
            var image = new byte[] { 1, 2, 3 };
            var hash = Convert.ToHexString(SHA256.HashData(image));
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                Add(zip, "tunein-artwork.json", Encoding.UTF8.GetBytes(Valid.Replace(Hash, hash)));
                Add(zip, "talk.jpg", image);
            }
            stream.Position = 0;
            return stream;
        }
    }
}
