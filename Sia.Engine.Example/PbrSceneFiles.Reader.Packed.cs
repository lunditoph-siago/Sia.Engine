using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sia.Asset;

namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    private sealed partial class Reader
    {
        private readonly record struct PackedInput(AssetId Id, string Location, AssetPackEntry Entry);
        private sealed record PackedGroup(AssetChunk Pack, List<PackedInput> Inputs);

        private bool TryGroupPacked(IReadOnlyList<AssetId> ids, Func<AssetId, string> location,
            out List<PackedGroup> groups)
        {
            groups = [];
            var byPack = new Dictionary<string, PackedGroup>(StringComparer.Ordinal);
            long total = EncodedBytes;
            foreach (var id in ids) {
                _token.ThrowIfCancellationRequested();
                var path = location(id);
                var file = Path.GetFileName(AssetInput.TryGetHttpUri(path, out var uri) ? uri!.AbsolutePath : path);
                if (!_packs!.Entries.TryGetValue(file, out var entry)) return false;
                if (entry.Chunk.Length > _maximumFileBytes || entry.Chunk.Length > _maximumTotalBytes - total)
                    throw new InvalidDataException("Asset payload exceeds its file or aggregate encoded size limit.");
                total += entry.Chunk.Length;
                if (!byPack.TryGetValue(entry.Slice.Pack.Id, out var group)) {
                    group = new(entry.Slice.Pack, []);
                    byPack.Add(group.Pack.Id, group);
                    groups.Add(group);
                }
                group.Inputs.Add(new(id, path, entry));
            }
            return true;
        }

        private async Task<Dictionary<AssetId, T>> ReadPackedBatch<T>(IReadOnlyList<AssetId> ids,
            IReadOnlyList<PackedGroup> groups, Func<AssetId, Stream, ulong, string?, T> decode) where T : class, IAssetRecord
        {
            async Task<T[]> ReadGroup(PackedGroup group, CancellationToken token)
            {
                var readStarted = Stopwatch.GetTimestamp();
                using var pack = await _packCache!.AcquireAsync(group.Pack, cancellationToken: token);
                Interlocked.Add(ref _readWaitTicks, Stopwatch.GetTimestamp() - readStarted);
                T[] DecodeGroup(CancellationToken cpuToken)
                {
                    var values = new T[group.Inputs.Count];
                    for (var i = 0; i < values.Length; i++) {
                        cpuToken.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                        var input = group.Inputs[i];
                        var bytes = pack.Memory.Slice(input.Entry.Slice.Offset, input.Entry.Chunk.Length);
                        var hashStarted = Stopwatch.GetTimestamp();
                        try {
                            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.Span), Convert.FromHexString(input.Entry.Chunk.Id)))
                                throw new InvalidDataException("Packed leaf checksum mismatch.");
                        }
                        finally { Interlocked.Add(ref _leafHashTicks, Stopwatch.GetTimestamp() - hashStarted); }
                        if (!MemoryMarshal.TryGetArray(bytes, out var segment)) throw new InvalidOperationException("Expected owned encoded array.");
                        using var stream = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
                        var decodeStarted = Stopwatch.GetTimestamp();
                        try { values[i] = decode(input.Id, stream, (ulong)_maximumFileBytes, input.Location); }
                        finally { Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - decodeStarted); }
                    }
                    cpuToken.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                    return values;
                }
#if BROWSER
                return await Program.BrowserOwner.RunCpuAsync(DecodeGroup, token);
#else
                return DecodeGroup(token);
#endif
            }

            // The pending results own decoded records only. Each physical lease is
            // released by ReadGroup before a result completes, so later groups
            // cannot pin an earlier pack while ordered consumption awaits another.
            var decoded = await ReadRequiredAsync(groups, ReadGroup, _token);
            var ordered = new Dictionary<AssetId, T>(ids.Count);
            foreach (var id in ids) ordered.Add(id, null!);
            for (var group = 0; group < groups.Count; group++)
                for (var i = 0; i < groups[group].Inputs.Count; i++)
                    ordered[groups[group].Inputs[i].Id] = decoded[group][i];
            foreach (var group in groups)
                foreach (var input in group.Inputs) EncodedBytes += input.Entry.Chunk.Length;
            return ordered;
        }
    }
}
