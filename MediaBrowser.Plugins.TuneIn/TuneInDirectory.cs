using MediaBrowser.Plugins.TuneIn.Configuration;
using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.Serialization;
using System.Net.Sockets;
using System.Security.Authentication;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class ConfigurationSnapshot
    {
        public PluginConfiguration Value { get; }
        public string Key { get; }
        public ConfigurationSnapshot(PluginConfiguration value)
        {
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            {
                serializer.Serialize(writer, value);
                var xml = writer.ToString();
                using (var reader = new StringReader(xml))
                    Value = (PluginConfiguration)serializer.Deserialize(reader);
                using (var hash = SHA256.Create())
                    Key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(xml))).Replace("-", "");
            }
        }

        public bool Ready => Value.ConfigurationCompleted && Value.SchemaVersion == 1;
        public IEnumerable<string> Categories => new[] { "local", "music", "language", "location",
            "talk", "sports", "podcast", "presets" }.Where(Enabled);

        public bool Enabled(string category)
        {
            if (!Ready) return false;
            switch (category)
            {
                case "local":
                    return Value.EnableLocalRadio
                    && ((Value.UseCoordinates && TryCoordinates(Value.LatLon, out _))
                        || Selections(category).Any());
                case "music": return Value.EnableMusic && Selections(category).Any();
                case "language": return Value.EnableLanguage && Selections(category).Any();
                case "location": return Value.EnableLocation && Selections(category).Any();
                case "talk": return Value.EnableTalk;
                case "sports": return Value.EnableSports;
                case "podcast": return Value.EnablePodcasts;
                case "presets": return Value.EnableFavorites && !string.IsNullOrWhiteSpace(Value.Username);
                default: return false;
            }
        }

        public IEnumerable<CatalogSelection> Selections(string category)
        {
            var items = category == "music" ? Value.Music : category == "language" ? Value.Languages
                : category == "location" ? Value.Locations : category == "local" ? Value.LocalCities : null;
            return (items ?? new List<CatalogSelection>()).Where(s => s != null && s.Enabled
                && ValidId(s.Id, category)).GroupBy(s => s.Id).Select(g => g.First());
        }

        public static bool ValidId(string id, string category) => id != null && Regex.IsMatch(id,
            category == "language" ? @"^l[1-9][0-9]{0,9}$"
                : category == "music" ? @"^[cg][1-9][0-9]{0,9}$" : @"^r[1-9][0-9]{0,9}$");

        public static bool TryCoordinates(string value, out string normalized)
        {
            normalized = null;
            var parts = (value ?? "").Split(',');
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var lat) || !double.TryParse(parts[1],
                NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                || double.IsNaN(lat) || double.IsInfinity(lat) || lat < -90 || lat > 90
                || double.IsNaN(lon) || double.IsInfinity(lon) || lon < -180 || lon > 180)
                return false;
            normalized = lat.ToString("R", CultureInfo.InvariantCulture) + ","
                + lon.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }

        public static void Validate(PluginConfiguration value)
        {
            if (Convert.FromBase64String(value.NavigationSecret ?? "").Length != 32)
                throw new ArgumentException("Invalid TuneIn navigation signing key.");
            if (value.UseCoordinates && !TryCoordinates(value.LatLon, out _))
                throw new ArgumentException("Enter finite latitude (-90..90), longitude (-180..180), separated by a comma.");
            if ((value.Username ?? "").Length > 128)
                throw new ArgumentException("TuneIn username exceeds 128 characters.");
            if (value.ApiRequestIntervalSeconds < PluginConfiguration.MinApiRequestIntervalSeconds
                || value.ApiRequestIntervalSeconds > PluginConfiguration.MaxApiRequestIntervalSeconds)
                throw new ArgumentException("TuneIn API request interval must be between 1 and 30 seconds.");
            foreach (var category in new[] { "music", "language", "location", "local" })
            {
                var items = category == "music" ? value.Music : category == "language" ? value.Languages
                    : category == "location" ? value.Locations : value.LocalCities;
                if ((items?.Count ?? 0) > 100 || (items ?? new List<CatalogSelection>()).Any(s =>
                    s == null || !ValidId(s.Id, category) || (s.Name ?? "").Length > 256
                    || (s.Path ?? "").Length > 2048 || (s.AncestorIds?.Count ?? 0) > 20
                    || (s.AncestorIds ?? new List<string>()).Any(id => id != "r0" && id != "music"
                        && id != "lang" && !ValidId(id, category))))
                    throw new ArgumentException("Invalid or excessive TuneIn selections.");
            }
        }
    }

    internal sealed class DirectoryRequest
    {
        private static readonly object LinkDiagnosticKey = new object();
        public string Method { get; set; } = "Browse.ashx";
        public Dictionary<string, string> Parameters { get; } = new Dictionary<string, string>();
        public string Url => "https://opml.radiotime.com/" + Method + "?"
            + string.Join("&", Parameters.OrderBy(p => p.Key).Select(p =>
                Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

        public DirectoryRequest Copy()
        {
            var copy = new DirectoryRequest { Method = Method };
            foreach (var pair in Parameters) copy.Parameters.Add(pair.Key, pair.Value);
            return copy;
        }

        public static DirectoryRequest ParseLink(string value)
        {
            value = HttpUtility.HtmlDecode(value);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                throw RejectedLink("missing or invalid absolute URL");
            if (uri.Scheme != "http" && uri.Scheme != "https")
                throw RejectedLink("unsupported scheme");
            if (!uri.IsDefaultPort) throw RejectedLink("non-default port");
            if (uri.UserInfo.Length != 0) throw RejectedLink("credentials in URL");
            if (!uri.Host.Equals("opml.radiotime.com", StringComparison.OrdinalIgnoreCase))
                throw RejectedLink("untrusted host");
            if (uri.AbsolutePath != "/Browse.ashx" && uri.AbsolutePath != "/Tune.ashx")
                throw RejectedLink("unsupported path");
            var query = HttpUtility.ParseQueryString(uri.Query);
            var lastParameter = HttpUtility.UrlDecode(uri.Query.Substring(uri.Query.LastIndexOf('&') + 1).TrimStart('?'));
            // TuneIn emits the # alphabet filter unescaped in some pagination links.
            var trailingAlphabetHash = uri.Fragment == "#"
                && (lastParameter == "filter=s:~" || lastParameter == "filter=p:~")
                && query.GetValues("filter")?.Length == 1;
            if (uri.Fragment.Length != 0 && !trailingAlphabetHash) throw RejectedLink("URL fragment");
            if (trailingAlphabetHash) query["filter"] += "#";
            var request = new DirectoryRequest { Method = uri.AbsolutePath.Substring(1) };
            foreach (var key in new[] { "id", "c", "filter", "offset", "pivot", "sid" })
            {
                var item = query[key];
                if (item == null) continue;
                if (!Regex.IsMatch(item, @"\A[A-Za-z0-9:,*-]{1,80}\z")
                    && !(key == "filter" && Regex.IsMatch(item,
                        @"\A[sp]:~[\p{L}\p{Nd}#](?::l[1-9][0-9]{0,9})?\z")))
                {
                    var reason = item.Length == 0 ? "empty"
                        : item.Length > 80 ? "longer than 80 characters"
                        : item.Any(char.IsControl) ? "contains a control character"
                        : item.Any(char.IsWhiteSpace) ? "contains whitespace"
                        : item.Any(character => character > 127) ? "contains non-ASCII characters"
                        : "contains unsupported punctuation";
                    var message = "Invalid catalogue link parameter '" + key + "' (" + reason
                        + "; length " + item.Length.ToString(CultureInfo.InvariantCulture)
                        + "; occurrences " + (query.GetValues(key)?.Length ?? 0).ToString(CultureInfo.InvariantCulture) + ").";
                    var error = new InvalidDataException(message);
                    error.Data[LinkDiagnosticKey] = message;
                    throw error;
                }
                request.Parameters[key] = item;
            }
            if (request.Parameters.Count == 0) throw new InvalidDataException("Empty catalogue link rejected.");
            return request;
        }

        private static InvalidDataException RejectedLink(string reason)
        {
            var message = "Untrusted catalogue link rejected (" + reason + ").";
            var error = new InvalidDataException(message);
            error.Data[LinkDiagnosticKey] = message;
            return error;
        }

        internal static string LinkDiagnostic(Exception error)
        {
            for (; error != null; error = error.InnerException)
                if (error.Data[LinkDiagnosticKey] is string message) return message;
            return null;
        }
    }

    internal sealed class NavigationEntry
    {
        public string Category { get; set; }
        public string SelectionId { get; set; }
        public DirectoryRequest Request { get; set; }
        public string Name { get; set; }
        public string StationId { get; set; }
        public string ArtworkReference { get; set; }
        public string ContentCategory { get; set; }
        public string MusicSelectionId { get; set; }
    }

    public sealed class SelectorItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public List<string> AncestorIds { get; set; }
    }

    public sealed class SelectorResult
    {
        public List<SelectorItem> Items { get; set; }
        public bool ContainsStationMetadata { get; set; }
        public string Warning { get; set; }
    }

    internal sealed class TuneInDirectory
    {
        private static readonly XmlSchemaSet CatalogueSchemas = CreateCatalogueSchemas();
        public static TuneInDirectory Instance { get; } = new TuneInDirectory();
        private readonly object gate = new object();
        private readonly Dictionary<string, SelectorResult> selectors = new Dictionary<string, SelectorResult>();
        private readonly Dictionary<string, CachedPage> pages = new Dictionary<string, CachedPage>();
        private readonly Dictionary<string, CachedPage> recentRefreshPages = new Dictionary<string, CachedPage>();
        private readonly Dictionary<string, CancellationTokenSource> categoryChanges = new Dictionary<string, CancellationTokenSource>();
        private readonly AsyncLocal<RefreshContext> refreshContext = new AsyncLocal<RefreshContext>();
        private readonly AsyncLocal<RefreshRun> refreshRun = new AsyncLocal<RefreshRun>();
        private int cachedBytes;
        private int recentRefreshBytes;
        private long cacheGeneration;
        private readonly SemaphoreSlim throttle = new SemaphoreSlim(1, 1);
        private readonly SafeHttpTransport transport;
        private readonly Func<PluginConfiguration> configuration;
        private readonly Func<DirectoryRequest, CancellationToken, Task<XDocument>> fetch;
        private readonly Func<DateTime> utcNow;
        private readonly Func<TimeSpan, CancellationToken, Task> wait;
        private CancellationTokenSource changed = new CancellationTokenSource();
        private string key;
        private DateTime lastRequest;
        private DateTime nextRequest;

        internal TuneInDirectory(Func<PluginConfiguration> configuration = null,
            Func<DirectoryRequest, CancellationToken, Task<XDocument>> fetch = null, SafeHttpTransport transport = null,
            Func<DateTime> utcNow = null, Func<TimeSpan, CancellationToken, Task> wait = null)
        {
            this.configuration = configuration ?? (() => Plugin.Instance.Configuration);
            this.fetch = fetch;
            this.transport = transport ?? new SafeHttpTransport();
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.wait = wait ?? Task.Delay;
        }

        public void Reset()
        {
            lock (gate)
            {
                changed.Cancel();
                changed.Dispose();
                changed = new CancellationTokenSource();
                selectors.Clear();
                ClearPages();
                key = null;
            }
        }

        public ConfigurationSnapshot Snapshot()
        {
            var snapshot = new ConfigurationSnapshot(configuration());
            lock (gate)
            {
                if (key != snapshot.Key)
                {
                    changed.Cancel();
                    changed.Dispose();
                    changed = new CancellationTokenSource();
                    selectors.Clear();
                    ClearPages();
                    key = snapshot.Key;
                }
            }
            return snapshot;
        }

        public CancellationTokenSource Operation(ConfigurationSnapshot snapshot, CancellationToken token)
        {
            lock (gate)
            {
                if (snapshot.Key != key) throw new OperationCanceledException("TuneIn configuration changed.");
                return CancellationTokenSource.CreateLinkedTokenSource(token, changed.Token);
            }
        }

        public string Register(ConfigurationSnapshot snapshot, NavigationEntry entry)
        {
            lock (gate)
            {
                if (snapshot.Key != key) throw new OperationCanceledException("TuneIn configuration changed.");
                var document = new XElement("entry", new XAttribute("key", snapshot.Key),
                    new XElement("category", entry.Category), new XElement("selection", entry.SelectionId ?? ""),
                    new XElement("station", entry.StationId ?? ""), new XElement("name", entry.Name ?? ""));
                if (entry.ContentCategory != null)
                    document.Add(new XElement("contentCategory", entry.ContentCategory));
                if (entry.MusicSelectionId != null)
                    document.Add(new XElement("musicSelection", entry.MusicSelectionId));
                if (entry.ArtworkReference != null)
                {
                    TuneInArtwork.ValidateReference(entry.ArtworkReference);
                    document.Add(new XElement("artwork", entry.ArtworkReference));
                }
                if (entry.Request != null)
                {
                    var request = new XElement("request", new XAttribute("method", entry.Request.Method));
                    foreach (var pair in entry.Request.Parameters.Where(p =>
                        new[] { "c", "id", "filter", "offset", "pivot", "sid" }.Contains(p.Key)).OrderBy(p => p.Key))
                        request.Add(new XElement(pair.Key, pair.Value));
                    document.Add(request);
                }
                var bytes = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
                if (bytes.Length > 4096) throw new InvalidDataException("TuneIn navigation entry exceeds limit.");
                using (var hmac = new HMACSHA256(Convert.FromBase64String(snapshot.Value.NavigationSecret)))
                    return Encode(bytes) + "." + Encode(hmac.ComputeHash(bytes));
            }
        }

        public NavigationEntry Resolve(ConfigurationSnapshot snapshot, string id)
        {
            lock (gate)
            {
                if (snapshot.Key != key || id == null || id.Length > 6000
                    || !Regex.IsMatch(id, @"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{43}$"))
                    throw new InvalidOperationException("Invalid TuneIn navigation identifier.");
                var parts = id.Split('.');
                var bytes = Decode(parts[0]);
                var supplied = Decode(parts[1]);
                using (var hmac = new HMACSHA256(Convert.FromBase64String(snapshot.Value.NavigationSecret)))
                {
                    var expected = hmac.ComputeHash(bytes);
                    var difference = 0;
                    for (var i = 0; i < expected.Length; i++) difference |= expected[i] ^ supplied[i];
                    if (difference != 0) throw new InvalidOperationException("TuneIn navigation signature rejected.");
                }
                using (var stream = new MemoryStream(bytes))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
                {
                    var document = XElement.Load(reader);
                    if ((string)document.Attribute("key") != snapshot.Key)
                        throw new InvalidOperationException("TuneIn configuration changed. Return to the channel root.");
                    var entry = new NavigationEntry
                    {
                        Category = document.Element("category")?.Value,
                        SelectionId = EmptyToNull(document.Element("selection")?.Value),
                        StationId = EmptyToNull(document.Element("station")?.Value),
                        Name = document.Element("name")?.Value,
                        ContentCategory = EmptyToNull(document.Element("contentCategory")?.Value),
                        MusicSelectionId = EmptyToNull(document.Element("musicSelection")?.Value),
                        ArtworkReference = EmptyToNull(document.Element("artwork")?.Value)
                    };
                    var request = document.Element("request");
                    if (request != null)
                    {
                        entry.Request = new DirectoryRequest { Method = (string)request.Attribute("method") };
                        foreach (var pair in request.Elements()) entry.Request.Parameters.Add(pair.Name.LocalName, pair.Value);
                    }
                    if (!Allowed(snapshot, entry))
                        throw new InvalidOperationException("TuneIn item is disabled. Return to the channel root.");
                    if (entry.ArtworkReference != null) TuneInArtwork.ValidateReference(entry.ArtworkReference);
                    return entry;
                }
            }
        }

        private static string EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;
        private static bool Allowed(ConfigurationSnapshot snapshot, NavigationEntry entry) =>
            snapshot.Enabled(entry.Category)
            && (entry.Category != "language" || LanguageContentAllowed(snapshot, entry))
            && (entry.SelectionId == null
                || (entry.SelectionId == "coordinates" ? entry.Category == "local" && snapshot.Value.UseCoordinates
                    && ConfigurationSnapshot.TryCoordinates(snapshot.Value.LatLon, out _)
                    : snapshot.Selections(entry.Category).Any(s => s.Id == entry.SelectionId)));

        internal static bool LanguageContentAllowed(ConfigurationSnapshot snapshot, NavigationEntry entry)
        {
            if (entry.ContentCategory != null && !new[] { "language", "music", "talk", "sports" }.Contains(entry.ContentCategory))
                return false;
            if (entry.ContentCategory != null && entry.ContentCategory != "language" && !snapshot.Enabled(entry.ContentCategory))
                return false;
            if (entry.StationId != null && entry.ContentCategory == null) return false;
            var group = TuneInChannel.LanguageGroup(entry, entry.Request);
            if (group != "language" && !snapshot.Enabled(group)) return false;
            if (group != "music") return entry.MusicSelectionId == null;
            if (entry.MusicSelectionId == null) return TuneInChannel.IsLanguageMusicHub(entry);
            if (!snapshot.Selections("music").Any(selection => selection.Id == entry.MusicSelectionId)) return false;
            if (entry.StationId != null) return entry.Request == null;
            var id = entry.Request?.Parameters.GetValueOrDefault("id");
            return ConfigurationSnapshot.ValidId(id, "music")
                && id != "c424724" && id != "c424725" && id != "c424726"
                && !entry.Request.Parameters.ContainsKey("c");
        }
        private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static byte[] Decode(string value)
        {
            value = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
        }

        private void ClearPages()
        {
            cacheGeneration++;
            foreach (var cancellation in categoryChanges.Values) { cancellation.Cancel(); cancellation.Dispose(); }
            categoryChanges.Clear();
            pages.Clear();
            cachedBytes = 0;
            recentRefreshPages.Clear();
            recentRefreshBytes = 0;
        }

        public void InvalidateCategory(string category)
        {
            lock (gate)
            {
                if (categoryChanges.TryGetValue(category, out var cancellation))
                { cancellation.Cancel(); cancellation.Dispose(); }
                categoryChanges[category] = new CancellationTokenSource();
                foreach (var id in pages.Where(pair => pair.Value.Category == category).Select(pair => pair.Key).ToList())
                { cachedBytes -= pages[id].Bytes.Length; pages.Remove(id); }
            }
        }

        public async Task<XDocument> FetchCategoryAsync(ConfigurationSnapshot snapshot, string category,
            DirectoryRequest request, CancellationToken token, bool refresh = false, Action<int> statusReceived = null)
        {
            var context = refreshContext.Value;
            try
            {
                return await FetchCategoryCoreAsync(snapshot, category, request, token, refresh, statusReceived).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (context != null) context.Error = error;
                throw;
            }
        }

        private async Task<XDocument> FetchCategoryCoreAsync(ConfigurationSnapshot snapshot, string category,
            DirectoryRequest request, CancellationToken token, bool refresh, Action<int> statusReceived)
        {
            if (!snapshot.Enabled(category)) throw new InvalidOperationException("Radio group is not configured or enabled.");
            if (refreshContext.Value != null && refreshContext.Value.Category != category)
                throw new InvalidOperationException("Emby attempted to refresh a different radio group.");
            var id = RequestKey(snapshot, category, request);
            CancellationToken categoryToken;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (snapshot.Key != key) throw new OperationCanceledException("Configuration changed.");
                if (refreshContext.Value != null && refreshRun.Value != null
                    && (refreshRun.Value.Key != key || refreshRun.Value.Generation != cacheGeneration))
                    throw new OperationCanceledException("TuneIn configuration changed.");
                if (!refresh && pages.TryGetValue(id, out var cached) && cached.Expires > DateTime.UtcNow)
                {
                    refreshContext.Value?.Pages.Add(id);
                    refreshContext.Value?.CacheHit?.Invoke();
                    return ParseCatalogue(cached.Bytes);
                }
                var sharedKey = RequestKey(snapshot, "", request);
                if (!refresh && refreshContext.Value != null && refreshRun.Value != null
                    && refreshRun.Value.Pages.TryGetValue(sharedKey, out var shared))
                {
                    refreshContext.Value.Pages.Add(id);
                    refreshContext.Value.CacheHit?.Invoke();
                    return ParseCatalogue(shared.Bytes);
                }
                if (!refresh && refreshContext.Value != null && refreshRun.Value != null
                    && recentRefreshPages.TryGetValue(sharedKey, out var recent) && recent.Expires > utcNow())
                {
                    refreshContext.Value.Pages.Add(id);
                    refreshContext.Value.CacheHit?.Invoke();
                    return ParseCatalogue(recent.Bytes);
                }
                if (refreshContext.Value != null && refreshContext.Value.RequestStarted == null)
                    throw new InvalidOperationException("Emby requested a page outside the bounded group refresh.");
                if (refreshContext.Value != null)
                {
                    if (++refreshContext.Value.RequestCount > 1000)
                        throw new InvalidOperationException("Group refresh exceeded 1000 catalogue requests.");
                    refreshContext.Value.RequestStarted(request);
                    statusReceived = refreshContext.Value.StatusReceived;
                }
                if (!categoryChanges.TryGetValue(category, out var cancellation))
                    categoryChanges[category] = cancellation = new CancellationTokenSource();
                categoryToken = cancellation.Token;
            }
            using (var operation = CancellationTokenSource.CreateLinkedTokenSource(token, categoryToken))
            {
                var document = await FetchCoreAsync(request, operation.Token, statusReceived, true).ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
                lock (gate)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    if (snapshot.Key != key) throw new OperationCanceledException("Configuration changed.");
                    if (pages.TryGetValue(id, out var previous)) cachedBytes -= previous.Bytes.Length;
                    pages.Remove(id);
                    while (pages.Count > 0 && (cachedBytes + bytes.Length > 8 * 1024 * 1024 || pages.Count >= 128))
                    {
                        var oldest = pages.OrderBy(pair => pair.Value.Expires).First();
                        cachedBytes -= oldest.Value.Bytes.Length;
                        pages.Remove(oldest.Key);
                    }
                    // Retain only bounded validated catalogue data, never streams or request URLs.
                    if (bytes.Length <= 2 * 1024 * 1024 && cachedBytes + bytes.Length <= 8 * 1024 * 1024 && pages.Count < 128)
                    {
                        pages[id] = new CachedPage { Category = category, Bytes = bytes, Expires = DateTime.UtcNow.AddMinutes(refresh ? 30 : 10) };
                        cachedBytes += bytes.Length;
                    }
                    else if (refresh)
                        throw new InvalidOperationException("Catalogue cache capacity exceeded; refresh fewer saved selections.");
                    var run = refreshRun.Value;
                    var sharedKey = RequestKey(snapshot, "", request);
                    if (run != null && refreshContext.Value != null && !run.Pages.ContainsKey(sharedKey)
                        && run.Pages.Count < 128 && run.Bytes + bytes.Length <= 8 * 1024 * 1024)
                    {
                        run.Pages.Add(sharedKey, new CachedPage { Bytes = bytes, Expires = utcNow().AddMinutes(10) });
                        run.Bytes += bytes.Length;
                    }
                }
                if (refreshContext.Value != null)
                {
                    lock (gate) refreshContext.Value.Pages.Add(RequestKey(snapshot, category, request));
                    refreshContext.Value.RequestCompleted?.Invoke(document);
                }
                return document;
            }
        }

        internal static string RequestKey(ConfigurationSnapshot snapshot, string category, DirectoryRequest request)
        {
            using (var hash = SHA256.Create())
                return category + ":" + Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(snapshot.Key + request.Url)));
        }

        public async Task SynchronizeCategoryAsync(string category, int expectedPages, Func<Task> synchronize,
            Action<DirectoryRequest> requestStarted = null, Action<int> statusReceived = null,
            Action<XDocument> requestCompleted = null, IEnumerable<string> requiredPages = null,
            Action cacheHit = null, Action discoveryComplete = null, Action<string> phaseChanged = null)
        {
            var previous = refreshContext.Value;
            var context = new RefreshContext
            {
                Category = category,
                RequestStarted = requestStarted,
                StatusReceived = statusReceived,
                RequestCompleted = requestCompleted,
                ExpectedPages = expectedPages,
                RequiredPages = requiredPages?.ToArray(),
                CacheHit = cacheHit,
                DiscoveryComplete = discoveryComplete,
                PhaseChanged = phaseChanged
            };
            refreshContext.Value = context;
            try
            {
                await synchronize().ConfigureAwait(false);
                if (context.Error is OperationCanceledException cancellation) throw cancellation;
                if (context.Error != null) throw new InvalidOperationException("Emby encountered a failed scoped catalogue request.", context.Error);
                VerifyRefreshDiscovery();
            }
            finally { refreshContext.Value = previous; }
        }

        internal async Task WithRefreshRunAsync(Func<Task> action)
        {
            var previous = refreshRun.Value;
            Snapshot();
            lock (gate) refreshRun.Value = new RefreshRun { Key = key, Generation = cacheGeneration };
            try { await action().ConfigureAwait(false); }
            finally { refreshRun.Value = previous; }
        }

        internal void CommitRefreshRun()
        {
            var run = refreshRun.Value ?? throw new InvalidOperationException("No refresh run is active.");
            lock (gate)
            {
                if (run.Key != key || run.Generation != cacheGeneration)
                    throw new OperationCanceledException("TuneIn configuration changed.");
                foreach (var pair in run.Pages)
                {
                    if (pair.Value.Expires <= utcNow()) continue;
                    if (recentRefreshPages.TryGetValue(pair.Key, out var previous))
                    {
                        if (previous.Expires >= pair.Value.Expires) continue;
                        recentRefreshBytes -= previous.Bytes.Length;
                        recentRefreshPages.Remove(pair.Key);
                    }
                    while (recentRefreshPages.Count > 0
                        && (recentRefreshPages.Count >= 128 || recentRefreshBytes + pair.Value.Bytes.Length > 8 * 1024 * 1024))
                    {
                        var oldest = recentRefreshPages.OrderBy(item => item.Value.Expires).First();
                        recentRefreshBytes -= oldest.Value.Bytes.Length;
                        recentRefreshPages.Remove(oldest.Key);
                    }
                    recentRefreshPages.Add(pair.Key, pair.Value);
                    recentRefreshBytes += pair.Value.Bytes.Length;
                }
            }
        }

        internal void CompleteRefreshDiscovery()
        {
            VerifyRefreshDiscovery();
            refreshContext.Value.DiscoveryComplete?.Invoke();
        }

        internal void ReportRefreshPhase(string phase) => refreshContext.Value?.PhaseChanged?.Invoke(phase);

        internal void VerifyRefreshDiscovery()
        {
            var context = refreshContext.Value ?? throw new InvalidOperationException("No scoped refresh is active.");
            if (context.Error is OperationCanceledException cancellation) throw cancellation;
            if (context.Error != null) throw new InvalidOperationException("Emby encountered a failed scoped catalogue request.", context.Error);
            if (context.Pages.Count < context.ExpectedPages
                || (context.RequiredPages != null && context.RequiredPages.Any(id => !context.Pages.Contains(id))))
                throw new InvalidOperationException("Emby did not enumerate all saved starting pages.");
        }

        public void AssertCategoryRefresh(string category)
        {
            if (refreshContext.Value != null && refreshContext.Value.Category != category)
            {
                var error = new InvalidOperationException("Emby attempted to refresh a different radio group.");
                RecordRefreshFailure(error);
                throw error;
            }
        }

        public void RecordRefreshFailure(Exception error)
        {
            if (refreshContext.Value != null) refreshContext.Value.Error = error;
        }

        private sealed class CachedPage
        {
            public string Category { get; set; }
            public byte[] Bytes { get; set; }
            public DateTime Expires { get; set; }
        }

        private sealed class RefreshContext
        {
            public string Category { get; set; }
            public HashSet<string> Pages { get; } = new HashSet<string>();
            public int RequestCount { get; set; }
            public int ExpectedPages { get; set; }
            public string[] RequiredPages { get; set; }
            public Exception Error { get; set; }
            public Action<DirectoryRequest> RequestStarted { get; set; }
            public Action<int> StatusReceived { get; set; }
            public Action<XDocument> RequestCompleted { get; set; }
            public Action CacheHit { get; set; }
            public Action DiscoveryComplete { get; set; }
            public Action<string> PhaseChanged { get; set; }
        }

        private sealed class RefreshRun
        {
            public string Key { get; set; }
            public long Generation { get; set; }
            public Dictionary<string, CachedPage> Pages { get; } = new Dictionary<string, CachedPage>();
            public int Bytes { get; set; }
        }

        public Task<XDocument> FetchAsync(DirectoryRequest request, CancellationToken token, Action<int> statusReceived = null) =>
            FetchCoreAsync(request, token, statusReceived, false);

        private async Task<XDocument> FetchCoreAsync(DirectoryRequest request, CancellationToken token, Action<int> statusReceived, bool categoryRequest)
        {
            if (refreshContext.Value != null && !categoryRequest)
                throw new InvalidOperationException("Emby requested uncached metadata outside the bounded group refresh.");
            var interval = configuration().ApiRequestIntervalSeconds;
            if (interval < PluginConfiguration.MinApiRequestIntervalSeconds || interval > PluginConfiguration.MaxApiRequestIntervalSeconds)
                throw new InvalidOperationException("TuneIn API request interval must be between 1 and 30 seconds.");
            if (fetch != null) return await fetch(request, token).ConfigureAwait(false);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(50 + interval));
                await throttle.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        var earliest = lastRequest.AddSeconds(interval);
                        if (nextRequest > earliest) earliest = nextRequest;
                        var delay = earliest - utcNow();
                        if (delay > TimeSpan.Zero) await wait(delay, timeout.Token).ConfigureAwait(false);
                        lastRequest = utcNow();
                        try
                        {
                            using (var response = await transport.OpenAsync(request.Url, true, timeout.Token, statusReceived).ConfigureAwait(false))
                            {
                                var bytes = await ReadBoundedAsync(response.Body, 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
                                return ParseCatalogue(bytes);
                            }
                        }
                        catch (CatalogueRateLimitException error)
                        {
                            var retryAfter = error.RetryAfter ?? TimeSpan.FromSeconds(6 * (attempt + 1));
                            nextRequest = utcNow().Add(retryAfter);
                            if (attempt >= 2 || retryAfter > TimeSpan.FromSeconds(20)) throw;
                        }
                    }
                }
                finally { throttle.Release(); }
            }
        }

        public async Task<SelectorResult> SelectAsync(string category, string parent, string path,
            bool refresh, CancellationToken token, Action<int> statusReceived = null)
        {
            var snapshot = Snapshot();
            if (category != "music" && category != "language" && category != "location" && category != "local")
                throw new ArgumentException("Invalid selector category.");
            var root = string.IsNullOrEmpty(parent);
            if (!root && (!ConfigurationSnapshot.ValidId(parent, category) || category == "language"))
                throw new ArgumentException("Invalid selector parent.");
            var cacheKey = category + ":" + parent;
            using (var operation = Operation(snapshot, token))
            {
                SelectorItem parentItem = null;
                lock (gate)
                {
                    if (!refresh && selectors.TryGetValue(cacheKey, out var cached)) return cached;
                    if (!root)
                        parentItem = selectors.Where(p => p.Key.StartsWith(category + ":", StringComparison.Ordinal))
                            .SelectMany(p => p.Value.Items).FirstOrDefault(i => i.Id == parent);
                    if (!root && parentItem == null)
                        throw new ArgumentException("Selector parent is unavailable. Load its ancestry from the root first.");
                }
                var request = new DirectoryRequest();
                if (category == "language") request.Parameters["c"] = "lang";
                else if (category == "music" && root) request.Parameters["c"] = "music";
                else request.Parameters["id"] = root ? "r0" : parent;
                var doc = await FetchAsync(request, operation.Token, statusReceived).ConfigureAwait(false);
                var rows = doc.Root.Element("body").Descendants("outline");
                var result = new SelectorResult
                {
                    ContainsStationMetadata = rows.Any(n => Attribute(n, "type") == "audio"),
                    Items = new List<SelectorItem>()
                };
                foreach (var row in rows.Where(n => Attribute(n, "type") == "link"))
                {
                    var link = DirectoryRequest.ParseLink(Attribute(row, "URL"));
                    var id = category == "language" ? link.Parameters.GetValueOrDefault("filter")
                        : link.Parameters.GetValueOrDefault("id");
                    if (!ConfigurationSnapshot.ValidId(id, category)) continue;
                    result.Items.Add(new SelectorItem
                    {
                        Id = id,
                        Name = Attribute(row, "text"),
                        Path = root ? Attribute(row, "text") : parentItem.Path + " / " + Attribute(row, "text"),
                        AncestorIds = root ? new List<string> { category == "music" ? "music"
                            : category == "language" ? "lang" : "r0" }
                            : parentItem.AncestorIds.Concat(new[] { parent }).ToList()
                    });
                }
                result.Items = result.Items.GroupBy(i => i.Id).Select(g => g.First()).ToList();
                result.Warning = result.ContainsStationMetadata
                    ? "TuneIn included station metadata in this explicit selector response. No streams were resolved or played."
                    : "Deeper TuneIn selector responses may include station metadata; no automatic crawl is performed.";
                lock (gate)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    if (selectors.Count >= 200) selectors.Clear();
                    selectors[cacheKey] = result;
                }
                return result;
            }
        }

        public static string Attribute(XElement element, string name) =>
            element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

        internal static XDocument ParseCatalogue(byte[] bytes)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 2 * 1024 * 1024,
                Schemas = CatalogueSchemas
            };
            settings.ValidationType = ValidationType.Schema;
            settings.ValidationFlags = XmlSchemaValidationFlags.None;
            settings.ValidationEventHandler += (_, __) =>
                throw new InvalidDataException("TuneIn catalogue does not match the required OPML structure.");
            using (var stream = new MemoryStream(bytes))
            using (var reader = XmlReader.Create(stream, settings))
            {
                var document = XDocument.Load(reader);
                if (document.Descendants().Any(element => element.Ancestors().Take(33).Count() > 32))
                    throw new InvalidDataException("TuneIn catalogue nesting exceeds limit.");
                if (document.Root?.Name != "opml" || document.Root.Element("body") == null
                    || document.Root.Element("head")?.Element("fault") != null
                    || (document.Root.Element("head")?.Element("status")?.Value ?? "200") != "200")
                    throw new InvalidDataException("TuneIn returned an API fault or invalid catalogue response.");
                return document;
            }
        }

        private static XmlSchemaSet CreateCatalogueSchemas()
        {
            const string schema = @"<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema"">
              <xs:complexType name=""outline"">
                <xs:sequence><xs:element name=""outline"" type=""outline"" minOccurs=""0"" maxOccurs=""unbounded""/></xs:sequence>
                <xs:anyAttribute processContents=""skip""/>
              </xs:complexType>
              <xs:element name=""opml"">
                <xs:complexType>
                  <xs:sequence>
                    <xs:element name=""head"" minOccurs=""0"">
                      <xs:complexType mixed=""true"">
                        <xs:sequence><xs:any minOccurs=""0"" maxOccurs=""unbounded"" processContents=""skip""/></xs:sequence>
                        <xs:anyAttribute processContents=""skip""/>
                      </xs:complexType>
                    </xs:element>
                    <xs:element name=""body"">
                      <xs:complexType>
                        <xs:sequence><xs:element name=""outline"" type=""outline"" minOccurs=""0"" maxOccurs=""unbounded""/></xs:sequence>
                        <xs:anyAttribute processContents=""skip""/>
                      </xs:complexType>
                    </xs:element>
                  </xs:sequence>
                  <xs:anyAttribute processContents=""skip""/>
                </xs:complexType>
              </xs:element>
            </xs:schema>";
            var schemas = new XmlSchemaSet { XmlResolver = null };
            using (var text = new StringReader(schema))
            using (var reader = XmlReader.Create(text, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                schemas.Add(null, reader);
            schemas.Compile();
            return schemas;
        }

        public static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
        {
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                {
                    if (memory.Length + count > limit) throw new InvalidDataException("Radio response exceeds size limit.");
                    memory.Write(buffer, 0, count);
                }
                return memory.ToArray();
            }
        }

        public async Task<SafeResponse> OpenStreamAsync(ConfigurationSnapshot snapshot, NavigationEntry entry,
            CancellationToken token, Action<string> warning,
            Func<SafeResponse, CancellationToken, Stream> decode = null)
        {
            if (!Allowed(snapshot, entry) || entry.StationId == null
                || !Regex.IsMatch(entry.StationId, @"^[spt][1-9][0-9]{0,9}$"))
                throw new InvalidOperationException("TuneIn playback is not configured or this item is disabled.");
            var request = new DirectoryRequest { Method = "Tune.ashx" };
            request.Parameters["id"] = entry.StationId;
            request.Parameters["formats"] = "mp3,aac";
            request.Parameters["partnerid"] = "uD1X52pA";
            if (entry.Category == "presets") request.Parameters["username"] = snapshot.Value.Username;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using (var response = await transport.OpenAsync(request.Url, true, timeout.Token).ConfigureAwait(false))
                {
                    var text = Encoding.UTF8.GetString(await ReadBoundedAsync(response.Body, 256 * 1024, timeout.Token).ConfigureAwait(false));
                    var urls = PlaylistUrls(text).Take(8).ToList();
                    if (urls.Count == 0) throw new InvalidDataException("TuneIn returned no playable stream.");
                    var preRolls = urls.TakeWhile(IsProviderPreRoll).ToList();
                    if (urls.Skip(preRolls.Count).Any(IsProviderPreRoll))
                        throw new InvalidDataException("TuneIn returned an invalid pre-roll/live stream order.");
                    if (preRolls.Count == 0)
                        return await OpenAlternativesAsync(urls, timeout.Token, warning).ConfigureAwait(false);
                    if (preRolls.Count == urls.Count)
                        throw new InvalidDataException("TuneIn returned an invalid pre-roll/live stream order.");
                    if (decode == null)
                        throw new InvalidOperationException("TuneIn pre-roll playback requires the host audio decoder.");

                    // Only the provider's recognized leading pre-roll assets are sequential.
                    // The remaining URLs are still alternatives for ONE live stream.
                    var stages = new Queue<Func<CancellationToken, Task<SafeResponse>>>();
                    foreach (var url in preRolls)
                        stages.Enqueue(ct => OpenAlternativesAsync(new[] { url }, ct, warning));
                    stages.Enqueue(ct => OpenAlternativesAsync(urls.Skip(preRolls.Count), ct, warning));
                    var first = await stages.Dequeue()(timeout.Token).ConfigureAwait(false);
                    return new SafeResponse(new PcmSequenceStream(first, stages, decode, token), "audio/wav");
                }
            }
        }

        internal static bool IsProviderPreRoll(string value)
        {
            var uri = new Uri(value);
            return uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
                && uri.Host.Equals("tunein-ondemand.cdnstream1.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath == "/multi_bump_sonic_pre_pre.mp3";
        }

        private async Task<SafeResponse> OpenAlternativesAsync(IEnumerable<string> urls,
            CancellationToken token, Action<string> warning)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                // Resolve playlists through the same pinned-address transport, never hand remote URLs to Emby.
                var pending = new Queue<KeyValuePair<string, int>>(urls.Select(url => new KeyValuePair<string, int>(url, 0)));
                Exception lastFailure = null;
                for (var attempts = 0; attempts < 16 && pending.Count > 0; attempts++)
                {
                    var candidate = pending.Dequeue();
                    SafeResponse stream = null;
                    try
                    {
                        stream = await transport.OpenAsync(candidate.Key, false, timeout.Token).ConfigureAwait(false);
                        var extension = Path.GetExtension(new Uri(candidate.Key).AbsolutePath);
                        if (extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
                            || stream.ContentType.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0
                            || extension.Equals(".m3u", StringComparison.OrdinalIgnoreCase)
                            || extension.Equals(".pls", StringComparison.OrdinalIgnoreCase)
                            || stream.ContentType.IndexOf("scpls", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (candidate.Value >= 4) throw new InvalidDataException("Radio playlist nesting limit exceeded.");
                            var playlist = Encoding.UTF8.GetString(await ReadBoundedAsync(stream.Body, 256 * 1024, timeout.Token).ConfigureAwait(false));
                            if (playlist.Contains("#EXT-X-"))
                                throw new InvalidDataException("HLS is not supported by the secure radio relay.");
                            var children = PlaylistUrls(playlist).Take(8).Select(url =>
                                new KeyValuePair<string, int>(url, candidate.Value + 1)).ToList();
                            if (children.Count == 0) throw new InvalidDataException("Empty radio playlist.");
                            pending = new Queue<KeyValuePair<string, int>>(children.Concat(pending).Take(16));
                        }
                        else
                        {
                            if (stream.ContentType != "audio/mpeg" && stream.ContentType != "audio/mp3" && stream.ContentType != "audio/aac"
                                && stream.ContentType != "audio/aacp" && stream.ContentType != "application/octet-stream")
                                throw new InvalidDataException("Unsupported radio stream content type.");
                            var playable = stream;
                            stream = null;
                            return playable;
                        }
                    }
                    catch (Exception error) when (error is IOException || error is InvalidDataException || error is SocketException
                        || error is AuthenticationException || error is TimeoutException)
                    {
                        lastFailure = error;
                        warning("Advertised radio stream alternative failed; trying the next available alternative.");
                    }
                    finally { stream?.Dispose(); }
                }
                throw new InvalidDataException("No advertised radio stream could be opened within the relay limits.", lastFailure);
            }
        }

        public Task<ArtworkResult> OpenArtworkAsync(NavigationEntry entry, CancellationToken token) =>
            TuneInArtwork.OpenAsync(entry, transport, token);

        internal static IEnumerable<string> PlaylistUrls(string text)
        {
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var item = line.Trim();
                if (item.StartsWith("File", StringComparison.OrdinalIgnoreCase))
                {
                    var equals = item.IndexOf('=');
                    if (equals < 0 || !Regex.IsMatch(item.Substring(0, equals), @"^File[1-9][0-9]*$", RegexOptions.IgnoreCase)) continue;
                    item = item.Substring(equals + 1).Trim();
                }
                if (item.StartsWith("#", StringComparison.Ordinal) || item.StartsWith("[", StringComparison.Ordinal)) continue;
                if (!item.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !item.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.IndexOf("/notcompatible.", StringComparison.OrdinalIgnoreCase) >= 0
                    || item.IndexOf("/nostream.", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidDataException("TuneIn reports that this station is unavailable or incompatible.");
                yield return PublicAddressPolicy.ValidateUri(item, false).AbsoluteUri;
            }
        }
    }

    internal static class DictionaryExtensions
    {
        public static string GetValueOrDefault(this Dictionary<string, string> value, string key) =>
            value.TryGetValue(key, out var item) ? item : null;
    }
}
