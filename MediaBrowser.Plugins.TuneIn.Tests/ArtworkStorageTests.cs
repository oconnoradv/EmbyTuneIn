using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class ArtworkStorageTests
    {
        [Fact]
        public void StartupCreatesMissingScopesAndPreservesExistingArtwork()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-startup-" + Guid.NewGuid().ToString("N"));
            try
            {
                var existing = Path.Combine(root, "genres", "g123.jpg");
                Directory.CreateDirectory(Path.GetDirectoryName(existing));
                File.WriteAllText(existing, "existing");
                var errors = new List<string>();
                TuneInArtworkStorage.InitializeDirectories(root, (path, error) => errors.Add(path));
                TuneInArtworkStorage.InitializeDirectories(root, (path, error) => errors.Add(path));
                Assert.Empty(errors);
                foreach (var scope in TuneInCategoryArtwork.Scopes)
                    Assert.True(Directory.Exists(Path.Combine(root, scope)));
                Assert.Equal("existing", File.ReadAllText(existing));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void StartupReportsBlockedScopeAndStillCreatesOtherScopes()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var blocker = Path.Combine(root, "genres");
            File.WriteAllText(blocker, "keep");
            try
            {
                var errors = new List<string>();
                TuneInArtworkStorage.InitializeDirectories(root, (path, error) => errors.Add(path));
                Assert.Equal(blocker, Assert.Single(errors));
                Assert.Equal("keep", File.ReadAllText(blocker));
                Assert.True(Directory.Exists(Path.Combine(root, "languages")));
                Assert.True(Directory.Exists(Path.Combine(root, "locations")));
                Assert.True(Directory.Exists(Path.Combine(root, "categories")));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void StartupReportsUnavailableRoot()
        {
            var errors = new List<Exception>();
            TuneInArtworkStorage.InitializeDirectories(null, (path, error) => errors.Add(error));
            Assert.Contains("unavailable", Assert.Single(errors).Message);
        }

        [Fact]
        public void WritableStorageLeavesNoProbeFiles()
        {
            var root = Path.Combine(Path.GetTempPath(), "tunein-storage-" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = TuneInArtworkStorage.Check(root);
                Assert.True(result.Writable);
                Assert.Empty(Directory.GetFileSystemEntries(root));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root); }
        }

        [Fact]
        public void UnusableStorageProvidesActionableGuidance()
        {
            var blocker = Path.Combine(Path.GetTempPath(), "tunein-storage-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blocker, "file");
            try
            {
                var result = TuneInArtworkStorage.Check(Path.Combine(blocker, "artwork"));
                Assert.False(result.Writable);
                Assert.Contains("Emby service account", result.Message);
                Assert.Contains("writable", result.Message);
            }
            finally { File.Delete(blocker); }
        }

        [Fact]
        public void StorageServiceRequiresAnAuthenticatedRequest()
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                new TuneInArtworkStorageService().Post(new CheckTuneInArtworkStorage()));
        }
    }
}
