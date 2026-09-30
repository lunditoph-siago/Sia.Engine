using System.Text.Json;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream
{
    public static async Task<byte[]> CookAsync(PbrSceneAsset source,
        Func<AssetChunk, ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(write);
        var opaque = source.Instances.ToArray().Where(i => !source.Materials.Span[i.Material].AlphaBlend
            && source.Geometry.Span[i.Geometry].Build.Tree.RootCount > 0).ToArray();
        var geometryIds = opaque.Select(i => i.Geometry).Distinct().Order().ToArray();
        if (geometryIds.Length is 0 or > 4096) throw new ArgumentException("Stream requires opaque geometry.");
        var transparent = source.Instances.ToArray().Where(i => source.Materials.Span[i.Material].AlphaBlend).ToArray();
        var transparentIds = transparent.Select(i => i.Geometry).Distinct().ToArray();
        var chunks = new Dictionary<string, AssetChunk>(StringComparer.Ordinal);
        var (Materials, Textures, Maps) = await CookTexturesAsync(source.Materials.ToArray(), WriteChunk, cancellationToken);
        var bootstrap = PbrSceneAsset.Create(transparentIds.Select(i => source.Geometry.Span[i].ExtractFinest()).ToArray(),
            Materials, transparent.Select(i => i with { Geometry = Array.IndexOf(transparentIds, i.Geometry) }).ToArray(), source.Attribution).Encode(cancellationToken);
        if (bootstrap.Length > 64 * 1024 * 1024)
            throw new ArgumentException("Material/transparent bootstrap exceeds its budget.");
        var pageInfo = new Dictionary<string, PageInfo>(StringComparer.Ordinal);
        var materialParts = new List<string>();
        for (var start = 0; start < bootstrap.Length; start += 4 * 1024 * 1024) {
            var encoded = SceneStreamBlock.Encode(bootstrap.AsSpan(start, System.Math.Min(4 * 1024 * 1024, bootstrap.Length - start)));
            materialParts.Add(await WriteChunk(encoded));
        }
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var trees = new HierarchyInfo[geometryIds.Length];
        // Worst-case independent triangle uses three 48-byte vertices and 12 index bytes.
        const int trianglesPerPage = (StreamGeometryPage.MaximumBytes - 16) / 156;
        for (var g = 0; g < geometryIds.Length; g++) {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = source.Geometry.Span[geometryIds[g]].Build.Tree;
            var geometry = tree.CopyGeometry().Geometry;
            var nodes = tree.Nodes.ToArray();
            var result = new NodeInfo[nodes.Length];
            var parts = Enumerable.Range(0, nodes.Length).Select(_ => new List<PagePart>()).ToArray();
            var indices = new List<uint>();
            var ranges = new List<(int Node, int First, int Count)>();
            var depths = new int[nodes.Length];
            for (var n = 0; n < nodes.Length; n++)
                depths[n] = nodes[n].Parent < 0 ? 0 : depths[nodes[n].Parent] + 1;
            foreach (var level in Enumerable.Range(0, nodes.Length).GroupBy(n => depths[n]).OrderBy(g => g.Key)) {
                foreach (var n in level) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = nodes[n];
                    for (var t = 0; t < node.TriangleCount;) {
                        var count = System.Math.Min(trianglesPerPage - (indices.Count / 3), node.TriangleCount - t);
                        ranges.Add((n, indices.Count / 3, count));
                        for (var j = 0; j < count * 3; j++)
                            indices.Add(geometry.Indices[((node.TriangleOffset + t) * 3) + j]);
                        t += count;
                        if (indices.Count / 3 == trianglesPerPage) await Flush();
                    }
                }
                await Flush();
            }
            for (var n = 0; n < nodes.Length; n++) {
                var node = nodes[n];
                var b = node.Bounds;
                result[n] = new(node.Parent, node.ChildOffset, node.ChildCount, node.EstimatedSpatialError,
                    [b.Min.x, b.Min.y, b.Min.z, b.Max.x, b.Max.y, b.Max.z], [.. parts[n]], node.TriangleCount);
            }
            async Task Flush()
            {
                if (indices.Count == 0) return;
                var page = StreamGeometryPage.Cook(geometry.Vertices, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
                var id = await WriteChunk(SceneStreamBlock.Encode(page.Bytes.Span));
                pageInfo.TryAdd(id, new(id, page.Bytes.Length, page.VertexCount, page.TriangleCount));
                foreach (var (Node, First, Count) in ranges) {
                    parts[Node].Add(new(id, First, Count));
                    if (Node < tree.RootCount) roots.Add(id);
                }
                indices.Clear();
                ranges.Clear();
            }
            trees[g] = new(tree.RootCount, result);
        }
        if (roots.Sum(id => (long)pageInfo[id].Bytes) > 256L * 1024 * 1024)
            throw new ArgumentException("Root bootstrap exceeds its budget.");
        var instances = opaque.Select(i => {
            var t = i.Transform;
            return new Instance(Array.IndexOf(geometryIds, i.Geometry), i.Material,
                [t.c0.x, t.c0.y, t.c0.z, t.c0.w, t.c1.x, t.c1.y, t.c1.z, t.c1.w, t.c2.x, t.c2.y, t.c2.z, t.c2.w, t.c3.x, t.c3.y, t.c3.z, t.c3.w]);
        }).ToArray();
        var manifest = new AssetChunkManifest(chunks.Values, materialParts.Concat(roots));
        var header = new Header(PbrSceneTransport.Identity(source), manifest.Encode(), [.. materialParts], bootstrap.Length,
            [.. pageInfo.Values], trees, instances, Textures, Maps);
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
