using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrGpuScene
{
    internal static void ValidateLightmapSource(PbrSceneAsset scene, PbrSceneStream? stream,
        ReadOnlySpan<byte> identity, ReadOnlySpan<PbrLightmapReceiver> receivers)
    {
        if (stream is not null && stream.LightmapIdentity.IsEmpty)
            throw new ArgumentException("Surface lightmaps require a version 3 stream cooked from the complete derived scene.");
        if (!identity.SequenceEqual(stream is null ? PbrLightmapAsset.Identity(scene) : stream.LightmapIdentity.Span))
            throw new ArgumentException("Lightmaps belong to different static geometry, materials or derived coordinates.");
        var receiverIds = receivers.ToArray().Select(r => r.StaticInstance).ToHashSet();
        if (stream is not null) {
            if (stream.PageTable.Values.Any(p => p.VertexBytes != 56))
                throw new ArgumentException("Every virtual lightmap receiver requires derived bake coordinates.");
            foreach (var slot in stream.OpaqueSourceInstances.Span)
                if (!receiverIds.Remove(stream.StaticSourceInstances.Span[slot])) throw new ArgumentException("Lightmaps must map every virtual opaque receiver.");
        }
        var staticIndex = 0;
        for (var i = 0; i < scene.Instances.Length; i++) {
            var instance = scene.Instances.Span[i];
            if (instance.Dynamic) continue;
            if (!scene.Materials.Span[instance.Material].AlphaBlend) {
                var slot = stream is null ? staticIndex : stream.StaticSourceInstances.Span[stream.BootstrapSourceInstances.Span[i]];
                if (!scene.Geometry.Span[instance.Geometry].HasLightmapUV || !receiverIds.Remove(slot))
                    throw new ArgumentException("Lightmaps must map every static opaque receiver with derived bake coordinates.");
            }
            staticIndex++;
        }
        if (receiverIds.Count != 0) throw new ArgumentException("Lightmap receiver metadata references a missing static opaque instance.");
    }

    internal static float4[] CreateLightmapMetadata(PbrLightmapAsset? asset, PbrLightmapStream? stream)
    {
        if (stream is not null) return PbrLightmapGpu.CreateMetadata(stream);
        if (asset is null) return [];
        if (asset.Charts.IsEmpty) return asset.Encoding == PbrLightmapEncoding.L1Unorm8 ? asset.DecodeScales.ToArray() : [];
        var metadata = new float4[1 + asset.Receivers.Length * 4 + asset.Charts.Length];
        metadata[0] = new(asset.Receivers.Length, asset.Charts.Length, asset.MipCount, 0);
        if (asset.Encoding == PbrLightmapEncoding.L1Unorm8) asset.DecodeScales.Span.CopyTo(metadata.AsSpan(1));
        var receivers = asset.Receivers.ToArray().Select((r, index) => (r.StaticInstance, index)).ToDictionary(r => r.StaticInstance, r => r.index);
        for (var c = 0; c < asset.Charts.Length; c++) {
            var chart = asset.Charts.Span[c];
            metadata[1 + asset.Receivers.Length * 4 + c] = new(
                BitConverter.UInt32BitsToSingle((uint)chart.X | (uint)chart.Y << 16),
                BitConverter.UInt32BitsToSingle((uint)chart.Width | (uint)chart.Height << 16),
                asset.ChartMaximumMip(chart), receivers[chart.StaticInstance]);
        }
        return metadata;
    }

    internal static (float4[] Metadata, PbrStreamResidency.VertexPacker Pack) CreateStreamLightmapMetadata(
        PbrSceneStream source, PbrLightmapAsset? asset, PbrLightmapStream? stream, bool sharedPages, int lightmapOwnerBits = 0,
        bool localInstances = false)
    {
        var common = CreateLightmapMetadata(asset, stream);
        var receivers = (asset?.Receivers ?? stream!.Receivers).ToArray();
        var charts = (asset?.Charts ?? stream!.Charts).ToArray();
        var receiverIds = receivers.Select((r, i) => (r.StaticInstance, i)).ToDictionary(r => r.StaticInstance, r => r.i);
        var grouped = charts.Select((chart, i) => (chart, i)).GroupBy(c => c.chart.StaticInstance)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.chart.Y).ThenBy(c => c.chart.X)
                .ThenBy(c => c.chart.Width).ThenBy(c => c.chart.Height).ToArray());
        var instanceReceivers = new PbrLightmapReceiver[source.Instances.Length];
        var instanceCharts = new PbrLightmapChart[source.Instances.Length][];
        var instanceChartIds = new int[source.Instances.Length][];
        var canonical = new Dictionary<int, int>();
        var lookupCount = 0;
        for (var i = 0; i < source.Instances.Length; i++) {
            var receiver = receivers[receiverIds[source.StaticSourceInstances.Span[source.OpaqueSourceInstances.Span[i]]]];
            instanceReceivers[i] = receiver;
            var group = grouped.GetValueOrDefault(receiver.StaticInstance) ?? [];
            instanceCharts[i] = group.Select(c => c.chart).ToArray();
            instanceChartIds[i] = group.Select(c => c.i).ToArray();
            lookupCount = checked(lookupCount + group.Length);
            var geometry = source.Instances.Span[i].AssetIndex;
            if (!sharedPages || canonical.TryAdd(geometry, i)) continue;
            var first = canonical[geometry];
            var previous = instanceReceivers[first];
            if (receiver.Resolution != previous.Resolution || group.Length != instanceCharts[first].Length)
                throw new ArgumentException("Shared virtual geometry requires identical local lightmap chart layouts.");
            for (var c = 0; c < group.Length; c++) {
                var a = group[c].chart; var b = instanceCharts[first][c];
                if (a.X - receiver.X != b.X - previous.X || a.Y - receiver.Y != b.Y - previous.Y
                    || a.Width != b.Width || a.Height != b.Height)
                    throw new ArgumentException("Shared virtual geometry requires identical local lightmap chart layouts.");
            }
        }
        // Prefix addresses are relative to the prefix itself, in vec4 records.
        var instanceBase = 1 + common.Length;
        var lookupBase = checked(instanceBase + source.Instances.Length * 2);
        var metadata = new float4[checked(lookupBase + (lookupCount + 3) / 4)];
        metadata[0] = new(BitConverter.UInt32BitsToSingle((uint)instanceBase), 0, 0, 0);
        common.CopyTo(metadata, 1);
        var lookup = MemoryMarshal.Cast<float4, uint>(metadata.AsSpan(lookupBase));
        var next = 0;
        for (var i = 0; i < source.Instances.Length; i++) {
            var receiver = instanceReceivers[i];
            metadata[instanceBase + i * 2] = receiver.ScaleBias;
            metadata[instanceBase + i * 2 + 1] = new(
                BitConverter.UInt32BitsToSingle((uint)next), BitConverter.UInt32BitsToSingle((uint)instanceChartIds[i].Length),
                BitConverter.UInt32BitsToSingle((uint)(receiverIds[receiver.StaticInstance] + 1)),
                BitConverter.UInt32BitsToSingle((uint)lookupBase));
            foreach (var id in instanceChartIds[i]) lookup[next++] = (uint)id + 1;
        }
        void Pack(Span<float4> packed, StreamGeometryPage page, int instance)
        {
            var receiver = instanceReceivers[instance];
            PackStreamLightmapVertices(packed, page, receiver, instanceCharts[instance], instanceChartIds[instance],
                receiverIds[receiver.StaticInstance], (localInstances || source.Bootstrap.HasDynamicInstances) && !sharedPages ? instance : source.Instances.Span[instance].MaterialIndex,
                sharedPages, lightmapOwnerBits);
        }
        return (metadata, Pack);
    }

    internal static void PackStreamLightmapVertices(Span<float4> packed, StreamGeometryPage page,
        PbrLightmapReceiver receiver, ReadOnlySpan<PbrLightmapChart> charts, ReadOnlySpan<int> chartIds,
        int receiverIndex, int material, bool local, int lightmapOwnerBits = 0)
    {
        if (!page.HasLightmapUV || packed.Length != page.VertexCount * 3 || charts.Length != chartIds.Length)
            throw new ArgumentException("A lightmapped page requires complete derived coordinates and chart mappings.");
        var words = MemoryMarshal.Cast<float4, uint>(packed);
        for (var v = 0; v < page.VertexCount; v++) {
            var p = packed[v]; var n = packed[page.VertexCount + v]; var t = packed[page.VertexCount * 2 + v];
            var uv = page.GetLightmapUV(v);
            if (!math.all(math.isfinite(uv)) || math.any(uv < 0) || math.any(uv > 1))
                throw new ArgumentException("Lightmap vertex is outside its receiver coordinates.");
            var marker = receiverIndex + 1;
            if (!charts.IsEmpty) {
                var x = System.Math.Min((int)MathF.Floor(uv.x * receiver.Resolution), receiver.Resolution - 1) + receiver.X;
                var y = System.Math.Min((int)MathF.Floor(uv.y * receiver.Resolution), receiver.Resolution - 1) + receiver.Y;
                var found = -1;
                for (var c = 0; c < charts.Length; c++) {
                    var chart = charts[c];
                    if (x >= chart.X && x < chart.X + chart.Width && y >= chart.Y && y < chart.Y + chart.Height) { found = c; break; }
                }
                if (found < 0) throw new ArgumentException("Lightmap vertex is outside its receiver's padded charts.");
                marker = (local ? found : chartIds[found]) + 1;
            }
            if (!local) uv = uv * receiver.ScaleBias.xy + receiver.ScaleBias.zw;
            var a = v * 4; var b = page.VertexCount * 4 + a; var cAt = page.VertexCount * 8 + v * (lightmapOwnerBits > 0 ? 1 : 2);
            words[a] = BitConverter.SingleToUInt32Bits(p.x); words[a + 1] = BitConverter.SingleToUInt32Bits(p.y);
            words[a + 2] = BitConverter.SingleToUInt32Bits(p.z); words[a + 3] = PbrResidentGeometry.PackDirection(new(p.w, n.x, n.y));
            words[b] = BitConverter.SingleToUInt32Bits(n.z); words[b + 1] = BitConverter.SingleToUInt32Bits(n.w);
            words[b + 2] = PbrResidentGeometry.PackDirection(t.xyz); words[b + 3] = (uint)material | (t.w < 0 ? 0x80000000u : 0);
            words[cAt] = (uint)MathF.Round(uv.x * 65535) | (uint)MathF.Round(uv.y * 65535) << 16;
            if (lightmapOwnerBits > 0)
                words[b + 3] = PbrResidentGeometry.PackLightmapOwner((uint)material, (uint)marker, t.w < 0, lightmapOwnerBits);
            else words[cAt + 1] = (uint)marker;
        }
        // Every vertex in a page has the same owner; ignore handedness when the marker shares its word.
        var markerBase = lightmapOwnerBits > 0 ? page.VertexCount * 4 + 3 : page.VertexCount * 8 + 1;
        var markerStride = lightmapOwnerBits > 0 ? 4 : 2;
        var markerMask = lightmapOwnerBits > 0 ? 0x7fffffffu : uint.MaxValue;
        for (var t = 0; t < page.Indices.Length; t += 3) {
            var a = words[markerBase + (int)page.Indices[t] * markerStride] & markerMask;
            if (a != (words[markerBase + (int)page.Indices[t + 1] * markerStride] & markerMask)
                || a != (words[markerBase + (int)page.Indices[t + 2] * markerStride] & markerMask))
                throw new ArgumentException("A lightmap triangle crosses padded chart allocations.");
        }
    }
}
