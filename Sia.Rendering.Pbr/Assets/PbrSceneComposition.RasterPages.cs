using Sia.Asset;
using Sia.Engine.Mesh;
using MeshReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshManifestRecord>.Id;
using RasterPagesReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshRasterPagesRecord>.Id;
using MaterialReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrMaterialRecord>.Id;
using TextureReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneComposition
{
    /// <summary>
    /// Resolves complete exact Raster page metadata without reading Source, whole Raster, Virtual, BVH or geometry pages.
    /// All authored actors and sections retain their original expansion order. The caller retains the acquired index/material assets.
    /// </summary>
    public static PbrRasterPagesScene ResolveRasterPages(PbrSceneData scene, Func<MeshReference, MeshManifest> meshes,
        Func<RasterPagesReference, MeshRasterPagesData> rasterPages, Func<MaterialReference, PbrMaterialData> materials,
        Func<TextureReference, PbrTextureData> textures,
        int maximumExpandedInstances = PbrSceneData.MaximumInstances, CancellationToken cancellationToken = default)
        => Resolve<MeshRasterPagesRecord, MeshRasterPagesData, PbrRasterPagesGeometry, PbrRasterPagesScene>(scene,
            meshes, rasterPages, materials, textures, static m => m.RasterPages,
            (manifest, binding, id, key, data) => ValidateRasterPages(manifest, binding, id, key, data, cancellationToken),
            static data => data.Sections.Length, static (data, section) => new(data, section),
            static (data, section) => data.Sections[section].MaterialSlot,
            (geometry, shading, instances, attribution, identity) => new(geometry, shading, instances, attribution, identity, cancellationToken),
            maximumExpandedInstances, cancellationToken);

    private static void ValidateRasterPages(MeshManifest manifest, PbrMeshBinding binding, AssetId id, string key,
        MeshRasterPagesData data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.ProductId != id || data.BuildKey != key || data.SourceHash != manifest.SourceHash
            || data.SourceVertexCount != manifest.VertexCount || data.SourceTriangleCount != manifest.TriangleCount
            || data.HasBakeUvs != manifest.HasBakeUvs || !data.Bounds.Equals(manifest.Bounds)
            || manifest.Raster is { } raster && (data.Raster.Id != raster.Id || data.Raster.BuildKey != raster.BuildKey))
            throw new InvalidAssetException("Raster page index does not match its Mesh inventory or selected Raster revision.");
        ValidateSlots(manifest, binding);
        var slots = new HashSet<int>();
        foreach (var section in data.Sections) {
            cancellationToken.ThrowIfCancellationRequested();
            if (manifest.MaterialSlots.BinarySearch(section.MaterialSlot) < 0)
                throw new InvalidAssetException("Raster page index contains an unknown material slot.");
            slots.Add(section.MaterialSlot);
        }
        if (slots.Count != manifest.MaterialSlots.Length)
            throw new InvalidAssetException("Raster page index omits a Mesh material slot.");
    }
}
