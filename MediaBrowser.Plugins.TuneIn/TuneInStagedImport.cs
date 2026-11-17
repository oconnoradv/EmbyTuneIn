using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInStagedImport
    {
        private readonly Dictionary<string, ChannelItemResult> folders = new Dictionary<string, ChannelItemResult>();
        private readonly HashSet<string> served = new HashSet<string>();
        private readonly CancellationToken token;
        private readonly Action<int, int> report;
        private readonly Action validate;
        private readonly object gate = new object();
        private Exception failure;
        internal ChannelItemInfo Root { get; private set; }
        internal int Count => folders.Count;

        private TuneInStagedImport(CancellationToken token, Action<int, int> report, Action validate)
        { this.token = token; this.report = report; this.validate = validate; }

        internal static async Task<TuneInStagedImport> DiscoverAsync(TuneInChannel channel, ChannelItemInfo root,
            CancellationToken token, Action<int, int> report, Action validate)
        {
            var staged = new TuneInStagedImport(token, report, validate) { Root = root };
            var pending = new Queue<(string Id, int Depth)>();
            pending.Enqueue((root.Id, 0));
            long bytes = 0;
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                validate();
                var current = pending.Dequeue();
                if (staged.folders.ContainsKey(current.Id)) continue;
                if (staged.folders.Count >= 1000)
                    throw new InvalidOperationException("TuneIn discovery exceeded 1000 folders.");
                var result = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = current.Id }, token)
                    .ConfigureAwait(false);
                bytes += 128 + current.Id.Length * 2L;
                foreach (var item in result.Items)
                {
                    bytes += 512 + 2L * ((item.Id?.Length ?? 0) + (item.Name?.Length ?? 0) + (item.ImageUrl?.Length ?? 0));
                    if (bytes > 32 * 1024 * 1024)
                        throw new InvalidOperationException("TuneIn discovery exceeded 32 MiB.");
                    if (item.Type == ChannelItemType.Folder)
                    {
                        if (current.Depth >= 8)
                            throw new InvalidOperationException("TuneIn discovery exceeded eight folder levels.");
                        pending.Enqueue((item.Id, current.Depth + 1));
                    }
                }
                staged.folders.Add(current.Id, result);
            }
            return staged;
        }

        internal ChannelItemResult Read(string folder, CancellationToken queryToken)
        {
            lock (gate)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    queryToken.ThrowIfCancellationRequested();
                    validate();
                    if (string.IsNullOrEmpty(folder))
                        return new ChannelItemResult { Items = new List<ChannelItemInfo> { Root } };
                    if (!folders.TryGetValue(folder, out var result))
                        throw new InvalidOperationException("Emby requested a folder outside the staged TuneIn category.");
                    served.Add(folder);
                    report(folders.Count, served.Count);
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
                validate();
                if (failure is OperationCanceledException cancelled) throw cancelled;
                if (failure != null)
                    throw new InvalidOperationException("Emby suppressed a staged TuneIn import failure.", failure);
                if (served.Count != folders.Count)
                    throw new InvalidOperationException("Emby did not consume every staged TuneIn folder; native import is incomplete.");
            }
        }
    }
}
