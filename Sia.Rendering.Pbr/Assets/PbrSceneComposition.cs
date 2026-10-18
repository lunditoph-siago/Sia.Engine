using Sia.Asset;
using Sia.Engine.Mesh;
using MeshReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshManifestRecord>.Id;
using VirtualReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshVirtualRecord>.Id;
using RasterReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshRasterRecord>.Id;
using MaterialReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrMaterialRecord>.Id;
using TextureReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Explicit render-input composition from caller lookups of acquired data, without IO or lifetime mutation.</summary>
public static partial class PbrSceneComposition
{
    /// <summary>
    /// Selects Virtual indexes explicitly. Source/Raster/BVH and page payloads are not needed.
    /// The caller retains selected assets while the returned transient render input is in use.
    /// </summary>
    public static PbrVirtualScene ResolveVirtual(PbrSceneData scene, Func<MeshReference, MeshManifest> meshes,
        Func<VirtualReference, MeshVirtualPagesData> virtuals, Func<MaterialReference, PbrMaterialData> materials,
        Func<TextureReference, PbrTextureData> textures, out Guid[] sourceInstances,
        int maximumExpandedInstances = PbrSceneData.MaximumInstances, CancellationToken cancellationToken = default)
        => Resolve<MeshVirtualRecord, MeshVirtualPagesData, PbrVirtualGeometry, PbrVirtualScene>(scene, meshes, virtuals, materials, textures,
            out sourceInstances, static m => m.Virtual, Validate,
            static d => d.Parts.Length, static (d, p) => new(d, p), static (d, p) => d.Parts[p].MaterialSlot,
            static (g, m, i, a, identity) => new(g, m, i, a, identity), maximumExpandedInstances, cancellationToken);

    /// <summary>Selects Raster explicitly. No Source/Virtual/BVH or geometry cooker is involved.</summary>
    public static PbrRasterScene ResolveRaster(PbrSceneData scene, Func<MeshReference, MeshManifest> meshes,
        Func<RasterReference, MeshRasterData> rasters, Func<MaterialReference, PbrMaterialData> materials,
        Func<TextureReference, PbrTextureData> textures, out Guid[] sourceInstances,
        int maximumExpandedInstances = PbrSceneData.MaximumInstances, CancellationToken cancellationToken = default)
        => Resolve<MeshRasterRecord, MeshRasterData, PbrRasterGeometry, PbrRasterScene>(scene, meshes, rasters, materials, textures,
            out sourceInstances, static m => m.Raster, ValidateRaster,
            static d => d.Sections.Length, static (d, p) => new(d, p), static (d, p) => d.Sections[p].MaterialSlot,
            PbrRasterScene.AdoptResolved, maximumExpandedInstances, cancellationToken);

    private static TScene Resolve<TRecord, TProduct, TGeometry, TScene>(PbrSceneData scene,
        Func<MeshReference, MeshManifest> meshes, Func<AssetRefer<TRecord>.Id, TProduct> products,
        Func<MaterialReference, PbrMaterialData> materials, Func<TextureReference, PbrTextureData> textures,
        out Guid[] sourceInstances, Func<MeshManifest, MeshProductReference<TRecord>?> select,
        Action<MeshManifest, PbrMeshBinding, AssetId, string, TProduct> validate,
        Func<TProduct, int> partCount, Func<TProduct, int, TGeometry> geometryAt, Func<TProduct, int, int> materialSlot,
        Func<TGeometry[], PbrMaterialAsset[], PbrSceneInstance[], string, byte[], TScene> createScene,
        int maximumExpandedInstances, CancellationToken cancellationToken)
        where TRecord : class, IAssetRecord where TProduct : class
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(meshes); ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(materials); ArgumentNullException.ThrowIfNull(textures);
        if (maximumExpandedInstances is < 0 or > PbrSceneData.MaximumInstances) throw new ArgumentOutOfRangeException(nameof(maximumExpandedInstances));
        cancellationToken.ThrowIfCancellationRequested();
        var meshValues = new Dictionary<AssetId, MeshManifest>(); var productValues = new Dictionary<AssetId, TProduct>();
        var textureValues = new Dictionary<AssetId, PbrTextureData>(); var materialIndices = new Dictionary<AssetId, int>();
        var geometryIndices = new Dictionary<(AssetId Product, int Part), int>();
        var geometry = new List<TGeometry>(); var shading = new List<PbrMaterialAsset>();
        var uses = new int[scene.Bindings.Length]; foreach (var instance in scene.Instances) uses[instance.Binding]++;
        var resolved = new (int Geometry, int Material)[scene.Bindings.Length][];
        long expanded = 0;

        PbrTextureData Texture(TextureReference reference)
        {
            if (!textureValues.TryGetValue(reference.Value, out var data)) {
                RequireRoom(textureValues.Count, "texture");
                data = textures(reference) ?? throw new AssetNotFoundException("Scene texture is not acquired.");
                textureValues.Add(reference.Value, data);
            }
            return data;
        }
        int Material(MaterialReference reference)
        {
            if (!materialIndices.TryGetValue(reference.Value, out var index)) {
                RequireRoom(shading.Count, "material");
                var data = materials(reference) ?? throw new AssetNotFoundException("Scene material is not acquired.");
                var value = data.ResolveTextures(Texture);
                index = shading.Count; shading.Add(value); materialIndices.Add(reference.Value, index);
            }
            return index;
        }

        for (var i = 0; i < resolved.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested(); if (uses[i] == 0) continue;
            var binding = scene.Bindings[i];
            if (!meshValues.TryGetValue(binding.Mesh.Value, out var manifest)) {
                manifest = meshes(binding.Mesh) ?? throw new AssetNotFoundException("Scene Mesh inventory is not acquired.");
                meshValues.Add(binding.Mesh.Value, manifest);
            }
            var product = select(manifest) ?? throw new InvalidAssetException("Selected scene Mesh product is absent.");
            if (!productValues.TryGetValue(product.Id, out var data)) {
                data = products(new(product.Id)) ?? throw new AssetNotFoundException("Selected scene Mesh product is not acquired.");
                productValues.Add(product.Id, data);
            }
            validate(manifest, binding, product.Id, product.BuildKey, data);
            var count = partCount(data);
            expanded += (long)uses[i] * count;
            if (expanded > maximumExpandedInstances) throw new InvalidOperationException("Expanded scene instance limit exceeded.");
            var parts = new (int Geometry, int Material)[count];
            for (var part = 0; part < parts.Length; part++) {
                cancellationToken.ThrowIfCancellationRequested();
                var key = (product.Id, part);
                if (!geometryIndices.TryGetValue(key, out var index)) {
                    RequireRoom(geometry.Count, "geometry");
                    index = geometry.Count; geometry.Add(geometryAt(data, part)); geometryIndices.Add(key, index);
                }
                parts[part] = (index, Material(binding.GetMaterial(materialSlot(data, part))));
            }
            resolved[i] = parts;
        }

        var instances = new PbrSceneInstance[(int)expanded]; var ids = new Guid[(int)expanded]; var at = 0;
        foreach (var instance in scene.Instances) {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var part in resolved[instance.Binding]) {
                instances[at] = new(part.Geometry, part.Material, instance.Transform); ids[at++] = instance.Id;
            }
        }
        var identity = PbrSceneTransport.ReferenceIdentity(scene, b => meshValues[b.Mesh.Value],
            s => shading[materialIndices[s.Material.Value]], cancellationToken);
        var result = createScene(geometry.ToArray(), shading.ToArray(), instances, scene.Attribution, identity);
        sourceInstances = ids; return result;
    }

    private static void RequireRoom(int count, string kind)
    {
        if (count >= 4096) throw new InvalidOperationException($"Resolved scene {kind} table limit exceeded.");
    }

    private static void Validate(MeshManifest manifest, PbrMeshBinding binding, AssetId id, string key, MeshVirtualPagesData data)
    {
        if (data.ProductId != id || data.BuildKey != key || data.SourceHash != manifest.SourceHash || data.SourceVertexCount != manifest.VertexCount
            || data.SourceTriangleCount != manifest.TriangleCount || data.HasBakeUvs != manifest.HasBakeUvs || !data.Bounds.Equals(manifest.Bounds))
            throw new InvalidAssetException("Virtual product does not match its Mesh inventory.");
        ValidateSlots(manifest, binding);
        var slots = new HashSet<int>();
        foreach (var part in data.Parts) {
            if (manifest.MaterialSlots.BinarySearch(part.MaterialSlot) < 0) throw new InvalidAssetException("Virtual product contains an unknown material slot.");
            slots.Add(part.MaterialSlot);
        }
        if (slots.Count != manifest.MaterialSlots.Length) throw new InvalidAssetException("Virtual product omits a Mesh material slot.");
    }

    private static void ValidateRaster(MeshManifest manifest, PbrMeshBinding binding, AssetId id, string key, MeshRasterData data)
    {
        if (data.BuildKey != key || data.SourceHash != manifest.SourceHash || data.Vertices.Length != manifest.VertexCount
            || data.Indices.Length / 3 != manifest.TriangleCount || data.HasBakeUvs != manifest.HasBakeUvs || !data.Bounds.Equals(manifest.Bounds))
            throw new InvalidAssetException("Raster product does not match its Mesh inventory.");
        ValidateSlots(manifest, binding);
        var slots = new HashSet<int>();
        foreach (var section in data.Sections) {
            if (manifest.MaterialSlots.BinarySearch(section.MaterialSlot) < 0) throw new InvalidAssetException("Raster contains an unknown material slot.");
            slots.Add(section.MaterialSlot);
        }
        if (slots.Count != manifest.MaterialSlots.Length) throw new InvalidAssetException("Raster omits a Mesh material slot.");
    }

    private static void ValidateSlots(MeshManifest manifest, PbrMeshBinding binding)
    {
        if (binding.Materials.Length != manifest.MaterialSlots.Length) throw new InvalidAssetException("Scene material slots differ from its Mesh inventory.");
        for (var i = 0; i < binding.Materials.Length; i++)
            if (binding.Materials[i].Slot != manifest.MaterialSlots[i]) throw new InvalidAssetException("Scene material slots differ from its Mesh inventory.");
    }
}
