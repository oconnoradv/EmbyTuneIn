using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using System;
using System.Diagnostics;
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

    public sealed class TuneInDiscoveryCheckpoint
    {
        public string ConfigurationKey { get; set; }
        public string RequestedCategory { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime SavedUtc { get; set; }
        public List<TuneInCachedPage> Pages { get; set; } = new List<TuneInCachedPage>();
    }

    public sealed class TuneInItemSnapshot
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string ContentType { get; set; }
        public string MediaType { get; set; }
        public string ImageUrl { get; set; }
    }

    public sealed class TuneInFolderSnapshot
    {
        public string Id { get; set; }
        public List<TuneInItemSnapshot> Items { get; set; } = new List<TuneInItemSnapshot>();
    }

    public sealed class TuneInCategorySnapshot
    {
        public string Category { get; set; }
        public string RootId { get; set; }
        public string Fingerprint { get; set; }
        public bool DiscoveryPartial { get; set; }
        public List<TuneInFolderSnapshot> Folders { get; set; } = new List<TuneInFolderSnapshot>();
    }

    public sealed class TuneInRefreshCheckpoint
    {
        public string ConfigurationKey { get; set; }
        public DateTime SavedUtc { get; set; }
        public DateTime StartedUtc { get; set; }
        public List<TuneInCategorySnapshot> Categories { get; set; } = new List<TuneInCategorySnapshot>();
        public string DirtyCategory { get; set; }
        public int ChannelImportVersion { get; set; }
        public List<string> PendingCategories { get; set; } = new List<string>();
        public bool NeedsImport { get; set; }
    }

    internal sealed class TuneInRefreshStore
    {
        private const int MaxStateBytes = 120 * 1024 * 1024;
        // Checkpoint XML base64-encodes the raw catalog pages.
        private const int MaxDiscoveryStateBytes = 64 * 1024 * 1024;
        private readonly string path;
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(TuneInRefreshCheckpoint));
        private static readonly XmlSerializer DiscoverySerializer = new XmlSerializer(typeof(TuneInDiscoveryCheckpoint));
        private string DiscoveryPath => string.IsNullOrEmpty(path) ? null : path + ".discovery";

        internal TuneInRefreshStore(string path) { this.path = path; }

        internal TuneInRefreshCheckpoint Read()
        {
            var checkpoint = ReadState<TuneInRefreshCheckpoint>(path, Serializer);
            if (checkpoint != null) Validate(checkpoint);
            return checkpoint;
        }

        internal void Write(TuneInRefreshCheckpoint checkpoint)
        {
            Validate(checkpoint);
            WriteState(path, Serializer, checkpoint);
        }

        internal TuneInDiscoveryCheckpoint ReadDiscovery(string configurationKey, string requestedCategory,
            DateTime now, TimeSpan maxAge)
        {
            var checkpoint = ReadState<TuneInDiscoveryCheckpoint>(DiscoveryPath, DiscoverySerializer, MaxDiscoveryStateBytes);
            if (checkpoint == null || checkpoint.ConfigurationKey != configurationKey
                || checkpoint.RequestedCategory != requestedCategory || checkpoint.StartedUtc > checkpoint.SavedUtc
                || checkpoint.SavedUtc > now || now - checkpoint.StartedUtc >= maxAge)
                return null;
            if (checkpoint.Pages == null || checkpoint.Pages.Count == 0
                || checkpoint.Pages.Count > TuneInCatalogueSession.MaxRefreshPages
                || checkpoint.Pages.Any(page => page?.Key == null || page.Key.Length != 64
                    || page.Bytes == null || page.SavedUtc < checkpoint.StartedUtc || page.SavedUtc > checkpoint.SavedUtc)
                || checkpoint.Pages.Select(page => page.Key).Distinct(StringComparer.Ordinal).Count() != checkpoint.Pages.Count
                || checkpoint.Pages.Sum(page => (long)page.Bytes.Length) > TuneInCatalogueSession.MaxRefreshBytes)
                throw new InvalidDataException("Invalid TuneIn discovery checkpoint.");
            foreach (var page in checkpoint.Pages)
                TuneInDirectory.ParseCatalogue(page.Bytes);
            return checkpoint;
        }

        internal void WriteDiscovery(TuneInDiscoveryCheckpoint checkpoint) =>
            WriteState(DiscoveryPath, DiscoverySerializer, checkpoint, MaxDiscoveryStateBytes);

        internal void ClearDiscovery()
        {
            if (DiscoveryPath != null) File.Delete(DiscoveryPath);
        }

        private static T ReadState<T>(string path, XmlSerializer serializer, int maxBytes = MaxStateBytes) where T : class
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maxBytes }))
                {
                    if (stream.Length > maxBytes)
                        throw new InvalidDataException("TuneIn refresh state exceeds its limit.");
                    return (T)serializer.Deserialize(reader) ?? throw new InvalidDataException("Empty TuneIn refresh state.");
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static void WriteState(string path, XmlSerializer serializer, object checkpoint, int maxBytes = MaxStateBytes)
        {
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("TuneIn refresh state path is unavailable.");
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    serializer.Serialize(stream, checkpoint);
                    stream.Flush(true);
                    if (stream.Length > maxBytes)
                        throw new InvalidDataException("TuneIn refresh state exceeds its limit.");
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal void Clear()
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }

        private static void Validate(TuneInRefreshCheckpoint checkpoint)
        {
            var validCategories = new[] { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };
            if (checkpoint == null || checkpoint.ConfigurationKey?.Length != 64
                || checkpoint.Categories == null || checkpoint.Categories.Count > 8
                || checkpoint.PendingCategories == null || checkpoint.PendingCategories.Count > 8
                || checkpoint.PendingCategories.Any(category => !validCategories.Contains(category))
                || checkpoint.PendingCategories.Distinct().Count() != checkpoint.PendingCategories.Count
                || checkpoint.Categories.Any(category => category != null && checkpoint.PendingCategories.Contains(category.Category))
                || checkpoint.Categories.Any(category => category == null || !validCategories.Contains(category.Category)
                    || string.IsNullOrEmpty(category.RootId) || category.RootId.Length > 6000
                    || category.Folders == null || category.Folders.Count > TuneInStagedImport.MaxFolders
                    || category.Fingerprint?.Length != 64)
                || checkpoint.Categories.Select(category => category.Category).Distinct().Count() != checkpoint.Categories.Count
                || checkpoint.Categories.Sum(category => category.Folders.Sum(folder =>
                    128L + (folder?.Id?.Length ?? 0) * 2L + (folder?.Items ?? new List<TuneInItemSnapshot>())
                    .Sum(item => 256L + 2L * ((item?.Id?.Length ?? 0) + (item?.Name?.Length ?? 0)
                    + (item?.Type?.Length ?? 0) + (item?.ContentType?.Length ?? 0)
                    + (item?.MediaType?.Length ?? 0) + (item?.ImageUrl?.Length ?? 0))))) > TuneInStagedImport.MaxEstimatedBytes
                || (checkpoint.DirtyCategory != null && !validCategories.Contains(checkpoint.DirtyCategory)))
                throw new InvalidDataException("Invalid TuneIn refresh state.");

            foreach (var category in checkpoint.Categories)
            {
                if (category.Folders.Any(folder => folder == null || string.IsNullOrEmpty(folder.Id)
                    || folder.Id.Length > 6000 || folder.Items == null || folder.Items.Count > TuneInStagedImport.MaxItems
                    || folder.Items.Any(item => item == null || string.IsNullOrEmpty(item.Id)
                        || item.Id.Length > 6000 || (item.Name ?? "").Length > 256
                        || (item.ImageUrl ?? "").Length > 2048
                        || !Enum.IsDefined(typeof(ChannelItemType), item.Type)
                        || !Enum.IsDefined(typeof(ChannelMediaContentType), item.ContentType)
                        || !Enum.IsDefined(typeof(ChannelMediaType), item.MediaType))))
                    throw new InvalidDataException("Invalid TuneIn folder snapshot.");
                if (!category.Folders.Any(folder => folder.Id == category.RootId)
                    || category.Folders.Select(folder => folder.Id).Distinct(StringComparer.Ordinal).Count() != category.Folders.Count)
                    throw new InvalidDataException("TuneIn category snapshot has invalid folder identities.");
                if (TuneInStagedImport.Fingerprint(category.Folders) != category.Fingerprint)
                    throw new InvalidDataException("TuneIn category snapshot fingerprint does not match its items.");
            }
        }

    }

    internal sealed class TuneInCatalogueSession
    {
        internal const int MaxPages = 128;
        internal const int MaxBytes = 8 * 1024 * 1024;
        internal const int MaxRefreshPages = 5000;
        internal const int MaxRefreshBytes = 40 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Dictionary<string, TuneInCachedPage> pages = new Dictionary<string, TuneInCachedPage>();
        private readonly Func<DateTime> clock;
        private readonly TimeSpan lifetime;
        private readonly Action<bool> requested;
        private readonly Action completed;
        private readonly Action<int> received;
        private readonly Action<string, string> skippedBrowseLink;
        private readonly bool strictBudget;
        private readonly int maxPages;
        private readonly int maxBytes;
        private long cachedBytes;
        private int rateLimitRetriesRemaining;
        internal int SkippedBrowseLinks { get; private set; }
        internal int SkippedStationUrls { get; private set; }
        internal TuneInRefreshTimings Timings { get; }

        internal TuneInCatalogueSession(IEnumerable<TuneInCachedPage> cached, TimeSpan lifetime, Func<DateTime> clock,
            Action<bool> requested = null, Action completed = null, Action<int> received = null,
            bool strictBudget = true, bool retryRateLimit = false, Action<string, string> skippedBrowseLink = null,
            int maxPages = MaxPages, TuneInRefreshTimings timings = null, int maxBytes = MaxBytes)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.lifetime = lifetime;
            this.requested = requested;
            this.completed = completed;
            this.received = received;
            this.skippedBrowseLink = skippedBrowseLink;
            this.strictBudget = strictBudget;
            this.maxPages = maxPages;
            this.maxBytes = maxBytes;
            Timings = timings;
            rateLimitRetriesRemaining = retryRateLimit ? 3 : 0;
            foreach (var page in cached ?? Enumerable.Empty<TuneInCachedPage>())
                if (Fresh(page)) CachePage(page);
        }

        private void CachePage(TuneInCachedPage page)
        {
            RemovePage(page.Key);
            pages.Add(page.Key, page);
            cachedBytes += page.Bytes.Length;
        }

        private void RemovePage(string key)
        {
            if (!pages.TryGetValue(key, out var page)) return;
            cachedBytes -= page.Bytes.Length;
            pages.Remove(key);
        }

        internal bool TrySkipCatalogLink(string category, string reason, bool stationUrl = false)
        {
            if (skippedBrowseLink == null) return false;
            SkippedBrowseLinks++;
            if (stationUrl) SkippedStationUrls++;
            skippedBrowseLink(category, stationUrl ? "station URL: " + reason : reason);
            return true;
        }

        internal List<TuneInCachedPage> SnapshotPages()
        {
            lock (gate) return pages.Values.ToList();
        }

        private bool Fresh(TuneInCachedPage page) =>
            page != null && page.SavedUtc <= clock() && clock() - page.SavedUtc < lifetime;

        internal async Task<XDocument> FetchAsync(DirectoryRequest request, Func<Task<XDocument>> fetch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string key;
            using (var hash = SHA256.Create())
                key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(request.Url))).Replace("-", "");

            TuneInCachedPage cached;
            lock (gate) pages.TryGetValue(key, out cached);
            if (cached != null && Fresh(cached))
            {
                requested?.Invoke(true);
                token.ThrowIfCancellationRequested();
                var replay = Timings == null ? null : Stopwatch.StartNew();
                try { return TuneInDirectory.ParseCatalogue(cached.Bytes); }
                finally { if (Timings != null) Timings.CacheReplay += replay.Elapsed; }
            }

            lock (gate)
                if (strictBudget && pages.Count >= maxPages && !pages.ContainsKey(key))
                    throw new InvalidDataException($"TuneIn refresh exceeds its {maxPages}-page catalog limit.");
            XDocument document;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                requested?.Invoke(false);
                var elapsed = Timings == null ? null : Stopwatch.StartNew();
                try
                {
                    document = await fetch().ConfigureAwait(false);
                    break;
                }
                catch (TuneInProviderResponseException failure)
                {
                    received?.Invoke(failure.StatusCode);
                    if (failure.StatusCode != 429 || Interlocked.Decrement(ref rateLimitRetriesRemaining) < 0)
                        throw;
                }
                finally { if (Timings != null) Timings.Fetch += elapsed.Elapsed; }
            }
            token.ThrowIfCancellationRequested();
            var bytes = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
            var validated = TuneInDirectory.ParseCatalogue(bytes);
            lock (gate)
            {
                if (bytes.Length > maxBytes)
                    throw new InvalidDataException($"TuneIn catalog page exceeds its {maxBytes / (1024 * 1024)} MiB cache limit.");
                if (!strictBudget)
                {
                    RemovePage(key);
                    foreach (var expired in pages.Where(pair => !Fresh(pair.Value)).Select(pair => pair.Key).ToList())
                        RemovePage(expired);
                    while (pages.Count >= maxPages || cachedBytes + bytes.Length > maxBytes)
                        RemovePage(pages.OrderBy(pair => pair.Value.SavedUtc).First().Key);
                }
                pages.TryGetValue(key, out var previous);
                var otherBytes = cachedBytes - (previous?.Bytes.Length ?? 0);
                if (pages.Count >= maxPages && previous == null)
                    throw new InvalidDataException($"TuneIn refresh exceeds its {maxPages}-page catalog limit.");
                if (otherBytes + bytes.Length > maxBytes)
                    throw new InvalidDataException($"TuneIn refresh exceeds its {maxBytes / (1024 * 1024)} MiB catalog cache limit.");
                CachePage(new TuneInCachedPage { Key = key, Bytes = bytes, SavedUtc = clock() });
            }
            completed?.Invoke();
            return validated;
        }

    }
}
