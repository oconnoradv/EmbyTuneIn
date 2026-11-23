using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace MediaBrowser.Plugins.TuneIn
{
    [XmlRoot("TuneInSettingsLock")]
    public sealed class TuneInSettingsLock
    {
        public bool Locked { get; set; }
    }

    internal sealed class TuneInSettingsLockStore
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(TuneInSettingsLock));
        private readonly object gate = new object();
        private readonly string path;
        private readonly Func<bool> initiallyLocked;
        private bool? locked;

        internal TuneInSettingsLockStore(string path, Func<bool> initiallyLocked = null)
        {
            this.path = path;
            this.initiallyLocked = initiallyLocked ?? (() => false);
        }

        internal bool? TryReadStatus()
        {
            if (!System.Threading.Monitor.TryEnter(gate)) return null;
            try { return IsLocked; }
            finally { System.Threading.Monitor.Exit(gate); }
        }

        internal bool IsLocked
        {
            get
            {
                lock (gate)
                {
                    if (!locked.HasValue) locked = Read();
                    return locked.Value;
                }
            }
        }

        internal void SaveConfiguration(Action save)
        {
            WithUnlockedSettings(() =>
            {
                // Lock before persisting settings so a lost response cannot leave saved settings editable.
                Set(true);
                save();
            });
        }

        internal void WithUnlockedSettings(Action save)
        {
            lock (gate)
            {
                if (IsLocked) throw new InvalidOperationException("TuneIn settings are locked. Unlock them before saving.");
                save();
            }
        }

        internal void EnsureLocked()
        {
            lock (gate)
                if (!IsLocked) Set(true);
        }

        internal TuneInSettingsLock Set(bool value)
        {
            lock (gate)
            {
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("TuneIn settings lock storage is unavailable.");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        Serializer.Serialize(stream, new TuneInSettingsLock { Locked = value });
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                locked = value;
                return new TuneInSettingsLock { Locked = value };
            }
        }

        private bool Read()
        {
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("TuneIn settings lock storage is unavailable.");
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
                    return ((TuneInSettingsLock)Serializer.Deserialize(reader)).Locked;
            }
            catch (FileNotFoundException) { return initiallyLocked(); }
            catch (DirectoryNotFoundException) { return initiallyLocked(); }
        }
    }
}
