using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class TuneInCachedPage
    {
        public string Key { get; set; }
        public DateTime SavedUtc { get; set; }
        public byte[] Bytes { get; set; }
    }

    public sealed class TuneInRefreshCheckpoint
    {
        public string ConfigurationKey { get; set; }
        public DateTime SavedUtc { get; set; }
        public DateTime StartedUtc { get; set; }
        public List<string> CompletedCategories { get; set; } = new List<string>();
        public List<TuneInCachedPage> Pages { get; set; } = new List<TuneInCachedPage>();
        public bool ImportMayHavePartiallyApplied { get; set; }
    }

    internal sealed class TuneInRefreshStore
    {
        private readonly string path;
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(TuneInRefreshCheckpoint));

        internal TuneInRefreshStore(string path = null) { this.path = path; }

        internal TuneInRefreshCheckpoint Read()
        {
            if (path == null || !File.Exists(path)) return null;
            if (new FileInfo(path).Length > 12 * 1024 * 1024)
                throw new InvalidDataException("TuneIn refresh checkpoint exceeds its limit.");
            using (var stream = File.OpenRead(path))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 12 * 1024 * 1024 }))
            {
                var checkpoint = (TuneInRefreshCheckpoint)Serializer.Deserialize(reader);
                Validate(checkpoint);
                return checkpoint;
            }
        }

        internal void Write(TuneInRefreshCheckpoint checkpoint)
        {
            Validate(checkpoint);
            if (path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    Serializer.Serialize(stream, checkpoint);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal void Clear()
        {
            if (path != null && File.Exists(path)) File.Delete(path);
        }

        private static void Validate(TuneInRefreshCheckpoint checkpoint)
        {
            if (checkpoint == null || checkpoint.ConfigurationKey?.Length != 64
                || checkpoint.CompletedCategories == null || checkpoint.CompletedCategories.Count > 8
                || checkpoint.CompletedCategories.Any(category => !new[] { "local", "music", "language",
                    "location", "talk", "sports", "podcast", "presets" }.Contains(category))
                || checkpoint.CompletedCategories.Distinct().Count() != checkpoint.CompletedCategories.Count
                || checkpoint.Pages == null || checkpoint.Pages.Count > TuneInCatalogueSession.MaxPages
                || checkpoint.Pages.Any(page => page?.Bytes == null || page.Bytes.Length == 0
                    || page.Bytes.Length > 2 * 1024 * 1024 || page.Key?.Length != 64)
                || checkpoint.Pages.Sum(page => (long)page.Bytes.Length) > TuneInCatalogueSession.MaxBytes
                || checkpoint.Pages.Select(page => page.Key).Distinct().Count() != checkpoint.Pages.Count)
                throw new InvalidDataException("Invalid TuneIn refresh checkpoint.");
            foreach (var page in checkpoint.Pages) TuneInDirectory.ParseCatalogue(page.Bytes);
        }
    }

    internal sealed class TuneInCatalogueSession
    {
        internal const int MaxPages = 128;
        internal const int MaxBytes = 8 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Dictionary<string, TuneInCachedPage> pages = new Dictionary<string, TuneInCachedPage>();
        private readonly Func<DateTime> clock;
        private readonly TimeSpan lifetime;
        private readonly Action<bool> requested;
        private readonly Action completed;
        private readonly Action<int> received;

        internal TuneInCatalogueSession(IEnumerable<TuneInCachedPage> cached, TimeSpan lifetime, Func<DateTime> clock,
            Action<bool> requested, Action completed, Action<int> received = null)
        {
            this.clock = clock;
            this.lifetime = lifetime;
            this.requested = requested;
            this.completed = completed;
            this.received = received;
            foreach (var page in cached ?? Enumerable.Empty<TuneInCachedPage>())
                if (Fresh(page)) pages.Add(page.Key, page);
        }

        private bool Fresh(TuneInCachedPage page) => page.SavedUtc <= clock() && clock() - page.SavedUtc < lifetime;

        internal async Task<XDocument> FetchAsync(DirectoryRequest request, Func<Task<XDocument>> fetch, CancellationToken token)
        {
            string key;
            using (var hash = SHA256.Create())
                key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(request.Url))).Replace("-", "");
            TuneInCachedPage cached;
            lock (gate) pages.TryGetValue(key, out cached);
            if (cached != null && Fresh(cached))
            {
                requested(true);
                return TuneInDirectory.ParseCatalogue(cached.Bytes);
            }
            lock (gate)
                if (pages.Count >= MaxPages && !pages.ContainsKey(key))
                    throw new InvalidDataException("TuneIn refresh exceeds its 128-page catalogue limit.");
            requested(false);
            var document = await fetch().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var bytes = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
            var validated = TuneInDirectory.ParseCatalogue(bytes);
            lock (gate)
            {
                if (pages.Count >= MaxPages && !pages.ContainsKey(key)
                    || pages.Where(pair => pair.Key != key).Sum(pair => (long)pair.Value.Bytes.Length) + bytes.Length > MaxBytes)
                    throw new InvalidDataException("TuneIn refresh exceeds its 128-page / 8 MiB catalogue limit.");
                pages[key] = new TuneInCachedPage { Key = key, Bytes = bytes, SavedUtc = clock() };
            }
            completed();
            return validated;
        }

        internal List<TuneInCachedPage> Capture()
        {
            lock (gate) return pages.Values.Where(Fresh).ToList();
        }

        internal void ReportHttpResponse(int status) => received?.Invoke(status);
    }
}
