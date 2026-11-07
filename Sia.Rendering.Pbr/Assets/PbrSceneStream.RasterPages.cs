using System.Runtime.CompilerServices;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Math;
using RasterPageReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshRasterPageRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream
{
    internal sealed record PreparedGeometryPage(MeshVirtualPageRecord? Virtual, MeshRasterPageRecord[]? Raster, int DecodedBytes)
    {
        internal (MeshRasterPagesData Product, int Page, StreamGeometryPage Geometry)? RasterGeometry { get; init; }
    }
    private sealed record RasterPageUse(MeshRasterPagesData Product, int Page, int PreparationBytes);
    private readonly Dictionary<string, RasterPageUse> _rasterPageUses = new(StringComparer.Ordinal);
    internal bool IsRasterPage(string id) => _rasterPageUses.ContainsKey(id)
        || (ResidentRoots.TryGetValue(id, out var root) && root.Data is null);
    private readonly Func<RasterPageReference, CancellationToken, Task<MeshRasterPageRecord>>? _prepareRaster;
    private readonly Func<RasterPageReference, MeshRasterPageRecord, MeshRasterPageData>? _acquireRaster;

    /// <summary>Largest retained preparation reservation required by this scene's independent geometry requests.</summary>
    /// <remarks>Includes Raster index/vertex dependencies, compact output and remap scratch; excludes the encoded transport cache and GPU storage.</remarks>
    public int MaximumPagePreparationBytes => System.Math.Max(2 * MeshVirtualPageData.MaximumEncodedBytes,
        Pages.Length == 0 ? 0 : Pages.Max(page => page.DecodedBytes));

    // A prepared compact can coexist with another compact when acquisition returns
    // different data. Owners whose existing budget cannot retain both keep owner compaction.
    internal long MaximumWorkerPagePreparationBytes => System.Math.Max(MaximumPagePreparationBytes,
        _rasterPageUses.Count == 0 ? 0 : _rasterPageUses.Values.Max(WorkerPreparationBytes));

    internal bool CanPrepareRasterGeometry(long maximumBytes)
        => MaximumWorkerPagePreparationBytes <= System.Math.Min(maximumBytes, int.MaxValue);

    internal PageInfo[] GetPreparationPages(bool compactRaster)
        => !compactRaster ? Pages : Pages.Select(page => _rasterPageUses.TryGetValue(page.Id, out var use)
            ? page with { DecodedBytes = checked((int)WorkerPreparationBytes(use)) } : page).ToArray();

    /// <summary>Enumerates coarse opaque and exact transparent Raster leaves and their vertex dependencies.</summary>
    /// <remarks>
    /// Admission counts deduplicated decoded source arrays, conservative retained compact output and one bounded scratch page.
    /// Global source vertex dependencies can make this bootstrap larger than the coarse index payload alone.
    /// This method reads no leaves and does not change the caller's byte limit.
    /// </remarks>
    public static MeshRasterPageDescriptor[] RequiredPages(PbrRasterPagesScene scene,
        long maximumBytes = 256L * 1024 * 1024)
        => RequiredRasterPages(scene, maximumBytes, out _);

    private static MeshRasterPageDescriptor[] RequiredRasterPages(PbrRasterPagesScene scene, long maximumBytes, out long requiredBytes)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var result = new Dictionary<AssetId, MeshRasterPageDescriptor>(); long bytes = StreamGeometryPage.MaximumBytes;
        var compacted = new HashSet<AssetId>();
        foreach (var instance in scene.Instances.Span) {
            var geometry = scene.Geometry.Span[instance.Geometry]; var product = geometry.Product;
            var lods = geometry.Lods;
            var lod = scene.Materials.Span[instance.Material].AlphaBlend ? lods[0] : lods[^1];
            for (var page = lod.FirstPage; page < lod.FirstPage + lod.PageCount; page++) {
                Add(product, page);
                foreach (var dependency in product.GetVertexDependencies(page)) Add(product, dependency);
                if (compacted.Add(product.Pages[page].Id)) bytes = checked(bytes + CompactPageBytes(product, product.Pages[page].ElementCount));
                if (bytes > maximumBytes) throw new InvalidOperationException("Required Raster pages exceed their decoded bootstrap budget.");
            }
        }
        requiredBytes = bytes;
        return [.. result.Values];

        void Add(MeshRasterPagesData product, int page)
        {
            var descriptor = product.Pages[page];
            if (result.TryGetValue(descriptor.Id, out var previous)) {
                if (previous != descriptor) throw new InvalidAssetException("Raster page identity has conflicting revisions.");
                return;
            }
            bytes = checked(bytes + RasterPageBytes(product, descriptor));
            if (bytes > maximumBytes) throw new InvalidOperationException("Required Raster pages exceed their decoded bootstrap budget.");
            result.Add(descriptor.Id, descriptor);
        }
    }

    /// <summary>Streams exact existing Raster LOD pages through the ordinary geometry scheduler and GPU arena.</summary>
    /// <remarks>
    /// Required opaque pages use the existing coarsest LOD; AlphaBlend sections retain complete LOD0.
    /// Preparation performs typed reads without World mutation and can compact original referenced vertices
    /// when the owner's decoded budget also admits a fallback compact. Acquisition runs synchronously on the
    /// World owner context and reuses matching prepared geometry or compacts the acquired original channels.
    /// The caller owns the transport and metadata Asset uses and must join streaming before releasing them.
    /// Set the renderer's decoded scheduling budget to at least <see cref="MaximumPagePreparationBytes"/>.
    /// </remarks>
    public static PbrSceneStream Create(PbrRasterPagesScene scene,
        Func<RasterPageReference, MeshRasterPageData> requiredPages,
        Func<RasterPageReference, CancellationToken, Task<MeshRasterPageRecord>> prepare,
        Func<RasterPageReference, MeshRasterPageRecord, MeshRasterPageData> acquire,
        long maximumRequiredBytes = 256L * 1024 * 1024,
        int maximumPagePreparationBytes = 32 * 1024 * 1024,
        Func<PbrTextureData, PbrTextureMipsData?>? textureMips = null,
        Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>>? prepareMip = null)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(requiredPages);
        ArgumentNullException.ThrowIfNull(prepare); ArgumentNullException.ThrowIfNull(acquire);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPagePreparationBytes, 1);
        if ((textureMips is null) != (prepareMip is null))
            throw new ArgumentException("Texture index and preparation callbacks must be provided together.");
        _ = RequiredPages(scene, maximumRequiredBytes);
        return new(scene, requiredPages, prepare, acquire, maximumPagePreparationBytes, textureMips, prepareMip);
    }

    /// <summary>Composes Virtual and exact paged Raster products in one bounded scene stream.</summary>
    /// <remarks>
    /// Uses a combined decoded bootstrap limit for both independent product kinds. Original authored
    /// identities and LOD errors are retained; complete refinement groups publish through the existing scheduler.
    /// The existing whole-Raster mixed overload remains available for inputs without paged products.
    /// </remarks>
    public static PbrSceneStream Create(PbrMixedScene scene,
        Func<AssetRefer<MeshVirtualPageRecord>.Id, MeshVirtualPageData> requiredVirtual,
        Func<AssetRefer<MeshVirtualPageRecord>.Id, CancellationToken, Task<MeshVirtualPageRecord>> prepareVirtual,
        Func<AssetRefer<MeshVirtualPageRecord>.Id, MeshVirtualPageRecord, MeshVirtualPageData> acquireVirtual,
        Func<RasterPageReference, MeshRasterPageData> requiredRaster,
        Func<RasterPageReference, CancellationToken, Task<MeshRasterPageRecord>> prepareRaster,
        Func<RasterPageReference, MeshRasterPageRecord, MeshRasterPageData> acquireRaster,
        long maximumRequiredBytes = 256L * 1024 * 1024,
        int maximumPagePreparationBytes = 32 * 1024 * 1024,
        Func<PbrTextureData, PbrTextureMipsData?>? textureMips = null,
        Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>>? prepareMip = null)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(scene.RasterPages);
        ArgumentNullException.ThrowIfNull(requiredVirtual); ArgumentNullException.ThrowIfNull(prepareVirtual);
        ArgumentNullException.ThrowIfNull(acquireVirtual); ArgumentNullException.ThrowIfNull(requiredRaster);
        ArgumentNullException.ThrowIfNull(prepareRaster); ArgumentNullException.ThrowIfNull(acquireRaster);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPagePreparationBytes, 1);
        var virtualPages = RequiredPages(scene.Virtual, maximumRequiredBytes);
        _ = RequiredRasterPages(scene.RasterPages, maximumRequiredBytes, out var requiredBytes);
        foreach (var page in virtualPages) {
            var hasBakeUvs = page.EncodedBytes == 73L + page.VertexCount * 56L + page.TriangleCount * 12L;
            requiredBytes = checked(requiredBytes + DecodedPageBytes(page, hasBakeUvs));
        }
        if (requiredBytes > maximumRequiredBytes) throw new InvalidOperationException("Mixed roots exceed their combined decoded bootstrap budget.");
        if ((textureMips is null) != (prepareMip is null))
            throw new ArgumentException("Texture index and preparation callbacks must be provided together.");
        return new(scene, requiredVirtual, prepareVirtual, acquireVirtual, requiredRaster, prepareRaster, acquireRaster,
            maximumPagePreparationBytes, textureMips, prepareMip);
    }

    private PbrSceneStream(PbrMixedScene scene,
        Func<AssetRefer<MeshVirtualPageRecord>.Id, MeshVirtualPageData> requiredVirtual,
        Func<AssetRefer<MeshVirtualPageRecord>.Id, CancellationToken, Task<MeshVirtualPageRecord>> prepareVirtual,
        Func<AssetRefer<MeshVirtualPageRecord>.Id, MeshVirtualPageRecord, MeshVirtualPageData> acquireVirtual,
        Func<RasterPageReference, MeshRasterPageData> requiredRaster,
        Func<RasterPageReference, CancellationToken, Task<MeshRasterPageRecord>> prepareRaster,
        Func<RasterPageReference, MeshRasterPageRecord, MeshRasterPageData> acquireRaster,
        int maximumPagePreparationBytes, Func<PbrTextureData, PbrTextureMipsData?>? textureMips,
        Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>>? prepareMip)
        : this(scene.RasterPages!, requiredRaster, prepareRaster, acquireRaster, maximumPagePreparationBytes, textureMips, prepareMip)
    {
        var virtualStream = new PbrSceneStream(scene.Virtual, requiredVirtual, prepareVirtual, acquireVirtual, textureMips, prepareMip);
        (_prepare, _acquire) = (prepareVirtual, acquireVirtual); _pageUses = virtualStream._pageUses;
        Identity = scene.TransportIdentity; SourceInstanceCount = checked(SourceInstanceCount + virtualStream.SourceInstanceCount);
        Bounds = scene.RasterPages!.Instances.IsEmpty ? virtualStream.Bounds
            : scene.Virtual.Instances.IsEmpty ? Bounds : Aabb.Union(virtualStream.Bounds, Bounds);
        GeometryBounds = scene.RasterPages.Geometry.IsEmpty ? virtualStream.GeometryBounds
            : scene.Virtual.Geometry.IsEmpty ? GeometryBounds : Aabb.Union(virtualStream.GeometryBounds, GeometryBounds);
        var sourceMaterials = scene.Virtual.Materials.ToArray().ToList();
        var materialLookup = new Dictionary<PbrMaterialAsset, int>();
        for (var i = 0; i < sourceMaterials.Count; i++) materialLookup.TryAdd(sourceMaterials[i], i);
        var rasterMaterials = new int[scene.RasterPages.Materials.Length];
        for (var i = 0; i < rasterMaterials.Length; i++) {
            var material = scene.RasterPages.Materials.Span[i];
            if (!materialLookup.TryGetValue(material, out var index)) {
                if (sourceMaterials.Count == 4096) throw new InvalidOperationException("Mixed material table exceeds its limit.");
                index = sourceMaterials.Count; sourceMaterials.Add(material); materialLookup.Add(material, index);
            }
            rasterMaterials[i] = index;
        }
        TextureSources.Clear(); _fullTextures.Clear(); _textureMips.Clear();
        Materials = InitializeTextures(sourceMaterials.ToArray());
        var pageOffset = virtualStream.Pages.Length; var hierarchyOffset = virtualStream.Hierarchies.Length;
        var hierarchies = virtualStream.Hierarchies.ToList();
        foreach (var tree in Hierarchies) {
            var parts = tree.Parts.Select(part => part with { Page = checked(part.Page + pageOffset) }).ToArray();
            hierarchies.Add(new(tree.Roots, tree.Nodes.ToArray(), parts));
        }
        if (hierarchies.Sum(tree => (long)tree.Nodes.Length) > MeshVirtualPagesData.MaximumNodes)
            throw new InvalidOperationException("Mixed hierarchy exceeds its node limit.");
        var rasterInstances = Instances.ToArray();
        var opaque = virtualStream.Instances.ToArray().ToList();
        foreach (var instance in rasterInstances)
            opaque.Add(instance with { AssetIndex = checked(instance.AssetIndex + hierarchyOffset), MaterialIndex = rasterMaterials[instance.MaterialIndex] });
        var transparentOffset = virtualStream.TransparentGeometry.Length;
        var transparent = virtualStream.TransparentInstances.ToList();
        foreach (var instance in TransparentInstances)
            transparent.Add(instance with { Geometry = checked(instance.Geometry + transparentOffset), Material = rasterMaterials[instance.Material] });
        var transparentGeometry = virtualStream.TransparentGeometry.Concat(TransparentGeometry).ToArray();
        var pages = virtualStream.Pages.Concat(Pages).ToArray();
        if (pages.Length > MeshVirtualPagesData.MaximumPages || opaque.Count + (long)transparent.Count > PbrSceneData.MaximumInstances)
            throw new InvalidOperationException("Mixed page or instance table exceeds its limit.");
        _pageIndices.Clear();
        for (var page = 0; page < pages.Length; page++)
            if (!_pageIndices.TryAdd(pages[page].Id, page)) throw new InvalidAssetException("Raster and Virtual page identities overlap.");
        foreach (var root in virtualStream.ResidentRoots)
            if (!ResidentRoots.TryAdd(root.Key, root.Value)) throw new InvalidAssetException("Raster and Virtual bootstrap identities overlap.");
        Pages = pages; Hierarchies = hierarchies.ToArray(); Instances = opaque.ToArray();
        TransparentGeometry = transparentGeometry; TransparentInstances = transparent.ToArray();
    }

    private PbrSceneStream(PbrRasterPagesScene scene, Func<RasterPageReference, MeshRasterPageData> requiredPages,
        Func<RasterPageReference, CancellationToken, Task<MeshRasterPageRecord>> prepare,
        Func<RasterPageReference, MeshRasterPageRecord, MeshRasterPageData> acquire,
        int maximumPagePreparationBytes, Func<PbrTextureData, PbrTextureMipsData?>? textureMips,
        Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>>? prepareMip)
    {
        _prepare = static (_, _) => throw new InvalidOperationException("This stream uses Raster geometry pages.");
        _acquire = static (_, _) => throw new InvalidOperationException("This stream uses Raster geometry pages.");
        (_prepareRaster, _acquireRaster) = (prepare, acquire); (_findTextureMips, _prepareMip) = (textureMips, prepareMip);
        Bounds = scene.Bounds; Identity = scene.TransportIdentity; RequiresInstancing = true;
        SourceInstanceCount = scene.Instances.Length; Materials = InitializeTextures(scene.Materials.Span);
        var hierarchies = new List<HierarchyInfo>(); var opaque = new List<VisibilityInstance>();
        var pageTable = new List<PageInfo>(); var geometries = new Dictionary<int, int>();
        var transparentGeometry = new List<PageGeometry>(); var transparent = new List<PbrSceneInstance>();
        var transparentPages = new Dictionary<AssetId, int>();
        var rootGeometry = new Dictionary<AssetId, StreamGeometryPage>();
        var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
        foreach (var geometry in scene.Geometry.Span) {
            minimum = math.min(minimum, geometry.Bounds.Min); maximum = math.max(maximum, geometry.Bounds.Max);
        }
        GeometryBounds = scene.Geometry.IsEmpty ? new(float3.zero, float3.zero) : new(minimum, maximum);

        StreamGeometryPage Required(MeshRasterPagesData product, int page)
        {
            var descriptor = product.Pages[page];
            if (rootGeometry.TryGetValue(descriptor.Id, out var existing)) return existing;
            var indices = requiredPages(descriptor.Reference) ?? throw new AssetNotFoundException("Required Raster index page is not acquired.");
            var vertices = new Dictionary<int, MeshRasterPageData>();
            foreach (var dependency in product.GetVertexDependencies(page))
                vertices.Add(dependency, requiredPages(product.Pages[dependency].Reference)
                    ?? throw new AssetNotFoundException("Required Raster vertex page is not acquired."));
            var compact = StreamGeometryPage.Create(product, page, indices, vertices);
            rootGeometry.Add(descriptor.Id, compact); return compact;
        }

        int Page(MeshRasterPagesData product, int page)
        {
            var descriptor = product.Pages[page]; var id = descriptor.Id.Value.ToString("N");
            if (_pageIndices.TryGetValue(id, out var existing)) {
                var previous = _rasterPageUses[id];
                if (previous.Product.Pages[previous.Page] != descriptor || previous.Product.SourceHash != product.SourceHash)
                    throw new InvalidAssetException("Raster page identity has conflicting revisions.");
                return existing;
            }
            var decoded = RasterPreparationBytes(product, page);
            if (decoded > maximumPagePreparationBytes)
                throw new InvalidOperationException("One Raster page dependency closure exceeds its decoded preparation budget.");
            var number = pageTable.Count;
            if (number >= MeshVirtualPagesData.MaximumPages) throw new InvalidOperationException("Raster stream page table exceeds its limit.");
            var vertices = descriptor.ElementCount;
            pageTable.Add(new(id, checked(vertices * StreamGeometryPage.VertexBytes + descriptor.ElementCount * sizeof(uint)),
                vertices, descriptor.ElementCount / 3, decoded));
            _pageIndices.Add(id, number); _rasterPageUses.Add(id, new(product, page, decoded));
            return number;
        }

        foreach (var instance in scene.Instances.Span) {
            var geometry = scene.Geometry.Span[instance.Geometry]; var product = geometry.Product; var lods = geometry.Lods;
            if (Materials.Span[instance.Material].AlphaBlend) {
                var finest = lods[0];
                for (var page = finest.FirstPage; page < finest.FirstPage + finest.PageCount; page++) {
                    var descriptor = product.Pages[page];
                    if (!transparentPages.TryGetValue(descriptor.Id, out var slot)) {
                        slot = transparentGeometry.Count; transparentPages.Add(descriptor.Id, slot);
                        var compact = Required(product, page); transparentGeometry.Add(new(compact, 0, compact.TriangleCount));
                    }
                    if (transparent.Count == PbrSceneData.MaximumInstances)
                        throw new InvalidOperationException("Transparent Raster page expansion exceeds its instance limit.");
                    transparent.Add(instance with { Geometry = slot });
                }
                continue;
            }
            if (!geometries.TryGetValue(instance.Geometry, out var hierarchy)) {
                hierarchy = hierarchies.Count; geometries.Add(instance.Geometry, hierarchy);
                var nodes = new MeshVirtualPageNode[lods.Length]; var parts = new List<PagePart>();
                for (var node = 0; node < nodes.Length; node++) {
                    var lod = lods[lods.Length - node - 1]; var first = parts.Count;
                    for (var page = lod.FirstPage; page < lod.FirstPage + lod.PageCount; page++) {
                        var number = Page(product, page); parts.Add(new(number, 0, product.Pages[page].ElementCount / 3));
                        if (node == 0) {
                            var root = Required(product, page);
                            ResidentRoots.TryAdd(pageTable[number].Id, root);
                            pageTable[number] = pageTable[number] with { Bytes = root.UploadBytes, Vertices = root.VertexCount };
                        }
                    }
                    nodes[node] = new(geometry.Bounds, lod.Error, node - 1, node + 1, node + 1 < nodes.Length ? 1 : 0,
                        first, lod.PageCount, lod.IndexCount / 3);
                }
                hierarchies.Add(new(1, nodes, parts.ToArray()));
            }
            opaque.Add(new(instance.Transform, instance.Material) {
                AssetIndex = hierarchy, AuthoredId = instance.AuthoredId, MaterialSlot = instance.MaterialSlot });
        }
        if (opaque.Count + (long)transparent.Count > PbrSceneData.MaximumInstances)
            throw new InvalidOperationException("Raster stream instance table exceeds its limit.");
        Hierarchies = hierarchies.ToArray(); Instances = opaque.ToArray(); Pages = pageTable.ToArray();
        TransparentGeometry = transparentGeometry.ToArray(); TransparentInstances = transparent.ToArray();
    }

    private static long RasterPageBytes(MeshRasterPagesData product, in MeshRasterPageDescriptor page)
        => page.Kind == MeshRasterPageKind.Indices ? (long)page.ElementCount * sizeof(uint)
            : (long)page.ElementCount * (Unsafe.SizeOf<MeshVertex>() + (product.HasBakeUvs ? Unsafe.SizeOf<float2>() : 0));

    private static long CompactPageBytes(MeshRasterPagesData product, int indices)
        => (long)indices * (Unsafe.SizeOf<MeshVertex>() + (product.HasBakeUvs ? Unsafe.SizeOf<float2>() : 0) + sizeof(uint));

    private static int RasterPreparationBytes(MeshRasterPagesData product, int page)
    {
        var descriptor = product.Pages[page];
        // Output arrays and bounded dictionary remap scratch coexist during owner-context acquisition.
        var bytes = checked(RasterPageBytes(product, descriptor) + CompactPageBytes(product, descriptor.ElementCount)
            + (long)descriptor.ElementCount * 32);
        foreach (var dependency in product.GetVertexDependencies(page)) bytes = checked(bytes + RasterPageBytes(product, product.Pages[dependency]));
        return checked((int)bytes);
    }

    private static long WorkerPreparationBytes(RasterPageUse use)
        => checked((long)use.PreparationBytes + CompactPageBytes(use.Product, use.Product.Pages[use.Page].ElementCount));

    internal async Task<PreparedGeometryPage> ReadPreparedPageAsync(string id, CancellationToken token, bool compactRaster = true)
    {
        token.ThrowIfCancellationRequested();
        if (!_rasterPageUses.TryGetValue(id, out var use)) {
            var record = await ReadPageAsync(id, token).ConfigureAwait(false);
            return new(record, null, checked((int)record.Data.PayloadBytes));
        }
        var dependencies = use.Product.GetVertexDependencies(use.Page).ToArray();
        var records = new MeshRasterPageRecord[dependencies.Length + 1];
        records[0] = await _prepareRaster!(use.Product.Pages[use.Page].Reference, token).ConfigureAwait(false);
        use.Product.ValidatePage(use.Page, records[0].Data, token);
        for (var i = 0; i < dependencies.Length; i++) {
            token.ThrowIfCancellationRequested();
            records[i + 1] = await _prepareRaster!(use.Product.Pages[dependencies[i]].Reference, token).ConfigureAwait(false);
            use.Product.ValidatePage(dependencies[i], records[i + 1].Data, token);
        }
        token.ThrowIfCancellationRequested();
        if (!compactRaster) return new(null, records, use.PreparationBytes);
        var vertices = new Dictionary<int, MeshRasterPageData>(dependencies.Length);
        for (var i = 0; i < dependencies.Length; i++) vertices.Add(dependencies[i], records[i + 1].Data);
        var geometry = StreamGeometryPage.Create(use.Product, use.Page, records[0].Data, vertices);
        token.ThrowIfCancellationRequested();
        return new(null, records, checked((int)WorkerPreparationBytes(use))) { RasterGeometry = (use.Product, use.Page, geometry) };
    }

    internal StreamGeometryPage UsePreparedPage(string id, PreparedGeometryPage prepared)
    {
        if (!_rasterPageUses.TryGetValue(id, out var use)) return UsePage(id, prepared.Virtual
            ?? throw new InvalidDataException("Virtual geometry preparation has another kind."));
        var dependencies = use.Product.GetVertexDependencies(use.Page);
        var records = prepared.Raster;
        if (records is null || records.Length != dependencies.Length + 1)
            throw new InvalidDataException("Raster geometry preparation has another dependency closure.");
        var indices = _acquireRaster!(use.Product.Pages[use.Page].Reference, records[0]);
        var sameData = ReferenceEquals(indices, records[0].Data);
        var vertices = new Dictionary<int, MeshRasterPageData>(dependencies.Length);
        for (var i = 0; i < dependencies.Length; i++) {
            var actual = _acquireRaster!(use.Product.Pages[dependencies[i]].Reference, records[i + 1]);
            sameData &= ReferenceEquals(actual, records[i + 1].Data);
            vertices.Add(dependencies[i], actual);
        }
        if (sameData && prepared.RasterGeometry is { } compact
            && ReferenceEquals(compact.Product, use.Product) && compact.Page == use.Page)
            return compact.Geometry;
        return StreamGeometryPage.Create(use.Product, use.Page, indices, vertices);
    }
}
