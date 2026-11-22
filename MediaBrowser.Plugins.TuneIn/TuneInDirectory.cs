using MediaBrowser.Plugins.TuneIn.Configuration;
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
using System.Xml.Serialization;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class ConfigurationSnapshot
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(PluginConfiguration));
        private static readonly XmlSerializer ScopeSerializer = CreateScopeSerializer();
        private static readonly string[] CategoryKeys = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };
        private static readonly string[] SelectionCategories = { "local", "music", "language", "location" };
        private static readonly int[] ScheduleHours = { 6, 12, 24, 168, 720 };
        public PluginConfiguration Value { get; }
        public string Key { get; }

        public ConfigurationSnapshot(PluginConfiguration value)
        {
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            {
                Serializer.Serialize(writer, value);
                using (var reader = new StringReader(writer.ToString()))
                    Value = (PluginConfiguration)Serializer.Deserialize(reader);
                writer.GetStringBuilder().Clear();
                var keyValue = (PluginConfiguration)Serializer.Deserialize(new StringReader(
                    SerializeConfiguration(Value)));
                foreach (var category in SelectionCategories.Where(category => !UsesSelectedSelections(keyValue, category)))
                    SetSelections(keyValue, category, new List<CatalogSelection>());
                ScopeSerializer.Serialize(writer, keyValue);
                // All retains the exact legacy XML hash, including when inactive choices are saved.
                foreach (var category in TopicCategories.Where(UsesSelectedTopics))
                    writer.Write("\n" + category + ":selected:" + string.Join(",", TopicIds(category)));
                foreach (var category in SelectionCategories.Where(category =>
                    UsesSelectedSelections(Value, category) && !ConfiguredSelections(Value, category).Any(s => s != null && s.Enabled)))
                    writer.Write("\n" + category + ":selected");
                using (var hash = SHA256.Create())
                    Key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(writer.ToString()))).Replace("-", "");
            }
        }

        private static string SerializeConfiguration(PluginConfiguration value)
        {
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            {
                Serializer.Serialize(writer, value);
                return writer.ToString();
            }
        }

        private static XmlSerializer CreateScopeSerializer()
        {
            // Preserve the legacy XML identity, including its readiness fields and element order.
            var members = new HashSet<string>(StringComparer.Ordinal)
            {
                "Username", "LatLon", "NavigationSecret", "SchemaVersion", "ConfigurationCompleted",
                "EnableLocalRadio", "EnableMusic", "EnableLanguage", "EnableLocation", "EnableTalk",
                "EnableSports", "EnablePodcasts", "EnableFavorites", "UseCoordinates", "LocalRadioSource",
                "Languages", "Locations", "Music", "LocalCities"
            };
            var overrides = new XmlAttributeOverrides();
            foreach (var property in typeof(PluginConfiguration).GetProperties())
                if (!members.Contains(property.Name))
                    overrides.Add(property.DeclaringType, property.Name, new XmlAttributes { XmlIgnore = true });
            return new XmlSerializer(typeof(PluginConfiguration), overrides);
        }

        internal string DiscoveryKey
        {
            get
            {
                var discoveryScope = Key + ":stations:" + (Value.MaxStationCount?.ToString(CultureInfo.InvariantCulture) ?? "unset")
                    + ":depth:" + Value.TraversalDepthLimit.ToString(CultureInfo.InvariantCulture);
                if (!Value.HideRegionalGenres) return discoveryScope;
                using (var hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(discoveryScope + ":hide-regional-genres"))).Replace("-", "");
            }
        }

        public bool Ready => Value.ConfigurationCompleted && Value.SchemaVersion == 1;
        private static readonly string[] TopicCategories = { "talk", "sports", "podcast" };
        internal static bool IsTopicCategory(string category) => TopicCategories.Contains(category);

        private static string TopicMode(PluginConfiguration value, string category) =>
            category == "talk" ? value.TalkMode : category == "sports" ? value.SportsMode : value.PodcastsMode;

        private static List<string> ConfiguredTopicIds(PluginConfiguration value, string category) =>
            category == "talk" ? value.TalkIds : category == "sports" ? value.SportsIds : value.PodcastIds;

        private static string SelectionMode(PluginConfiguration value, string category) =>
            category == "language" ? value.LanguageMode : category == "location" ? value.LocationMode
                : category == "music" ? value.MusicMode : value.LocalMode;

        private static List<CatalogSelection> ConfiguredSelections(PluginConfiguration value, string category) =>
            (category == "music" ? value.Music : category == "language" ? value.Languages
                : category == "location" ? value.Locations : value.LocalCities) ?? new List<CatalogSelection>();

        private static void SetSelections(PluginConfiguration value, string category, List<CatalogSelection> selections)
        {
            if (category == "music") value.Music = selections;
            else if (category == "language") value.Languages = selections;
            else if (category == "location") value.Locations = selections;
            else value.LocalCities = selections;
        }

        private static bool UsesSelectedSelections(PluginConfiguration value, string category)
        {
            if (!SelectionCategories.Contains(category)) return false;
            var mode = SelectionMode(value, category);
            if (string.Equals(mode, "selected", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(mode)) return true;
            return ConfiguredSelections(value, category).Count > 0;
        }

        internal bool UsesSelectedTopics(string category)
        {
            if (!IsTopicCategory(category)) return false;
            var mode = TopicMode(Value, category);
            // An unrecognized persisted mode must not broaden browsing to All.
            return !string.IsNullOrEmpty(mode) && !string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase);
        }

        internal IEnumerable<string> TopicIds(string category) =>
            string.Equals(TopicMode(Value, category), "selected", StringComparison.OrdinalIgnoreCase)
                ? (ConfiguredTopicIds(Value, category) ?? new List<string>()).Where(id => ValidId(id, category))
                    .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        internal bool UsesSelectedSelections(string category) => UsesSelectedSelections(Value, category);

        internal bool UsesSelections(string category) => UsesSelectedTopics(category) || UsesSelectedSelections(category);

        // Language includes its Music, Talk and Sports branches regardless of those categories;
        // saved Music genres narrow Language only while the Music category is actively using them.
        internal bool LanguageUsesMusicGenres => Enabled("music") && UsesSelectedSelections("music");

        private void CopyTopicScope(PluginConfiguration scope, string category)
        {
            if (!UsesSelectedTopics(category)) return;
            var ids = TopicIds(category).ToList();
            switch (category)
            {
                case "talk": scope.TalkMode = "selected"; scope.TalkIds = ids; break;
                case "sports": scope.SportsMode = "selected"; scope.SportsIds = ids; break;
                case "podcast": scope.PodcastsMode = "selected"; scope.PodcastIds = ids; break;
            }
        }

        public string LocalSource => Value.EnableLocalRadio ? ResolveLocalSource(Value) : null;
        public IEnumerable<string> Categories => CategoryKeys.Where(Enabled);
        internal string CategoryScopeKey(string category)
        {
            var scope = new PluginConfiguration
            {
                NavigationSecret = Value.NavigationSecret,
                SchemaVersion = Value.SchemaVersion,
                ConfigurationCompleted = Value.ConfigurationCompleted
            };
            switch (category)
            {
                case "local":
                    scope.EnableLocalRadio = Value.EnableLocalRadio;
                    scope.LocalRadioSource = Value.LocalRadioSource;
                    scope.UseCoordinates = Value.UseCoordinates;
                    scope.LatLon = Value.LatLon;
                    scope.LocalCities = Selections("local").ToList();
                    break;
                case "language":
                    scope.EnableLanguage = Value.EnableLanguage;
                    scope.Languages = Selections("language").ToList();
                    if (LanguageUsesMusicGenres)
                    {
                        scope.EnableMusic = true;
                        scope.Music = Selections("music").ToList();
                    }
                    break;
                case "music":
                    scope.EnableMusic = Value.EnableMusic;
                    scope.Music = Selections("music").ToList();
                    break;
                case "location":
                    scope.EnableLocation = Value.EnableLocation;
                    scope.Locations = Selections("location").ToList();
                    break;
                case "talk": scope.EnableTalk = Value.EnableTalk; break;
                case "sports": scope.EnableSports = Value.EnableSports; break;
                case "podcast": scope.EnablePodcasts = Value.EnablePodcasts; break;
                case "presets":
                    scope.EnableFavorites = Value.EnableFavorites;
                    scope.Username = Value.Username;
                    break;
                default: throw new ArgumentException("Invalid TuneIn category.", nameof(category));
            }
            if (IsTopicCategory(category)) CopyTopicScope(scope, category);
            if (SelectionCategories.Contains(category))
                SetSelectionMode(scope, category, UsesSelectedSelections(category) ? "selected" : "all");
            if (category == "language")
                scope.MusicMode = LanguageUsesMusicGenres ? "selected" : "all";
            return new ConfigurationSnapshot(scope).Key;
        }

        private static void SetSelectionMode(PluginConfiguration value, string category, string mode)
        {
            if (category == "music") value.MusicMode = mode;
            else if (category == "language") value.LanguageMode = mode;
            else if (category == "location") value.LocationMode = mode;
            else value.LocalMode = mode;
        }
        internal static bool ValidSchedule(CategoryRefreshSchedule schedule) =>
            schedule != null && CategoryKeys.Contains(schedule.Category) && ScheduleHours.Contains(schedule.IntervalHours);

        public bool Enabled(string category)
        {
            if (!Ready) return false;
            switch (category)
            {
                case "local": return Value.EnableLocalRadio && LocalSource != null;
                case "music": return Value.EnableMusic && (!UsesSelectedSelections(category) || Selections(category).Any());
                case "language": return Value.EnableLanguage && (!UsesSelectedSelections(category) || Selections(category).Any());
                case "location": return Value.EnableLocation && (!UsesSelectedSelections(category) || Selections(category).Any());
                case "talk": return Value.EnableTalk;
                case "sports": return Value.EnableSports;
                case "podcast": return Value.EnablePodcasts;
                case "presets": return Value.EnableFavorites && !string.IsNullOrWhiteSpace(Value.Username);
                default: return false;
            }
        }

        public IEnumerable<CatalogSelection> Selections(string category)
        {
            if (!UsesSelectedSelections(category)) return Enumerable.Empty<CatalogSelection>();
            if (category == "local" && LocalSource != "regions") return Enumerable.Empty<CatalogSelection>();
            var items = ConfiguredSelections(Value, category);
            return (items ?? new List<CatalogSelection>()).Where(s => s != null && s.Enabled
                && ValidId(s.Id, category)).GroupBy(s => s.Id).Select(g => g.First());
        }

        private static string ResolveLocalSource(PluginConfiguration value)
        {
            var regions = ConfiguredSelections(value, "local").Any(s => s != null && s.Enabled && ValidId(s.Id, "local"));
            var hasRegionSource = !UsesSelectedSelections(value, "local") || regions;
            if (value.LocalRadioSource == "regions") return hasRegionSource && !value.UseCoordinates ? "regions" : null;
            if (value.LocalRadioSource == "coordinates")
                return value.UseCoordinates && TryCoordinates(value.LatLon, out _) ? "coordinates" : null;
            if (!string.IsNullOrEmpty(value.LocalRadioSource)) return null;
            if (!value.UseCoordinates) return regions ? "regions" : null;
            return !regions && TryCoordinates(value.LatLon, out _) ? "coordinates" : null;
        }

        public static bool ValidId(string id, string category) => id != null && Regex.IsMatch(id,
            category == "language" ? @"\Al[1-9][0-9]{0,9}\z"
                : category == "music" || IsTopicCategory(category) ? @"\A[cg][1-9][0-9]{0,9}\z"
                : @"\Ar[1-9][0-9]{0,9}\z");

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
            if (value.SchemaVersion != 1 && (value.ConfigurationCompleted || value.SchemaVersion != 0))
                throw new ArgumentException("Unsupported TuneIn configuration schema.");
            if (value.ApiRequestIntervalSeconds < PluginConfiguration.MinApiRequestIntervalSeconds
                || value.ApiRequestIntervalSeconds > PluginConfiguration.MaxApiRequestIntervalSeconds)
                throw new ArgumentException("TuneIn API request interval must be between 1 and 30 seconds.");
            if (value.ImportDeadlineHours < PluginConfiguration.MinImportDeadlineHours
                || value.ImportDeadlineHours > PluginConfiguration.MaxImportDeadlineHours)
                throw new ArgumentException($"TuneIn import deadline must be between {PluginConfiguration.MinImportDeadlineHours} and {PluginConfiguration.MaxImportDeadlineHours} hours.");
            if (value.MaxStationCount.HasValue && (value.MaxStationCount < PluginConfiguration.MinMaxStationCount
                || value.MaxStationCount > PluginConfiguration.MaxMaxStationCount))
                throw new ArgumentException($"TuneIn Max Station Count must be not set or between {PluginConfiguration.MinMaxStationCount} and {PluginConfiguration.MaxMaxStationCount}.");
            if (value.TraversalDepthLimit < PluginConfiguration.MinTraversalDepthLimit
                || value.TraversalDepthLimit > PluginConfiguration.MaxTraversalDepthLimit)
                throw new ArgumentException($"TuneIn traversal depth limit must be between {PluginConfiguration.MinTraversalDepthLimit} and {PluginConfiguration.MaxTraversalDepthLimit}.");
            if (value.DiscoveryDeadlineHours < PluginConfiguration.MinDiscoveryDeadlineHours
                || value.DiscoveryDeadlineHours > PluginConfiguration.MaxDiscoveryDeadlineHours)
                throw new ArgumentException("TuneIn discovery deadline must be between 1 and 10 hours.");
            var schedules = value.RefreshSchedules;
            if (schedules != null && (schedules.Count > CategoryKeys.Length || schedules.Any(schedule => !ValidSchedule(schedule))
                || schedules.Select(schedule => schedule.Category).Distinct(StringComparer.Ordinal).Count() != schedules.Count))
                throw new ArgumentException("Invalid TuneIn category refresh schedule.");
            if (value.EnableLocalRadio && ResolveLocalSource(value) == null)
                throw new ArgumentException("Choose one Local Radio source: all or selected regions, or valid coordinates.");
            if ((value.Username ?? "").Length > 128)
                throw new ArgumentException("TuneIn username exceeds 128 characters.");
            foreach (var category in TopicCategories)
            {
                var mode = TopicMode(value, category);
                if (!string.IsNullOrEmpty(mode) && !string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(mode, "selected", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Invalid TuneIn topic selection mode.");
                var ids = ConfiguredTopicIds(value, category);
                if (ids != null && (ids.Count > 100 || ids.Any(id => !ValidId(id, category))))
                    throw new ArgumentException("Invalid or excessive TuneIn topic selections.");
            }
            foreach (var category in SelectionCategories)
            {
                var mode = SelectionMode(value, category);
                if (!string.IsNullOrEmpty(mode) && !string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(mode, "selected", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Invalid TuneIn selection mode.");
                var items = ConfiguredSelections(value, category);
                if ((items?.Count ?? 0) > 100 || (items ?? new List<CatalogSelection>()).Any(s =>
                    s == null || !ValidId(s.Id, category) || (s.Name ?? "").Length > 256
                    || (s.Path ?? "").Length > 2048 || (s.AncestorIds?.Count ?? 0) > 20
                    || (s.AncestorIds ?? new List<string>()).Any(id =>
                        id != (category == "music" ? "music" : category == "language" ? "lang" : "r0")
                        && !ValidId(id, category))))
                    throw new ArgumentException("Invalid or excessive TuneIn selections.");
            }
        }
    }

    internal sealed class DirectoryRequest
    {
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

        public static DirectoryRequest ParseLink(string value, Func<string, bool> skipMalformed = null,
            bool allowSkippingTune = false)
        {
            if (!Uri.TryCreate(HttpUtility.HtmlDecode(value), UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https") || !uri.IsDefaultPort
                || uri.UserInfo.Length != 0 || !uri.Host.Equals("opml.radiotime.com", StringComparison.OrdinalIgnoreCase)
                || (uri.AbsolutePath != "/Browse.ashx" && uri.AbsolutePath != "/Tune.ashx"))
                throw new InvalidDataException("Untrusted TuneIn catalog link.");

            DirectoryRequest RejectQuery(string reason)
            {
                if ((uri.AbsolutePath == "/Browse.ashx" || allowSkippingTune) && skipMalformed?.Invoke(reason) == true)
                    return null;
                throw new InvalidDataException(reason);
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            var last = HttpUtility.UrlDecode(uri.Query.Substring(uri.Query.LastIndexOf('&') + 1).TrimStart('?'));
            var region = ConfigurationSnapshot.ValidId(query["id"], "location") && query.GetValues("id")?.Length == 1;
            var alphabetHash = uri.AbsolutePath == "/Browse.ashx"
                && (uri.Fragment == "#" || region && uri.Fragment == "#:country")
                && (last == "filter=s:~" || last == "filter=p:~") && query.GetValues("filter")?.Length == 1;
            if (uri.Fragment.Length != 0 && !alphabetHash) return RejectQuery("Invalid catalog fragment.");
            if (alphabetHash) query["filter"] += uri.Fragment;
            var request = new DirectoryRequest { Method = uri.AbsolutePath.Substring(1) };
            foreach (var key in new[] { "id", "c", "filter", "offset", "pivot", "sid" })
            {
                var item = query[key];
                if (item == null) continue;
                if (query.GetValues(key).Length != 1 || (!Regex.IsMatch(item, @"\A[A-Za-z0-9:,*-]{1,80}\z")
                    && !(key == "filter" && Regex.IsMatch(item, @"\A[sp]:~[\p{L}\p{Nd}#](?::l[1-9][0-9]{0,9})?\z"))
                    && !(key == "filter" && region && Regex.IsMatch(item, @"\A[sp]:~[\p{L}\p{Nd}#]:country\z"))))
                    return RejectQuery("Invalid catalog link parameter: " + key);
                request.Parameters[key] = item;
            }
            if (request.Parameters.Count == 0) return RejectQuery("Empty catalog link.");
            return request;
        }
    }

    internal sealed class NavigationEntry
    {
        public string Category { get; set; }
        public string SelectionId { get; set; }
        public DirectoryRequest Request { get; set; }
        public string Name { get; set; }
        public string StationId { get; set; }
        public string ContentCategory { get; set; }
        public string MusicSelectionId { get; set; }
        public string TopicBranchKey { get; set; }
    }

    public sealed class SelectorItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public List<string> AncestorIds { get; set; }
        public bool CanExpand { get; set; }
        public bool CanSelect { get; set; }
        internal string ProviderHub { get; set; }
        internal DirectoryRequest TopicRequest { get; set; }
    }

    public sealed class SelectorResult
    {
        public List<SelectorItem> Items { get; set; }
        public bool ContainsStationMetadata { get; set; }
        public string Warning { get; set; }
    }

    internal sealed class TuneInDirectory
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, SelectorResult> selectors = new Dictionary<string, SelectorResult>();
        private readonly Func<PluginConfiguration> configuration;
        private readonly Func<DirectoryRequest, CancellationToken, TuneInRefreshTimings, Task<XDocument>> fetch;
        private string key;
        private int selectorCharacters;

        internal TuneInDirectory(Func<PluginConfiguration> configuration,
            Func<DirectoryRequest, CancellationToken, Task<XDocument>> fetch)
            : this(configuration, (request, token, _) => fetch(request, token)) { }

        internal TuneInDirectory(Func<PluginConfiguration> configuration,
            Func<DirectoryRequest, CancellationToken, TuneInRefreshTimings, Task<XDocument>> fetch)
        {
            this.configuration = configuration;
            this.fetch = fetch;
        }

        public ConfigurationSnapshot Snapshot()
        {
            var snapshot = new ConfigurationSnapshot(configuration());
            lock (gate)
            {
                if (key != snapshot.Key) { selectors.Clear(); selectorCharacters = 0; key = snapshot.Key; }
            }
            return snapshot;
        }

        public void CheckCurrent(ConfigurationSnapshot snapshot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Snapshot().Key != snapshot.Key) throw new OperationCanceledException("TuneIn configuration changed.");
        }

        public string Register(ConfigurationSnapshot snapshot, NavigationEntry entry)
        {
            AssertSnapshot(snapshot);
            if (!Allowed(snapshot, entry)) throw new InvalidOperationException("TuneIn item is outside its configured scope.");
            var document = new XElement("entry", new XAttribute("key", snapshot.Key),
                new XElement("category", entry.Category), new XElement("selection", entry.SelectionId ?? ""),
                new XElement("station", entry.StationId ?? ""), new XElement("name", entry.Name ?? ""),
                new XElement("contentCategory", entry.ContentCategory ?? ""),
                new XElement("musicSelection", entry.MusicSelectionId ?? ""));
            if (entry.TopicBranchKey != null)
                document.Add(new XElement("topicBranch", entry.TopicBranchKey));
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

        public NavigationEntry Resolve(ConfigurationSnapshot snapshot, string id)
        {
            AssertSnapshot(snapshot);
            return ResolveSigned(snapshot, id);
        }

        internal Func<string, string> CategoryRebinder(ConfigurationSnapshot previous, ConfigurationSnapshot current,
            string category, bool filterSelections = false)
        {
            if (!filterSelections && previous.CategoryScopeKey(category) != current.CategoryScopeKey(category))
                throw new InvalidOperationException("Cannot reuse a TuneIn item whose category scope changed.");
            return id =>
            {
                var entry = ResolveSigned(previous, id);
                if (entry.Category != category)
                    throw new InvalidOperationException("Saved TuneIn item belongs to a different category.");
                if (filterSelections && !Allowed(current, entry)) return null;
                return Register(current, entry);
            };
        }

        internal string MembershipKey(ConfigurationSnapshot snapshot, string id)
        {
            var entry = Resolve(snapshot, id);
            return string.Join("\0", entry.Category, entry.SelectionId ?? "", entry.StationId ?? "",
                entry.ContentCategory ?? "", entry.MusicSelectionId ?? "", entry.TopicBranchKey ?? "",
                entry.Request?.Url ?? "");
        }

        internal string RebindOwned(ConfigurationSnapshot snapshot, string id)
        {
            var bytes = ReadSignedPayload(snapshot, id);
            if (bytes == null) throw new InvalidOperationException("Existing TuneIn item cannot be authenticated.");
            using (var stream = new MemoryStream(bytes))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
            {
                var document = XElement.Load(reader);
                var entry = ReadEntry(document);
                return Allowed(snapshot, entry) ? Register(snapshot, entry) : null;
            }
        }

        // Ownership survives settings changes; navigation still requires the current key and scope.
        internal static bool IsGeneratedId(ConfigurationSnapshot snapshot, string id) =>
            ReadSignedPayload(snapshot, id) != null;

        internal static string GeneratedCategory(ConfigurationSnapshot snapshot, string id, bool requireRoot = false)
        {
            var bytes = ReadSignedPayload(snapshot, id);
            if (bytes == null) return null;
            try
            {
                using (var stream = new MemoryStream(bytes))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
                {
                    var document = XElement.Load(reader);
                    var category = document.Element("category")?.Value;
                    if (document.Name != "entry" || !new[] { "local", "music", "language", "location",
                        "talk", "sports", "podcast", "presets" }.Contains(category))
                        return null;
                    if (requireRoot && (document.Element("request") != null
                        || !string.IsNullOrEmpty(document.Element("selection")?.Value)
                        || !string.IsNullOrEmpty(document.Element("station")?.Value)
                        || !string.IsNullOrEmpty(document.Element("contentCategory")?.Value)
                        || !string.IsNullOrEmpty(document.Element("musicSelection")?.Value)
                        || !string.IsNullOrEmpty(document.Element("topicBranch")?.Value)))
                        return null;
                    return category;
                }

            }
            catch (XmlException) { return null; }
        }

        internal static bool GeneratedStation(ConfigurationSnapshot snapshot, string id)
        {
            var bytes = ReadSignedPayload(snapshot, id);
            if (bytes == null) return false;
            using (var stream = new MemoryStream(bytes))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
            {
                var document = XElement.Load(reader);
                return document.Name == "entry" && TuneInBrowsing.ValidStation(document.Element("station")?.Value);
            }
        }

        private static byte[] ReadSignedPayload(ConfigurationSnapshot snapshot, string id)
        {
            if (id == null || id.Length > 6000 || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{43}\z"))
                return null;
            var parts = id.Split('.');
            if (parts[0].Length % 4 == 1) return null;
            var bytes = Decode(parts[0]);
            var supplied = Decode(parts[1]);
            if (bytes.Length > 4096) return null;
            using (var hmac = new HMACSHA256(Convert.FromBase64String(snapshot.Value.NavigationSecret)))
            {
                var expected = hmac.ComputeHash(bytes);
                var difference = expected.Length ^ supplied.Length;
                for (var i = 0; i < expected.Length && i < supplied.Length; i++) difference |= expected[i] ^ supplied[i];
                return difference == 0 ? bytes : null;
            }
        }

        private NavigationEntry ResolveSigned(ConfigurationSnapshot snapshot, string id)
        {
            var bytes = ReadSignedPayload(snapshot, id);
            if (bytes == null)
                throw new InvalidOperationException("Invalid or unauthenticated TuneIn navigation identifier. Return to the channel root.");
            using (var stream = new MemoryStream(bytes))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
            {
                var document = XElement.Load(reader);
                if ((string)document.Attribute("key") != snapshot.Key)
                    throw new InvalidOperationException("TuneIn configuration changed. Return to the channel root.");
                var entry = ReadEntry(document);
                if (!Allowed(snapshot, entry)) throw new InvalidOperationException("TuneIn item is disabled or outside its selection.");
                return entry;
            }
        }

        private static NavigationEntry ReadEntry(XElement document)
        {
            if (document.Name != "entry") throw new InvalidDataException("Invalid TuneIn entry document.");
            var entry = new NavigationEntry
            {
                Category = document.Element("category")?.Value,
                SelectionId = EmptyToNull(document.Element("selection")?.Value),
                StationId = EmptyToNull(document.Element("station")?.Value),
                Name = document.Element("name")?.Value,
                ContentCategory = EmptyToNull(document.Element("contentCategory")?.Value),
                MusicSelectionId = EmptyToNull(document.Element("musicSelection")?.Value),
                TopicBranchKey = EmptyToNull(document.Element("topicBranch")?.Value)
            };
            var request = document.Element("request");
            if (request != null)
            {
                entry.Request = new DirectoryRequest { Method = (string)request.Attribute("method") };
                foreach (var pair in request.Elements()) entry.Request.Parameters.Add(pair.Name.LocalName, pair.Value);
            }
            return entry;
        }

        private void AssertSnapshot(ConfigurationSnapshot snapshot)
        {
            lock (gate)
                if (key != snapshot.Key) throw new OperationCanceledException("TuneIn configuration changed.");
        }

        private static bool Allowed(ConfigurationSnapshot snapshot, NavigationEntry entry) =>
            snapshot.Enabled(entry.Category)
            && (entry.StationId == null || TuneInBrowsing.ValidStation(entry.StationId))
            && (entry.Category != "language" || LanguageContentAllowed(snapshot, entry))
            && (snapshot.UsesSelectedTopics(entry.Category)
                ? (entry.SelectionId != null) == (entry.TopicBranchKey != null)
                : entry.TopicBranchKey == null)
            && (entry.SelectionId == null ? entry.Request == null && entry.StationId == null
                || !snapshot.UsesSelections(entry.Category)
                : entry.SelectionId == "coordinates" ? entry.Category == "local" && snapshot.LocalSource == "coordinates"
                    : snapshot.UsesSelectedTopics(entry.Category) ? snapshot.TopicIds(entry.Category).Contains(entry.SelectionId)
                        : snapshot.UsesSelectedSelections(entry.Category)
                            && snapshot.Selections(entry.Category).Any(s => s.Id == entry.SelectionId));

        internal static bool LanguageContentAllowed(ConfigurationSnapshot snapshot, NavigationEntry entry)
        {
            if (entry.ContentCategory != null && !new[] { "language", "music", "talk", "sports" }.Contains(entry.ContentCategory))
                return false;
            if (entry.StationId != null && entry.ContentCategory == null) return false;
            var group = TuneInBrowsing.LanguageGroup(entry, entry.Request);
            if (group != "language" && group != "music" && group != "talk" && group != "sports") return false;
            if (group != "music") return entry.MusicSelectionId == null;
            if (entry.MusicSelectionId == null)
                return !snapshot.LanguageUsesMusicGenres || TuneInBrowsing.IsLanguageMusicHub(entry);
            if (!snapshot.LanguageUsesMusicGenres) return false;
            if (!snapshot.Selections("music").Any(s => s.Id == entry.MusicSelectionId)) return false;
            if (entry.StationId != null) return entry.Request == null;
            var id = entry.Request?.Parameters.GetValueOrDefault("id");
            return ConfigurationSnapshot.ValidId(id, "music")
                && id != "c424724" && id != "c424725" && id != "c424726"
                && !entry.Request.Parameters.ContainsKey("c");
        }

        public async Task<XDocument> FetchAsync(ConfigurationSnapshot snapshot, DirectoryRequest request, CancellationToken token,
            TuneInCatalogueSession session = null, bool liveOnly = false)
        {
            CheckCurrent(snapshot, token);
            var document = session == null ? await fetch(request, token, null).ConfigureAwait(false)
                : await session.FetchAsync(request, () => fetch(request, token, session.Timings), token, liveOnly).ConfigureAwait(false);
            CheckCurrent(snapshot, token);
            return document;
        }

        public async Task<SelectorResult> SelectAsync(string category, string parent, bool refresh, CancellationToken token,
            TuneInCatalogueSession catalogue = null)
        {
            var snapshot = Snapshot();
            var topicCategory = ConfigurationSnapshot.IsTopicCategory(category);
            if (!topicCategory && !new[] { "music", "language", "location", "local" }.Contains(category))
                throw new ArgumentException("Invalid selector category.");
            var root = string.IsNullOrEmpty(parent);
            if (!root && (topicCategory || !ConfigurationSnapshot.ValidId(parent, category)))
                throw new ArgumentException("Invalid selector parent.");
            var cacheKey = category + ":" + parent;
            SelectorItem parentItem = null;
            lock (gate)
            {
                if (catalogue == null && !refresh && selectors.TryGetValue(cacheKey, out var cached)) return cached;
                if (!root)
                    parentItem = selectors.Where(p => p.Key.StartsWith(category + ":", StringComparison.Ordinal))
                        .SelectMany(p => p.Value.Items).FirstOrDefault(i => i.Id == parent);
                if (!root && (parentItem == null || parentItem.AncestorIds.Count >= 20))
                    throw new ArgumentException("Load selector ancestry from the root first (maximum depth 20).");
            }
            if (topicCategory)
            {
                var topics = await ReadTopicRootAsync(snapshot, category, token).ConfigureAwait(false);
                CacheSelector(snapshot, cacheKey, topics, token);
                return topics;
            }
            var request = new DirectoryRequest();
            if (category == "language")
            {
                var hub = root ? null : parentItem.ProviderHub;
                if (hub == null) request.Parameters["c"] = "lang";
                else request.Parameters["id"] = hub;
                if (!root) request.Parameters["filter"] = parent;
            }
            else if (category == "music" && root) request.Parameters["c"] = "music";
            else request.Parameters["id"] = root ? "r0" : parent;
            var doc = await FetchAsync(snapshot, request, token, catalogue).ConfigureAwait(false);
            var rows = doc.Root.Element("body").Descendants("outline").ToList();
            var result = new SelectorResult
            {
                ContainsStationMetadata = rows.Any(n => Attribute(n, "type") == "audio"),
                Items = new List<SelectorItem>()
            };
            var omitted = false;
            foreach (var row in rows.Where(n => Attribute(n, "type") == "link"))
            {
                DirectoryRequest link;
                try { link = DirectoryRequest.ParseLink(Attribute(row, "URL")); }
                catch (InvalidDataException) { omitted = true; continue; }
                var linkId = link.Parameters.GetValueOrDefault("id");
                var id = category == "language" ? link.Parameters.GetValueOrDefault("filter") : link.Parameters.GetValueOrDefault("id");
                if (link.Method != "Browse.ashx" || !ConfigurationSnapshot.ValidId(id, category)) continue;
                var group = category == "language" && !root && id == parent
                    && (linkId == "c424724" || linkId == "c424725" || linkId == "c424726");
                if (!group && !root && (id == parent || parentItem.AncestorIds.Contains(id))) continue;
                if (category != "language" && link.Parameters.ContainsKey("filter")) continue;
                if (category == "language" && !group && link.Parameters.GetValueOrDefault("c") != "lang"
                    && linkId != "c424724" && linkId != "c424725" && linkId != "c424726") continue;
                if (group) id = linkId;
                var name = Attribute(row, "text") ?? id;
                var path = root ? name : parentItem.Path + " / " + name;
                if (name.Length > 256 || path.Length > 2048) throw new InvalidDataException("Selector label exceeds limit.");
                result.Items.Add(new SelectorItem { Id = id, Name = name, Path = path,
                    CanSelect = !group, CanExpand = !group,
                    ProviderHub = category == "language" && !group ? linkId : null,
                    AncestorIds = root ? new List<string> { category == "music" ? "music" : category == "language" ? "lang" : "r0" }
                        : parentItem.AncestorIds.Concat(new[] { parent }).ToList() });
                if (result.Items.Count > 2000) throw new InvalidDataException("Selector response exceeds 2000 choices.");
            }
            result.Items = result.Items.GroupBy(i => i.Id).Select(g => g.First()).ToList();
            result.Warning = omitted ? "Some TuneIn links could not be shown; this branch is incomplete." : null;
            if (catalogue != null)
            {
                if (omitted) throw new InvalidDataException("TuneIn language validation returned incomplete links.");
                return result;
            }
            CacheSelector(snapshot, cacheKey, result, token);
            return result;
        }

        private void CacheSelector(ConfigurationSnapshot snapshot, string cacheKey, SelectorResult result, CancellationToken token)
        {
            var characters = SelectorCharacters(result);
            if (characters > 2 * 1024 * 1024) throw new InvalidDataException("TuneIn selector labels exceed cache limit.");
            lock (gate)
            {
                CheckCurrent(snapshot, token);
                if (selectors.TryGetValue(cacheKey, out var previous)) selectorCharacters -= SelectorCharacters(previous);
                selectors.Remove(cacheKey);
                if (selectors.Count >= 128 || selectorCharacters + characters > 2 * 1024 * 1024)
                { selectors.Clear(); selectorCharacters = 0; }
                selectors[cacheKey] = result;
                selectorCharacters += characters;
            }
        }

        internal async Task<SelectorResult> ReadTopicRootAsync(ConfigurationSnapshot snapshot, string category,
            CancellationToken token, TuneInCatalogueSession session = null)
        {
            if (!ConfigurationSnapshot.IsTopicCategory(category)) throw new ArgumentException("Invalid topic category.");
            var request = new DirectoryRequest();
            request.Parameters["c"] = category;
            var document = await FetchAsync(snapshot, request, token, session, liveOnly: true).ConfigureAwait(false);
            var rows = TuneInBrowsing.ContentRows(document.Root.Element("body")).ToList();
            var items = new Dictionary<string, SelectorItem>(StringComparer.Ordinal);
            var omitted = false;
            foreach (var row in rows.Where(row => Attribute(row, "type") == "link"))
            {
                if ((Attribute(row, "key") ?? "").StartsWith("next", StringComparison.Ordinal)) continue;
                DirectoryRequest link;
                try { link = DirectoryRequest.ParseLink(Attribute(row, "URL")); }
                catch (InvalidDataException) { omitted = true; continue; }
                var id = link.Parameters.GetValueOrDefault("id");
                var guideId = Attribute(row, "guide_id");
                var filter = link.Parameters.GetValueOrDefault("filter");
                if (link.Method != "Browse.ashx" || !ConfigurationSnapshot.ValidId(id, category)
                    || (guideId != null && guideId != id) || link.Parameters.Keys.Any(key => key != "id" && key != "filter")
                    || filter != (category == "podcast" ? "p:topic" : null)) continue;
                if (items.ContainsKey(id)) continue;
                var label = Attribute(row, "text") ?? id;
                if (label.Length > 256 || items.Count >= 2000)
                    throw new InvalidDataException("TuneIn topic options exceed the selector limit.");
                items.Add(id, new SelectorItem { Id = id, Name = label, Path = label,
                    AncestorIds = new List<string> { category }, CanSelect = true, TopicRequest = link });
            }
            var containsStations = rows.Any(row => Attribute(row, "type") == "audio");
            return new SelectorResult
            {
                Items = items.Values.ToList(),
                ContainsStationMetadata = containsStations,
                Warning = omitted ? "Some TuneIn links could not be shown; this branch is incomplete." : null
            };
        }

        private static int SelectorCharacters(SelectorResult result) => result.Items.Sum(i =>
            i.Id.Length + i.Name.Length + i.Path.Length + i.AncestorIds.Sum(a => a.Length));

        public static string Attribute(XElement element, string name) =>
            element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

        internal static XDocument ParseCatalogue(byte[] bytes)
        {
            if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("TuneIn catalog exceeds limit.");
            using (var stream = new MemoryStream(bytes))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 }))
            {
                var document = XDocument.Load(reader);
                if (document.Root?.Name != "opml" || document.Root.Elements("body").Count() != 1
                    || document.Root.Element("head")?.Element("fault") != null
                    || (document.Root.Element("head")?.Element("status") is XElement status && status.Value != "200")
                    || document.Descendants().Any(e => e.Ancestors().Take(33).Count() > 32)
                    || document.Root.Element("body").Descendants().Any(e => e.Name != "outline"))
                    throw new InvalidDataException("TuneIn returned an invalid OPML catalog.");
                return document;
            }
        }

        private static string EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;
        private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static byte[] Decode(string value)
        {
            value = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
        }
    }

    internal static class DictionaryExtensions
    {
        public static string GetValueOrDefault(this Dictionary<string, string> value, string key) =>
            value.TryGetValue(key, out var item) ? item : null;
    }
}
