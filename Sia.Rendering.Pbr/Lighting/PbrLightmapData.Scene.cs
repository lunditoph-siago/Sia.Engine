using Sia.Asset;
using Sia.Engine.Mesh;
using MeshReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshManifestRecord>.Id;
using MaterialReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrMaterialRecord>.Id;
using TextureReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapData
{
    /// <summary>
    /// Checks the exact static transport domain using caller lookups of already acquired values.
    /// Dynamic/unused instances, products and pages are not requested. No IO, acquisition or mutation.
    /// </summary>
    public void ValidateScene(PbrSceneData scene, Func<MeshReference, MeshManifest> meshes,
        Func<MaterialReference, PbrMaterialData> materials, Func<TextureReference, PbrTextureData> textures,
        CancellationToken cancellationToken = default)
        => PbrLightmapSceneValidation.Validate(SceneIdentity,_receivers,scene,meshes,materials,textures,cancellationToken);

    /// <summary>Checks the whole immutable revision, without assuming one logical ID replaces an already acquired value.</summary>
    public bool SameRevision(PbrLightmapData? other)
        => other is not null && (ReferenceEquals(this, other) || ProductId == other.ProductId
            && SceneIdentity == other.SceneIdentity && BakeIdentity == other.BakeIdentity
            && BakeSettings.SameInputs(other.BakeSettings)
            && _receivers.AsSpan().SequenceEqual(other._receivers) && _charts.AsSpan().SequenceEqual(other._charts)
            && _pages.AsSpan().SequenceEqual(other._pages));
}

// One pure static-domain check shared by an individual product and the scene-sized entry.
internal static class PbrLightmapSceneValidation
{
    internal static void Validate(string sceneIdentity,ReadOnlySpan<PbrLightmapReceiver> receivers,PbrSceneData scene,
        Func<MeshReference,MeshManifest> meshes,Func<MaterialReference,PbrMaterialData> materials,
        Func<TextureReference,PbrTextureData> textures,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(materials); ArgumentNullException.ThrowIfNull(textures);
        cancellationToken.ThrowIfCancellationRequested();
        var ids = new HashSet<Guid>(); foreach (var receiver in receivers) ids.Add(receiver.InstanceId);
        var selected = scene.SelectInstances(ids.ToArray(), cancellationToken);
        var instances = new Dictionary<Guid, PbrMeshInstance>(selected.Instances.Length);
        foreach (var instance in selected.Instances) instances.Add(instance.Id, instance);
        var meshValues = new Dictionary<AssetId, MeshManifest>();
        var materialValues = new Dictionary<AssetId, PbrMaterialAsset>();
        var textureValues = new Dictionary<AssetId, PbrTextureData>();
        MeshManifest Mesh(PbrMeshBinding binding)
        {
            if (!meshValues.TryGetValue(binding.Mesh.Value, out var mesh)) {
                mesh = meshes(binding.Mesh) ?? throw new AssetNotFoundException("Static Lightmap Mesh is not acquired.");
                meshValues.Add(binding.Mesh.Value, mesh);
            }
            return mesh;
        }
        PbrTextureData Texture(TextureReference reference)
        {
            if (!textureValues.TryGetValue(reference.Value, out var texture)) {
                texture = textures(reference) ?? throw new AssetNotFoundException("Static Lightmap texture is not acquired.");
                textureValues.Add(reference.Value, texture);
            }
            return texture;
        }
        PbrMaterialAsset Material(PbrMaterialSlot slot)
        {
            if (!materialValues.TryGetValue(slot.Material.Value, out var material)) {
                var value = materials(slot.Material) ?? throw new AssetNotFoundException("Static Lightmap material is not acquired.");
                material = value.ResolveTextures(Texture); materialValues.Add(slot.Material.Value, material);
            }
            return material;
        }
        for (var at = 0; at < receivers.Length; at++) {
            cancellationToken.ThrowIfCancellationRequested(); var receiver = receivers[at];
            var instance = instances[receiver.InstanceId]; var binding = selected.Bindings[instance.Binding];
            _ = binding.GetMaterial(receiver.MaterialSlot); var mesh=Mesh(binding);
            if (!mesh.HasBakeUvs || mesh.SourceHash!=receiver.SourceHash || !mesh.MaterialSlots.Contains(receiver.MaterialSlot))
                throw new InvalidDataException("Lightmap receiver differs from its canonical source/material binding.");
        }
        var identity = PbrSceneTransport.ReferenceIdentity(selected, Mesh, Material, cancellationToken);
        if (Convert.ToHexString(identity) != sceneIdentity)
            throw new InvalidAssetException("Lightmap static source, transform or transport material domain is stale.");
    }

}
