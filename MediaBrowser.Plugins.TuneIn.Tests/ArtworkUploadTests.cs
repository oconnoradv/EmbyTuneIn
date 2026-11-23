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
    public class ArtworkUploadTests
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string Valid = "{\"schemaVersion\":1,\"scope\":\"categories\",\"packName\":\"Test\",\"imageCount\":1,\"images\":[{\"id\":\"talk\",\"file\":\"talk.jpg\",\"sha256\":\"" + Hash + "\"}]}";

        [Fact]
        public void StrictManifestAcceptsExactRecords()
        {
            var manifest = Read(Valid);
            Assert.Equal("categories", manifest.Scope);
            Assert.Equal("Test", manifest.Name);
            Assert.Equal("talk.jpg", Assert.Single(manifest.Images).File);
        }

        [Theory]
        [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")]
        [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
        [InlineData("\"scope\":\"categories\"", "\"scope\":\"other\"")]
        [InlineData("\"packName\":\"Test\"", "\"packName\":\"https://example.com\"")]
        [InlineData("\"packName\":\"Test\"", "\"packName\":\"www.example.com\"")]
        [InlineData("\"packName\":\"Test\"", "\"packName\":\"\\uD800\"")]
        [InlineData("\"imageCount\":1", "\"imageCount\":2")]
        [InlineData("\"file\":\"talk.jpg\"", "\"file\":\"../talk.jpg\"")]
        [InlineData("\"id\":\"talk\"", "\"id\":\"channel\"")]
        [InlineData("\"id\":\"talk\"", "\"id\":\"talk\",\"url\":\"https://example.com\"")]
        [InlineData("\"images\":", "\"unexpected\":1,\"images\":")]
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
                    (_, __) => TuneInArtworkScanner.Result.NotScanned, action => action());
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
                    (_, __) => TuneInArtworkScanner.Result.NotScanned, action => action());
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
                    (_, __) => throw new InvalidOperationException("Scanner failed"), action => action());
                using (var archive = CreateArchive())
                    await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Prepare(archive, CancellationToken.None));
                Assert.Empty(Directory.GetDirectories(root, ".tunein-upload-*"));
                Assert.Equal("old", File.ReadAllText(Path.Combine(previous, "talk.jpg")));

                var coordinator = new TuneInArtworkUploadCoordinator(root, (_, __) => { },
                    (_, __) => TuneInArtworkScanner.Result.Clean, action => action());
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
                    (_, __) => TuneInArtworkScanner.Result.Clean, action => action());
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
