using Sia.Asset;
using Sia.Engine.Mesh;
using MeshReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshManifestRecord>.Id;
using VirtualReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshVirtualRecord>.Id;
using RasterReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshRasterRecord>.Id;
using MaterialReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrMaterialRecord>.Id;
using TextureReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public enum PbrMeshPresentation { Raster, Virtual }

public static partial class PbrSceneComposition
{
    /// <summary>
    /// Partitions authored instances by the caller's explicit presentation choice. Products remain
    /// independently acquired and shared. Transparent content requires Raster; no fallback or cook
    /// occurs. The returned inputs borrow asset data and retain authored IDs and section slots.
    /// </summary>
    public static PbrMixedScene ResolveMixed(PbrSceneData scene,
        Func<PbrMeshInstance, PbrMeshPresentation> select,
        Func<MeshReference, MeshManifest> meshes, Func<RasterReference, MeshRasterData> rasters,
        Func<VirtualReference, MeshVirtualPagesData> virtuals,
        Func<MaterialReference, PbrMaterialData> materials, Func<TextureReference, PbrTextureData> textures,
        int maximumExpandedInstances = PbrSceneData.MaximumInstances, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(meshes); ArgumentNullException.ThrowIfNull(rasters); ArgumentNullException.ThrowIfNull(virtuals);
        ArgumentNullException.ThrowIfNull(materials); ArgumentNullException.ThrowIfNull(textures);
        if (maximumExpandedInstances is < 0 or > PbrSceneData.MaximumInstances)
            throw new ArgumentOutOfRangeException(nameof(maximumExpandedInstances));
        cancellationToken.ThrowIfCancellationRequested();
        var rasterInstances = new List<PbrMeshInstance>(); var virtualInstances = new List<PbrMeshInstance>();
        foreach (var instance in scene.Instances) {
            cancellationToken.ThrowIfCancellationRequested();
            switch (select(instance)) {
                case PbrMeshPresentation.Raster: rasterInstances.Add(instance); break;
                case PbrMeshPresentation.Virtual: virtualInstances.Add(instance); break;
                default: throw new ArgumentException("An instance selected an unknown Mesh presentation.", nameof(select));
            }
        }
        var meshValues = new Dictionary<AssetId, MeshManifest>();
        var materialValues = new Dictionary<AssetId, PbrMaterialData>(); var textureValues = new Dictionary<AssetId, PbrTextureData>();
        MeshManifest Mesh(MeshReference reference) => Lookup(reference.Value, meshValues, () => meshes(reference), "Mesh inventory");
        PbrMaterialData Material(MaterialReference reference) => Lookup(reference.Value, materialValues, () => materials(reference), "material");
        PbrTextureData Texture(TextureReference reference) => Lookup(reference.Value, textureValues, () => textures(reference), "texture");
        var checkedBindings = new HashSet<int>();
        foreach (var instance in virtualInstances) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!checkedBindings.Add(instance.Binding)) continue;
            foreach (var slot in scene.Bindings[instance.Binding].Materials)
                if (Material(slot.Material).AlphaBlend)
                    throw new InvalidAssetException("Transparent Mesh instances must explicitly select Raster.");
        }
        var rasterData = PbrSceneData.Create(scene.Bindings, rasterInstances.ToArray(), scene.Attribution);
        var virtualData = PbrSceneData.Create(scene.Bindings, virtualInstances.ToArray(), scene.Attribution);
        var raster = ResolveRaster(rasterData, Mesh, rasters, Material, Texture, maximumExpandedInstances, cancellationToken);
        var remaining = maximumExpandedInstances - raster.Instances.Length;
        var virtualScene = ResolveVirtual(virtualData, Mesh, virtuals, Material, Texture, remaining, cancellationToken);
        var shading = new Dictionary<AssetId, PbrMaterialAsset>();
        var identity = PbrSceneTransport.ReferenceIdentity(scene, b => Mesh(b.Mesh),
            slot => Lookup(slot.Material.Value, shading, () => Material(slot.Material).ResolveTextures(Texture), "material"), cancellationToken);
        return new(raster, virtualScene, identity);
    }

    private static TValue Lookup<TValue>(AssetId id, Dictionary<AssetId, TValue> cache, Func<TValue> lookup, string kind)
        where TValue : class
    {
        if (cache.TryGetValue(id, out var value)) return value;
        RequireRoom(cache.Count, kind);
        value = lookup() ?? throw new AssetNotFoundException($"Scene {kind} is not acquired.");
        cache.Add(id, value); return value;
    }
}
