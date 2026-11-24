using MediaBrowser.Plugins.TuneIn.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;

namespace MediaBrowser.Plugins.TuneIn
{
    [DataContract]
    public sealed class TuneInDiagnosticPack
    {
        [DataMember(Order = 0)] public int SchemaVersion { get; set; } = 1;
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public DateTime CapturedUtc { get; set; }
        [DataMember(Order = 3)] public string PluginVersion { get; set; }
        [DataMember(Order = 4)] public string EmbyVersion { get; set; }
        [DataMember(Order = 5)] public bool PublicNavigationIncluded { get; set; }
        [DataMember(Order = 6)] public TuneInDiagnosticRun Refresh { get; set; }
        [DataMember(Order = 7)] public TuneInDiagnosticSettings Settings { get; set; }
        [DataMember(Order = 8)] public TuneInDiagnosticCounts Counts { get; set; }
        [DataMember(Order = 9)] public TuneInDiagnosticReason[] Reasons { get; set; }
        [DataMember(Order = 10)] public TuneInDiagnosticExample[] Examples { get; set; }
        [DataMember(Order = 11)] public int OmittedExamples { get; set; }
        [DataMember(Order = 12)] public int OmittedNavigationFields { get; set; }
        [DataMember(Order = 13)] public bool Truncated { get; set; }
    }

    [DataContract]
    public sealed class TuneInDiagnosticRun
    {
        [DataMember(Order = 0)] public string RunId { get; set; }
        [DataMember(Order = 1)] public string Category { get; set; }
        [DataMember(Order = 2)] public string State { get; set; }
        [DataMember(Order = 3)] public string Phase { get; set; }
        [DataMember(Order = 4)] public DateTime? StartedUtc { get; set; }
        [DataMember(Order = 5)] public DateTime? FinishedUtc { get; set; }
        [DataMember(Order = 6)] public bool Busy { get; set; }
        [DataMember(Order = 7)] public string ErrorCode { get; set; }
        [DataMember(Order = 8)] public string CurrentCategory { get; set; }
    }

    [DataContract]
    public sealed class TuneInDiagnosticSettings
    {
        [DataMember] public int DiscoveryDeadlineHours { get; set; }
        [DataMember] public int ImportDeadlineHours { get; set; }
        [DataMember] public int? MaxStationCount { get; set; }
        [DataMember] public int TraversalDepthLimit { get; set; }
        [DataMember] public int ApiRequestIntervalSeconds { get; set; }
        [DataMember] public int MaximumRateLimitWaitMinutes { get; set; }
        [DataMember] public int MaximumConsecutiveRateLimitRetries { get; set; }
        [DataMember] public bool AllowPartialCatalogImport { get; set; }
        [DataMember] public bool EnableDiscoveryProgressiveImport { get; set; }
        [DataMember] public int ProgressiveFirstEntries { get; set; }
        [DataMember] public int ProgressiveMinimumNewEntries { get; set; }
        [DataMember] public int ProgressiveMinimumGrowthPercent { get; set; }
        [DataMember] public int ProgressiveMinimumIntervalSeconds { get; set; }
        [DataMember] public int ProgressiveMaximumPasses { get; set; }

        internal static TuneInDiagnosticSettings From(PluginConfiguration c) => new TuneInDiagnosticSettings
        {
            DiscoveryDeadlineHours = c.DiscoveryDeadlineHours, ImportDeadlineHours = c.ImportDeadlineHours,
            MaxStationCount = c.MaxStationCount, TraversalDepthLimit = c.TraversalDepthLimit,
            ApiRequestIntervalSeconds = c.ApiRequestIntervalSeconds, MaximumRateLimitWaitMinutes = c.MaximumRateLimitWaitMinutes,
            MaximumConsecutiveRateLimitRetries = c.MaximumConsecutiveRateLimitRetries,
            AllowPartialCatalogImport = c.AllowPartialCatalogImport, EnableDiscoveryProgressiveImport = c.EnableDiscoveryProgressiveImport,
            ProgressiveFirstEntries = c.ProgressiveFirstEntries, ProgressiveMinimumNewEntries = c.ProgressiveMinimumNewEntries,
            ProgressiveMinimumGrowthPercent = c.ProgressiveMinimumGrowthPercent,
            ProgressiveMinimumIntervalSeconds = c.ProgressiveMinimumIntervalSeconds, ProgressiveMaximumPasses = c.ProgressiveMaximumPasses
        };
    }

    [DataContract]
    public sealed class TuneInDiagnosticCounts
    {
        [DataMember] public int RequestsStarted { get; set; }
        [DataMember] public int RequestsCompleted { get; set; }
        [DataMember] public int CacheHits { get; set; }
        [DataMember] public int RateLimitRetries { get; set; }
        [DataMember] public int SkippedBrowseLinks { get; set; }
        [DataMember] public int? LastHttpStatus { get; set; }
        [DataMember] public int DiscoveryTimeouts { get; set; }
        [DataMember] public int ImportTimeouts { get; set; }
        [DataMember] public int ImportFoldersTotal { get; set; }
        [DataMember] public int ImportFoldersServed { get; set; }
        [DataMember] public int StationEntriesTotal { get; set; }
        [DataMember] public int StationEntriesServed { get; set; }
        [DataMember] public int SubfolderEntriesTotal { get; set; }
        [DataMember] public int SubfolderEntriesServed { get; set; }
        [DataMember] public bool ImportMayHavePartiallyApplied { get; set; }
        [DataMember] public bool Resumed { get; set; }
        [DataMember] public int ImportAttempts { get; set; }
        [DataMember] public int LastImportRefreshLevel { get; set; }
        [DataMember] public bool LastImportRestricted { get; set; }
        [DataMember] public bool LastImportConsumptionVerified { get; set; }
    }

    [DataContract]
    public sealed class TuneInDiagnosticReason
    {
        [DataMember] public string Category { get; set; }
        [DataMember] public string Code { get; set; }
        [DataMember] public int Count { get; set; }
    }

    [DataContract]
    public sealed class TuneInDiagnosticExample
    {
        [DataMember] public string Category { get; set; }
        [DataMember] public string Code { get; set; }
        [DataMember] public string Source { get; set; }
        [DataMember] public string Target { get; set; }
        [DataMember] public string FilterShape { get; set; }
        internal TuneInDiagnosticExample Copy() => (TuneInDiagnosticExample)MemberwiseClone();
    }

    internal sealed class TuneInDiagnosticCollector
    {
        internal const int MaximumExamples = 100;
        private readonly object gate = new object();
        private string runId;
        private string requestedCategory;
        private TuneInDiagnosticSettings settings;
        private readonly Dictionary<string, TuneInDiagnosticReason> reasons = new Dictionary<string, TuneInDiagnosticReason>();
        private readonly Dictionary<string, TuneInDiagnosticExample> examples = new Dictionary<string, TuneInDiagnosticExample>();
        private int totalExamples, omittedFields;
        private int importAttempts, importLevel;
        private bool importRestricted, importVerified;

        internal void Begin(PluginConfiguration configuration, string category = null)
        {
            lock (gate)
            {
                runId = Guid.NewGuid().ToString("N");
                requestedCategory = TuneInDiagnosticFormat.Category(category);
                settings = TuneInDiagnosticSettings.From(configuration);
                reasons.Clear();
                examples.Clear();
                totalExamples = omittedFields = 0;
                importAttempts = importLevel = 0;
                importRestricted = importVerified = false;
            }
        }

        internal void ImportStarted(int level, bool restricted)
        {
            lock (gate) { importAttempts++; importLevel = level; importRestricted = restricted; importVerified = false; }
        }

        internal void ImportVerified() { lock (gate) importVerified = true; }

        internal void Record(string category, string reason, string source, string target, bool station)
        {
            lock (gate)
            {
                if (runId == null) return;
                category = TuneInDiagnosticFormat.Category(category);
                var code = station ? "station_url_rejected"
                    : reason?.StartsWith("Invalid catalog link parameter: filter", StringComparison.Ordinal) == true ? "unsupported_filter"
                    : reason?.StartsWith("TuneIn pagination escaped its selected node", StringComparison.Ordinal) == true ? "pagination_scope_escape"
                    : "catalog_link_rejected";
                var key = category + ":" + code;
                if (!reasons.TryGetValue(key, out var aggregate))
                    reasons.Add(key, aggregate = new TuneInDiagnosticReason { Category = category, Code = code });
                if (aggregate.Count < int.MaxValue) aggregate.Count++;
                if (totalExamples < int.MaxValue) totalExamples++;
                if (station || category == "presets") return;
                var sourceText = TuneInDiagnosticFormat.Navigation(source, out var sourceOmitted, out _);
                var targetText = TuneInDiagnosticFormat.Navigation(target, out var targetOmitted, out var shape);
                omittedFields = (int)Math.Min(int.MaxValue, (long)omittedFields + sourceOmitted + targetOmitted);
                key += "|" + sourceText + "|" + targetText + "|" + shape;
                if (examples.Count >= MaximumExamples || examples.ContainsKey(key)) return;
                examples.Add(key, new TuneInDiagnosticExample
                {
                    Category = category, Code = code, Source = sourceText, Target = targetText, FilterShape = shape
                });
            }
        }

        internal TuneInDiagnosticRun Latest(TuneInRefreshStatus status, bool busy)
        {
            lock (gate)
                return runId == null ? null : new TuneInDiagnosticRun
                {
                    RunId = runId, Category = requestedCategory, CurrentCategory = TuneInDiagnosticFormat.Category(status.Category),
                    State = TuneInDiagnosticFormat.State(status.State),
                    Phase = status.Phase == "Discovering" ? "discovery" : status.Phase == "Importing" ? "import"
                        : busy ? "preparing_or_recovering" : "finished",
                    StartedUtc = status.StartedUtc, FinishedUtc = status.FinishedUtc, Busy = busy,
                    ErrorCode = TuneInDiagnosticFormat.Error(status.ErrorCode)
                };
        }

        internal TuneInDiagnosticPack Capture(string expectedRunId, TuneInRefreshStatus status, bool busy,
            bool includeNavigation, DateTime now, string embyVersion)
        {
            lock (gate)
            {
                if (runId == null || expectedRunId != runId)
                    throw new InvalidOperationException("The selected refresh is no longer available. Reload diagnostics and try again.");
                var selected = includeNavigation ? examples.Values.Select(e => e.Copy()).ToArray() : Array.Empty<TuneInDiagnosticExample>();
                return new TuneInDiagnosticPack
                {
                    Id = Guid.NewGuid().ToString("N"), CapturedUtc = now,
                    PluginVersion = typeof(Plugin).Assembly.GetName().Version.ToString(),
                    EmbyVersion = Version.TryParse(embyVersion, out var version) ? version.ToString() : null,
                    PublicNavigationIncluded = includeNavigation, Refresh = Latest(status, busy),
                    Settings = TuneInDiagnosticFormat.Clone(settings),
                    Counts = new TuneInDiagnosticCounts
                    {
                        RequestsStarted = status.RequestsStarted, RequestsCompleted = status.RequestsCompleted,
                        CacheHits = status.CacheHits, RateLimitRetries = status.RateLimitRetries,
                        SkippedBrowseLinks = status.SkippedBrowseLinks,
                        LastHttpStatus = status.LastHttpStatus >= 100 && status.LastHttpStatus <= 599 ? status.LastHttpStatus : null,
                        DiscoveryTimeouts = status.DiscoveryTimeouts, ImportTimeouts = status.ImportTimeouts,
                        ImportFoldersTotal = status.ImportFoldersTotal, ImportFoldersServed = status.ImportFoldersServed,
                        StationEntriesTotal = status.ImportProgress?.StationEntriesTotal ?? 0,
                        StationEntriesServed = status.ImportProgress?.StationEntriesServed ?? 0,
                        SubfolderEntriesTotal = status.ImportProgress?.SubfolderEntriesTotal ?? 0,
                        SubfolderEntriesServed = status.ImportProgress?.SubfolderEntriesServed ?? 0,
                        ImportMayHavePartiallyApplied = status.ImportMayHavePartiallyApplied, Resumed = status.Resumed,
                        ImportAttempts = importAttempts, LastImportRefreshLevel = importLevel,
                        LastImportRestricted = importRestricted, LastImportConsumptionVerified = importVerified
                    },
                    Reasons = reasons.Values.Select(r => new TuneInDiagnosticReason { Category = r.Category, Code = r.Code, Count = r.Count }).ToArray(),
                    Examples = selected, OmittedExamples = Math.Max(0, totalExamples - selected.Length),
                    OmittedNavigationFields = omittedFields, Truncated = totalExamples > selected.Length
                };
            }
        }
    }

    internal static class TuneInDiagnosticFormat
    {
        internal const string DefaultRepository = "oconnoradv/EmbyTuneIn";
        private static readonly string[] Categories = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets", "all" };
        private static readonly string[] States = { "NeverRun", "Running", "Completed", "Partial", "Limited", "Error", "Cancelled", "TimedOut", "Blocked", "BaselineRequired" };
        private static readonly string[] Errors = { "Cancelled", "TimedOut", "InvalidDataException", "InvalidOperationException",
            "IOException", "HttpRequestException", "TuneInProviderResponseException", "TimeoutException", "OperationCanceledException", "XmlException" };
        private static readonly Regex RepositoryPattern = new Regex(@"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9._-]{1,100}\z");
        private static readonly Regex Alphabet = new Regex(@"\A[sp]:~[\p{L}\p{Nd}#](?::(?:l[1-9][0-9]{0,9}|country))?\z");
        internal static string Category(string value) => value == null ? "all" : Categories.Contains(value) ? value : "unknown";
        internal static string State(string value) => States.Contains(value) ? value : "Unknown";
        internal static string Error(string value) => value == null ? null : Errors.Contains(value) ? value : "operation_failed";

        internal static string Repository(string value)
        {
            if (string.IsNullOrEmpty(value)) return DefaultRepository;
            if (!RepositoryPattern.IsMatch(value) || value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                || value.Split('/')[1] == "." || value.Split('/')[1] == "..")
                throw new ArgumentException("DiagnosticsIssueRepository must be a GitHub owner/repo name, not a URL.");
            return value;
        }

        internal static string Navigation(string value, out int omitted, out string filterShape)
        {
            omitted = 0;
            filterShape = null;
            if (string.IsNullOrEmpty(value) || value.Length > 2048
                || !Uri.TryCreate(HttpUtility.HtmlDecode(value), UriKind.Absolute, out var uri)
                || uri.Host != "opml.radiotime.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                || uri.AbsolutePath != "/Browse.ashx" || (uri.Scheme != "https" && uri.Scheme != "http"))
            { omitted = 1; return null; }
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (query["c"] == "presets") { omitted = 1; return null; }
            var fields = new List<string>();
            foreach (var key in query.AllKeys)
            {
                var values = key == null ? null : query.GetValues(key);
                var text = values?.Length == 1 ? values[0] : null;
                var safe = text != null && text.Length <= 80 && (key == "id" && Regex.IsMatch(text, @"\A[rcglp][1-9][0-9]{0,11}\z")
                    || key == "offset" && Regex.IsMatch(text, @"\A[0-9]{1,9}\z")
                    || key == "c" && new[] { "local", "music", "location", "language", "talk", "sports", "podcast", "genres", "regions" }.Contains(text)
                    || key == "pivot" && new[] { "location", "genre", "language", "topic" }.Contains(text)
                    || key == "filter" && (Alphabet.IsMatch(text) || new[] { "s", "p", "g", "*" }.Contains(text)));
                if (safe) fields.Add(key + "=" + Uri.EscapeDataString(text));
                else
                {
                    omitted++;
                    if (key == "filter" && text != null)
                    {
                        // Preserve only shape, never arbitrary unsupported strings or encoded query tails.
                        filterShape = Regex.Replace(text.Substring(0, Math.Min(text.Length, 80)), @"[\p{L}]+|[\p{Nd}]+|[^:~#,*\-]",
                            m => char.IsLetter(m.Value[0]) ? "L" + m.Length : char.IsDigit(m.Value[0]) ? "N" + m.Length : "?");
                    }
                }
            }
            if (uri.Fragment.Length != 0) omitted++;
            return fields.Count == 0 ? null : "/Browse.ashx?" + string.Join("&", fields.OrderBy(f => f, StringComparer.Ordinal));
        }

        internal static byte[] Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true, "  "))
                    Serializer<T>().WriteObject(writer, value);
                return stream.ToArray();
            }
        }

        internal static T Deserialize<T>(byte[] value)
        {
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(value, new XmlDictionaryReaderQuotas
            {
                MaxDepth = 32, MaxStringContentLength = 512, MaxArrayLength = 128,
                MaxBytesPerRead = 4096, MaxNameTableCharCount = 4096
            }))
                return (T)Serializer<T>().ReadObject(reader);
        }

        private static DataContractJsonSerializer Serializer<T>() => new DataContractJsonSerializer(typeof(T),
            new DataContractJsonSerializerSettings
            {
                DateTimeFormat = new DateTimeFormat("O", CultureInfo.InvariantCulture) { DateTimeStyles = DateTimeStyles.RoundtripKind }
            });

        internal static T Clone<T>(T value) => Deserialize<T>(Serialize(value));
        internal static string Revision(byte[] bytes)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
