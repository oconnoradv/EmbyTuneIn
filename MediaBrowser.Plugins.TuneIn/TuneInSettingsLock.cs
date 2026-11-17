using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class TuneInSettingsLock
    {
        public bool Locked { get; set; }
        public bool RefreshActive { get; set; }
    }

    [XmlRoot("TuneInSettingsLock")]
    public sealed class TuneInSettingsLockDocument
    {
        public bool Locked { get; set; }
    }

    internal sealed class TuneInSettingsLockStore
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(TuneInSettingsLockDocument));
        private readonly object gate = new object();
        private readonly string path;
        private bool locked;

        internal TuneInSettingsLockStore(string path = null)
        {
            this.path = path;
            if (path == null || !File.Exists(path)) return;
            using (var stream = File.OpenRead(path))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
            {
                var document = (TuneInSettingsLockDocument)Serializer.Deserialize(reader);
                if (document == null) throw new InvalidDataException("TuneIn settings lock state is incomplete.");
                locked = document.Locked;
            }
        }

        internal bool IsLocked
        {
            get { lock (gate) return locked; }
        }

        internal TuneInSettingsLock Read(bool refreshActive) =>
            new TuneInSettingsLock { Locked = IsLocked, RefreshActive = refreshActive };

        internal TuneInSettingsLock Set(bool value, bool refreshActive)
        {
            lock (gate)
            {
                if (path != null)
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    var temporary = path + ".tmp";
                    try
                    {
                        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            Serializer.Serialize(stream, new TuneInSettingsLockDocument { Locked = value });
                            stream.Flush(true);
                        }
                        if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }
                locked = value;
                return new TuneInSettingsLock { Locked = locked, RefreshActive = refreshActive };
            }
        }
    }
}
