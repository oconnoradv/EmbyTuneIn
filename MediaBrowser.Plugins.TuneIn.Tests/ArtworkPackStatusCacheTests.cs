using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    // Artwork paths are process-wide; tests that replace them must also exclude concurrent readers.
    [CollectionDefinition("Artwork globals", DisableParallelization = true)]
    public sealed class ArtworkGlobalsCollection { }

    [Collection("Artwork globals")]
    public class ArtworkPackStatusCacheTests
    {
        private sealed class Clock
        {
            internal DateTimeOffset Utc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            internal TimeSpan Elapsed;

            internal void Advance(TimeSpan amount)
            {
                Utc += amount;
                Elapsed += amount;
            }

            internal TuneInArtworkPackStatusCache Cache(Func<string, TuneInArtworkPackStatus> scan) =>
                new TuneInArtworkPackStatusCache(TuneInArtworkPackStatusCache.MaximumAge, scan, () => Utc, () => Elapsed);
        }

        [Fact]
        public void ScansOnlyOnDemandAndExpiresAtTheBoundUsingMonotonicTime()
        {
            var clock = new Clock();
            var scans = 0;
            var cache = clock.Cache(scope => { scans++; return new TuneInArtworkPackStatus { Scope = scope, Status = "identified" }; });
            Assert.Null(cache.Read("genres"));
            Assert.Equal(0, scans);
            var first = cache.Read("genres", scanIfNeeded: true);
            Assert.Equal(clock.Utc, first.CheckedAtUtc);
            clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
            Assert.Same(first, cache.Read("genres", scanIfNeeded: true));
            clock.Advance(TimeSpan.FromTicks(1));
            Assert.Same(first, cache.Read("genres"));
            Assert.Equal(1, scans);
            clock.Utc -= TimeSpan.FromDays(1);
            var second = cache.Read("genres", scanIfNeeded: true);
            Assert.NotSame(first, second);
            Assert.Equal(clock.Utc, second.CheckedAtUtc);
            Assert.Equal(2, scans);
            Assert.Null(clock.Cache(_ => throw new Exception("Skip must not scan.")).Read("genres"));
        }

        [Fact]
        public void ManualFileEditsAreVisibleOnForceOrExpiryAndInvalidationDoesNotScan()
        {
            var root = Path.Combine(Directory.GetCurrentDirectory(), ".artwork-cache-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var previous = TuneInCategoryArtwork.CategoryFolder;
            try
            {
                TuneInCategoryArtwork.CategoryFolder = root;
                var image = Path.Combine(root, "talk.jpg");
                var manifest = Path.Combine(root, "tunein-artwork.json");
                var bytes = new byte[] { 1, 2, 3 };
                File.WriteAllBytes(image, bytes);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                File.WriteAllText(manifest, "{\"schemaVersion\":2,\"scope\":\"categories\",\"packId\":\"test\",\"packName\":\"Test\",\"theme\":\"Test\",\"version\":\"1\",\"imageCount\":1,"
                    + "\"images\":[{\"id\":\"talk\",\"file\":\"talk.jpg\",\"sha256\":\"" + hash + "\"}]}");
                var clock = new Clock();
                var cache = clock.Cache(TuneInCategoryArtwork.ReadPackStatus);
                var first = cache.Read("categories", scanIfNeeded: true);
                Assert.Equal(1, first.Status.MatchedCount);
                TuneInArtworkPackStatusSnapshot unavailableFile;
                using (var held = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    unavailableFile = cache.Read("categories", forceRescan: true);
                    Assert.Equal("unavailable", unavailableFile.Status.Status);
                    Assert.Equal(1, unavailableFile.Status.InstalledCount);
                    Assert.Null(unavailableFile.Status.MatchedCount);
                }
                Assert.Same(unavailableFile, cache.Read("categories", scanIfNeeded: true));
                first = cache.Read("categories", forceRescan: true);
                File.WriteAllBytes(image, new byte[] { 4, 5, 6 });
                Assert.Same(first, cache.Read("categories", scanIfNeeded: true));
                clock.Advance(TimeSpan.FromSeconds(1));
                var forced = cache.Read("categories", forceRescan: true);
                Assert.Equal("identified", forced.Status.Status);
                Assert.Equal(1, forced.Status.ModifiedCount);
                Assert.Equal(0, forced.Status.MatchedCount);
                Assert.Equal(clock.Utc, forced.CheckedAtUtc);
                File.Delete(image);
                clock.Advance(TimeSpan.FromMinutes(5));
                Assert.Same(forced, cache.Read("categories"));
                Assert.Equal(0, cache.Read("categories", scanIfNeeded: true).Status.InstalledCount);
                File.WriteAllText(manifest, "{");
                cache.Invalidate("categories");
                Assert.Null(cache.Read("categories"));
                var invalid = cache.Read("categories", scanIfNeeded: true);
                Assert.Equal("invalid", invalid.Status.Status);
                Assert.Equal(clock.Utc, invalid.CheckedAtUtc);
                Assert.Same(invalid, cache.Read("categories", scanIfNeeded: true));
                TuneInCategoryArtwork.CategoryFolder = null;
                var unavailable = cache.Read("categories", forceRescan: true);
                Assert.Equal("unavailable", unavailable.Status.Status);
                Assert.Null(unavailable.Status.InstalledCount);
            }
            finally
            {
                TuneInCategoryArtwork.CategoryFolder = previous;
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void ScopeResultsAndTimestampsArePreservedAndDefensivelyCopied()
        {
            var clock = new Clock();
            var result = new TuneInArtworkPackStatus
            {
                Scope = "genres", Path = "pack", Status = "unavailable", InstalledCount = 2,
                Name = "Actual", PackId = "actual", Theme = "Custom", Version = "2.0", ImageCount = 3, MatchedCount = 1, ModifiedCount = 1, AddedCount = 0
            };
            var cache = clock.Cache(scope => scope == "genres" ? result
                : new TuneInArtworkPackStatus { Scope = scope, Status = "invalid" });
            var genres = cache.Read("genres", scanIfNeeded: true);
            clock.Advance(TimeSpan.FromSeconds(1));
            var languages = cache.Read("languages", scanIfNeeded: true);
            Assert.Equal("invalid", languages.Status.Status);
            Assert.Null(languages.Status.InstalledCount);
            Assert.NotEqual(genres.CheckedAtUtc, languages.CheckedAtUtc);
            result.Status = "identified";
            var copy = genres.Status;
            copy.InstalledCount = 99;
            Assert.Equal("unavailable", genres.Status.Status);
            Assert.Equal("genres", genres.Status.Scope);
            Assert.Equal("pack", genres.Status.Path);
            Assert.Equal("Actual", genres.Status.Name);
            Assert.Equal("actual", genres.Status.PackId);
            Assert.Equal("Custom", genres.Status.Theme);
            Assert.Equal("2.0", genres.Status.Version);
            Assert.Equal(3, genres.Status.ImageCount);
            Assert.Equal(2, genres.Status.InstalledCount);
            Assert.Equal(1, genres.Status.MatchedCount);
            Assert.Equal(1, genres.Status.ModifiedCount);
            Assert.Equal(0, genres.Status.AddedCount);
            cache.Invalidate("genres");
            Assert.Null(cache.Read("genres"));
            Assert.Same(languages, cache.Read("languages"));
            cache.Invalidate();
            Assert.Null(cache.Read("languages"));
        }

        [Fact]
        public void UnexpectedScanFailureDoesNotReplaceOrRetimestampLastResult()
        {
            var clock = new Clock();
            var fail = false;
            var cache = clock.Cache(scope =>
            {
                if (fail) throw new IOException("Unexpected scanner failure.");
                clock.Advance(TimeSpan.FromSeconds(10));
                return new TuneInArtworkPackStatus { Scope = scope, Status = "missing", InstalledCount = 0 };
            });
            var first = cache.Read("locations", scanIfNeeded: true);
            Assert.Equal(clock.Utc, first.CheckedAtUtc);
            clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
            Assert.Same(first, cache.Read("locations", scanIfNeeded: true));
            fail = true;
            Assert.Throws<IOException>(() => cache.Read("locations", forceRescan: true));
            Assert.Same(first, cache.Read("locations"));
        }

        [Fact]
        public async Task ConcurrentDemandCoalescesAndInstallInvalidationUsesTheSameGate()
        {
            var scans = 0;
            var version = "before";
            var clock = new Clock();
            var cache = clock.Cache(scope =>
            {
                Assert.True(Monitor.IsEntered(TuneInCategoryArtwork.PackGate));
                Interlocked.Increment(ref scans);
                return new TuneInArtworkPackStatus { Scope = scope, Status = "identified", Name = version };
            });
            using var start = new ManualResetEventSlim();
            var readers = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            {
                Assert.True(start.Wait(TimeSpan.FromSeconds(10)));
                return cache.Read("genres", scanIfNeeded: true);
            })).ToArray();
            start.Set();
            var results = await Task.WhenAll(readers);
            Assert.Equal(1, scans);
            Assert.All(results, result => Assert.Same(results[0], result));

            using var attempting = new ManualResetEventSlim();
            Task<TuneInArtworkPackStatusSnapshot> pending;
            lock (TuneInCategoryArtwork.PackGate)
            {
                // Represents the installer's atomic file publication and cache invalidation.
                version = "during";
                pending = Task.Run(() =>
                {
                    attempting.Set();
                    return cache.Read("genres", scanIfNeeded: true);
                });
                Assert.True(attempting.Wait(TimeSpan.FromSeconds(10)));
                Assert.False(pending.IsCompleted);
                version = "after";
                cache.Invalidate("genres");
            }

            var installed = await pending;
            Assert.Equal("after", installed.Status.Name);
            Assert.Equal(2, scans);
        }

        [Fact]
        public async Task OptionalPollingDoesNotWaitForAnActivePackOperation()
        {
            var cache = new TuneInArtworkPackStatusCache();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = Task.Run(() =>
            {
                lock (TuneInCategoryArtwork.PackGate)
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }
            });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                Assert.False(cache.TryRead("genres", true, out var snapshot));
                Assert.Null(snapshot);
            }
            finally { release.Set(); }
            await holder;
        }

        [Fact]
        public void InstallationInvalidatesThePublishedStatus()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-cache-install-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(root, "categories");
            var staged = Path.Combine(root, "staged");
            Directory.CreateDirectory(destination);
            Directory.CreateDirectory(staged);
            var previous = TuneInCategoryArtwork.CategoryFolder;
            try
            {
                TuneInCategoryArtwork.CategoryFolder = destination;
                File.WriteAllText(Path.Combine(destination, "talk.jpg"), "old");
                File.WriteAllText(Path.Combine(staged, "sports.jpg"), "new");
                var cache = TuneInRefreshService.ArtworkStatusCache;
                cache.Invalidate("categories");
                Assert.Equal(1, cache.Read("categories", scanIfNeeded: true).Status.InstalledCount);
                TuneInArtworkInstall.Install(root, "categories", staged);
                Assert.Null(cache.Read("categories"));
                Assert.Equal(1, cache.Read("categories", scanIfNeeded: true).Status.InstalledCount);
                Assert.True(File.Exists(Path.Combine(destination, "sports.jpg")));
            }
            finally
            {
                TuneInRefreshService.ArtworkStatusCache.Invalidate("categories");
                TuneInCategoryArtwork.CategoryFolder = previous;
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Genres")]
        [InlineData("other")]
        public void UnknownScopesAreRejectedEvenWhenSkipping(string scope)
        {
            var cache = new TuneInArtworkPackStatusCache();
            Assert.Throws<ArgumentException>(() => cache.Read(scope));
            if (scope != null) Assert.Throws<ArgumentException>(() => cache.Invalidate(scope));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(301)]
        public void ExpiryCannotBeDisabledOrExceedFiveMinutes(int seconds)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TuneInArtworkPackStatusCache(TimeSpan.FromSeconds(seconds)));
        }
    }
}
