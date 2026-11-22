using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInStagedImport
    {
        // A category root can be generated locally without fetching a page.
        internal const int MaxFolders = TuneInCatalogueSession.MaxRefreshPages + 1;
        internal const int MaxItems = 1000000;
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
        private Action<TuneInImportProgress> reportProgress;
        private readonly Dictionary<string, (int Stations, int Subfolders)> listingCounts =
            new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        private Dictionary<string, LinkedListNode<TuneInImportFolderDetails>> pendingDetails;
        private LinkedList<TuneInImportFolderDetails> pendingOrder;
        private int stationEntriesTotal, stationEntriesServed, subfolderEntriesTotal, subfolderEntriesServed;
        private int? lastFolderStationEntries, lastFolderSubfolderEntries;
        private Func<string, string, bool> isForwardPage;

        internal ChannelItemInfo Root { get; private set; }
        internal int Count => folders.Count;
        internal int ImportRefreshLevel { get; private set; }
        internal bool DiscoveryIncomplete => incompleteFolders.Count != 0;
        internal string DiscoveryLimitReason { get; private set; }
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
            Action<int, int> report, Action validate, Func<string, string, bool> isForwardPage, Action<int> checkpoint = null,
            int? maxStationCount = null, int traversalDepthLimit = Configuration.PluginConfiguration.DefaultTraversalDepthLimit,
            Func<string, string> stationId = null)
        {
            if (channel == null || root == null || session == null || getItems == null)
                throw new ArgumentNullException("A complete TuneIn discovery context is required.");
            if (maxStationCount.HasValue && (maxStationCount < 1 || stationId == null) || traversalDepthLimit < 0)
                throw new ArgumentException("Valid discovery limits and station identity lookup are required.");
            var staged = new TuneInStagedImport(token, report, validate) { Root = root, isForwardPage = isForwardPage };
            var pending = new Queue<KeyValuePair<string, int>>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(new KeyValuePair<string, int>(root.Id, 0));
            long estimatedBytes = 0;
            var itemCount = 0;
            var stationCount = 0;
            var distinctStations = new HashSet<string>(StringComparer.Ordinal);
            var stationLimited = false;
            var depthLimited = false;

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
                    if (maxStationCount.HasValue && distinctStations.Count == maxStationCount.Value
                        && pending.Any(entry => !visited.Contains(entry.Key)))
                    {
                        stationLimited = true;
                        break;
                    }
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
                        var accepted = new List<ChannelItemInfo>();
                        foreach (var item in result.Items)
                        {
                            if (item.Type == ChannelItemType.Media && maxStationCount.HasValue)
                            {
                                var identity = stationId(item.Id);
                                if (identity == null) throw new InvalidDataException("TuneIn returned a station without an identity.");
                                if (!distinctStations.Contains(identity) && distinctStations.Count == maxStationCount.Value)
                                {
                                    stationLimited = true;
                                    continue;
                                }
                                distinctStations.Add(identity);
                            }
                            if (item.Type == ChannelItemType.Folder)
                            {
                                if (entry.Value == traversalDepthLimit)
                                {
                                    depthLimited = true;
                                    continue;
                                }
                                pending.Enqueue(new KeyValuePair<string, int>(item.Id, entry.Value + 1));
                            }
                            accepted.Add(item);
                        }
                        stationCount += accepted.Count(item => item.Type == ChannelItemType.Media);
                        staged.folders.Add(entry.Key, new ChannelItemResult { Items = accepted });
                        checkpoint?.Invoke(stationCount);
                    }

                    var results = await Task.WhenAll(entries.Select(entry => Read(entry.Key))).ConfigureAwait(false);
                    for (var i = 0; i < entries.Count; i++)
                        Accept(entries[i], results[i].Result, results[i].Skips);
                }
            }
            foreach (var listing in staged.folders.Values)
                listing.Items.RemoveAll(item => item.Type == ChannelItemType.Folder && !staged.folders.ContainsKey(item.Id));
            depthLimited |= staged.TrimDiscoveryDepth(traversalDepthLimit);
            staged.DiscoveryLimitReason = string.Join("; ", new[]
            {
                stationLimited ? $"Max Station Count {maxStationCount} reached (per top-level category)" : null,
                depthLimited ? $"TuneIn traversal depth limit {traversalDepthLimit} reached (including pagination)" : null
            }.Where(reason => reason != null));
            if (staged.DiscoveryLimitReason.Length == 0) staged.DiscoveryLimitReason = null;
            return staged;
        }

        private bool TrimDiscoveryDepth(int maximum)
        {
            var incoming = folders.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
            foreach (var item in folders.Values.SelectMany(listing => listing.Items).Where(item => item.Type == ChannelItemType.Folder))
                incoming[item.Id]++;
            if (incoming[Root.Id] != 0) throw new InvalidOperationException("TuneIn discovery contains cyclic folders.");
            var levels = new Dictionary<string, int>(StringComparer.Ordinal) { [Root.Id] = 0 };
            var pending = new Queue<string>();
            pending.Enqueue(Root.Id);
            var visited = 0;
            var limited = false;
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var id = pending.Dequeue();
                visited++;
                foreach (var item in folders[id].Items.Where(item => item.Type == ChannelItemType.Folder).ToArray())
                {
                    if (levels.TryGetValue(id, out var depth) && depth < maximum)
                    {
                        levels.TryGetValue(item.Id, out var previous);
                        levels[item.Id] = Math.Max(previous, depth + 1);
                    }
                    else
                    {
                        folders[id].Items.Remove(item);
                        limited = true;
                    }
                    if (--incoming[item.Id] == 0) pending.Enqueue(item.Id);
                }
            }
            if (visited != folders.Count) throw new InvalidOperationException("TuneIn discovery contains cyclic or unreachable folders.");
            foreach (var id in folders.Keys.Where(id => !levels.ContainsKey(id)).ToArray()) folders.Remove(id);
            return limited;
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
                    pending.Enqueue((item.Id, depth + 1));
                }
            }
            return CreateSnapshot(previous.Category, merged, true);
        }

        internal TuneInCategorySnapshot MergeRetainedCategory(string category, TuneInCategorySnapshot previous,
            Func<string, string> membershipKey, bool allowExclusions)
        {
            if (DiscoveryIncomplete && !allowExclusions) return null;
            if (previous == null) return CreateSnapshot(category, folders, DiscoveryIncomplete);
            if (previous.Category != category || previous.RootId != Root.Id)
                throw new InvalidOperationException("The retained category does not match TuneIn discovery.");
            var merged = previous.Folders.ToDictionary(folder => folder.Id, RestoreFolder, StringComparer.Ordinal);
            var known = new Dictionary<string, List<(string Folder, ChannelItemInfo Item)>>(StringComparer.Ordinal);
            foreach (var folder in merged)
                foreach (var item in folder.Value.Items)
                {
                    var key = membershipKey(item.Id);
                    if (!known.TryGetValue(key, out var placements)) known[key] = placements = new List<(string, ChannelItemInfo)>();
                    placements.Add((folder.Key, item));
                }
            var retainedFolders = new HashSet<string>(merged.Keys, StringComparer.Ordinal);
            var folderIds = previous.Folders.GroupBy(folder => membershipKey(folder.Id), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
            foreach (var folder in folders.Keys)
                if (!folderIds.ContainsKey(membershipKey(folder))) folderIds[membershipKey(folder)] = folder;
            foreach (var folder in folders)
            {
                token.ThrowIfCancellationRequested();
                var id = folderIds[membershipKey(folder.Key)];
                if (!merged.TryGetValue(id, out var listing))
                    merged[id] = listing = new ChannelItemResult { Items = new List<ChannelItemInfo>() };
                foreach (var item in folder.Value.Items)
                {
                    var key = membershipKey(item.Id);
                    if (known.TryGetValue(key, out var placements))
                    {
                        foreach (var placement in placements)
                        {
                            placement.Item.ForceUpdate |= placement.Item.Name != item.Name
                                || placement.Item.ContentType != item.ContentType || placement.Item.MediaType != item.MediaType
                                || !string.IsNullOrEmpty(item.ImageUrl) && placement.Item.ImageUrl != item.ImageUrl;
                            placement.Item.Name = item.Name;
                            placement.Item.ContentType = item.ContentType;
                            placement.Item.MediaType = item.MediaType;
                            if (!string.IsNullOrEmpty(item.ImageUrl)) placement.Item.ImageUrl = item.ImageUrl;
                        }
                        continue;
                    }
                    var copy = CopyItem(item);
                    if (item.Type == ChannelItemType.Folder) copy.Id = folderIds[key];
                    listing.Items.Add(copy);
                    known[key] = new List<(string, ChannelItemInfo)> { (id, copy) };
                }
            }
            // Discard new pagination branches that contributed no stations or retained folders.
            bool removed;
            do
            {
                var empty = new HashSet<string>(merged.Where(folder => folder.Key != Root.Id
                    && !retainedFolders.Contains(folder.Key) && folder.Value.Items.Count == 0)
                    .Select(folder => folder.Key), StringComparer.Ordinal);
                removed = empty.Count != 0;
                foreach (var id in empty) merged.Remove(id);
                foreach (var listing in merged.Values)
                    listing.Items.RemoveAll(item => item.Type == ChannelItemType.Folder && empty.Contains(item.Id));
            } while (removed);
            long bytes = 0;
            var count = 0;
            if (merged.Count > MaxFolders) throw new InvalidOperationException("Retained TuneIn category exceeds its folder limit.");
            foreach (var folder in merged) CheckListingBudget(folder.Key, folder.Value, ref bytes, ref count);
            return CreateSnapshot(category, merged, DiscoveryIncomplete);
        }

        private static ChannelItemInfo CopyItem(ChannelItemInfo item) => new ChannelItemInfo
        {
            Id = item.Id, Name = item.Name, Type = item.Type, ContentType = item.ContentType,
            MediaType = item.MediaType, ImageUrl = item.ImageUrl, ForceUpdate = item.ForceUpdate
        };

        // Emby shows TuneIn's More Stations/Shows pages as folders; list their entries in the paged folder instead.
        private Dictionary<string, ChannelItemResult> FoldForwardPages(Dictionary<string, ChannelItemResult> source)
        {
            if (isForwardPage == null || Root == null || !source.ContainsKey(Root.Id)) return source;
            var result = new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal);
            var folded = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            pending.Enqueue(Root.Id);
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var id = pending.Dequeue();
                if (result.ContainsKey(id)) continue;
                var items = new List<ChannelItemInfo>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var pages = new HashSet<string>(StringComparer.Ordinal) { id };
                var queue = new Queue<string>();
                queue.Enqueue(id);
                while (queue.Count != 0)
                {
                    var page = queue.Dequeue();
                    foreach (var item in source[page].Items)
                    {
                        if (item.Type == ChannelItemType.Folder && source.ContainsKey(item.Id)
                            && !pages.Contains(item.Id) && isForwardPage(page, item.Id))
                        {
                            pages.Add(item.Id);
                            folded.Add(item.Id);
                            queue.Enqueue(item.Id);
                        }
                        else if (seen.Add(item.Id))
                        {
                            items.Add(item);
                            if (item.Type == ChannelItemType.Folder && source.ContainsKey(item.Id)) pending.Enqueue(item.Id);
                        }
                    }
                }
                result.Add(id, new ChannelItemResult { Items = items });
            }
            foreach (var folder in source)
                if (!result.ContainsKey(folder.Key) && !folded.Contains(folder.Key)) result.Add(folder.Key, folder.Value);
            return result;
        }

        private TuneInCategorySnapshot CreateSnapshot(string category,
            Dictionary<string, ChannelItemResult> source, bool discoveryPartial)
        {
            source = FoldForwardPages(source);
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
                        ImageUrl = item.ImageUrl ?? "",
                        ForceUpdate = item.ForceUpdate
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
                DiscoveryLimitReason = DiscoveryLimitReason,
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
            Action<int, int> report, Action validate, string restrictTopLevelFolderId = null,
            Action<TuneInImportProgress> reportProgress = null, bool verboseDiagnostics = false)
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
            if (reportProgress != null)
                staged.PrepareProgress(rootItems, restrictTopLevelFolderId, reportProgress, verboseDiagnostics);
            return staged;
        }

        private void PrepareProgress(List<ChannelItemInfo> roots, string restriction,
            Action<TuneInImportProgress> callback, bool verbose)
        {
            reportProgress = callback;
            foreach (var folder in folders)
            {
                token.ThrowIfCancellationRequested();
                var stations = folder.Value.Items.Count(item => item.Type == ChannelItemType.Media);
                var subfolders = folder.Value.Items.Count(item => item.Type == ChannelItemType.Folder);
                listingCounts.Add(folder.Key, (stations, subfolders));
                stationEntriesTotal += stations;
                subfolderEntriesTotal += subfolders;
            }
            if (verbose)
            {
                pendingDetails = new Dictionary<string, LinkedListNode<TuneInImportFolderDetails>>(StringComparer.Ordinal);
                pendingOrder = new LinkedList<TuneInImportFolderDetails>();
                var pending = new Queue<(string Id, string Name, string Path)>();
                var startName = restriction == null ? "TuneIn" : SafeLabel(roots.Single(root => root.Id == restriction).Name);
                pending.Enqueue((restriction ?? string.Empty, startName, startName));
                while (pending.Count != 0)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = pending.Dequeue();
                    if (pendingDetails.ContainsKey(entry.Id)) continue;
                    var counts = listingCounts[entry.Id];
                    using (var hash = SHA256.Create())
                        pendingDetails.Add(entry.Id, pendingOrder.AddLast(new TuneInImportFolderDetails
                        {
                            Reference = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(entry.Id)))
                                .Replace("-", "").Substring(0, 16),
                            Name = entry.Name, Path = entry.Path,
                            StationEntries = counts.Stations, SubfolderEntries = counts.Subfolders
                        }));
                    foreach (var child in folders[entry.Id].Items.Where(item => item.Type == ChannelItemType.Folder))
                    {
                        var name = SafeLabel(child.Name);
                        var path = entry.Path + " > " + name;
                        if (path.Length > 512) path = "... " + path.Substring(path.Length - 508);
                        pending.Enqueue((child.Id, name, path));
                    }
                }
            }
            ReportProgress();
        }

        private static string SafeLabel(string value)
        {
            value = value ?? "(unnamed folder)";
            value = Regex.Replace(value, @"[\x00-\x1f\x7f]", " ");
            value = Regex.Replace(value, @"(?i)\b(?:https?|file)://\S+", "[URL omitted]");
            value = Regex.Replace(value, @"[A-Za-z][A-Za-z0-9_-]*\s*=\s*[^\s;]*", "[assignment omitted]");
            value = Regex.Replace(value, @"[A-Za-z0-9_-]{80,}(?:\.[A-Za-z0-9_-]{20,})?", "[identifier omitted]");
            return value.Length > 128 ? value.Substring(0, 125) + "..." : value;
        }

        private void ReportProgress() => reportProgress?.Invoke(new TuneInImportProgress
        {
            StationEntriesTotal = stationEntriesTotal, StationEntriesServed = stationEntriesServed,
            SubfolderEntriesTotal = subfolderEntriesTotal, SubfolderEntriesServed = subfolderEntriesServed,
            LastFolderStationEntries = lastFolderStationEntries, LastFolderSubfolderEntries = lastFolderSubfolderEntries,
            PendingFolders = pendingOrder?.Take(TuneInImportStallDiagnostics.MaxFolderDetails).ToArray(),
            PendingFoldersOmitted = Math.Max(0, (pendingDetails?.Count ?? 0) - TuneInImportStallDiagnostics.MaxFolderDetails)
        });

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
                ImageUrl = item.ImageUrl,
                ForceUpdate = item.ForceUpdate
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
                    {
                        report?.Invoke(folders.Count, served.Count);
                        if (listingCounts.TryGetValue(key, out var counts))
                        {
                            stationEntriesServed += counts.Stations;
                            subfolderEntriesServed += counts.Subfolders;
                            lastFolderStationEntries = counts.Stations;
                            lastFolderSubfolderEntries = counts.Subfolders;
                            if (pendingDetails != null && pendingDetails.TryGetValue(key, out var node))
                            {
                                pendingOrder.Remove(node);
                                pendingDetails.Remove(key);
                            }
                            ReportProgress();
                        }
                    }
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
