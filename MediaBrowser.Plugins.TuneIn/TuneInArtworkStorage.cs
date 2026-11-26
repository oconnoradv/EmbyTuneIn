using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.IO;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneIn/Artwork/CheckStorage", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class CheckTuneInArtworkStorage { }

    public sealed class TuneInArtworkStorageStatus
    {
        public bool Writable { get; set; }
        public string Message { get; set; }
    }

    public sealed class TuneInArtworkStorageService : IService, IRequiresRequest
    {
        public IRequest Request { get; set; }

        public object Post(CheckTuneInArtworkStorage request)
        {
            if (Request == null) throw new UnauthorizedAccessException("Artwork storage checks require administrator access.");
            return TuneInArtworkStorage.Check(Plugin.Instance?.ArtworkRoot);
        }
    }

    internal static class TuneInArtworkStorage
    {
        internal static void InitializeDirectories(string root, Action<string, Exception> reportFailure)
        {
            if (reportFailure == null) throw new ArgumentNullException(nameof(reportFailure));
            if (string.IsNullOrWhiteSpace(root))
            {
                reportFailure(root, new IOException("The artwork storage path is unavailable."));
                return;
            }

            lock (TuneInCategoryArtwork.PackGate)
            {
                if (!EnsureDirectory(root, reportFailure)) return;
                foreach (var scope in TuneInCategoryArtwork.Scopes)
                    EnsureDirectory(Path.Combine(root, scope), reportFailure);
            }
        }

        private static bool EnsureDirectory(string path, Action<string, Exception> reportFailure)
        {
            try
            {
                Directory.CreateDirectory(path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Artwork directories cannot be symbolic links or reparse points.");
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                reportFailure(path, error);
                return false;
            }
        }

        internal static TuneInArtworkStorageStatus Check(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return Failure("The artwork storage path is unavailable.");
            string probe = null, renamed = null;
            try
            {
                try
                {
                    Directory.CreateDirectory(root);
                    if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("The artwork folder is a symbolic link or reparse point.");
                    var path = Path.Combine(root, ".tunein-probe-" + Guid.NewGuid().ToString("N"));
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        probe = path;
                        stream.WriteByte(0);
                    }
                    var destination = path + ".renamed";
                    File.Move(probe, destination);
                    probe = null;
                    renamed = destination;
                    File.Delete(renamed);
                    renamed = null;
                }
                finally
                {
                    if (probe != null) File.Delete(probe);
                    if (renamed != null) File.Delete(renamed);
                }
                return new TuneInArtworkStorageStatus { Writable = true, Message = "Artwork storage is writable." };
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return Failure("Artwork storage check failed for '" + root + "': " + error.Message);
            }
        }

        private static TuneInArtworkStorageStatus Failure(string message) => new TuneInArtworkStorageStatus
        {
            Writable = false,
            Message = message + " Check that the Emby service account can create, rename and delete files "
                + "in the artwork folder and that its filesystem or container mount is writable."
        };
    }
}
