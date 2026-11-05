using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Engine.Mesh;

namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    internal sealed partial class PageSource
    {
        private readonly record struct BootstrapInput(bool Raster, int Ordinal, AssetId Id, AssetChunk Leaf, AssetChunkSlice Slice);
        private sealed record BootstrapGroup(AssetChunk Pack, List<BootstrapInput> Inputs);
        private int _bootstrapDecodeGroups, _bootstrapDecodeLeaves;
        private long _bootstrapDecodeTicks, _bootstrapHashTicks;
        internal int BootstrapDecodeGroups => Volatile.Read(ref _bootstrapDecodeGroups);
        internal int BootstrapDecodeLeaves => Volatile.Read(ref _bootstrapDecodeLeaves);
        internal double BootstrapDecodeMilliseconds => (double)Interlocked.Read(ref _bootstrapDecodeTicks) / Stopwatch.Frequency * 1000;
        internal double BootstrapLeafHashMilliseconds => (double)Interlocked.Read(ref _bootstrapHashTicks) / Stopwatch.Frequency * 1000;

        // Callers admit the complete required encoded/decoded set before entering
        // this method. Runtime detail requests retain the original single-page API.
        internal async Task<(MeshVirtualPageRecord[] Virtual, MeshRasterPageRecord[] Raster)> ReadRequiredPagesAsync(
            IReadOnlyList<MeshVirtualPageDescriptor> virtualPages, IReadOnlyList<MeshRasterPageDescriptor> rasterPages,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(virtualPages); ArgumentNullException.ThrowIfNull(rasterPages);
            cancellationToken.ThrowIfCancellationRequested();
            if (_packs is null || !TryGroupBootstrap(virtualPages, rasterPages, cancellationToken, out var groups)) {
                var virtualRecords = await ReadRequiredAsync(virtualPages, (page, token) => ReadAsync(page.Reference, token), cancellationToken);
                var rasterRecords = await ReadRequiredAsync(rasterPages, (page, token) => ReadRasterAsync(page.Reference, token), cancellationToken);
                return (virtualRecords, rasterRecords);
            }

            async Task<IAssetRecord[]> ReadGroup(BootstrapGroup group, CancellationToken token)
            {
                // Only one lease belongs to a group. Decode consumes it before
                // completion, so ordered A/B/A inputs cannot pin A while waiting B.
                using var pack = await _cache.AcquireAsync(group.Pack, cancellationToken: token);
                IAssetRecord[] DecodeGroup(CancellationToken cpuToken)
                {
                    Interlocked.Increment(ref _bootstrapDecodeGroups);
                    var records = new IAssetRecord[group.Inputs.Count];
                    for (var index = 0; index < records.Length; index++) {
                        cpuToken.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                        var input = group.Inputs[index];
                        var bytes = pack.Memory.Slice(input.Slice.Offset, input.Leaf.Length);
                        var hashStarted = Stopwatch.GetTimestamp();
                        try {
                            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.Span), Convert.FromHexString(input.Leaf.Id)))
                                throw new InvalidDataException("Packed leaf checksum mismatch.");
                        }
                        finally { Interlocked.Add(ref _bootstrapHashTicks, Stopwatch.GetTimestamp() - hashStarted); }
                        if (!MemoryMarshal.TryGetArray(bytes, out var segment)) throw new InvalidOperationException("Expected owned encoded array.");
                        using var stream = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
                        var decodeStarted = Stopwatch.GetTimestamp();
                        try {
                            if (input.Raster) {
                                var use = _rasterPages[input.Id];
                                var data = MeshRasterPageData.Load(stream, MeshRasterPageData.DefaultMaximumBytes, cpuToken);
                                use.Index.ValidatePage(use.Page, data, cpuToken);
                                records[index] = new MeshRasterPageRecord { Data = data };
                            }
                            else {
                                var use = _pages[input.Id];
                                var data = MeshVirtualPageData.Load(stream, MeshVirtualPageData.MaximumEncodedBytes, cpuToken);
                                use.Index.ValidatePage(use.Page, data, cpuToken);
                                records[index] = new MeshVirtualPageRecord { Data = data };
                            }
                            Interlocked.Increment(ref _bootstrapDecodeLeaves);
                        }
                        finally { Interlocked.Add(ref _bootstrapDecodeTicks, Stopwatch.GetTimestamp() - decodeStarted); }
                    }
                    cpuToken.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                    return records;
                }
#if BROWSER
                // Keep the imports owner context across physical IO; only the
                // bounded group's hash/typed codecs run on the CPU worker.
                return await Program.BrowserOwner.RunCpuAsync(DecodeGroup, token);
#else
                return DecodeGroup(token);
#endif
            }

            var decoded = await ReadRequiredAsync(groups, ReadGroup, cancellationToken);
            var virtualResults = new MeshVirtualPageRecord[virtualPages.Count];
            var rasterResults = new MeshRasterPageRecord[rasterPages.Count];
            for (var group = 0; group < groups.Count; group++)
                for (var index = 0; index < groups[group].Inputs.Count; index++) {
                    var input = groups[group].Inputs[index];
                    if (input.Raster) rasterResults[input.Ordinal] = (MeshRasterPageRecord)decoded[group][index];
                    else virtualResults[input.Ordinal] = (MeshVirtualPageRecord)decoded[group][index];
                }
            return (virtualResults, rasterResults);
        }

        private bool TryGroupBootstrap(IReadOnlyList<MeshVirtualPageDescriptor> virtualPages,
            IReadOnlyList<MeshRasterPageDescriptor> rasterPages, CancellationToken token, out List<BootstrapGroup> groups)
        {
            groups = [];
            var byPack = new Dictionary<string, BootstrapGroup>(StringComparer.Ordinal);
            bool Add(bool raster, int ordinal, AssetId id, string hash, int length)
            {
                var leaf = new AssetChunk(hash, length);
                if (_packs!.Resolve(leaf) is not { } slice) return false;
                if (!byPack.TryGetValue(slice.Pack.Id, out var group)) {
                    group = new(slice.Pack, []);
                    byPack.Add(slice.Pack.Id, group);
                }
                group.Inputs.Add(new(raster, ordinal, id, leaf, slice));
                return true;
            }
            for (var ordinal = 0; ordinal < virtualPages.Count; ordinal++) {
                token.ThrowIfCancellationRequested();
                var page = virtualPages[ordinal];
                if (!_pages.TryGetValue(page.Id, out var use) || use.Index.Pages[use.Page] != page)
                    throw new AssetNotFoundException("Unknown typed virtual page revision.");
                if (!Add(false, ordinal, page.Id, page.ContentHash, page.EncodedBytes)) return false;
            }
            for (var ordinal = 0; ordinal < rasterPages.Count; ordinal++) {
                token.ThrowIfCancellationRequested();
                var page = rasterPages[ordinal];
                if (!_rasterPages.TryGetValue(page.Id, out var use) || use.Index.Pages[use.Page] != page)
                    throw new AssetNotFoundException("Unknown typed Raster page revision.");
                if (!Add(true, ordinal, page.Id, page.ContentHash, page.EncodedBytes)) return false;
            }
            // Dictionary insertion order follows the first required occurrence.
            groups.AddRange(byPack.Values);
            return true;
        }
    }
}
