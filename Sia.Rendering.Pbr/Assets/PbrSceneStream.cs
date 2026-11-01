using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Math;
using PageReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshVirtualPageRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Transient rendering use of typed Mesh pages. The caller owns transport and asset lifetimes.</summary>
public sealed partial class PbrSceneStream
{
    internal readonly record struct PageInfo(string Id, int Bytes, int Vertices, int Triangles, int DecodedBytes);
    internal readonly record struct PagePart(int Page, int First, int Count);

    // Virtual nodes remain a view of the authenticated parent index. Only page
    // references need translation into the composed scene's dense page table.
    internal sealed class HierarchyInfo
    {
        private readonly MeshVirtualPagesData? _index;
        private readonly MeshVirtualPageNode[]? _nodes;
        private readonly int _firstNode, _nodeCount, _firstRange;
        internal int Roots { get; }
        internal PagePart[] Parts { get; }
        internal ReadOnlySpan<MeshVirtualPageNode> Nodes => _index is { } index
            ? index.Nodes.Slice(_firstNode, _nodeCount) : _nodes;

        internal HierarchyInfo(MeshVirtualPagesData index, int partIndex, PagePart[] parts)
        {
            var part = index.Parts[partIndex];
            _index = index; _firstNode = part.FirstNode; _nodeCount = part.NodeCount;
            _firstRange = part.NodeCount == 0 ? 0 : index.Nodes[part.FirstNode].FirstRange;
            Roots = part.RootCount; Parts = parts;
        }

        internal HierarchyInfo(int roots, MeshVirtualPageNode[] nodes, PagePart[] parts)
            => (Roots, _nodes, Parts) = (roots, nodes, parts);

        internal ReadOnlySpan<PagePart> GetPages(in MeshVirtualPageNode node)
            => Parts.AsSpan(node.FirstRange - _firstRange, node.RangeCount);
    }
    internal readonly record struct PageGeometry(StreamGeometryPage Page, int FirstTriangle, int TriangleCount);
    private readonly (MeshVirtualPagesData Index, int Page)[] _pageUses = [];
    private readonly Dictionary<string, int> _pageIndices = new(StringComparer.Ordinal);
    private readonly Func<PageReference, CancellationToken, Task<MeshVirtualPageRecord>> _prepare;
    private readonly Func<PageReference, MeshVirtualPageRecord, MeshVirtualPageData> _acquire;
    public ReadOnlyMemory<VisibilityInstance> Instances { get; }
    /// <summary>Authored section instances before opaque/transparent page partitioning.</summary>
    public int SourceInstanceCount { get; }
    public ReadOnlyMemory<PbrMaterialAsset> Materials { get; }
    public Aabb Bounds { get; }
    public Aabb GeometryBounds { get; }
    public ReadOnlyMemory<byte> Identity { get; }
    internal HierarchyInfo[] Hierarchies { get; }
    internal Dictionary<string, StreamGeometryPage> ResidentRoots { get; } = new(StringComparer.Ordinal);
    internal PageInfo[] Pages { get; private set; } = [];
    internal int GetPageIndex(string id) => _pageIndices[id];
    internal PageGeometry[] TransparentGeometry { get; }
    internal PbrSceneInstance[] TransparentInstances { get; }
    internal bool RequiresInstancing { get; }

    /// <summary>Enumerates only roots and transparent finest pages required before graphics creation.</summary>
    public static MeshVirtualPageDescriptor[] RequiredPages(PbrVirtualScene scene, long maximumBytes = 256L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var result = new Dictionary<AssetId, MeshVirtualPageDescriptor>(); long bytes = StreamGeometryPage.MaximumBytes;
        foreach (var instance in scene.Instances.Span) {
            var geometry = scene.Geometry.Span[instance.Geometry]; var index = geometry.Product; var part = index.Parts[geometry.Part];
            var transparent = scene.Materials.Span[instance.Material].AlphaBlend;
            for (var n = 0; n < part.NodeCount; n++) {
                var node = index.Nodes[part.FirstNode + n];
                if (transparent ? node.ChildCount != 0 : n >= part.RootCount) continue;
                foreach (var range in index.Ranges.Slice(node.FirstRange, node.RangeCount)) {
                    var descriptor = index.Pages[range.Page];
                    if (result.TryGetValue(descriptor.Id, out var prior)) {
                        if (prior != descriptor) throw new InvalidAssetException("Page identity has conflicting revisions.");
                        continue;
                    }
                    bytes = checked(bytes + descriptor.VertexCount * (long)(MeshVertex.Stride + (index.HasBakeUvs ? 8 : 0))
                        + descriptor.TriangleCount * 12L);
                    if (bytes > maximumBytes) throw new InvalidOperationException("Required virtual pages exceed their decoded bootstrap budget.");
                    result.Add(descriptor.Id, descriptor);
                }
            }
        }
        return [.. result.Values];
    }

    /// <summary>
    /// Composes acquired roots and transparent pages without IO. Prepare runs asynchronously;
    /// acquire runs synchronously on the graphics owner's context before an upload is installed.
    /// The caller must retain the parent scene use and transport until rendering requests stop.
    /// </summary>
    public static PbrSceneStream Create(PbrVirtualScene scene, Func<PageReference, MeshVirtualPageData> requiredPages,
        Func<PageReference, CancellationToken, Task<MeshVirtualPageRecord>> prepare,
        Func<PageReference, MeshVirtualPageRecord, MeshVirtualPageData> acquire,
        long maximumRequiredBytes = 256L * 1024 * 1024,
        Func<PbrTextureData, PbrTextureMipsData?>? textureMips = null,
        Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>>? prepareMip = null)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(requiredPages);
        ArgumentNullException.ThrowIfNull(prepare); ArgumentNullException.ThrowIfNull(acquire);
        _ = RequiredPages(scene, maximumRequiredBytes);
        if ((textureMips is null) != (prepareMip is null)) throw new ArgumentException("Texture index and preparation callbacks must be provided together.");
        return new(scene, requiredPages, prepare, acquire, textureMips, prepareMip);
    }

    private PbrSceneStream(PbrVirtualScene scene, Func<PageReference, MeshVirtualPageData> requiredPages,
        Func<PageReference, CancellationToken, Task<MeshVirtualPageRecord>> prepare,
        Func<PageReference, MeshVirtualPageRecord, MeshVirtualPageData> acquire,
        Func<PbrTextureData, PbrTextureMipsData?>? textureMips,
        Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>>? prepareMip)
    {
        (_prepare, _acquire) = (prepare, acquire); (_findTextureMips, _prepareMip) = (textureMips, prepareMip);
        Bounds = scene.Bounds; Identity = scene.TransportIdentity;
        SourceInstanceCount = scene.Instances.Length;
        var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
        long nodeCount = 0;
        foreach (var geometry in scene.Geometry.Span) {
            var part = geometry.Product.Parts[geometry.Part];
            nodeCount += part.NodeCount;
            if (nodeCount > MeshVirtualPagesData.MaximumNodes) throw new InvalidOperationException("Composed virtual hierarchy exceeds its node limit.");
            foreach (var node in geometry.Product.Nodes.Slice(part.FirstNode, part.RootCount)) {
                minimum = math.min(minimum, node.Bounds.Min); maximum = math.max(maximum, node.Bounds.Max);
            }
        }
        GeometryBounds = math.all(math.isfinite(minimum)) ? new(minimum, maximum) : new(float3.zero, float3.zero);
        var materials = InitializeTextures(scene.Materials.Span);
        Materials = materials;
        var hierarchies = new List<HierarchyInfo>(); var opaque = new List<VisibilityInstance>();
        var geometryIndices = new Dictionary<int, int>();
        var transparentGeometry = new List<PageGeometry>(); var transparentInstances = new List<PbrSceneInstance>();
        var transparentIndices = new Dictionary<(int Geometry, int Range), int>();
        var validated = new Dictionary<AssetId, MeshVirtualPageData>();
        var pageTable = new List<PageInfo>();
        var pageUses = new List<(MeshVirtualPagesData Index, int Page)>();
        var pageIndices = new Dictionary<AssetId, (int Number, MeshVirtualPagesData Index, int Page)>();
        int Page(MeshVirtualPagesData index, int page)
        {
            var descriptor = index.Pages[page];
            if (pageIndices.TryGetValue(descriptor.Id, out var prior)) {
                if (prior.Index.Pages[prior.Page] != descriptor) throw new InvalidAssetException("Page identity has conflicting revisions.");
                return prior.Number;
            }
            var id = descriptor.Id.Value.ToString("N");
            var number = pageTable.Count;
            var gpuBytes = checked(descriptor.VertexCount * StreamGeometryPage.VertexBytes + descriptor.TriangleCount * 12);
            var decoded = checked(descriptor.VertexCount * (MeshVertex.Stride + (index.HasBakeUvs ? 8 : 0)) + descriptor.TriangleCount * 12);
            pageTable.Add(new(id, gpuBytes, descriptor.VertexCount, descriptor.TriangleCount, decoded));
            pageIndices.Add(descriptor.Id, (number, index, page)); _pageIndices.Add(id, number); pageUses.Add((index, page));
            return number;
        }
        MeshVirtualPageData Required(MeshVirtualPagesData index, int page)
        {
            var descriptor = index.Pages[page];
            if (!validated.TryGetValue(descriptor.Id, out var value)) {
                value = requiredPages(descriptor.Reference) ?? throw new AssetNotFoundException("Required typed page is not acquired.");
                index.ValidatePage(page, value); validated.Add(descriptor.Id, value);
            }
            else index.ValidatePage(page, value);
            return value;
        }
        foreach (var instance in scene.Instances.Span) {
            var geometry = scene.Geometry.Span[instance.Geometry]; var index = geometry.Product; var part = index.Parts[geometry.Part];
            if (materials[instance.Material].AlphaBlend) {
                for (var n = 0; n < part.NodeCount; n++) {
                    var node = index.Nodes[part.FirstNode + n]; if (node.ChildCount != 0) continue;
                    for (var r = node.FirstRange; r < node.FirstRange + node.RangeCount; r++) {
                        var range = index.Ranges[r]; var key = (instance.Geometry, r);
                        if (!transparentIndices.TryGetValue(key, out var g)) {
                            g = transparentGeometry.Count; transparentIndices.Add(key, g);
                            transparentGeometry.Add(new(StreamGeometryPage.Create(Required(index, range.Page)), range.FirstTriangle, range.TriangleCount));
                        }
                        if (transparentInstances.Count == PbrSceneData.MaximumInstances)
                            throw new InvalidOperationException("Transparent page expansion exceeds its instance limit.");
                        transparentInstances.Add(new(g, instance.Material, instance.Transform) {
                            AuthoredId = instance.AuthoredId, MaterialSlot = instance.MaterialSlot });
                    }
                }
                continue;
            }
            if (part.RootCount == 0) continue;
            if (!geometryIndices.TryGetValue(instance.Geometry, out var h)) {
                h = hierarchies.Count; geometryIndices.Add(instance.Geometry, h);
                var nodes = index.Nodes.Slice(part.FirstNode, part.NodeCount);
                var firstRange = nodes[0].FirstRange;
                var parts = new PagePart[nodes[^1].FirstRange + nodes[^1].RangeCount - firstRange];
                for (var n = 0; n < nodes.Length; n++) {
                    var node = nodes[n];
                    for (var r = 0; r < node.RangeCount; r++) {
                        var range = index.Ranges[node.FirstRange + r]; var page = Page(index, range.Page); var id = pageTable[page].Id;
                        parts[node.FirstRange - firstRange + r] = new(page, range.FirstTriangle, range.TriangleCount);
                        if (n < part.RootCount && !ResidentRoots.ContainsKey(id)) ResidentRoots.Add(id, StreamGeometryPage.Create(Required(index, range.Page)));
                    }
                }
                hierarchies.Add(new(index, geometry.Part, parts));
            }
            opaque.Add(new(instance.Transform, instance.Material) {
                AssetIndex = h, AuthoredId = instance.AuthoredId, MaterialSlot = instance.MaterialSlot });
        }
        Hierarchies = [.. hierarchies]; Instances = opaque.ToArray(); Pages = [.. pageTable]; _pageUses = [.. pageUses];
        TransparentGeometry = [.. transparentGeometry]; TransparentInstances = [.. transparentInstances];
    }

    internal static Aabb NodeBounds(in MeshVirtualPageNode node) => node.Bounds;
    internal Task<MeshVirtualPageRecord> ReadPageAsync(string id, CancellationToken token)
    {
        var use = _pageUses[GetPageIndex(id)];
        return _prepare(use.Index.Pages[use.Page].Reference, token);
    }

    internal StreamGeometryPage UsePage(string id, MeshVirtualPageRecord prepared)
    {
        var use = _pageUses[GetPageIndex(id)]; var descriptor = use.Index.Pages[use.Page];
        var page = _acquire(descriptor.Reference, prepared) ?? throw new AssetNotFoundException("Typed page acquisition failed.");
        use.Index.ValidatePage(use.Page, page);
        return StreamGeometryPage.Create(page);
    }
}
