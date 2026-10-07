using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapInput
{
    internal sealed record Prepared(PbrSceneAsset Scene, int Resolution, PbrLightmapReceiver[] Receivers,
        Dictionary<int, MeshLightmapLayout> Layouts, byte[] Identity, PbrLightmapChart[] Charts);

    internal sealed record Allocation(int Resolution, List<PbrLightmapReceiver> Receivers,
        Dictionary<int, MeshLightmapLayout> Layouts);

    internal static Prepared PrepareAdaptive(PbrSceneAsset scene, int minimumResolution, int maximumResolution,
        int padding, int maximumAtlasResolution, ulong maximumAssetBytes, ulong maximumWorkingBytes,
        CancellationToken cancellationToken, PbrLightmapEncoding encoding = PbrLightmapEncoding.L1Half,
        int? maximumStreamMetadataBytes = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        static bool PowerOfTwo(int value) => value >= 8 && value <= 8192 && (value & (value - 1)) == 0;
        if (!PowerOfTwo(minimumResolution) || !PowerOfTwo(maximumResolution) || maximumResolution < minimumResolution
            || !PowerOfTwo(maximumAtlasResolution))
            throw new ArgumentException("Adaptive lightmaps require power-of-two resolution bounds within 8..8192.");
        if (padding < 1 || padding > (minimumResolution - 2) / 2) throw new ArgumentOutOfRangeException(nameof(padding));
        cancellationToken.ThrowIfCancellationRequested();
        var receivers = new List<PbrLightmapReceiver>();
        var staticIndex = 0;
        for (var source = 0; source < scene.Instances.Length; source++) {
            if ((source & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var instance = scene.Instances.Span[source];
            if (instance.Dynamic) continue;
            if (!scene.Materials.Span[instance.Material].AlphaBlend)
                receivers.Add(new(source, staticIndex, 0, 0, minimumResolution, default));
            staticIndex++;
        }
        if (receivers.Count == 0) throw new InvalidOperationException("Surface baking requires at least one static opaque receiver.");
        if (encoding is not (PbrLightmapEncoding.L1Half or PbrLightmapEncoding.L1Unorm8))
            throw new ArgumentOutOfRangeException(nameof(encoding));
        var overhead = 256ul + (ulong)receivers.Count * (encoding == PbrLightmapEncoding.L1Half ? 36ul : 100ul);
        if (maximumAssetBytes <= overhead || maximumAssetBytes > int.MaxValue)
            throw new InvalidOperationException("Lightmap asset metadata exceeds its encoded-byte budget.");
        var maximumTexels = (maximumAssetBytes - overhead) / (encoding == PbrLightmapEncoding.L1Half ? 32ul : 16ul);
        if ((ulong)receivers.Count * (uint)minimumResolution * (uint)minimumResolution > maximumTexels)
            throw new InvalidOperationException("Minimum receiver tiles exceed the encoded lightmap budget.");
        var allocation = AllocateAdaptive(scene, receivers, minimumResolution, maximumResolution, padding,
            maximumAtlasResolution, maximumTexels, maximumWorkingBytes, cancellationToken,
            maximumStreamMetadataBytes.HasValue ? 132 : PbrLightmapTexel.Stride + sizeof(int) + 64);
        var charts = AtlasCharts(scene, receivers, allocation.Layouts);
        var actualBytes = 256ul + (ulong)receivers.Count * (encoding == PbrLightmapEncoding.L1Half ? 16ul : 80ul)
            + (ulong)charts.Length * 20 + (ulong)allocation.Resolution * (uint)allocation.Resolution * (encoding == PbrLightmapEncoding.L1Half ? 32ul : 16ul);
        if (charts.Length > 1_000_000 || actualBytes > maximumAssetBytes)
            throw new InvalidOperationException("Lightmap chart metadata exceeds the encoded-byte budget.");
        if (maximumStreamMetadataBytes.HasValue)
            PbrLightmapStream.ReserveMetadata(allocation.Resolution, receivers.ToArray(), charts, maximumStreamMetadataBytes.Value);
        // Receiver/asset/tile bounds are established before recooking or allocating the final atlas.
        var geometryAssets = scene.Geometry.ToArray();
        foreach (var (geometry, layout) in allocation.Layouts) {
            cancellationToken.ThrowIfCancellationRequested();
            geometryAssets[geometry] = MeshPatchAsset.Cook(layout.Mesh, geometryAssets[geometry].Settings, cancellationToken);
        }
        var mapped = PbrSceneAsset.Create(geometryAssets, scene.Materials.Span, scene.Instances.Span, scene.Attribution);
        return new(mapped, allocation.Resolution, receivers.ToArray(), allocation.Layouts,
            Identity(mapped, receivers, allocation.Resolution, minimumResolution, padding, allocation.Layouts), charts);
    }

    // Pure allocation phase is reusable by source-linked studies without recooking,
    // constructing a BVH or allocating a dense output coefficient atlas.
    internal static Allocation AllocateAdaptive(PbrSceneAsset scene, List<PbrLightmapReceiver> receivers,
        int minimumResolution, int maximumResolution, int padding, int maximumAtlasResolution,
        ulong maximumTexels, ulong maximumWorkingBytes, CancellationToken cancellationToken,
        int tileWorkingStride = PbrLightmapTexel.Stride + sizeof(int) + 64)
    {
        var layouts = new Dictionary<int, MeshLightmapLayout>();
        foreach (var receiver in receivers) {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = scene.Instances.Span[receiver.SourceInstance].Geometry;
            if (layouts.ContainsKey(geometry)) continue;
            var finest = scene.Geometry.Span[geometry].ExtractFinest().Build.Tree;
            var mesh = new MeshData(finest.Vertices.ToArray(), finest.Indices.ToArray(), finest.Bounds);
            for (var resolution = minimumResolution; ; resolution *= 2) {
                if ((ulong)resolution * (uint)resolution * (uint)tileWorkingStride > maximumWorkingBytes)
                    throw new InvalidOperationException("Receiver surface/coefficient buffers exceed the tile working-byte budget.");
                try {
                    var layout = MeshLightmap.Generate(mesh, resolution, padding, cancellationToken);
                    // Capacity alone does not prove that every thin chart has an actual sample.
                    var samples = new PbrLightmapTexel[resolution * resolution];
                    Array.Fill(samples, new(default, default, -1, -1, -1));
                    var sources = new int[samples.Length];
                    Array.Fill(sources, -1);
                    Rasterize(layout, receiver with { X = 0, Y = 0, Resolution = resolution }, float4x4.identity,
                        resolution, samples, sources, cancellationToken);
                    layouts.Add(geometry, layout);
                    break;
                }
                catch (InvalidOperationException) when (resolution < maximumResolution) { }
                catch (InvalidOperationException error) {
                    throw new InvalidOperationException($"Lightmap geometry {geometry} failed at maximum receiver resolution {resolution}: {error.Message}", error);
                }
            }
        }
        ulong area = 0;
        var largest = minimumResolution;
        for (var i = 0; i < receivers.Count; i++) {
            var resolution = layouts[scene.Instances.Span[receivers[i].SourceInstance].Geometry].Resolution;
            receivers[i] = receivers[i] with { Resolution = resolution };
            area += (ulong)resolution * (uint)resolution;
            largest = System.Math.Max(largest, resolution);
        }
        var dimension = largest;
        while ((ulong)dimension * (uint)dimension < area && dimension < maximumAtlasResolution) dimension *= 2;
        if (dimension > maximumAtlasResolution || (ulong)dimension * (uint)dimension < area
            || (ulong)dimension * (uint)dimension > maximumTexels)
            throw new InvalidOperationException("Adaptive lightmap atlas exceeds its dimension or encoded-byte budget.");
        PackAdaptive(receivers, dimension, cancellationToken);
        return new(dimension, receivers, layouts);
    }

    private static void PackAdaptive(List<PbrLightmapReceiver> receivers, int dimension, CancellationToken cancellationToken)
    {
        // Largest-first quadtree squares pack power-of-two tiles without retaining
        // an atlas occupancy bitmap. Keep receiver order/identity independent of packing order.
        var free = new Dictionary<int, Queue<(int X, int Y)>>();
        for (var size = 8; size <= dimension; size *= 2) free.Add(size, new());
        free[dimension].Enqueue((0, 0));
        foreach (var index in Enumerable.Range(0, receivers.Count).OrderByDescending(i => receivers[i].Resolution).ThenBy(i => i)) {
            cancellationToken.ThrowIfCancellationRequested();
            var receiver = receivers[index];
            var size = receiver.Resolution;
            while (size <= dimension && free[size].Count == 0) size *= 2;
            if (size > dimension) throw new InvalidOperationException("Adaptive receiver packing exhausted the atlas.");
            var (x, y) = free[size].Dequeue();
            while (size > receiver.Resolution) {
                size /= 2;
                free[size].Enqueue((x + size, y));
                free[size].Enqueue((x, y + size));
                free[size].Enqueue((x + size, y + size));
            }
            var scale = (float)receiver.Resolution / dimension;
            receivers[index] = receiver with { X = x, Y = y, ScaleBias = new(scale, scale, (float)x / dimension, (float)y / dimension) };
        }
    }

    internal static PbrLightmapInput ReceiverTile(Prepared prepared, PbrLightmapReceiver receiver,
        CancellationToken cancellationToken)
    {
        var local = receiver with { X = 0, Y = 0, ScaleBias = new(1, 1, 0, 0) };
        var texels = new PbrLightmapTexel[receiver.Resolution * receiver.Resolution];
        Array.Fill(texels, new(default, default, -1, -1, -1));
        var sources = new int[texels.Length];
        Array.Fill(sources, -1);
        var instance = prepared.Scene.Instances.Span[receiver.SourceInstance];
        var layout = prepared.Layouts[instance.Geometry];
        var covered = Rasterize(layout, local, instance.Transform, receiver.Resolution, texels, sources, cancellationToken);
        Dilate(layout, local, receiver.Resolution, texels, sources, cancellationToken);
        return new(prepared.Scene, receiver.Resolution, [local], texels, sources, prepared.Identity, covered);
    }
}
