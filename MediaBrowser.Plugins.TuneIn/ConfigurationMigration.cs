using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using MediaBrowser.Plugins.TuneIn.Configuration;

namespace MediaBrowser.Plugins.TuneIn
{
    internal static class ConfigurationMigration
    {
        internal static void CopyPreviousForkConfiguration(string directory)
        {
            var source = Path.Combine(directory, "MediaBrowser.Plugins.TuneIn.Fork.xml");
            var destination = Path.Combine(directory, "MediaBrowser.Plugins.TuneIn.xml");
            if (File.Exists(destination) || !File.Exists(source)) return;

            var temporary = Path.Combine(directory, ".tunein-migration-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, temporary, false);
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (var reader = XmlReader.Create(temporary, settings))
                {
                    var value = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader);
                    ConfigurationSnapshot.Validate(value);
                }
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
