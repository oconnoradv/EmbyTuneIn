using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInIdentityDiagnostics
    {
        internal bool HasSavedBaseline { get; private set; }
        internal int SavedStations { get; private set; }
        internal int FreshStations { get; private set; }
        internal int RetainedStations { get; private set; }
        internal int AddedStations { get; private set; }
        internal int RemovedStations { get; private set; }
        internal int ChangedIdentities { get; private set; }
        internal int ChangedNames { get; private set; }
        internal int ChangedTypes { get; private set; }
        internal int ChangedParents { get; private set; }
        internal int AddedMemberships { get; private set; }
        internal int RemovedMemberships { get; private set; }

        private sealed class Station
        {
            internal readonly HashSet<string> Ids = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<(string ContentType, string MediaType)> Types =
                new HashSet<(string, string)>();
            internal readonly HashSet<string> Parents = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<(string Parent, string Id)> Memberships =
                new HashSet<(string, string)>();
        }

        internal static TuneInIdentityDiagnostics Compare(TuneInCategorySnapshot saved,
            TuneInCategorySnapshot fresh, Func<string, string> stationId, CancellationToken token)
        {
            var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            var before = Read(saved);
            var after = Read(fresh);
            var result = new TuneInIdentityDiagnostics
            {
                HasSavedBaseline = saved != null,
                SavedStations = before.Count,
                FreshStations = after.Count
            };
            foreach (var pair in after)
            {
                token.ThrowIfCancellationRequested();
                var current = pair.Value;
                if (!before.TryGetValue(pair.Key, out var old))
                {
                    result.AddedStations++;
                    result.AddedMemberships += current.Memberships.Count;
                    continue;
                }
                result.RetainedStations++;
                if (!old.Ids.SetEquals(current.Ids)) result.ChangedIdentities++;
                if (!old.Names.SetEquals(current.Names)) result.ChangedNames++;
                if (!old.Types.SetEquals(current.Types)) result.ChangedTypes++;
                if (!old.Parents.SetEquals(current.Parents)) result.ChangedParents++;
                result.AddedMemberships += current.Memberships.Count(item => !old.Memberships.Contains(item));
                result.RemovedMemberships += old.Memberships.Count(item => !current.Memberships.Contains(item));
            }
            foreach (var pair in before)
            {
                token.ThrowIfCancellationRequested();
                if (after.ContainsKey(pair.Key)) continue;
                result.RemovedStations++;
                result.RemovedMemberships += pair.Value.Memberships.Count;
            }
            return result;

            Dictionary<string, Station> Read(TuneInCategorySnapshot snapshot)
            {
                token.ThrowIfCancellationRequested();
                var stations = new Dictionary<string, Station>(StringComparer.Ordinal);
                if (snapshot == null) return stations;
                foreach (var folder in snapshot.Folders)
                {
                    foreach (var item in folder.Items)
                    {
                        token.ThrowIfCancellationRequested();
                        if (item.Type != "Media") continue;
                        if (!resolved.TryGetValue(item.Id, out var id))
                        {
                            id = stationId(item.Id);
                            resolved.Add(item.Id, id);
                        }
                        if (id == null)
                            throw new InvalidOperationException("TuneIn identity diagnostics encountered media without a station identity.");
                        if (!stations.TryGetValue(id, out var station))
                            stations.Add(id, station = new Station());
                        station.Ids.Add(item.Id);
                        station.Names.Add(item.Name ?? "");
                        station.Types.Add((item.ContentType, item.MediaType));
                        station.Parents.Add(folder.Id);
                        station.Memberships.Add((folder.Id, item.Id));
                    }
                }
                return stations;
            }
        }

        public override string ToString() =>
            $"savedBaseline={HasSavedBaseline}; unique stations saved={SavedStations}, fresh={FreshStations}, "
            + $"retained={RetainedStations}, added={AddedStations}, removed={RemovedStations}; "
            + $"retained stations with changed external identity sets={ChangedIdentities}, "
            + $"changed name sets={ChangedNames}, changed type sets={ChangedTypes}, changed parent sets={ChangedParents}; "
            + $"folder/external-ID memberships added={AddedMemberships}, removed={RemovedMemberships}";
    }
}
