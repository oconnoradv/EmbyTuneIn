using System;
using System.IO;
using System.Xml.Serialization;
using MediaBrowser.Plugins.TuneIn.Configuration;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class ConfigurationMigrationTests
    {
        [Fact]
        public void ForkConfigurationIsCopiedWithoutChangingSelectionsOrSigningKey()
        {
            var directory = Path.Combine(Path.GetTempPath(), "tunein-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var source = Path.Combine(directory, "MediaBrowser.Plugins.TuneIn.Fork.xml");
                var target = Path.Combine(directory, "MediaBrowser.Plugins.TuneIn.xml");
                var original = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
                original.Music.Add(new CatalogSelection { Id = "c1", Name = "Music" });
                using (var file = File.Create(source)) new XmlSerializer(typeof(PluginConfiguration)).Serialize(file, original);

                ConfigurationMigration.CopyPreviousForkConfiguration(directory);
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
                using (var file = File.OpenRead(target))
                {
                    var copied = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(file);
                    Assert.Equal(original.NavigationSecret, copied.NavigationSecret);
                    Assert.True(copied.EnableTalk);
                    Assert.Equal("c1", Assert.Single(copied.Music).Id);
                }
                ConfigurationMigration.CopyPreviousForkConfiguration(directory);
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public void ExistingOriginalConfigurationIsNeverOverwritten()
        {
            var directory = Path.Combine(Path.GetTempPath(), "tunein-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var source = Path.Combine(directory, "MediaBrowser.Plugins.TuneIn.Fork.xml");
                var target = Path.Combine(directory, "MediaBrowser.Plugins.TuneIn.xml");
                File.WriteAllText(source, "fork");
                File.WriteAllText(target, "original");
                ConfigurationMigration.CopyPreviousForkConfiguration(directory);
                Assert.Equal("original", File.ReadAllText(target));
                Assert.Equal("fork", File.ReadAllText(source));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public void InvalidForkConfigurationFailsWithoutCreatingDestination()
        {
            var directory = Path.Combine(Path.GetTempPath(), "tunein-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var source = Path.Combine(directory, "MediaBrowser.Plugins.TuneIn.Fork.xml");
                File.WriteAllText(source, "<broken>");
                Assert.Throws<InvalidOperationException>(() => ConfigurationMigration.CopyPreviousForkConfiguration(directory));
                Assert.False(File.Exists(Path.Combine(directory, "MediaBrowser.Plugins.TuneIn.xml")));
                Assert.Single(Directory.GetFiles(directory));
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
