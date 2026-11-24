using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneIn/Diagnostics", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInDiagnostics : IReturn<TuneInDiagnosticsStatus> { }

    [Route("/TuneIn/Diagnostics/Capture", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class CaptureTuneInDiagnostics : IReturn<TuneInDiagnosticPreview>
    {
        public string RunId { get; set; }
        public bool IncludePublicNavigation { get; set; }
    }

    [Route("/TuneIn/Diagnostics/Pack", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInDiagnosticPack : IReturn<TuneInDiagnosticPreview>
    {
        public string Id { get; set; }
    }

    [Route("/TuneIn/Diagnostics/Delete", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class DeleteTuneInDiagnosticPacks : IReturn<TuneInDiagnosticsStatus>
    {
        public string Id { get; set; }
        public string Revision { get; set; }
        public bool All { get; set; }
    }

    [Route("/TuneIn/Diagnostics/Draft", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class CreateTuneInDiagnosticDraft : IReturn<TuneInDiagnosticDraft>
    {
        public string Id { get; set; }
        public string Revision { get; set; }
        public string Repository { get; set; }
    }

    public sealed class TuneInDiagnosticDraft
    {
        public string Id { get; set; }
        public string Revision { get; set; }
        public string Repository { get; set; }
        public string Url { get; set; }
    }

    public sealed class TuneInDiagnosticsService : IService, IRequiresRequest
    {
        private readonly TuneInRefresh refresh;
        private readonly TuneInDiagnosticStore store;
        private readonly Func<string> repository;
        private readonly string embyVersion;
        private readonly ILogger logger;
        private readonly Func<DateTime> clock;
        public IRequest Request { get; set; }

        public TuneInDiagnosticsService(IHttpClient client, IApplicationHost host, IApplicationPaths paths,
            IChannelManager manager, ILogManager logs, ILibraryManager library, ITaskManager tasks)
            : this(TuneInRefreshService.GetShared(client, host, paths, manager, logs, library, tasks),
                new TuneInDiagnosticStore(Path.Combine(paths.PluginConfigurationsPath, "TuneInDiagnostics")),
                () => Plugin.Instance.Configuration.DiagnosticsIssueRepository,
                host.GetType().Assembly.GetName().Version?.ToString(), logs.GetLogger(nameof(TuneInDiagnosticsService))) { }

        internal TuneInDiagnosticsService(TuneInRefresh refresh, TuneInDiagnosticStore store, Func<string> repository,
            string embyVersion, ILogger logger = null, Func<DateTime> clock = null)
        {
            this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
            this.embyVersion = embyVersion;
            this.logger = logger;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public object Get(GetTuneInDiagnostics request)
        {
            EnsureAdmin();
            var destination = Destination(out var error);
            var latest = refresh.LatestDiagnosticRun();
            TuneInDiagnosticPackInfo[] packs;
            string storageError = null;
            try { packs = store.List(); }
            catch (Exception failure) when (StorageFailure(failure))
            {
                logger?.Error("TuneIn diagnostic storage failed ({0}).", failure.GetType().Name);
                storageError = StorageError;
                packs = Array.Empty<TuneInDiagnosticPackInfo>();
            }
            return new TuneInDiagnosticsStatus
            {
                CaptureAvailable = latest != null && storageError == null, LatestRefresh = latest, Packs = packs,
                Repository = destination, ReportingError = error, StorageError = storageError,
                HasStoredPacks = Storage(() => store.HasStoredPacks())
            };
        }

        public object Post(CaptureTuneInDiagnostics request)
        {
            EnsureAdmin();
            var pack = refresh.CaptureDiagnostics(request.RunId, request.IncludePublicNavigation, clock(), embyVersion);
            return WithDraft(Storage(() => store.Save(pack)));
        }

        public object Get(GetTuneInDiagnosticPack request)
        {
            EnsureAdmin();
            return WithDraft(Storage(() => store.Read(request.Id)));
        }

        public object Post(DeleteTuneInDiagnosticPacks request)
        {
            EnsureAdmin();
            Storage(() => { store.Delete(request.Id, request.Revision, request.All); return true; });
            return Get(new GetTuneInDiagnostics());
        }

        public object Post(CreateTuneInDiagnosticDraft request)
        {
            EnsureAdmin();
            var preview = WithDraft(Storage(() => store.Read(request.Id)));
            if (preview.ReportingError != null) throw new InvalidOperationException(preview.ReportingError);
            if (request.Revision != preview.Revision || request.Repository != preview.Repository)
                throw new InvalidOperationException("The diagnostic pack or reporting destination changed. Reload the preview and review it again.");
            return new TuneInDiagnosticDraft
            {
                Id = preview.Id, Revision = preview.Revision, Repository = preview.Repository, Url = preview.DraftUrl
            };
        }

        private TuneInDiagnosticPreview WithDraft(TuneInDiagnosticPreview preview)
        {
            preview.Repository = Destination(out var error);
            preview.ReportingError = error;
            if (error != null) return preview;
            preview.DraftTitle = "[TuneIn diagnostics] " + preview.Category + " refresh - " + preview.State;
            preview.DraftBody = "## Refresh diagnostic summary\n"
                + "Category: " + preview.Category + "\nResult: " + preview.State
                + "\nPublic navigation details: " + (preview.PublicNavigationIncluded ? "included; review before sharing" : "excluded")
                + "\nPack revision: " + preview.Revision
                + "\n\n## What happened?\nDescribe the expected and actual behavior here."
                + "\n\n## Diagnostic pack\nAttach the downloaded redacted JSON pack after reviewing it."
                + "\nOpening this draft has not created an issue or uploaded the pack.";
            preview.DraftUrl = "https://github.com/" + preview.Repository + "/issues/new?title="
                + Uri.EscapeDataString(preview.DraftTitle) + "&body=" + Uri.EscapeDataString(preview.DraftBody);
            if (preview.DraftUrl.Length > 1800)
                throw new InvalidOperationException("The GitHub issue draft is too large to open safely. Download the pack and create an issue manually.");
            return preview;
        }

        private string Destination(out string error)
        {
            try
            {
                var result = TuneInDiagnosticFormat.Repository(repository());
                error = null;
                return result;
            }
            catch (ArgumentException)
            {
                error = "The hidden DiagnosticsIssueRepository setting is invalid. Correct its GitHub owner/repo value before reporting. Local capture and download are still available.";
                return null;
            }
        }

        private T Storage<T>(Func<T> operation)
        {
            try { return operation(); }
            catch (Exception error) when (StorageFailure(error))
            {
                logger?.Error("TuneIn diagnostic storage failed ({0}).", error.GetType().Name);
                throw new InvalidOperationException(StorageError);
            }
        }

        private const string StorageError = "The diagnostic storage operation failed. Check storage access and available space; corrupt saved packs can be removed with Delete all packs.";
        private static bool StorageFailure(Exception error) => error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
            || error is SerializationException || error is XmlException || error is ArgumentException;

        private void EnsureAdmin()
        {
            if (Request == null) throw new UnauthorizedAccessException("TuneIn diagnostics requires administrator access.");
        }
    }
}
