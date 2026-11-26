using System;
using System.IO;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class ArtworkStorageTests
    {
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
