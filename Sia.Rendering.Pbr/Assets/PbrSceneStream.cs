using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Math;
using PageReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshVirtualPageRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Transient rendering use of typed Mesh pages. The caller owns transport and asset lifetimes.</summary>
public sealed partial class PbrSceneStream
{
    internal sealed record PageInfo(string Id, int Bytes, int Vertices, int Triangles, int DecodedBytes);
    internal sealed record PagePart(string Id, int First, int Count);
    internal sealed record NodeInfo(int Parent, int Children, int ChildCount, float Error, float[] Bounds, PagePart[] Pages, int Triangles);
    internal sealed record HierarchyInfo(int Roots, NodeInfo[] Nodes);
    internal readonly record struct PageGeometry(StreamGeometryPage Page, int FirstTriangle, int TriangleCount);
    private readonly Dictionary<string, (MeshVirtualPagesData Index, int Page)> _pageUses = new(StringComparer.Ordinal);
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
    internal Dictionary<string, PageInfo> PageTable { get; } = new(StringComparer.Ordinal);
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
                var nodes = new NodeInfo[part.NodeCount];
                for (var n = 0; n < nodes.Length; n++) {
                    var node = index.Nodes[part.FirstNode + n]; var pages = new PagePart[node.RangeCount];
                    for (var r = 0; r < pages.Length; r++) {
                        var range = index.Ranges[node.FirstRange + r]; var descriptor = index.Pages[range.Page]; var id = descriptor.Id.Value.ToString("N");
                        if (_pageUses.TryGetValue(id, out var prior)) {
                            if (prior.Index.Pages[prior.Page] != descriptor) throw new InvalidAssetException("Page identity has conflicting revisions.");
                        }
                        else {
                            _pageUses.Add(id, (index, range.Page));
                            var gpuBytes = checked(descriptor.VertexCount * StreamGeometryPage.VertexBytes + descriptor.TriangleCount * 12);
                            var decoded = checked(descriptor.VertexCount * (MeshVertex.Stride + (index.HasBakeUvs ? 8 : 0)) + descriptor.TriangleCount * 12);
                            PageTable.Add(id, new(id, gpuBytes, descriptor.VertexCount, descriptor.TriangleCount, decoded));
                        }
                        pages[r] = new(id, range.FirstTriangle, range.TriangleCount);
                        if (n < part.RootCount && !ResidentRoots.ContainsKey(id)) ResidentRoots.Add(id, StreamGeometryPage.Create(Required(index, range.Page)));
                    }
                    var b = node.Bounds;
                    nodes[n] = new(node.Parent, node.FirstChild, node.ChildCount, node.Error,
                        [b.Min.x, b.Min.y, b.Min.z, b.Max.x, b.Max.y, b.Max.z], pages, node.TriangleCount);
                }
                hierarchies.Add(new(part.RootCount, nodes));
            }
            opaque.Add(new(instance.Transform, instance.Material) {
                AssetIndex = h, AuthoredId = instance.AuthoredId, MaterialSlot = instance.MaterialSlot });
        }
        Hierarchies = [.. hierarchies]; Instances = opaque.ToArray();
        TransparentGeometry = [.. transparentGeometry]; TransparentInstances = [.. transparentInstances];
    }

    internal static Aabb NodeBounds(NodeInfo node) => new(new(node.Bounds[0], node.Bounds[1], node.Bounds[2]), new(node.Bounds[3], node.Bounds[4], node.Bounds[5]));
    internal Task<MeshVirtualPageRecord> ReadPageAsync(string id, CancellationToken token)
        => _prepare(_pageUses[id].Index.Pages[_pageUses[id].Page].Reference, token);

    internal StreamGeometryPage UsePage(string id, MeshVirtualPageRecord prepared)
    {
        var use = _pageUses[id]; var descriptor = use.Index.Pages[use.Page];
        var page = _acquire(descriptor.Reference, prepared) ?? throw new AssetNotFoundException("Typed page acquisition failed.");
        use.Index.ValidatePage(use.Page, page);
        return StreamGeometryPage.Create(page);
    }
}
