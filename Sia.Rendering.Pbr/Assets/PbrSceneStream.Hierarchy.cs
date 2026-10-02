using System.Text.Json;
using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream
{
    internal sealed record PageInfo(string Id, int Bytes, int Vertices, int Triangles);

    internal sealed record PagePart(string Id, int First, int Count);

    internal sealed record NodeInfo(
        int Parent,
        int Children,
        int ChildCount,
        float Error,
        float[] Bounds,
        PagePart[] Pages,
        int Triangles);

    internal sealed record HierarchyInfo(int Roots, NodeInfo[] Nodes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        MeshPatchErrorMetric ErrorMetric = MeshPatchErrorMetric.VertexDisplacement);

    private sealed record Header(
        byte[] Identity,
        byte[] Manifest,
        string[] Materials,
        int MaterialBytes,
        PageInfo[] Pages,
        HierarchyInfo[] Hierarchies,
        Instance[] Instances,
        TextureInfo[] Textures,
        int[][] TextureMaps);

    public ReadOnlyMemory<byte> Identity { get; }

    internal HierarchyInfo[] Hierarchies { get; }
    internal Dictionary<string, StreamGeometryPage> ResidentRoots { get; } = [with(StringComparer.Ordinal)];
    internal Dictionary<string, PageInfo> PageTable { get; } = [with(StringComparer.Ordinal)];

    private PbrSceneStream(AssetChunkCache cache, AssetChunkManifest manifest, Header header,
        PbrSceneAsset bootstrap, Dictionary<string, StreamGeometryPage> roots, VisibilityInstance[] instances)
    {
        _cache = cache;
        _manifest = manifest;
        Bootstrap = bootstrap;
        Instances = instances;
        Identity = header.Identity;
        Hierarchies = header.Hierarchies;
        ResidentRoots = roots;
        PageTable = header.Pages.ToDictionary(p => p.Id, StringComparer.Ordinal);
        InitializeTextures(header, bootstrap);
        var minimum = new float3(float.PositiveInfinity);
        var maximum = new float3(float.NegativeInfinity);
        foreach (var instance in instances) {
            var tree = Hierarchies[instance.AssetIndex];
            foreach (var node in tree.Nodes.AsSpan(0, tree.Roots)) {
                var b = BoundsTransform.Apply(NodeBounds(node), instance.Transform);
                minimum = math.min(minimum, b.Min);
                maximum = math.max(maximum, b.Max);
            }
        }
        Bounds = new(minimum, maximum);
    }

    internal static Aabb NodeBounds(NodeInfo node)
        => new(new(node.Bounds[0], node.Bounds[1], node.Bounds[2]), new(node.Bounds[3], node.Bounds[4], node.Bounds[5]));

    public static async Task<PbrSceneStream> OpenAsync(ReadOnlyMemory<byte> metadata,
        Func<AssetChunk, CancellationToken, ValueTask<Stream>> open, long byteBudget = 32 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (metadata.Length > MaximumMetadataBytes)
            throw new InvalidDataException("Stream metadata exceeds its limit.");
        metadata = DecodeMetadata(metadata);
        var h = JsonSerializer.Deserialize(metadata.Span, StreamJsonContext.Default.Header)
            ?? throw new InvalidDataException("Missing hierarchy metadata.");
        metadata = default; // Do not retain decoded JSON through bootstrap I/O.
        if (h.Identity is not { Length: 32 } || h.Manifest is null
            || h.Pages is not { Length: > 0 and <= 1000000 } || h.Hierarchies is not { Length: > 0 and <= 4096 }
            || h.Instances is not { Length: > 0 and <= 1000000 } || h.Materials is null || h.MaterialBytes is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("Invalid hierarchy stream header.");
        var manifest = AssetChunkManifest.Decode(h.Manifest);
        ValidateTextures(h, manifest);
        var pages = new Dictionary<string, PageInfo>(StringComparer.Ordinal);
        foreach (var page in h.Pages) {
            if (page is null || string.IsNullOrEmpty(page.Id) || page.Vertices <= 0 || page.Triangles <= 0
                || page.Bytes > StreamGeometryPage.MaximumBytes || page.Bytes != 16L + (page.Vertices * 48L) + (page.Triangles * 12L)
                || !pages.TryAdd(page.Id, page) || manifest.GetChunk(page.Id).Length > SceneStreamBlock.MaximumEncodedLength(page.Bytes))
                throw new InvalidDataException("Invalid streamed page descriptor.");
        }
        var rootIds = new HashSet<string>(StringComparer.Ordinal);
        var totalNodes = 0;
        foreach (var tree in h.Hierarchies) {
            if (tree is null || tree.Nodes is null || tree.Roots <= 0 || tree.Roots > tree.Nodes.Length
                || !Enum.IsDefined(tree.ErrorMetric) || (totalNodes += tree.Nodes.Length) > 1000000)
                throw new InvalidDataException("Invalid hierarchy size.");
            var parents = Enumerable.Repeat(-1, tree.Nodes.Length).ToArray();
            var depths = new int[tree.Nodes.Length];
            for (var i = 0; i < tree.Nodes.Length; i++) {
                var node = tree.Nodes[i];
                if (node is null || node.Bounds is not { Length: 6 } || node.Bounds.Any(v => !float.IsFinite(v))
                    || node.Bounds[0] > node.Bounds[3] || node.Bounds[1] > node.Bounds[4] || node.Bounds[2] > node.Bounds[5]
                    || !float.IsFinite(node.Error) || node.Error < 0 || node.ChildCount < 0
                    || (node.ChildCount == 0 && node.Error != 0) || node.Pages is not { Length: > 0 } || node.Triangles <= 0
                    || (node.ChildCount > 0 && (node.Children <= i || (long)node.Children + node.ChildCount > tree.Nodes.Length))
                    || node.Pages.Any(p => p is null || string.IsNullOrEmpty(p.Id)))
                    throw new InvalidDataException("Invalid hierarchy node.");
                long count = 0;
                foreach (var part in node.Pages) {
                    if (!pages.TryGetValue(part.Id, out var p) || part.First < 0 || part.Count <= 0
                        || (long)part.First + part.Count > p.Triangles) throw new InvalidDataException("Invalid node page range.");
                    count += part.Count;
                    if (i < tree.Roots) rootIds.Add(part.Id);
                }
                if (count != node.Triangles)
                    throw new InvalidDataException("Node triangle count disagrees with its pages.");
                for (var child = node.Children; child < node.Children + node.ChildCount; child++) {
                    if (child < tree.Roots || parents[child] != -1)
                        throw new InvalidDataException("Invalid hierarchy ownership.");
                    parents[child] = i;
                }
            }
            for (var i = 0; i < tree.Nodes.Length; i++) {
                if (tree.Nodes[i].Parent != parents[i] || (i >= tree.Roots && parents[i] == -1))
                    throw new InvalidDataException("Hierarchy is not a complete forest.");
                if (parents[i] >= 0 && tree.Nodes[i].Error > tree.Nodes[parents[i]].Error)
                    throw new InvalidDataException("Hierarchy error is not monotonic.");
                if (parents[i] >= 0) {
                    depths[i] = depths[parents[i]] + 1;
                    var child = NodeBounds(tree.Nodes[i]);
                    var parent = NodeBounds(tree.Nodes[parents[i]]);
                    if (depths[i] > 64 || math.any(child.Min < parent.Min) || math.any(child.Max > parent.Max))
                        throw new InvalidDataException("Hierarchy depth or bounds are invalid.");
                }
            }
        }
        if (rootIds.Sum(id => (long)pages[id].Bytes) > 256L * 1024 * 1024)
            throw new InvalidDataException("Resident root data exceeds its bootstrap budget.");
        var cache = new AssetChunkCache(open, byteBudget);
        try {
            var materialBytes = new byte[h.MaterialBytes];
            var offset = 0;
            foreach (var id in h.Materials) {
                using var lease = await cache.AcquireAsync(manifest.GetChunk(id), cancellationToken: cancellationToken);
                offset += SceneStreamBlock.Decode(lease.Memory, materialBytes.AsSpan(offset));
            }
            if (offset != materialBytes.Length) throw new InvalidDataException("Bootstrap length mismatch.");
            var bootstrap = await Task.Run(() => PbrSceneAsset.Decode(materialBytes, 256 * 1024 * 1024, cancellationToken), cancellationToken);
            var roots = new Dictionary<string, StreamGeometryPage>(StringComparer.Ordinal);
            foreach (var id in rootIds) {
                using var lease = await cache.AcquireAsync(manifest.GetChunk(id), cancellationToken: cancellationToken);
                var page = StreamGeometryPage.Decode(SceneStreamBlock.Decode(lease.Memory, pages[id].Bytes));
                ValidatePage(page, pages[id]);
                roots.Add(id, page);
            }
            var instances = new VisibilityInstance[h.Instances.Length];
            for (var i = 0; i < instances.Length; i++) {
                var instance = h.Instances[i];
                if (instance is null || (uint)instance.Geometry >= h.Hierarchies.Length
                    || (uint)instance.Material >= bootstrap.Materials.Length || bootstrap.Materials.Span[instance.Material].AlphaBlend
                    || instance.Transform is not { Length: 16 } v || v.Any(f => !float.IsFinite(f)))
                    throw new InvalidDataException("Invalid streamed instance.");
                var t = new float4x4(new(v[0], v[1], v[2], v[3]), new(v[4], v[5], v[6], v[7]), new(v[8], v[9], v[10], v[11]), new(v[12], v[13], v[14], v[15]));
                if (!float.IsFinite(math.determinant(t)) || math.determinant(t) <= 1e-12f || v[3] != 0 || v[7] != 0 || v[11] != 0 || v[15] != 1)
                    throw new InvalidDataException("Invalid streamed transform.");
                instances[i] = new(t, instance.Material) { AssetIndex = instance.Geometry };
            }
            return new(cache, manifest, h, bootstrap, roots, instances);
        }
        catch { await cache.DisposeAsync(); throw; }
    }

    private static void ValidatePage(StreamGeometryPage page, PageInfo info)
    {
        if (page.Bytes.Length != info.Bytes || page.VertexCount != info.Vertices || page.TriangleCount != info.Triangles)
            throw new InvalidDataException("Stream payload disagrees with metadata reservation.");
    }

    internal async Task<StreamGeometryPage> ReadPageAsync(string id, CancellationToken cancellationToken)
    {
        var info = PageTable[id];
        using var lease = await _cache.AcquireAsync(_manifest.GetChunk(id), cancellationToken: cancellationToken);
        var result = await Task.Run(() => StreamGeometryPage.Decode(SceneStreamBlock.Decode(lease.Memory, info.Bytes)), cancellationToken);
        ValidatePage(result, info);
        return result;
    }
}
