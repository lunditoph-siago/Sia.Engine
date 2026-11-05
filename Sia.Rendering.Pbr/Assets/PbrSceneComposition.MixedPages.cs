using Sia.Asset;
using Sia.Engine.Mesh;
using MeshReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshManifestRecord>.Id;
using VirtualReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshVirtualRecord>.Id;
using RasterPagesReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshRasterPagesRecord>.Id;
using MaterialReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrMaterialRecord>.Id;
using TextureReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneComposition
{
    /// <summary>Partitions exact paged Raster and Virtual products without reading geometry leaves.</summary>
    /// <remarks>
    /// The caller explicitly selects each authored instance; transparent bindings must select Raster.
    /// Products retain their original source identity, section order, LOD errors and transforms.
    /// The result borrows acquired metadata and shading assets and owns no transport or Asset use.
    /// </remarks>
    public static PbrMixedScene ResolveMixedRasterPages(PbrSceneData scene,
        Func<PbrMeshInstance, PbrMeshPresentation> select,
        Func<MeshReference, MeshManifest> meshes, Func<RasterPagesReference, MeshRasterPagesData> rasterPages,
        Func<VirtualReference, MeshVirtualPagesData> virtuals,
        Func<MaterialReference, PbrMaterialData> materials, Func<TextureReference, PbrTextureData> textures,
        int maximumExpandedInstances = PbrSceneData.MaximumInstances, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(meshes); ArgumentNullException.ThrowIfNull(rasterPages); ArgumentNullException.ThrowIfNull(virtuals);
        ArgumentNullException.ThrowIfNull(materials); ArgumentNullException.ThrowIfNull(textures);
        if (maximumExpandedInstances is < 0 or > PbrSceneData.MaximumInstances)
            throw new ArgumentOutOfRangeException(nameof(maximumExpandedInstances));
        var conventional = new List<PbrMeshInstance>(); var virtualInstances = new List<PbrMeshInstance>();
        foreach (var instance in scene.Instances) {
            cancellationToken.ThrowIfCancellationRequested();
            switch (select(instance)) {
                case PbrMeshPresentation.Raster: conventional.Add(instance); break;
                case PbrMeshPresentation.Virtual: virtualInstances.Add(instance); break;
                default: throw new ArgumentException("An instance selected an unknown Mesh presentation.", nameof(select));
            }
        }
        var meshValues = new Dictionary<AssetId, MeshManifest>(); var materialValues = new Dictionary<AssetId, PbrMaterialData>();
        var textureValues = new Dictionary<AssetId, PbrTextureData>();
        MeshManifest Mesh(MeshReference r) => Lookup(r.Value, meshValues, () => meshes(r), "Mesh inventory");
        PbrMaterialData Material(MaterialReference r) => Lookup(r.Value, materialValues, () => materials(r), "material");
        PbrTextureData Texture(TextureReference r) => Lookup(r.Value, textureValues, () => textures(r), "texture");
        var checkedBindings = new HashSet<int>();
        foreach (var instance in virtualInstances) {
            if (!checkedBindings.Add(instance.Binding)) continue;
            foreach (var slot in scene.Bindings[instance.Binding].Materials)
                if (Material(slot.Material).AlphaBlend)
                    throw new InvalidAssetException("Transparent Mesh instances must explicitly select Raster.");
        }
        var raster = ResolveRasterPages(PbrSceneData.Create(scene.Bindings, conventional.ToArray(), scene.Attribution),
            Mesh, rasterPages, Material, Texture, maximumExpandedInstances, cancellationToken);
        var virtualScene = ResolveVirtual(PbrSceneData.Create(scene.Bindings, virtualInstances.ToArray(), scene.Attribution),
            Mesh, virtuals, Material, Texture, maximumExpandedInstances - raster.Instances.Length, cancellationToken);
        var shading = new Dictionary<AssetId, PbrMaterialAsset>();
        var identity = PbrSceneTransport.ReferenceIdentity(scene, binding => Mesh(binding.Mesh),
            slot => Lookup(slot.Material.Value, shading, () => Material(slot.Material).ResolveTextures(Texture), "material"), cancellationToken);
        return new(raster, virtualScene, identity);
    }
}
