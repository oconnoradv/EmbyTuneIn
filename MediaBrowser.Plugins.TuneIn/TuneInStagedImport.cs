using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInStagedImport
    {
        internal const int MaxFolders = 1000;
        internal const int MaxItems = 50000;
        private const long MaxEstimatedBytes = 32 * 1024 * 1024;
        private readonly Dictionary<string, ChannelItemResult> folders = new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal);
        private readonly HashSet<string> served = new HashSet<string>(StringComparer.Ordinal);
        private readonly CancellationToken token;
        private readonly Action<int, int> report;
        private readonly Action validate;
        private readonly object gate = new object();
        private Exception failure;

        internal ChannelItemInfo Root { get; private set; }
        internal int Count => folders.Count;

        private TuneInStagedImport(CancellationToken token, Action<int, int> report, Action validate)
        {
            this.token = token;
            this.report = report;
            this.validate = validate;
        }

        internal static async Task<TuneInStagedImport> DiscoverAsync(TuneInChannel channel, ChannelItemInfo root,
            TuneInCatalogueSession session, CancellationToken token,
            Func<string, TuneInCatalogueSession, bool, CancellationToken, Task<ChannelItemResult>> getItems,
            Action<int, int> report, Action validate)
        {
            if (channel == null || root == null || session == null || getItems == null)
                throw new ArgumentNullException("A complete TuneIn discovery context is required.");
            var staged = new TuneInStagedImport(token, report, validate) { Root = root };
            var pending = new Queue<KeyValuePair<string, int>>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(new KeyValuePair<string, int>(root.Id, 0));
            long estimatedBytes = 0;
            var itemCount = 0;

            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                validate?.Invoke();
                var current = pending.Dequeue();
                if (!visited.Add(current.Key)) continue;
                if (staged.folders.Count >= MaxFolders)
                    throw new InvalidOperationException("TuneIn discovery exceeded its folder limit.");

                var result = await getItems(current.Key, session, true, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                validate?.Invoke();
                if (result?.Items == null) throw new InvalidOperationException("TuneIn returned an incomplete folder.");

                estimatedBytes += 128 + current.Key.Length * 2L;
                itemCount += result.Items.Count;
                foreach (var item in result.Items)
                {
                    if (item == null || string.IsNullOrEmpty(item.Id) || item.Id.Length > 6000)
                        throw new InvalidOperationException("TuneIn returned an invalid item during complete discovery.");
                    estimatedBytes += 256 + 2L * ((item.Id?.Length ?? 0) + (item.Name?.Length ?? 0)
                        + (item.ImageUrl?.Length ?? 0));
                    if (itemCount > MaxItems || estimatedBytes > MaxEstimatedBytes)
                        throw new InvalidOperationException("TuneIn discovery exceeded its bounded catalog limit.");
                    if (item.Type == ChannelItemType.Folder)
                    {
                        if (current.Value >= 8)
                            throw new InvalidOperationException("TuneIn discovery exceeded its folder depth limit.");
                        pending.Enqueue(new KeyValuePair<string, int>(item.Id, current.Value + 1));
                    }
                }
                staged.folders.Add(current.Key, new ChannelItemResult { Items = result.Items.ToList() });
            }
            return staged;
        }

        internal TuneInCategorySnapshot CreateSnapshot(string category)
        {
            var listings = folders.OrderBy(folder => folder.Key, StringComparer.Ordinal)
                .Select(folder => new TuneInFolderSnapshot
                {
                    Id = folder.Key,
                    Items = folder.Value.Items.Select(item => new TuneInItemSnapshot
                    {
                        Id = item.Id,
                        Name = item.Name ?? "",
                        Type = item.Type.ToString(),
                        ContentType = item.ContentType.ToString(),
                        MediaType = item.MediaType.ToString(),
                        ImageUrl = item.ImageUrl ?? ""
                    }).ToList()
                })
                .ToList();
            var snapshotBytes = listings.Sum(folder => 128L + (folder.Id?.Length ?? 0) * 2L
                + folder.Items.Sum(item => 256L + 2L * ((item.Id?.Length ?? 0) + (item.Name?.Length ?? 0)
                    + (item.Type?.Length ?? 0) + (item.ContentType?.Length ?? 0)
                    + (item.MediaType?.Length ?? 0) + (item.ImageUrl?.Length ?? 0))));
            if (snapshotBytes > 1024 * 1024)
                throw new InvalidOperationException("TuneIn category snapshot exceeds its durable state limit.");
            var snapshot = new TuneInCategorySnapshot
            {
                Category = category,
                RootId = Root.Id,
                Folders = listings
            };
            snapshot.Fingerprint = Fingerprint(snapshot.Folders);
            return snapshot;
        }

        internal static string Fingerprint(IEnumerable<TuneInFolderSnapshot> folders)
        {
            var canonical = string.Join("\n", (folders ?? Enumerable.Empty<TuneInFolderSnapshot>())
                .OrderBy(folder => folder.Id, StringComparer.Ordinal)
                .Select(folder => folder.Id + "\n" + string.Join("\n", folder.Items.Select(item =>
                    string.Join("\0", item.Id ?? "", item.Name ?? "", item.Type ?? "", item.ContentType ?? "",
                        item.MediaType ?? "", item.ImageUrl ?? "")))));
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "");
        }

        internal static TuneInStagedImport ForChannel(IEnumerable<ChannelItemInfo> roots,
            IEnumerable<TuneInCategorySnapshot> categories, CancellationToken token,
            Action<int, int> report, Action validate)
        {
            var staged = new TuneInStagedImport(token, report, validate);
            var rootItems = roots.ToList();
            staged.folders.Add(string.Empty, new ChannelItemResult { Items = rootItems });
            foreach (var category in categories)
            {
                if (!rootItems.Any(root => root.Id == category.RootId))
                    throw new InvalidOperationException("The saved TuneIn category does not match the channel root.");
                foreach (var folder in category.Folders)
                    staged.folders.Add(folder.Id, RestoreFolder(folder));
            }
            if (rootItems.Any(root => !staged.folders.ContainsKey(root.Id)) ||
                staged.folders.Values.SelectMany(folder => folder.Items).Any(item =>
                    item.Type == ChannelItemType.Folder && !staged.folders.ContainsKey(item.Id)))
                throw new InvalidOperationException("The staged TuneIn channel is incomplete.");
            return staged;
        }

        internal static ChannelItemResult RestoreFolder(TuneInFolderSnapshot folder) => new ChannelItemResult
        {
            Items = folder.Items.Select(item => new ChannelItemInfo
            {
                Id = item.Id,
                Name = item.Name,
                Type = (ChannelItemType)Enum.Parse(typeof(ChannelItemType), item.Type),
                ContentType = (ChannelMediaContentType)Enum.Parse(typeof(ChannelMediaContentType), item.ContentType),
                MediaType = (ChannelMediaType)Enum.Parse(typeof(ChannelMediaType), item.MediaType),
                ImageUrl = item.ImageUrl
            }).ToList()
        };

        internal ChannelItemResult Read(string folder, CancellationToken queryToken)
        {
            lock (gate)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    queryToken.ThrowIfCancellationRequested();
                    validate?.Invoke();
                    var key = folder ?? string.Empty;
                    if (!folders.TryGetValue(key, out var result))
                        throw new InvalidOperationException("Emby requested a folder outside the staged TuneIn channel.");
                    served.Add(key);
                    report?.Invoke(folders.Count, served.Count);
                    return new ChannelItemResult { Items = result.Items.ToList() };
                }
                catch (Exception error) { failure = error; throw; }
            }
        }

        internal void Verify()
        {
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                validate?.Invoke();
                if (failure is OperationCanceledException cancelled) throw cancelled;
                if (failure != null)
                    throw new InvalidOperationException("Emby suppressed a staged TuneIn import failure.", failure);
                if (served.Count != folders.Count)
                    throw new InvalidOperationException("Emby did not consume every staged TuneIn folder; import is incomplete.");
            }
        }
    }
}
