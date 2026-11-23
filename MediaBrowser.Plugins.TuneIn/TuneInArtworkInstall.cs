using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MediaBrowser.Plugins.TuneIn
{
    internal static class TuneInArtworkInstall
    {
        internal static bool Install(string root, string scope, string staged,
            Action<string, string> move = null)
        {
            if (!TuneInCategoryArtwork.Scopes.Contains(scope, StringComparer.Ordinal))
                throw new ArgumentException("Unsupported artwork scope.", nameof(scope));
            lock (TuneInCategoryArtwork.PackGate)
            {
                try { return InstallLocked(root, scope, staged, move); }
                finally { TuneInRefreshService.ArtworkStatusCache.Invalidate(scope); }
            }
        }

        private static bool InstallLocked(string root, string scope, string staged,
            Action<string, string> move)
        {
            if (!TuneInCategoryArtwork.Scopes.Contains(scope, StringComparer.Ordinal))
                throw new ArgumentException("Unsupported artwork scope.", nameof(scope));
            if (!Directory.Exists(staged)) throw new DirectoryNotFoundException("Artwork staging directory is missing.");
            move = move ?? Directory.Move;
            Recover(root, scope);
            var destination = Path.Combine(root, scope);
            var backup = Path.Combine(root, ".artwork-backup-" + scope);
            var journal = Path.Combine(root, ".artwork-install-" + scope);
            var committed = journal + ".committed";
            EnsureNotLink(root);
            EnsureNotLink(staged);
            if (Directory.Exists(destination)) EnsureNotLink(destination);
            var hadPrevious = Directory.Exists(destination);
            WriteMarker(journal, hadPrevious ? "old" : "new");
            try
            {
                if (hadPrevious) move(destination, backup);
                move(staged, destination);
                WriteMarker(committed, "committed");
            }
            catch (Exception error)
            {
                try
                {
                    if (File.Exists(committed)) File.Delete(committed);
                    Recover(root, scope);
                }
                catch (Exception recovery)
                {
                    throw new AggregateException("Artwork installation failed; recovery also failed. Preserve the backup and resolve the server storage error.", error, recovery);
                }
                throw;
            }
            try { Recover(root, scope); return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        internal static void Recover(string root, string scope)
        {
            if (!TuneInCategoryArtwork.Scopes.Contains(scope, StringComparer.Ordinal))
                throw new ArgumentException("Unsupported artwork scope.", nameof(scope));
            var destination = Path.Combine(root, scope);
            var backup = Path.Combine(root, ".artwork-backup-" + scope);
            var journal = Path.Combine(root, ".artwork-install-" + scope);
            var committed = journal + ".committed";
            if (!File.Exists(journal))
            {
                if (Directory.Exists(backup))
                    throw new InvalidDataException("Artwork recovery metadata is incomplete; preserve the backup.");
                if (File.Exists(committed))
                {
                    if (File.ReadAllText(committed, Encoding.UTF8) != "committed" || !Directory.Exists(destination))
                        throw new InvalidDataException("Artwork recovery metadata is incomplete; preserve the backup.");
                    File.Delete(committed);
                }
                return;
            }
            var state = File.ReadAllText(journal, Encoding.UTF8);
            if (state != "old" && state != "new") throw new InvalidDataException("Artwork recovery metadata is invalid; preserve the backup.");
            EnsureNotLink(root);
            if (Directory.Exists(destination)) EnsureNotLink(destination);
            if (Directory.Exists(backup)) EnsureNotLink(backup);
            if (File.Exists(committed) && File.ReadAllText(committed, Encoding.UTF8) == "committed")
            {
                if (!Directory.Exists(destination))
                    throw new InvalidDataException("Artwork commit metadata is invalid; preserve the backup.");
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
                File.Delete(journal);
                File.Delete(committed);
                return;
            }
            if (state == "old" && Directory.Exists(backup))
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                Directory.Move(backup, destination);
            }
            else if (state == "new" && Directory.Exists(destination)) Directory.Delete(destination, true);
            else if (state == "old" && !Directory.Exists(destination))
                throw new InvalidDataException("Previous artwork is unavailable; preserve recovery files.");
            if (File.Exists(committed)) File.Delete(committed);
            File.Delete(journal);
        }

        private static void EnsureNotLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Artwork directories cannot be symbolic links or reparse points.");
        }

        private static void WriteMarker(string path, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
    }
}
