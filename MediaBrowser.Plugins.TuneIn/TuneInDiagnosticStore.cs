using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MediaBrowser.Plugins.TuneIn
{
    public class TuneInDiagnosticPackInfo
    {
        public string Id { get; set; }
        public string Revision { get; set; }
        public DateTime CapturedUtc { get; set; }
        public string Category { get; set; }
        public string State { get; set; }
        public int Bytes { get; set; }
        public bool PublicNavigationIncluded { get; set; }
    }

    public sealed class TuneInDiagnosticPreview : TuneInDiagnosticPackInfo
    {
        public string PreviewText { get; set; }
        public string FileName { get; set; }
        public string Repository { get; set; }
        public string DraftTitle { get; set; }
        public string DraftBody { get; set; }
        public string DraftUrl { get; set; }
        public string ReportingError { get; set; }
    }

    public sealed class TuneInDiagnosticsStatus
    {
        public bool CaptureAvailable { get; set; }
        public string StorageError { get; set; }
        public bool HasStoredPacks { get; set; }
        public TuneInDiagnosticRun LatestRefresh { get; set; }
        public TuneInDiagnosticPackInfo[] Packs { get; set; }
        public string Repository { get; set; }
        public string ReportingError { get; set; }
        public int MaximumPacks { get; set; } = TuneInDiagnosticStore.MaximumPacks;
        public int RetentionDays { get; set; } = TuneInDiagnosticStore.RetentionDays;
        public int MaximumPackBytes { get; set; } = TuneInDiagnosticStore.MaximumPackBytes;
        public int MaximumTotalBytes { get; set; } = TuneInDiagnosticStore.MaximumTotalBytes;
    }

    internal sealed class TuneInDiagnosticStore
    {
        internal const int MaximumPacks = 5, RetentionDays = 7, MaximumPackBytes = 256 * 1024, MaximumTotalBytes = 1024 * 1024;
        private static readonly ConcurrentDictionary<string, object> Gates =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly string directory;
        private readonly object gate;
        private readonly Func<DateTime> clock;

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);

        internal TuneInDiagnosticStore(string directory, Func<DateTime> clock = null)
        {
            this.directory = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
            gate = Gates.GetOrAdd(this.directory, _ => new object());
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        internal TuneInDiagnosticPackInfo[] List()
        {
            lock (gate) return Prune().Select(e => Info(e.Pack, e.Bytes)).ToArray();
        }

        internal bool HasStoredPacks()
        {
            lock (gate)
            {
                if (!StorageDirectoryExists()) return false;
                EnsureDirectory();
                return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                    .Any(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _));
            }
        }

        internal TuneInDiagnosticPreview Save(TuneInDiagnosticPack pack, CancellationToken token = default)
        {
            Validate(pack);
            var bytes = TuneInDiagnosticFormat.Serialize(pack);
            if (bytes.Length > MaximumPackBytes) throw new InvalidDataException("The diagnostic pack exceeds its size limit.");
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (pack.CapturedUtc > clock().AddMinutes(5) || clock() - pack.CapturedUtc >= TimeSpan.FromDays(RetentionDays))
                    throw new InvalidDataException("The diagnostic capture timestamp is outside its retention window.");
                EnsureDirectory();
                var target = FilePath(pack.Id);
                var temporary = Path.Combine(directory, pack.Id + ".tmp");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        Restrict(temporary, false);
                        token.ThrowIfCancellationRequested();
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, target);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                Prune(pack.Id);
                return Preview(pack, bytes);
            }
        }

        internal TuneInDiagnosticPreview Read(string id)
        {
            lock (gate)
            {
                ValidateId(id);
                var entries = Prune();
                var entry = entries.SingleOrDefault(e => e.Pack.Id == id);
                if (entry.Pack == null) throw new InvalidOperationException("The diagnostic pack expired or was deleted. Reload saved packs.");
                return Preview(entry.Pack, entry.Bytes);
            }
        }

        internal void Delete(string id, string revision, bool all)
        {
            lock (gate)
            {
                if (all)
                {
                    if (!StorageDirectoryExists()) return;
                    EnsureDirectory();
                    foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                        .Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)))
                    { RejectReparse(path); File.Delete(path); }
                    return;
                }
                var preview = Read(id);
                if (preview.Revision != revision) throw new InvalidOperationException("The diagnostic pack changed. Review it again before deletion.");
                File.Delete(FilePath(id));
            }
        }

        private (TuneInDiagnosticPack Pack, byte[] Bytes)[] Prune(string preserve = null)
        {
            if (!StorageDirectoryExists()) return Array.Empty<(TuneInDiagnosticPack, byte[])>();
            EnsureDirectory();
            var now = clock();
            var entries = Paths().Select(path =>
            {
                RejectReparse(path);
                var size = new FileInfo(path).Length;
                if (size > MaximumPackBytes) throw new InvalidDataException("A saved diagnostic pack exceeds its size limit. Delete saved packs to recover.");
                var bytes = ReadBounded(path);
                var pack = TuneInDiagnosticFormat.Deserialize<TuneInDiagnosticPack>(bytes);
                Validate(pack);
                if (pack.Id != Path.GetFileNameWithoutExtension(path)
                    || !bytes.SequenceEqual(TuneInDiagnosticFormat.Serialize(pack)))
                    throw new InvalidDataException("A saved diagnostic pack failed validation. Delete saved packs to recover.");
                return (Pack: pack, Bytes: bytes);
            }).OrderByDescending(e => e.Pack.Id == preserve).ThenByDescending(e => e.Pack.CapturedUtc)
                .ThenBy(e => e.Pack.Id, StringComparer.Ordinal).ToArray();
            var count = 0;
            var total = 0;
            var kept = new System.Collections.Generic.List<(TuneInDiagnosticPack Pack, byte[] Bytes)>();
            foreach (var entry in entries)
            {
                if (entry.Pack.CapturedUtc > now.AddMinutes(5))
                    throw new InvalidDataException("A saved diagnostic pack has an invalid timestamp.");
                if (now - entry.Pack.CapturedUtc >= TimeSpan.FromDays(RetentionDays)
                    || count >= MaximumPacks || total + entry.Bytes.Length > MaximumTotalBytes)
                    File.Delete(FilePath(entry.Pack.Id));
                else
                {
                    kept.Add(entry);
                    count++;
                    total += entry.Bytes.Length;
                }
            }
            return kept.OrderByDescending(e => e.Pack.CapturedUtc).ToArray();
        }

        private string[] Paths()
        {
            var paths = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)).Take(65).ToArray();
            if (paths.Length > 64) throw new InvalidDataException("Too many diagnostic files. Remove old diagnostic files before continuing.");
            return paths;
        }

        private static byte[] ReadBounded(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                int read;
                while ((read = file.Read(buffer, 0, Math.Min(buffer.Length, MaximumPackBytes + 1 - (int)output.Length))) != 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > MaximumPackBytes) throw new InvalidDataException("A saved diagnostic pack exceeds its size limit.");
                }
                return output.ToArray();
            }
        }

        private void EnsureDirectory()
        {
            Directory.CreateDirectory(directory);
            RejectReparse(directory);
            Restrict(directory, true);
            foreach (var temporary in Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly)
                .Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)).Take(65))
            {
                RejectReparse(temporary);
                File.Delete(temporary);
            }

        }

        private bool StorageDirectoryExists()
        {
            if (File.Exists(directory)) throw new IOException("The diagnostic storage path is not a directory.");
            return Directory.Exists(directory);
        }

        private static void RejectReparse(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Diagnostic storage cannot use symbolic links or reparse points.");
        }

        private static void Restrict(string path, bool folder)
        {
            // Windows inherits the Emby configuration directory's ACL; Unix also needs explicit owner-only modes.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && chmod(path, folder ? 448u : 384u) != 0)
                throw new IOException("Could not restrict diagnostic storage permissions.");
        }

        private string FilePath(string id) { ValidateId(id); return Path.Combine(directory, id + ".json"); }
        private static void ValidateId(string id)
        {
            if (id == null || !Guid.TryParseExact(id, "N", out var guid) || guid.ToString("N") != id)
                throw new ArgumentException("Select a valid diagnostic pack.");
        }

        private static TuneInDiagnosticPackInfo Info(TuneInDiagnosticPack pack, byte[] bytes) => new TuneInDiagnosticPackInfo
        {
            Id = pack.Id, Revision = TuneInDiagnosticFormat.Revision(bytes), CapturedUtc = pack.CapturedUtc,
            Category = pack.Refresh.Category, State = pack.Refresh.State, Bytes = bytes.Length,
            PublicNavigationIncluded = pack.PublicNavigationIncluded
        };

        private static TuneInDiagnosticPreview Preview(TuneInDiagnosticPack pack, byte[] bytes)
        {
            var info = Info(pack, bytes);
            return new TuneInDiagnosticPreview
            {
                Id = info.Id, Revision = info.Revision, CapturedUtc = info.CapturedUtc, Category = info.Category,
                State = info.State, Bytes = info.Bytes, PublicNavigationIncluded = info.PublicNavigationIncluded,
                PreviewText = Encoding.UTF8.GetString(bytes), FileName = "TuneIn-diagnostics-" + info.Id + ".json"
            };
        }

        private static void Validate(TuneInDiagnosticPack pack)
        {
            if (pack == null) throw new InvalidDataException("Invalid diagnostic pack.");
            ValidateId(pack.Id);
            var run = pack.Refresh;
            if (pack.SchemaVersion != 1 || pack.CapturedUtc == default || !Version.TryParse(pack.PluginVersion, out _)
                || pack.EmbyVersion != null && !Version.TryParse(pack.EmbyVersion, out _)
                || run == null || !Guid.TryParseExact(run.RunId, "N", out _)
                || run.Category != TuneInDiagnosticFormat.Category(run.Category)
                || run.CurrentCategory != TuneInDiagnosticFormat.Category(run.CurrentCategory)
                || run.State != TuneInDiagnosticFormat.State(run.State)
                || !new[] { "discovery", "import", "preparing_or_recovering", "finished" }.Contains(run.Phase)
                || run.ErrorCode != TuneInDiagnosticFormat.Error(run.ErrorCode)
                || pack.Settings == null || pack.Counts == null || pack.Reasons == null || pack.Reasons.Length > 40
                || pack.Examples == null || pack.Examples.Length > TuneInDiagnosticCollector.MaximumExamples
                || !pack.PublicNavigationIncluded && pack.Examples.Length != 0)
                throw new InvalidDataException("Invalid diagnostic pack fields.");
            foreach (var reason in pack.Reasons)
                if (reason == null || !ValidReason(reason.Category, reason.Code) || reason.Count < 1)
                    throw new InvalidDataException("Invalid diagnostic reason.");
            foreach (var example in pack.Examples)
                if (example == null || !ValidReason(example.Category, example.Code) || example.Category == "presets"
                    || example.Code == "station_url_rejected" || !ValidNavigation(example.Source) || !ValidNavigation(example.Target)
                    || example.FilterShape != null && !Regex.IsMatch(example.FilterShape, @"\A(?:L[0-9]{1,2}|N[0-9]{1,2}|[:~#,*?\-]){0,80}\z"))
                    throw new InvalidDataException("Invalid diagnostic example.");
        }

        private static bool ValidReason(string category, string code) => category == TuneInDiagnosticFormat.Category(category)
            && new[] { "station_url_rejected", "unsupported_filter", "pagination_scope_escape", "catalog_link_rejected" }.Contains(code);
        private static bool ValidNavigation(string text) => text == null || text.Length <= 512
            && TuneInDiagnosticFormat.Navigation("https://opml.radiotime.com" + text, out var omitted, out _) == text && omitted == 0;
    }
}
