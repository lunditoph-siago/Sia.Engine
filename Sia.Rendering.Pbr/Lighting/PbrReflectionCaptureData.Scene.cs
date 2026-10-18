using Sia.Asset;
using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrReflectionCaptureData
{
    /// <summary>Checks acquired source/material revisions without loading any geometry representation or performing effects.</summary>
    public void ValidateScene(PbrSceneData scene,
        Func<AssetRefer<MeshManifestRecord>.Id, MeshManifest> meshes,
        Func<AssetRefer<PbrMaterialRecord>.Id, PbrMaterialData> materials,
        Func<AssetRefer<PbrTextureRecord>.Id, PbrTextureData> textures,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(materials); ArgumentNullException.ThrowIfNull(textures);
        var selected = scene.SelectInstances(_staticInstances, cancellationToken);
        var resolved = new Dictionary<AssetId, PbrMaterialAsset>();
        PbrMaterialAsset Material(PbrMaterialSlot slot)
        {
            if (!resolved.TryGetValue(slot.Material.Value, out var value)) {
                value = materials(slot.Material).ResolveTextures(textures); resolved.Add(slot.Material.Value, value);
            }
            return value;
        }
        var identity = PbrSceneTransport.ReferenceIdentity(selected, binding => meshes(binding.Mesh), Material, cancellationToken);
        if (Convert.ToHexString(identity) != SceneIdentity)
            throw new InvalidAssetException("Capture static source, transform or transport material domain is stale.");
    }

    public bool SameRevision(PbrReflectionCaptureData? other)
        => other is not null && (ReferenceEquals(this, other) || ProductId == other.ProductId && Scene == other.Scene
            && SceneIdentity == other.SceneIdentity && InputIdentity == other.InputIdentity
            && SourceEnvironment == other.SourceEnvironment && Environment == other.Environment
            && Position.Equals(other.Position) && Bounds.Equals(other.Bounds) && Settings == other.Settings
            && _staticInstances.AsSpan().SequenceEqual(other._staticInstances));
}
