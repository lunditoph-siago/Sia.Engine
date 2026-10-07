using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapStream
{
    /// <summary>Converts a charted resident bake into independently readable compact pages.</summary>
    public static Task<byte[]> CookAsync(PbrLightmapAsset source,
        Func<AssetChunk, ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        int maximumMetadataBytes = MaximumMetadataBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var charts = source.Charts.ToArray();
        var groups = charts.GroupBy(c => c.StaticInstance).ToDictionary(g => g.Key, g => g.ToArray());
        return CookTilesAsync(source.Resolution, source.Receivers.ToArray(), charts,
            source.SceneIdentity, source.SurfaceIdentity, source.Settings,
            i => ExtractTile(source, i, groups[source.Receivers.Span[i].StaticInstance], cancellationToken), write, maximumMetadataBytes, cancellationToken);
    }

    private static PbrLightmapAsset ExtractTile(PbrLightmapAsset source, int receiverIndex, PbrLightmapChart[] receiverCharts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var r = source.Receivers.Span[receiverIndex];
        var local = r with { X = 0, Y = 0, ScaleBias = new(1, 1, 0, 0) };
        var charts = receiverCharts.Select(c => c with { X = c.X - r.X, Y = c.Y - r.Y }).ToArray();
        var data = new byte[r.Resolution * r.Resolution * 16]; var scales = new float4[4];
        if (source.Encoding == PbrLightmapEncoding.L1Unorm8) {
            source.DecodeScales.Span.Slice(receiverIndex * 4, 4).CopyTo(scales);
            for (var y = 0; y < r.Resolution; y++) {
                token.ThrowIfCancellationRequested();
                source.QuantizedData.Span.Slice(((r.Y + y) * source.Resolution + r.X) * 16, r.Resolution * 16)
                    .CopyTo(data.AsSpan(y * r.Resolution * 16));
            }
        } else {
            float4 Read(int pixel, int band) {
                var at = ((r.Y + pixel / r.Resolution) * source.Resolution + r.X + pixel % r.Resolution) * 16 + band * 4;
                var values = source.Data.Span;
                return new((float)values[at], (float)values[at + 1], (float)values[at + 2], (float)values[at + 3]);
            }
            PbrLightmapAsset.QuantizeReceiver(local, r.Resolution, data, scales, Read, token);
        }
        return PbrLightmapAsset.FromUnorm8(r.Resolution, [local], source.SceneIdentity.Span, source.SurfaceIdentity.Span,
            source.Settings, data, scales, charts);
    }

    internal static async Task<byte[]> CookTilesAsync(int resolution, PbrLightmapReceiver[] receiverInput,
        PbrLightmapChart[] chartInput, ReadOnlyMemory<byte> scene, ReadOnlyMemory<byte> surface, PbrLightmapBakeSettings settings,
        Func<int, PbrLightmapAsset> tile, Func<AssetChunk, ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        int maximumMetadataBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(write); ArgumentNullException.ThrowIfNull(tile);
        token.ThrowIfCancellationRequested(); settings.Validate();
        if (resolution is < 8 or > 8192 || (resolution & (resolution - 1)) != 0
            || scene.Length != 32 || surface.Length != 32 || chartInput.Length == 0
            || maximumMetadataBytes is < 0 or > MaximumMetadataBytes)
            throw new ArgumentException("Invalid lightmap stream authoring metadata or byte budget.");
        var receivers = PbrLightmapAsset.ValidateReceivers(resolution, receiverInput);
        if (receivers.Any(r => r.Resolution > 1024 || (r.Resolution & (r.Resolution - 1)) != 0))
            throw new ArgumentException("Streamed receivers require powers of two within 8..1024.");
        var charts = PbrLightmapAsset.ValidateCharts(resolution, receivers, chartInput);
        var maximumMips = ReserveMetadata(resolution, receivers, charts, maximumMetadataBytes);
        var chartGroups = charts.Select((c, index) => (c, index)).GroupBy(c => c.c.StaticInstance).ToDictionary(g => g.Key, g => g.ToArray());
        var coarse = new byte[charts.Length * 16]; var scales = new float4[receivers.Length * 4];
        var chunks = new Dictionary<string, AssetChunk>(StringComparer.Ordinal); var pages = new List<PbrLightmapPage>();
        for (var i = 0; i < receivers.Length; i++) {
            token.ThrowIfCancellationRequested();
            // Only this tile's packed coefficients survive through its chunk writes.
            PbrLightmapAsset? data = tile(i); var receiver = receivers[i];
            if (data.Resolution != receiver.Resolution || data.Encoding != PbrLightmapEncoding.L1Unorm8
                || data.Receivers.Length != 1 || data.Receivers.Span[0].StaticInstance != receiver.StaticInstance
                || !data.SceneIdentity.Span.SequenceEqual(scene.Span) || !data.SurfaceIdentity.Span.SequenceEqual(surface.Span)
                || data.Settings != settings || data.MipCount <= maximumMips[i])
                throw new ArgumentException("Tile producer disagrees with the streamed bake manifest.");
            data.DecodeScales.Span.CopyTo(scales.AsSpan(i * 4, 4));
            foreach (var (chart, index) in chartGroups[receiver.StaticInstance])
                MeanChart(data, chart with { X = chart.X - receiver.X, Y = chart.Y - receiver.Y }, coarse.AsSpan(index * 16, 16), token);
            for (var mip = 0; mip <= maximumMips[i]; mip++) {
                var side = ((receiver.Resolution >> mip) + PageResolution - 1) / PageResolution;
                for (var y = 0; y < side; y++) for (var x = 0; x < side; x++) {
                    token.ThrowIfCancellationRequested();
                    var payload = EncodePage(data, mip, x, y);
                    // Empty pages have no fine contribution; chart means remain available.
                    var covered = false;
                    for (var at = 16 + 3; at < payload.Length; at += 16) if (payload[at] != 0) { covered = true; break; }
                    if (!covered) continue;
                    var encoded = SceneStreamBlock.Encode(payload);
                    var chunk = AssetChunk.FromBytes(encoded);
                    if (chunks.TryAdd(chunk.Id, chunk)) await write(chunk, encoded, token).ConfigureAwait(false);
                    pages.Add(new(i, mip, x, y, chunk));
                }
            }
            // Do not retain the previous packed tile while the next tile's surface arrays are allocated.
            data = null;
        }
        token.ThrowIfCancellationRequested();
        var manifest = new AssetChunkManifest(chunks.Values, []);
        var manifestBytes = manifest.Encode();
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write("SIALMST1"u8); writer.Write(resolution); writer.Write(receivers.Length); writer.Write(charts.Length);
        writer.Write(pages.Count); writer.Write(manifestBytes.Length);
        writer.Write(scene.Span); writer.Write(surface.Span); writer.Write(new byte[32]);
        PbrLightmapAsset.WriteSettings(writer, settings);
        for (var i = 0; i < receivers.Length; i++) {
            var r = receivers[i]; writer.Write(r.StaticInstance); writer.Write(r.X); writer.Write(r.Y); writer.Write(r.Resolution);
            foreach (var scale in scales.AsSpan(i * 4, 4)) { writer.Write(scale.x); writer.Write(scale.y); writer.Write(scale.z); writer.Write(scale.w); }
        }
        foreach (var chart in charts) { writer.Write(chart.StaticInstance); writer.Write(chart.X); writer.Write(chart.Y); writer.Write(chart.Width); writer.Write(chart.Height); }
        writer.Write(coarse);
        var indices = manifest.Chunks.Select((c, i) => (c.Id, i)).ToDictionary(c => c.Id, c => c.i, StringComparer.Ordinal);
        foreach (var p in pages) { writer.Write(p.Receiver); writer.Write(p.Mip); writer.Write(p.X); writer.Write(p.Y); writer.Write(indices[p.Chunk.Id]); }
        writer.Write(manifestBytes); writer.Flush();
        if (output.Length + 32 > maximumMetadataBytes) throw new InvalidOperationException("Encoded lightmap stream exceeds its metadata budget.");
        ManifestIdentity(output.GetBuffer().AsSpan(0, (int)output.Length)).CopyTo(output.GetBuffer(), BakeIdentityOffset);
        writer.Write(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length)));
        return output.ToArray();
    }

    // Reserve a manifest entry for every possible page before producing tiles or writing chunks.
    // Sparse coverage and content deduplication can only reduce this upper bound.
    internal static int[] ReserveMetadata(int resolution, PbrLightmapReceiver[] receivers,
        PbrLightmapChart[] charts, int maximumMetadataBytes)
    {
        var maximumMips = ReceiverMaximumMips(resolution, receivers, charts);
        long reservedPages = 0;
        for (var i = 0; i < receivers.Length; i++)
            for (var mip = 0; mip <= maximumMips[i]; mip++) {
                var side = ((receivers[i].Resolution >> mip) + PageResolution - 1) / PageResolution;
                reservedPages += (long)side * side;
            }
        var reservation = HeaderBytes + 32L + receivers.Length * 80L + charts.Length * 36L
            + 16L + reservedPages * 60L;
        if (reservedPages > AssetChunkManifest.MaximumChunks || reservation > maximumMetadataBytes)
            throw new InvalidOperationException("Lightmap page directory exceeds its chunk-count or metadata-byte budget.");
        return maximumMips;
    }

    private static void MeanChart(PbrLightmapAsset tile, PbrLightmapChart chart, Span<byte> target, CancellationToken token)
    {
        Span<long> sums = stackalloc long[16]; var valid = 0;
        for (var y = chart.Y; y < chart.Y + chart.Height; y++) {
            token.ThrowIfCancellationRequested();
            for (var x = chart.X; x < chart.X + chart.Width; x++) {
                var values = tile.QuantizedData.Span.Slice((y * tile.Resolution + x) * 16, 16);
                if (values[3] == 0) continue;
                for (var c = 0; c < 16; c++) sums[c] += values[c];
                valid++;
            }
        }
        if (valid == 0) throw new InvalidOperationException("A streamed chart has no covered illumination.");
        for (var c = 0; c < 16; c++) target[c] = (byte)System.Math.Round((double)sums[c] / valid);
    }

    private static byte[] EncodePage(PbrLightmapAsset tile, int mip, int x, int y)
    {
        var output = new byte[16 + PageBytes]; "SIALMPG1"u8.CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), PageResolution);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), PageBorder);
        var size = tile.Resolution >> mip;
        var left = x * PageResolution - PageBorder; var top = y * PageResolution - PageBorder;
        var firstX = System.Math.Max(0, left); var firstY = System.Math.Max(0, top);
        var width = System.Math.Min(size, left + PageSide) - firstX; var height = System.Math.Min(size, top + PageSide) - firstY;
        var scratch = new byte[width * height * 4];
        for (var band = 0; band < 4; band++) {
            tile.WriteMipRegion(scratch, mip, band, firstX, firstY, width, height);
            for (var row = 0; row < height; row++) for (var column = 0; column < width; column++)
                scratch.AsSpan((row * width + column) * 4, 4).CopyTo(output.AsSpan(16
                    + ((firstY - top + row) * PageSide + firstX - left + column) * 16 + band * 4, 4));
        }
        return output;
    }
}
