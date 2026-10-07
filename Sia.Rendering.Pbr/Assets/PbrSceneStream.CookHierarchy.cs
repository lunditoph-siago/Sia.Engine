using System.Text.Json;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream
{
    public static async Task<byte[]> CookAsync(PbrSceneAsset source,
        Func<AssetChunk, ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default)
        => await CookCoreAsync(source, write, default, cancellationToken).ConfigureAwait(false);

    /// <summary>Keeps selected source geometry assets on the conventional indexed path, sharing the stream's materials and depth.</summary>
    public static Task<byte[]> CookAsync(PbrSceneAsset source,
        Func<AssetChunk, ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        ReadOnlyMemory<int> conventionalGeometry, CancellationToken cancellationToken = default)
        => CookCoreAsync(source, write, conventionalGeometry, cancellationToken);

    private static async Task<byte[]> CookCoreAsync(PbrSceneAsset source,
        Func<AssetChunk, ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        ReadOnlyMemory<int> conventionalGeometry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(write);
        var conventional = new HashSet<int>();
        foreach (var geometry in conventionalGeometry.Span)
            if ((uint)geometry >= (uint)source.Geometry.Length || !conventional.Add(geometry))
                throw new ArgumentException("Conventional geometry IDs must be unique source asset indices.", nameof(conventionalGeometry));
        var opaqueSlots = Enumerable.Range(0, source.Instances.Length).Where(i =>
            !source.Instances.Span[i].Dynamic && !source.Materials.Span[source.Instances.Span[i].Material].AlphaBlend
            && !conventional.Contains(source.Instances.Span[i].Geometry)
            && source.Geometry.Span[source.Instances.Span[i].Geometry].Build.Tree.RootCount > 0).ToArray();
        var opaque = opaqueSlots.Select(i => source.Instances.Span[i]).ToArray();
        var geometryIds = opaque.Select(i => i.Geometry).Distinct().Order().ToArray();
        if (geometryIds.Length is 0 or > 4096) throw new ArgumentException("Stream requires opaque geometry.");
        var bootstrapSlots = Enumerable.Range(0, source.Instances.Length).Where(i =>
            source.Instances.Span[i].Dynamic || source.Materials.Span[source.Instances.Span[i].Material].AlphaBlend
            || conventional.Contains(source.Instances.Span[i].Geometry)).ToArray();
        if (opaqueSlots.Length + bootstrapSlots.Length != source.Instances.Length)
            throw new ArgumentException("Every source instance must belong to virtual or conventional geometry.", nameof(source));
        var bootstrapInstances = bootstrapSlots.Select(i => source.Instances.Span[i]).ToArray();
        var bootstrapGeometry = bootstrapInstances.Select(i => i.Geometry).Distinct().ToArray();
        var chunks = new Dictionary<string, AssetChunk>(StringComparer.Ordinal);
        var (Materials, Textures, Maps) = await CookTexturesAsync(source.Materials.ToArray(), WriteChunk, cancellationToken);
        var dynamicGeometry = bootstrapInstances.Where(i => i.Dynamic).Select(i => i.Geometry).ToHashSet();
        var bootstrap = PbrSceneAsset.Create(bootstrapGeometry.Select(i => conventional.Contains(i) || dynamicGeometry.Contains(i)
                ? source.Geometry.Span[i] : source.Geometry.Span[i].ExtractFinest()).ToArray(),
            Materials, bootstrapInstances.Select(i => i with { Geometry = Array.IndexOf(bootstrapGeometry, i.Geometry) }).ToArray(), source.Attribution).Encode(cancellationToken);
        if (bootstrap.Length > 64 * 1024 * 1024)
            throw new ArgumentException("Material/conventional bootstrap exceeds its budget.");
        var pageInfo = new Dictionary<string, PageInfo>(StringComparer.Ordinal);
        var materialParts = new List<string>();
        for (var start = 0; start < bootstrap.Length; start += 4 * 1024 * 1024) {
            var encoded = SceneStreamBlock.Encode(bootstrap.AsSpan(start, System.Math.Min(4 * 1024 * 1024, bootstrap.Length - start)));
            materialParts.Add(await WriteChunk(encoded));
        }
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var trees = new HierarchyInfo[geometryIds.Length];
        for (var g = 0; g < geometryIds.Length; g++) {
            cancellationToken.ThrowIfCancellationRequested();
            var vertexBytes = source.Geometry.Span[geometryIds[g]].HasLightmapUV ? 56 : 48;
            var trianglesPerPage = (StreamGeometryPage.MaximumBytes - 16) / (3 * vertexBytes + 12);
            var tree = source.Geometry.Span[geometryIds[g]].Build.Tree;
            var nodes = tree.Nodes;
            var result = new NodeInfo[nodes.Length];
            var parts = Enumerable.Range(0, nodes.Length).Select(_ => new List<PagePart>()).ToArray();
            var indices = new List<uint>();
            var ranges = new List<(int Node, int First, int Count)>();
            var depths = new int[nodes.Length];
            for (var n = 0; n < nodes.Length; n++)
                depths[n] = nodes.Span[n].Parent < 0 ? 0 : depths[nodes.Span[n].Parent] + 1;
            foreach (var level in Enumerable.Range(0, nodes.Length).GroupBy(n => depths[n]).OrderBy(g => g.Key)) {
                foreach (var n in level) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = nodes.Span[n];
                    for (var t = 0; t < node.TriangleCount;) {
                        var count = System.Math.Min(trianglesPerPage - (indices.Count / 3), node.TriangleCount - t);
                        ranges.Add((n, indices.Count / 3, count));
                        for (var j = 0; j < count * 3; j++)
                            indices.Add(tree.Indices[((node.TriangleOffset + t) * 3) + j]);
                        t += count;
                        if (indices.Count / 3 == trianglesPerPage) await Flush();
                    }
                }
                await Flush();
            }
            for (var n = 0; n < nodes.Length; n++) {
                var node = nodes.Span[n];
                var b = node.Bounds;
                result[n] = new(node.Parent, node.ChildOffset, node.ChildCount, node.EstimatedSpatialError,
                    [b.Min.x, b.Min.y, b.Min.z, b.Max.x, b.Max.y, b.Max.z], [.. parts[n]], node.TriangleCount);
            }
            async Task Flush()
            {
                if (indices.Count == 0) return;
                var page = StreamGeometryPage.Cook(tree.Vertices, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
                var id = await WriteChunk(SceneStreamBlock.Encode(page.Bytes.Span));
                pageInfo.TryAdd(id, new(id, page.Bytes.Length, page.VertexCount, page.TriangleCount, page.EncodedVertexBytes));
                foreach (var (Node, First, Count) in ranges) {
                    parts[Node].Add(new(id, First, Count));
                    if (Node < tree.RootCount) roots.Add(id);
                }
                indices.Clear();
                ranges.Clear();
            }
            trees[g] = new(tree.RootCount, result, QuadricErrorMetric);
        }
        if (roots.Sum(id => (long)pageInfo[id].Bytes) > 256L * 1024 * 1024)
            throw new ArgumentException("Root bootstrap exceeds its budget.");
        var instances = opaque.Select(i => {
            var t = i.Transform;
            return new Instance(Array.IndexOf(geometryIds, i.Geometry), i.Material,
                [t.c0.x, t.c0.y, t.c0.z, t.c0.w, t.c1.x, t.c1.y, t.c1.z, t.c1.w, t.c2.x, t.c2.y, t.c2.z, t.c2.w, t.c3.x, t.c3.y, t.c3.z, t.c3.w]);
        }).ToArray();
        var manifest = new AssetChunkManifest(chunks.Values, materialParts.Concat(roots));
        var staticSlots = new int[source.Instances.Length];
        var staticIndex = 0;
        for (var i = 0; i < staticSlots.Length; i++) staticSlots[i] = source.Instances.Span[i].Dynamic ? -1 : staticIndex++;
        var header = new Header(PbrSceneTransport.Identity(source), manifest.Encode(), [.. materialParts], bootstrap.Length,
            [.. pageInfo.Values], trees, instances, Textures, Maps, Version: 4,
            StaticIdentity: PbrSceneTransport.StaticIdentity(source), SourceInstanceCount: source.Instances.Length,
            OpaqueSourceInstances: opaqueSlots, BootstrapSourceInstances: bootstrapSlots,
            LightmapIdentity: PbrLightmapAsset.Identity(source), StaticSourceInstances: staticSlots);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(header, StreamJsonContext.Default.Header);
        return EncodeMetadata(metadata);

        async Task<string> WriteChunk(byte[] encoded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = AssetChunk.FromBytes(encoded);
            if (chunks.TryAdd(chunk.Id, chunk)) await write(chunk, encoded, cancellationToken);
            return chunk.Id;
        }
    }
}
