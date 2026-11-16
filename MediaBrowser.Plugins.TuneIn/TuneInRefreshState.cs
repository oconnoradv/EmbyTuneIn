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

    public sealed class TuneInRefreshState
    {
        public List<RadioGroupRefreshHistory> History { get; set; } = new List<RadioGroupRefreshHistory>();
        public TuneInScheduleSettings Schedule { get; set; } = new TuneInScheduleSettings();
        public List<TuneInTriggerBackup> TriggerBackups { get; set; } = new List<TuneInTriggerBackup>();
    }

    internal sealed class TuneInRefreshStateStore
    {
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
                using (var stream = File.OpenRead(path))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
                    Value = (TuneInRefreshState)Serializer.Deserialize(reader);
                if (Value.History == null || Value.Schedule == null || Value.TriggerBackups == null)
                    throw new InvalidDataException("TuneIn refresh state is incomplete.");
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
