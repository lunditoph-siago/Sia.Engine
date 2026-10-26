using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Math;
using PageReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshVirtualPageRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream
{
    /// <summary>One local shared GPU scene. Raster batches retain exact indexed geometry; Virtual keeps independent demand pages.</summary>
    public static PbrSceneStream Create(PbrMixedScene scene, Func<PageReference, MeshVirtualPageData> requiredPages,
        Func<PageReference, CancellationToken, Task<MeshVirtualPageRecord>> prepare,
        Func<PageReference, MeshVirtualPageRecord, MeshVirtualPageData> acquire,
        long maximumRequiredBytes = 256L * 1024 * 1024,
        Func<PbrTextureData, PbrTextureMipsData?>? textureMips = null,
        Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>>? prepareMip = null)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(requiredPages);
        ArgumentNullException.ThrowIfNull(prepare); ArgumentNullException.ThrowIfNull(acquire);
        _ = RequiredPages(scene.Virtual, maximumRequiredBytes);
        if ((textureMips is null) != (prepareMip is null)) throw new ArgumentException("Texture index and preparation callbacks must be provided together.");
        return new(scene, requiredPages, prepare, acquire, maximumRequiredBytes, textureMips, prepareMip);
    }

    private PbrSceneStream(PbrMixedScene scene, Func<PageReference, MeshVirtualPageData> requiredPages,
        Func<PageReference, CancellationToken, Task<MeshVirtualPageRecord>> prepare,
        Func<PageReference, MeshVirtualPageRecord, MeshVirtualPageData> acquire, long maximumRequiredBytes,
        Func<PbrTextureData, PbrTextureMipsData?>? textureMips,
        Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>>? prepareMip)
        : this(scene.Virtual, requiredPages, prepare, acquire, textureMips, prepareMip)
    {
        RequiresInstancing = true; Identity = scene.TransportIdentity;
        var raster = scene.Raster; SourceInstanceCount = checked(SourceInstanceCount + raster.Instances.Length);
        Bounds = Instances.Length + TransparentInstances.Length == 0 ? raster.Bounds
            : raster.Instances.Length == 0 ? Bounds : Aabb.Union(Bounds, raster.Bounds);
        var materials = scene.Virtual.Materials.ToArray().ToList();
        var materialLookup = new Dictionary<PbrMaterialAsset, int>();
        for (var i = 0; i < materials.Count; i++) materialLookup.TryAdd(materials[i], i);
        var rasterMaterials = new int[raster.Materials.Length];
        for (var i = 0; i < rasterMaterials.Length; i++) {
            var material = raster.Materials.Span[i];
            if (!materialLookup.TryGetValue(material, out var index)) {
                if (materials.Count == 4096) throw new InvalidOperationException("Mixed material table exceeds its limit.");
                index = materials.Count; materials.Add(material); materialLookup.Add(material, index);
            }
            rasterMaterials[i] = index;
        }
        TextureSources.Clear(); _fullTextures.Clear(); _textureMips.Clear();
        Materials = InitializeTextures(materials.ToArray());
        var hierarchies = Hierarchies.ToList(); var opaque = Instances.ToArray().ToList();
        var nodeCount = hierarchies.Sum(h => (long)h.Nodes.Length);
        var transparentGeometry = TransparentGeometry.ToList(); var transparent = TransparentInstances.ToList();
        var rasterShapes = new Dictionary<(MeshRasterData, int), (int Hierarchy, PageGeometry[] Pages, int[]? Transparent)>();
        var required = new HashSet<MeshVirtualPageData>(ReferenceEqualityComparer.Instance);
        foreach (var root in ResidentRoots.Values) if (root.Data is { } data) required.Add(data);
        foreach (var page in TransparentGeometry) if (page.Page.Data is { } data) required.Add(data);
        long requiredBytes = StreamGeometryPage.MaximumBytes + required.Sum(p => checked((long)p.PayloadBytes));
        var geometryBounds = GeometryBounds;
        var hasBounds = scene.Virtual.Geometry.Length != 0;
        foreach (var instance in raster.Instances.Span) {
            var geometry = raster.Geometry.Span[instance.Geometry]; var key = (geometry.Product, geometry.Section);
            if (!rasterShapes.TryGetValue(key, out var shape)) {
                var section = geometry.Product.Sections[geometry.Section];
                var nodes = new List<NodeInfo>(); var pages = new List<PageGeometry>();
                for (var first = 0; first < section.IndexCount / 3; first += MeshVirtualPageData.MaximumTriangles) {
                    if (++nodeCount > MeshVirtualPagesData.MaximumNodes)
                        throw new InvalidOperationException("Mixed hierarchy exceeds its node limit.");
                    var count = System.Math.Min(MeshVirtualPageData.MaximumTriangles, section.IndexCount / 3 - first);
                    var page = StreamGeometryPage.Create(geometry.Product, section.FirstIndex / 3 + first, count);
                    var id = $"raster/{rasterShapes.Count}/{nodes.Count}";
                    var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
                    for (var vertex = 0; vertex < page.VertexCount; vertex++) {
                        var position = page.GetVertex(vertex).Position; minimum = math.min(minimum, position); maximum = math.max(maximum, position);
                    }
                    requiredBytes = checked(requiredBytes + page.VertexCount * 4L + count * 12L);
                    if (requiredBytes > maximumRequiredBytes) throw new InvalidOperationException("Mixed resident metadata exceeds its CPU bootstrap budget.");
                    if (PageTable.Count >= MeshVirtualPagesData.MaximumPages) throw new InvalidOperationException("Mixed GPU page table exceeds its limit.");
                    PageTable.Add(id, new(id, page.UploadBytes, page.VertexCount, count, 0)); ResidentRoots.Add(id, page);
                    nodes.Add(new(-1, 0, 0, 0, [minimum.x, minimum.y, minimum.z, maximum.x, maximum.y, maximum.z], [new(id, 0, count)], count));
                    pages.Add(new(page, 0, count));
                }
                shape = (hierarchies.Count, pages.ToArray(), null); rasterShapes.Add(key, shape);
                hierarchies.Add(new(nodes.Count, nodes.ToArray()));
                geometryBounds = hasBounds ? Aabb.Union(geometryBounds, geometry.Bounds) : geometry.Bounds; hasBounds = true;
            }
            var material = rasterMaterials[instance.Material];
            if (materials[material].AlphaBlend) {
                if (shape.Transparent is null) {
                    shape.Transparent = new int[shape.Pages.Length];
                    for (var i = 0; i < shape.Pages.Length; i++) {
                        shape.Transparent[i] = transparentGeometry.Count; transparentGeometry.Add(shape.Pages[i]);
                    }
                    rasterShapes[key] = shape;
                }
                foreach (var page in shape.Transparent) {
                    if (transparent.Count == PbrSceneData.MaximumInstances) throw new InvalidOperationException("Mixed transparency expansion exceeds its instance limit.");
                    transparent.Add(instance with { Geometry = page, Material = material });
                }
            }
            else opaque.Add(new(instance.Transform, material) { AssetIndex = shape.Hierarchy, AuthoredId = instance.AuthoredId, MaterialSlot = instance.MaterialSlot });
        }
        if (opaque.Count + (long)transparent.Count > PbrSceneData.MaximumInstances) throw new InvalidOperationException("Mixed GPU instances exceed their limit.");
        Hierarchies = hierarchies.ToArray(); Instances = opaque.ToArray();
        TransparentGeometry = transparentGeometry.ToArray(); TransparentInstances = transparent.ToArray(); GeometryBounds = geometryBounds;
    }
}
