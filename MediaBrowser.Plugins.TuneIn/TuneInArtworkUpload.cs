using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneIn/Artwork/Upload", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class UploadTuneInArtwork : IRequiresRequestStream
    {
        public Stream RequestStream { get; set; }
    }

    [Route("/TuneIn/Artwork/Status", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInArtworkUploadStatus
    {
        public string Operation { get; set; }
    }

    public sealed class TuneInArtworkUploadStarted
    {
        public string Operation { get; set; }
    }

    [Route("/TuneIn/Artwork/Install", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class InstallTuneInArtwork
    {
        public string Token { get; set; }
        public bool ApproveUnscanned { get; set; }
    }

    [Route("/TuneIn/Artwork/Discard", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class DiscardTuneInArtwork
    {
        public string Token { get; set; }
    }

    public sealed class TuneInArtworkUploadStatus
    {
        public string State { get; set; }
        public string Message { get; set; }
        public TuneInArtworkUploadResult Result { get; set; }
    }

    public sealed class TuneInArtworkUploadResult
    {
        public string Token { get; set; }
        public string Scope { get; set; }
        public string PackId { get; set; }
        public string Name { get; set; }
        public string Theme { get; set; }
        public string Version { get; set; }
        public int ImageCount { get; set; }
        public string Destination { get; set; }
        public string ScanStatus { get; set; }
        public bool CleanupPending { get; set; }
        public string[] AffectedCategories { get; set; }
    }

    public sealed class TuneInArtworkUploadService : IService, IRequiresRequest
    {
        private static readonly object SharedGate = new object();
        private static TuneInArtworkUploadCoordinator shared;
        private readonly TuneInArtworkUploadCoordinator coordinator;
        public IRequest Request { get; set; }

        public TuneInArtworkUploadService(IFfmpegManager ffmpeg)
        {
            lock (SharedGate)
            {
                if (shared == null)
                {
                    var root = Plugin.Instance?.ArtworkRoot;
                    if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("Artwork storage is unavailable.");
                    shared = new TuneInArtworkUploadCoordinator(root,
                        (path, token) =>
                        {
                            var configuration = ffmpeg?.FfmpegConfiguration;
                            TuneInArtworkImage.Validate(path, configuration?.EncoderPath,
                                configuration?.EnvironmentVariables?.ToDictionary(setting => setting.VariableName, setting => setting.Value), token);
                        },
                        TuneInArtworkScanner.Scan,
                        install => TuneInRefreshService.InstallArtwork(install),
                        () =>
                        {
                            if (Plugin.Instance.SettingsLock.IsLocked)
                                throw new InvalidOperationException("TuneIn settings are locked. Unlock them before uploading artwork.");
                        });
                }
                coordinator = shared;
            }
        }

        public async Task<object> Post(UploadTuneInArtwork request)
        {
            EnsureAdmin();
            return await coordinator.Start(request.RequestStream, Request.CancellationToken).ConfigureAwait(false);
        }

        public object Get(GetTuneInArtworkUploadStatus request)
        {
            EnsureAdmin();
            return coordinator.Status(request.Operation);
        }

        public async Task<object> Post(InstallTuneInArtwork request)
        {
            EnsureAdmin();
            return await coordinator.Install(request.Token, request.ApproveUnscanned, Request.CancellationToken).ConfigureAwait(false);
        }

        public async Task<object> Post(DiscardTuneInArtwork request)
        {
            EnsureAdmin();
            await coordinator.Discard(request.Token, Request.CancellationToken).ConfigureAwait(false);
            return coordinator.Status();
        }

        private void EnsureAdmin()
        {
            if (Request == null) throw new UnauthorizedAccessException("Artwork uploads require administrator access.");
        }
    }

    internal sealed class TuneInArtworkUploadCoordinator
    {
        private readonly string root;
        private readonly Action<string, CancellationToken> validateImage;
        private readonly Func<string, CancellationToken, TuneInArtworkScanner.Result> scan;
        private readonly Func<Func<bool>, bool> activate;
        private readonly Action ensureUploadAllowed;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly object statusGate = new object();
        private TuneInArtworkUploadStatus status = new TuneInArtworkUploadStatus { State = "idle" };
        private string operation;
        private Task<TuneInArtworkUploadResult> operationTask;
        private Candidate candidate;
        private readonly Timer expiryTimer;

        internal TuneInArtworkUploadCoordinator(string root, Action<string, CancellationToken> validateImage,
            Func<string, CancellationToken, TuneInArtworkScanner.Result> scan, Func<Func<bool>, bool> activate,
            Action ensureUploadAllowed)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.validateImage = validateImage ?? throw new ArgumentNullException(nameof(validateImage));
            this.scan = scan ?? throw new ArgumentNullException(nameof(scan));
            this.activate = activate ?? throw new ArgumentNullException(nameof(activate));
            this.ensureUploadAllowed = ensureUploadAllowed ?? throw new ArgumentNullException(nameof(ensureUploadAllowed));
            if (Directory.Exists(root))
            {
                foreach (var directory in Directory.GetDirectories(root, ".tunein-upload-*"))
                {
                    if (!Regex.IsMatch(Path.GetFileName(directory), @"\A\.tunein-upload-[0-9a-f]{32}\z")) continue;
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("An artwork staging directory is a symbolic link.");
                    Directory.Delete(directory, true);
                }
            }
            expiryTimer = new Timer(ExpireCandidate, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        internal TuneInArtworkUploadStatus Status(string requestedOperation = null)
        {
            lock (statusGate)
            {
                if (requestedOperation != null && requestedOperation != operation)
                    throw new InvalidOperationException("The artwork upload is no longer available.");
                var result = new TuneInArtworkUploadStatus { State = status.State, Message = status.Message };
                if (requestedOperation != null && operationTask != null)
                {
                    if (operationTask.IsFaulted)
                    {
                        result.State = "error";
                        result.Message = operationTask.Exception.GetBaseException().Message;
                    }
                    else if (operationTask.IsCanceled)
                    {
                        result.State = "error";
                        result.Message = "Artwork processing was cancelled.";
                    }
                    else if (operationTask.Status == TaskStatus.RanToCompletion)
                    {
                        result.State = "awaiting confirmation";
                        result.Result = operationTask.Result;
                    }
                    else if (result.State == "error")
                    {
                        result.State = "validating";
                        result.Message = null;
                    }
                }
                return result;
            }
        }

        internal async Task<TuneInArtworkUploadResult> Prepare(Stream input, CancellationToken requestToken)
        {
            var work = await Receive(input, requestToken, false).ConfigureAwait(false);
            return await work.Task.ConfigureAwait(false);
        }

        internal async Task<TuneInArtworkUploadStarted> Start(Stream input, CancellationToken requestToken)
        {
            var work = await Receive(input, requestToken, true).ConfigureAwait(false);
            return new TuneInArtworkUploadStarted { Operation = work.Operation };
        }

        private async Task<UploadWork> Receive(Stream input, CancellationToken requestToken, bool rejectBusy)
        {
            if (input == null) throw new ArgumentException("Select an artwork ZIP.");
            ensureUploadAllowed();
            if (rejectBusy)
            {
                if (!await gate.WaitAsync(0, requestToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Another artwork operation is in progress.");
            }
            else await gate.WaitAsync(requestToken).ConfigureAwait(false);
            string staging = null;
            var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
            try
            {
                ensureUploadAllowed();
                ClearCandidate();
                lock (statusGate) { operation = null; operationTask = null; }
                Directory.CreateDirectory(root);
                staging = Path.Combine(root, ".tunein-upload-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                var archive = Path.Combine(staging, "upload.zip");
                var content = Path.Combine(staging, "content");
                Directory.CreateDirectory(content);
                using (var receiveToken = CancellationTokenSource.CreateLinkedTokenSource(requestToken, timeout.Token))
                {
                    var token = receiveToken.Token;
                    SetStatus("uploading");
                    using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                        {
                            total += count;
                            if (total > TuneInArtworkArchive.MaxUploadBytes) throw new InvalidDataException("Artwork ZIP exceeds the upload limit.");
                            await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                        }
                    }
                    token.ThrowIfCancellationRequested();
                }
                var ownedStaging = staging;
                UploadWork work;
                lock (statusGate)
                {
                    var id = Guid.NewGuid().ToString("N");
                    Task<TuneInArtworkUploadResult> task;
                    using (ExecutionContext.SuppressFlow())
                        task = Task.Run(() => Process(ownedStaging, archive, content, timeout));
                    operation = id;
                    operationTask = task;
                    work = new UploadWork { Operation = id, Task = task };
                    task.ContinueWith(failed => { var ignored = failed.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                staging = null;
                return work;
            }
            catch (Exception error)
            {
                SetStatus("error", error.Message);
                try { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
                finally { timeout.Dispose(); gate.Release(); }
                throw;
            }
        }

        private TuneInArtworkUploadResult Process(string staging, string archive, string content, CancellationTokenSource timeout)
        {
            try
            {
                var token = timeout.Token;
                SetStatus("validating");
                var manifest = TuneInArtworkArchive.ValidateAndExtract(archive, content, validateImage, token);
                File.Delete(archive);
                var staged = new Candidate { Token = Guid.NewGuid().ToString("N"), Staging = staging,
                    Manifest = manifest, ManifestHash = HashFile(Path.Combine(content, "tunein-artwork.json")),
                    Expires = DateTime.UtcNow.AddMinutes(30) };
                VerifyStaged(staged, content, token);
                SetStatus("scanning");
                var scanned = scan(content, token);
                token.ThrowIfCancellationRequested();
                VerifyStaged(staged, content, token);
                staged.Scan = scanned;
                staged.Expires = DateTime.UtcNow.AddMinutes(30);
                candidate = staged;
                staging = null;
                SetStatus("awaiting confirmation");
                return Result(candidate);
            }
            catch (Exception error)
            {
                SetStatus("error", error.Message);
                throw;
            }
            finally
            {
                try { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
                finally { timeout.Dispose(); gate.Release(); }
            }
        }

        internal async Task<TuneInArtworkUploadResult> Install(string token, bool approveUnscanned, CancellationToken requestToken)
        {
            await gate.WaitAsync(requestToken).ConfigureAwait(false);
            try
            {
                var selected = RequireCandidate(token);
                if (selected.Scan == TuneInArtworkScanner.Result.NotScanned && !approveUnscanned)
                    throw new InvalidOperationException("Explicit approval is required to install an unscanned pack.");
                SetStatus("installing");
                var content = Path.Combine(selected.Staging, "content");
                VerifyStaged(selected, content, requestToken);
                string[] affected = null;
                var cleanupPending = activate(() =>
                {
                    affected = Affected(selected);
                    return TuneInArtworkInstall.Install(root, selected.Manifest.Scope, content);
                });
                var result = Result(selected);
                result.Token = null;
                result.CleanupPending = cleanupPending;
                result.AffectedCategories = affected;
                try { ClearCandidate(); }
                catch (IOException) { result.CleanupPending = true; candidate = null; }
                catch (UnauthorizedAccessException) { result.CleanupPending = true; candidate = null; }
                SetStatus("installed", result.CleanupPending ? "Installation succeeded; staging or backup cleanup is pending." : null);
                return result;
            }
            catch (Exception error)
            {
                SetStatus("error", error.Message);
                throw;
            }
            finally { gate.Release(); }
        }

        internal async Task Discard(string token, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { RequireCandidate(token); ClearCandidate(); SetStatus("idle"); }
            finally { gate.Release(); }
        }

        private Candidate RequireCandidate(string token)
        {
            if (candidate == null || token == null || candidate.Token != token)
                throw new InvalidOperationException("The artwork confirmation is no longer available.");
            if (DateTime.UtcNow >= candidate.Expires)
            {
                ClearCandidate();
                throw new InvalidOperationException("The artwork confirmation expired; upload the pack again.");
            }
            return candidate;
        }

        private static void VerifyStaged(Candidate selected, string content, CancellationToken token)
        {
            if ((File.GetAttributes(selected.Staging) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(content) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Staged artwork cannot contain symbolic links.");
            var files = Directory.GetFiles(content);
            if (Directory.GetDirectories(content).Length != 0
                || files.Length != selected.Manifest.Images.Count + selected.Manifest.SupplementalFiles.Count + 1)
                throw new InvalidDataException("Staged artwork changed after validation.");
            var manifestPath = Path.Combine(content, "tunein-artwork.json");
            if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0
                || HashFile(manifestPath) != selected.ManifestHash)
                throw new InvalidDataException("Staged artwork manifest changed after validation.");
            using (var stream = File.OpenRead(manifestPath))
            {
                var current = TuneInUploadManifest.Read(stream);
                if (current.Scope != selected.Manifest.Scope || current.Name != selected.Manifest.Name
                    || current.Images.Count != selected.Manifest.Images.Count
                    || !current.Images.Zip(selected.Manifest.Images, (a, b) =>
                        a.Id == b.Id && a.File == b.File && a.Sha256 == b.Sha256).All(equal => equal))
                    throw new InvalidDataException("Staged artwork manifest changed after validation.");
            }

            foreach (var image in selected.Manifest.Images)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(content, image.File);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Staged artwork cannot contain symbolic links.");
                using (var stream = File.OpenRead(path))
                using (var hash = SHA256.Create())
                {
                    if (!string.Equals(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""),
                        image.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Staged artwork changed after validation.");
                }
            }
            foreach (var file in selected.Manifest.SupplementalFiles)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(content, file.Key);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || HashFile(path) != file.Value)
                    throw new InvalidDataException("Staged artwork changed after validation.");
            }
        }

        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream));
        }

        private TuneInArtworkUploadResult Result(Candidate value) => new TuneInArtworkUploadResult
        {
            Token = value.Token, Scope = value.Manifest.Scope, PackId = value.Manifest.PackId, Name = value.Manifest.Name,
            Theme = value.Manifest.Theme, Version = value.Manifest.Version,
            ImageCount = value.Manifest.Images.Count, Destination = Path.Combine(root, value.Manifest.Scope),
            ScanStatus = value.Scan == TuneInArtworkScanner.Result.Clean ? "Clean" : "Not scanned"
        };

        private void ClearCandidate()
        {
            if (candidate == null) return;
            if (Directory.Exists(candidate.Staging)) Directory.Delete(candidate.Staging, true);
            candidate = null;
            lock (statusGate) { operation = null; operationTask = null; }
        }

        private void SetStatus(string state, string message = null)
        {
            lock (statusGate) status = new TuneInArtworkUploadStatus { State = state, Message = message };
        }

        private string[] Affected(Candidate value)
        {
            if (value.Manifest.Scope == "genres") return new[] { "music", "language" };
            if (value.Manifest.Scope == "languages") return new[] { "language" };
            if (value.Manifest.Scope == "locations") return new[] { "location" };
            var ids = value.Manifest.Images.Select(image => image.Id).ToList();
            var previous = Path.Combine(root, "categories");
            if (Directory.Exists(previous))
                ids.AddRange(Directory.GetFiles(previous)
                    .Where(path => string.Equals(Path.GetExtension(path), ".jpg", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(id => TuneInCategoryArtwork.ValidId("categories", id)));
            return ids.Distinct(StringComparer.Ordinal).ToArray();
        }

        private void ExpireCandidate(object state)
        {
            if (!gate.Wait(0)) return;
            try
            {
                if (candidate == null || DateTime.UtcNow < candidate.Expires) return;
                try { ClearCandidate(); SetStatus("idle", "Artwork confirmation expired."); }
                catch (IOException error) { SetStatus("error", "Could not remove expired artwork staging: " + error.Message); }
                catch (UnauthorizedAccessException error) { SetStatus("error", "Could not remove expired artwork staging: " + error.Message); }
            }
            finally { gate.Release(); }
        }

        private sealed class Candidate
        {
            internal string Token;
            internal string Staging;
            internal TuneInUploadManifest Manifest;
            internal TuneInArtworkScanner.Result Scan;
            internal DateTime Expires;
            internal string ManifestHash;
        }

        private sealed class UploadWork
        {
            internal string Operation;
            internal Task<TuneInArtworkUploadResult> Task;
        }
    }
}
