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
        // A category root can be generated locally without fetching a page.
        internal const int MaxFolders = TuneInCatalogueSession.MaxRefreshPages + 1;
        internal const int MaxItems = 1000000;
        private const int MaxFolderDepth = 8;
        internal const long MaxEstimatedBytes = 1024L * 1024 * 1024;
        private readonly Dictionary<string, ChannelItemResult> folders = new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal);
        private readonly HashSet<string> incompleteFolders = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> incompleteStationListings = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> served = new HashSet<string>(StringComparer.Ordinal);
        private readonly CancellationToken token;
        private readonly Action<int, int> report;
        private readonly Action validate;
        private readonly ChannelItemResult scopedChannelRoot;
        private readonly object gate = new object();
        private Exception failure;

        internal ChannelItemInfo Root { get; private set; }
        internal int Count => folders.Count;
        internal int ImportRefreshLevel { get; private set; }
        internal bool DiscoveryIncomplete => incompleteFolders.Count != 0;
        internal int CountStations(Func<string, string> stationId) => folders.Values
            .SelectMany(folder => folder.Items).Where(item => item.Type == ChannelItemType.Media)
            .Select(item => stationId(item.Id)).Where(id => id != null).Distinct(StringComparer.Ordinal).Count();

        private TuneInStagedImport(CancellationToken token, Action<int, int> report, Action validate,
            ChannelItemResult scopedChannelRoot = null)
        {
            this.token = token;
            this.report = report;
            this.validate = validate;
            this.scopedChannelRoot = scopedChannelRoot;
        }

        internal static async Task<TuneInStagedImport> DiscoverAsync(TuneInChannel channel, ChannelItemInfo root,
            TuneInCatalogueSession session, CancellationToken token,
            Func<string, TuneInCatalogueSession, bool, CancellationToken, Task<ChannelItemResult>> getItems,
            Action<int, int> report, Action validate, Func<string, string, bool> isForwardPage, Action<int> checkpoint = null)
        {
            if (channel == null || root == null || session == null || getItems == null)
                throw new ArgumentNullException("A complete TuneIn discovery context is required.");
            var staged = new TuneInStagedImport(token, report, validate) { Root = root };
            var pending = new Queue<KeyValuePair<string, int>>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(new KeyValuePair<string, int>(root.Id, 0));
            long estimatedBytes = 0;
            var itemCount = 0;
            var stationCount = 0;

            using (var batch = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                async Task<(ChannelItemResult Result, TuneInCatalogueSession.FolderRead Skips)> Read(string id)
                {
                    try
                    {
                        return await session.ReadFolderAsync(() => getItems(id, session, true, batch.Token)).ConfigureAwait(false);
                    }
                    catch
                    {
                        batch.Cancel();
                        throw;
                    }
                }

                while (pending.Count != 0)
                {
                    token.ThrowIfCancellationRequested();
                    validate?.Invoke();
                    var entries = new List<KeyValuePair<string, int>>(TuneInRequestPacer.MaxParallelCatalogue);
                    while (pending.Count != 0 && entries.Count < Math.Min(TuneInRequestPacer.MaxParallelCatalogue, MaxFolders - staged.folders.Count))
                    {
                        var entry = pending.Dequeue();
                        if (visited.Add(entry.Key)) entries.Add(entry);
                    }
                    if (entries.Count == 0)
                    {
                        if (pending.Any(entry => !visited.Contains(entry.Key)))
                            throw new InvalidOperationException($"TuneIn discovery exceeded its {MaxFolders}-folder limit.");
                        break;
                    }

                    void Accept(KeyValuePair<string, int> entry, ChannelItemResult result, TuneInCatalogueSession.FolderRead skips)
                    {
                        token.ThrowIfCancellationRequested();
                        validate?.Invoke();
                        if (result?.Items == null) throw new InvalidOperationException("TuneIn returned an incomplete folder.");
                        if (skips.SkippedLink) staged.incompleteFolders.Add(entry.Key);
                        if (skips.SkippedStation) staged.incompleteStationListings.Add(entry.Key);
                        CheckListingBudget(entry.Key, result, ref estimatedBytes, ref itemCount);
                        stationCount += result.Items.Count(item => item.Type == ChannelItemType.Media);
                        foreach (var item in result.Items)
                            if (item.Type == ChannelItemType.Folder)
                                pending.Enqueue(new KeyValuePair<string, int>(item.Id,
                                    ChildDepth(entry.Value, entry.Key, item.Id, isForwardPage)));
                        staged.folders.Add(entry.Key, new ChannelItemResult { Items = result.Items.ToList() });
                        checkpoint?.Invoke(stationCount);
                    }

                    var results = await Task.WhenAll(entries.Select(entry => Read(entry.Key))).ConfigureAwait(false);
                    for (var i = 0; i < entries.Count; i++)
                        Accept(entries[i], results[i].Result, results[i].Skips);
                }
            }
            return staged;
        }

        private static int ChildDepth(int parentDepth, string parent, string child, Func<string, string, bool> isForwardPage)
        {
            if (isForwardPage(parent, child)) return parentDepth;
            if (parentDepth >= MaxFolderDepth)
                throw new InvalidOperationException($"TuneIn discovery exceeded its folder depth limit ({MaxFolderDepth} levels, excluding forward pages).");
            return parentDepth + 1;
        }

        private static void CheckListingBudget(string id, ChannelItemResult result, ref long estimatedBytes, ref int itemCount)
        {
            estimatedBytes += 128 + id.Length * 2L;
            itemCount += result.Items.Count;
            foreach (var item in result.Items)
            {
                if (item == null || string.IsNullOrEmpty(item.Id) || item.Id.Length > 6000)
                    throw new InvalidOperationException("TuneIn returned an invalid item during discovery.");
                estimatedBytes += 256 + 2L * (item.Id.Length + (item.Name?.Length ?? 0) + (item.ImageUrl?.Length ?? 0));
            }
            if (itemCount > MaxItems || estimatedBytes > MaxEstimatedBytes)
                throw new InvalidOperationException("TuneIn discovery exceeded its bounded catalog limit.");
        }

        internal TuneInCategorySnapshot CreateSnapshot(string category)
        {
            if (DiscoveryIncomplete)
                throw new InvalidOperationException("Cannot create a snapshot from incomplete TuneIn discovery.");
            return CreateSnapshot(category, folders, false);
        }

        internal TuneInCategorySnapshot MergePartialCategory(string category, TuneInCategorySnapshot previous,
            Func<string, string> stationId, Func<string, string, bool> isForwardPage, bool allowExclusions)
        {
            if (!DiscoveryIncomplete)
                return null;
            if (previous == null)
                return allowExclusions ? CreateSnapshot(category, folders, true) : null;
            if (previous.Category != category || previous.RootId != Root.Id)
                return null;
            var saved = previous.Folders.ToDictionary(folder => folder.Id, StringComparer.Ordinal);
            if (!allowExclusions && incompleteFolders.Any(id => !saved.ContainsKey(id))) return null;
            var merged = new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal);
            var pending = new Queue<(string Id, int Depth)>();
            pending.Enqueue((Root.Id, 0));
            long estimatedBytes = 0;
            var itemCount = 0;
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                validate?.Invoke();
                var (id, depth) = pending.Dequeue();
                if (merged.ContainsKey(id)) continue;
                if (merged.Count >= MaxFolders)
                    throw new InvalidOperationException($"TuneIn discovery exceeded its {MaxFolders}-folder limit.");
                if (!folders.TryGetValue(id, out var listing))
                {
                    if (!saved.TryGetValue(id, out var old)) return null;
                    listing = RestoreFolder(old);
                }
                else if (incompleteFolders.Contains(id) && saved.TryGetValue(id, out var savedFolder))
                {
                    var freshFolders = new HashSet<string>(listing.Items.Where(item => item.Type == ChannelItemType.Folder)
                        .Select(item => item.Id), StringComparer.Ordinal);
                    var freshStations = incompleteStationListings.Contains(id)
                        ? new HashSet<string>(listing.Items.Where(item => item.Type == ChannelItemType.Media)
                            .Select(item => stationId(item.Id)), StringComparer.Ordinal)
                        : null;
                    listing = new ChannelItemResult
                    {
                        Items = listing.Items.Concat(RestoreFolder(savedFolder).Items.Where(item =>
                            item.Type == ChannelItemType.Folder
                                ? freshFolders.Add(item.Id)
                                : item.Type == ChannelItemType.Media && freshStations != null
                                    && freshStations.Add(stationId(item.Id)))).ToList()
                    };
                }
                CheckListingBudget(id, listing, ref estimatedBytes, ref itemCount);
                merged.Add(id, listing);
                foreach (var item in listing.Items.Where(item => item.Type == ChannelItemType.Folder))
                {
                    pending.Enqueue((item.Id, ChildDepth(depth, id, item.Id, isForwardPage)));
                }
            }
            return CreateSnapshot(previous.Category, merged, true);
        }

        private TuneInCategorySnapshot CreateSnapshot(string category,
            Dictionary<string, ChannelItemResult> source, bool discoveryPartial)
        {
            var listings = source.OrderBy(folder => folder.Key, StringComparer.Ordinal)
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
            if (snapshotBytes > MaxEstimatedBytes)
                throw new InvalidOperationException("TuneIn category snapshot exceeds its durable state limit.");
            var snapshot = new TuneInCategorySnapshot
            {
                Category = category,
                RootId = Root.Id,
                DiscoveryPartial = discoveryPartial,
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
            Action<int, int> report, Action validate, string restrictTopLevelFolderId = null)
        {
            var rootItems = roots.ToList();
            var channelRoot = new ChannelItemResult { Items = rootItems };
            var staged = new TuneInStagedImport(token, report, validate,
                restrictTopLevelFolderId == null ? null : channelRoot);
            if (restrictTopLevelFolderId == null)
                staged.folders.Add(string.Empty, channelRoot);
            else
                categories = new[] { categories.Single(category => category.RootId == restrictTopLevelFolderId) };
            foreach (var category in categories)
            {
                token.ThrowIfCancellationRequested();
                if (!rootItems.Any(root => root.Id == category.RootId))
                    throw new InvalidOperationException("The saved TuneIn category does not match the channel root.");
                foreach (var folder in category.Folders)
                {
                    token.ThrowIfCancellationRequested();
                    staged.folders.Add(folder.Id, RestoreFolder(folder));
                }
            }
            staged.ImportRefreshLevel = staged.MeasureImportRefreshLevel(restrictTopLevelFolderId);
            return staged;
        }

        private int MeasureImportRefreshLevel(string restrictTopLevelFolderId)
        {
            var incoming = folders.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
            foreach (var listing in folders.Values)
            {
                token.ThrowIfCancellationRequested();
                foreach (var item in listing.Items.Where(item => item.Type == ChannelItemType.Folder))
                {
                    if (!incoming.TryGetValue(item.Id, out var references))
                        throw new InvalidOperationException("The staged TuneIn channel is incomplete.");
                    incoming[item.Id] = references + 1;
                }
            }

            // Emby counts real folder levels, including each forward pagination folder.
            var start = restrictTopLevelFolderId ?? string.Empty;
            if (!incoming.TryGetValue(start, out var referencesToStart) || referencesToStart != 0)
                throw new InvalidOperationException("The staged TuneIn channel contains cyclic folders.");

            // Keep the channel-relative depth even when Emby starts reading at the category.
            var startLevel = restrictTopLevelFolderId == null ? 1 : 2;
            var levels = new Dictionary<string, int>(StringComparer.Ordinal) { [start] = startLevel };
            var pending = new Queue<string>();
            pending.Enqueue(start);
            var visited = 0;
            var deepest = startLevel;
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var id = pending.Dequeue();
                visited++;
                var next = levels[id] + 1;
                foreach (var item in folders[id].Items.Where(item => item.Type == ChannelItemType.Folder))
                {
                    levels.TryGetValue(item.Id, out var level);
                    levels[item.Id] = Math.Max(level, next);
                    deepest = Math.Max(deepest, next);
                    if (--incoming[item.Id] == 0) pending.Enqueue(item.Id);
                }
            }
            if (visited != folders.Count)
                throw new InvalidOperationException("The staged TuneIn channel contains cyclic or unreachable folders.");
            return deepest;
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
                    if (key.Length == 0 && scopedChannelRoot != null)
                        return new ChannelItemResult { Items = scopedChannelRoot.Items.ToList() };
                    if (!folders.TryGetValue(key, out var result))
                        throw new InvalidOperationException("Emby requested a folder outside the staged TuneIn channel.");
                    if (served.Add(key))
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
                    throw new InvalidOperationException($"Emby did not consume every staged TuneIn folder; import is incomplete. "
                        + $"Folders served: {served.Count}/{folders.Count}; requested refresh level: {ImportRefreshLevel}.");
            }
        }
    }
}
