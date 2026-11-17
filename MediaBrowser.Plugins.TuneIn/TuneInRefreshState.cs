using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class RadioGroupRefreshHistory
    {
        public string Category { get; set; }
        public DateTime? LastSuccessfulUtc { get; set; }
        public DateTime? LastAttemptUtc { get; set; }
        public string LastAttemptState { get; set; }
    }

    public sealed class RadioGroupInterval
    {
        public string Category { get; set; }
        public string Mode { get; set; } = "inherit";
        public int Minutes { get; set; }
    }

    public class TuneInScheduleSettings
    {
        public bool ManageIntervals { get; set; }
        public int GlobalIntervalMinutes { get; set; }
        public List<RadioGroupInterval> Overrides { get; set; } = new List<RadioGroupInterval>();
        public bool DisableGlobalRefreshTrigger { get; set; }
        public DateTime? AppliedUtc { get; set; }
    }

    public sealed class TuneInTriggerBackup
    {
        public string Key { get; set; }
        public TaskTriggerInfo[] Triggers { get; set; }
    }

    public sealed class TuneInCategoryRequestCount
    {
        public string Category { get; set; }
        public int Requests { get; set; }
    }

    public sealed class TuneInRequestBaseline
    {
        public string ConfigurationKey { get; set; }
        public int TotalRequests { get; set; }
        public DateTime SavedUtc { get; set; }
        public List<TuneInCategoryRequestCount> Categories { get; set; } = new List<TuneInCategoryRequestCount>();
    }

    public sealed class TuneInTimedOutCheckpoint
    {
        public bool PagesComplete { get; set; } = true;
        public string Category { get; set; }
        public string AttemptId { get; set; }
        public string ConfigurationKey { get; set; }
        public int TimeoutMinutes { get; set; }
        public DateTime TimedOutUtc { get; set; }
        public bool ConfiguredTask { get; set; }
        public List<string> Categories { get; set; } = new List<string>();
        public List<string> CompletedCategories { get; set; } = new List<string>();
        public List<string> CompletedImportFolders { get; set; } = new List<string>();
        public List<TuneInCategoryRequestCount> CompletedRequestCounts { get; set; } = new List<TuneInCategoryRequestCount>();
        public bool ImportMayHavePartiallyApplied { get; set; }
        public List<TuneInRefreshPageCheckpoint> Pages { get; set; } = new List<TuneInRefreshPageCheckpoint>();
    }

    public sealed class TuneInRefreshPageCheckpoint
    {
        public string Key { get; set; }
        public byte[] Bytes { get; set; }
    }

    public sealed class TuneInRefreshState
    {
        public List<RadioGroupRefreshHistory> History { get; set; } = new List<RadioGroupRefreshHistory>();
        public TuneInScheduleSettings Schedule { get; set; } = new TuneInScheduleSettings();
        public List<TuneInTriggerBackup> TriggerBackups { get; set; } = new List<TuneInTriggerBackup>();
        public TuneInRequestBaseline RequestBaseline { get; set; }
        public TuneInTimedOutCheckpoint TimedOutCheckpoint { get; set; }
    }

    internal sealed class TuneInRefreshStateStore
    {
        private const int MaxPersistedStateBytes = 24 * 1024 * 1024;
        internal object Gate { get; } = new object();
        internal TuneInRefreshState Value { get; }
        private readonly string path;
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(TuneInRefreshState));

        internal TuneInRefreshStateStore(string path = null)
        {
            this.path = path;
            if (path == null || !File.Exists(path)) Value = new TuneInRefreshState();
            else
            {
                if (new FileInfo(path).Length > MaxPersistedStateBytes)
                    throw new InvalidDataException("TuneIn refresh state exceeds its persisted size limit.");
                using (var stream = File.OpenRead(path))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxPersistedStateBytes }))
                    Value = (TuneInRefreshState)Serializer.Deserialize(reader);
                if (Value.History == null || Value.Schedule == null || Value.TriggerBackups == null)
                    throw new InvalidDataException("TuneIn refresh state is incomplete.");
                if (!ValidCheckpoint(Value.TimedOutCheckpoint)) Value.TimedOutCheckpoint = null;
            }
        }

        internal RadioGroupRefreshHistory History(string category)
        {
            lock (Gate)
            {
                var previous = Value.History.SingleOrDefault(item => item.Category == category);
                return new RadioGroupRefreshHistory
                {
                    Category = category,
                    LastSuccessfulUtc = previous?.LastSuccessfulUtc,
                    LastAttemptUtc = previous?.LastAttemptUtc,
                    LastAttemptState = previous?.LastAttemptState
                };
            }
        }

        internal void Record(string category, string state, DateTime finished)
        {
            lock (Gate)
            {
                var history = Value.History.SingleOrDefault(item => item.Category == category);
                if (history == null) { history = new RadioGroupRefreshHistory { Category = category }; Value.History.Add(history); }
                history.LastAttemptUtc = finished;
                history.LastAttemptState = state;
                if (state == "Completed") history.LastSuccessfulUtc = finished;
                Save();
            }
        }

        internal TuneInRequestBaseline Baseline(string configurationKey)
        {
            lock (Gate)
            {
                var previous = Value.RequestBaseline;
                if (previous == null || previous.ConfigurationKey != configurationKey) return null;
                return new TuneInRequestBaseline
                {
                    ConfigurationKey = previous.ConfigurationKey,
                    TotalRequests = previous.TotalRequests,
                    SavedUtc = previous.SavedUtc,
                    Categories = previous.Categories.Select(item => new TuneInCategoryRequestCount
                    { Category = item.Category, Requests = item.Requests }).ToList()
                };
            }
        }

        internal void RecordBaseline(string configurationKey, int totalRequests,
            IEnumerable<TuneInCategoryRequestCount> categories)
        {
            lock (Gate)
            {
                Value.RequestBaseline = new TuneInRequestBaseline
                {
                    ConfigurationKey = configurationKey,
                    TotalRequests = totalRequests,
                    SavedUtc = DateTime.UtcNow,
                    Categories = categories.Select(item => new TuneInCategoryRequestCount
                    { Category = item.Category, Requests = item.Requests }).ToList()
                };
                Save();
            }
        }

        internal TuneInTimedOutCheckpoint TimeoutCheckpoint
        {
            get
            {
                lock (Gate)
                {
                    var value = Value.TimedOutCheckpoint;
                    return value == null ? null : new TuneInTimedOutCheckpoint
                    {
                        Category = value.Category,
                        AttemptId = value.AttemptId,
                        ConfigurationKey = value.ConfigurationKey,
                        PagesComplete = value.PagesComplete,
                        TimeoutMinutes = value.TimeoutMinutes,
                        TimedOutUtc = value.TimedOutUtc,
                        ConfiguredTask = value.ConfiguredTask,
                        Categories = (value.Categories ?? new List<string>()).ToList(),
                        CompletedCategories = (value.CompletedCategories ?? new List<string>()).ToList(),
                        CompletedImportFolders = (value.CompletedImportFolders ?? new List<string>()).ToList(),
                        CompletedRequestCounts = (value.CompletedRequestCounts ?? new List<TuneInCategoryRequestCount>())
                            .Select(item => new TuneInCategoryRequestCount { Category = item.Category, Requests = item.Requests }).ToList(),
                        ImportMayHavePartiallyApplied = value.ImportMayHavePartiallyApplied,
                        Pages = (value.Pages ?? new List<TuneInRefreshPageCheckpoint>()).Select(page =>
                            new TuneInRefreshPageCheckpoint { Key = page.Key, Bytes = page.Bytes == null ? null : (byte[])page.Bytes.Clone() }).ToList()
                    };
                }
            }
        }

        internal void RecordTimeoutCheckpoint(string category, string attemptId, string configurationKey,
            int timeoutMinutes, bool configuredTask, DateTime timedOutUtc,
            IEnumerable<string> categories = null, IEnumerable<string> completedCategories = null,
            IEnumerable<TuneInRefreshPageCheckpoint> pages = null, IEnumerable<string> completedImportFolders = null,
            IEnumerable<TuneInCategoryRequestCount> completedRequestCounts = null, bool importMayHavePartiallyApplied = false,
            bool pagesComplete = true)
        {
            lock (Gate)
            {
                var checkpoint = new TuneInTimedOutCheckpoint
                {
                    Category = category,
                    AttemptId = attemptId,
                    ConfigurationKey = configurationKey,
                    PagesComplete = pagesComplete,
                    TimeoutMinutes = timeoutMinutes,
                    ConfiguredTask = configuredTask,
                    TimedOutUtc = timedOutUtc,
                    Categories = categories?.ToList() ?? new List<string>(),
                    CompletedCategories = completedCategories?.ToList() ?? new List<string>(),
                    CompletedImportFolders = completedImportFolders?.ToList() ?? new List<string>(),
                    CompletedRequestCounts = completedRequestCounts?.Select(item => new TuneInCategoryRequestCount
                    { Category = item.Category, Requests = item.Requests }).ToList() ?? new List<TuneInCategoryRequestCount>(),
                    ImportMayHavePartiallyApplied = importMayHavePartiallyApplied,
                    Pages = pages?.Select(page => new TuneInRefreshPageCheckpoint
                    { Key = page.Key, Bytes = page.Bytes == null ? null : (byte[])page.Bytes.Clone() }).ToList()
                        ?? new List<TuneInRefreshPageCheckpoint>()
                };
                if (!ValidCheckpoint(checkpoint))
                    throw new InvalidDataException("TuneIn timeout checkpoint exceeds its validation limits.");
                Value.TimedOutCheckpoint = checkpoint;
                Save();
            }
        }

        private static bool ValidCheckpoint(TuneInTimedOutCheckpoint checkpoint)
        {
            if (checkpoint == null) return true;
            var pages = checkpoint.Pages ?? new List<TuneInRefreshPageCheckpoint>();
            var imports = checkpoint.CompletedImportFolders ?? new List<string>();
            var categories = checkpoint.Categories ?? new List<string>();
            var completed = checkpoint.CompletedCategories ?? new List<string>();
            var counts = checkpoint.CompletedRequestCounts ?? new List<TuneInCategoryRequestCount>();
            if (string.IsNullOrWhiteSpace(checkpoint.Category) || string.IsNullOrWhiteSpace(checkpoint.AttemptId)
                || string.IsNullOrWhiteSpace(checkpoint.ConfigurationKey) || checkpoint.TimeoutMinutes < 1
                || pages.Count > 128 || imports.Count > 1000 || categories.Count > 8 || completed.Count > 8 || counts.Count > 8
                || imports.Any(folder => string.IsNullOrEmpty(folder) || folder.Length > 6000)
                || imports.Distinct(StringComparer.Ordinal).Count() != imports.Count
                || categories.Distinct(StringComparer.Ordinal).Count() != categories.Count
                || completed.Distinct(StringComparer.Ordinal).Count() != completed.Count
                || counts.Any(item => item == null || item.Requests < 0))
                return false;
            var bytes = 0;
            foreach (var page in pages)
            {
                if (page == null || page.Bytes == null || page.Bytes.Length == 0 || page.Bytes.Length > 2 * 1024 * 1024
                    || !System.Text.RegularExpressions.Regex.IsMatch(page.Key ?? "", @"^:[A-Za-z0-9+/]{43}=$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    return false;
                bytes += page.Bytes.Length;
                if (bytes > 8 * 1024 * 1024) return false;
            }
            return pages.Select(page => page.Key).Distinct(StringComparer.Ordinal).Count() == pages.Count;
        }

        internal void ClearTimeoutCheckpoint(string category)
        {
            lock (Gate)
            {
                if (Value.TimedOutCheckpoint?.Category != category) return;
                Value.TimedOutCheckpoint = null;
                Save();
            }
        }

        internal void ClearTimeoutCheckpoint()
        {
            lock (Gate)
            {
                if (Value.TimedOutCheckpoint == null) return;
                Value.TimedOutCheckpoint = null;
                Save();
            }
        }

        internal void Save()
        {
            lock (Gate)
            {
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        Serializer.Serialize(stream, Value);
                        if (stream.Length > MaxPersistedStateBytes)
                            throw new InvalidDataException("TuneIn refresh state exceeds its persisted size limit.");
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
        }
    }
}
