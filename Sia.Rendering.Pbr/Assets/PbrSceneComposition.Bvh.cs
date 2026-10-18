using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Math;
using MeshReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshManifestRecord>.Id;
using BvhReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshBvhRecord>.Id;
using MaterialReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrMaterialRecord>.Id;
using TextureReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneComposition
{
    /// <summary>Selects acquired BVHs without expanding triangles/sections or requesting Source/Raster/Virtual.</summary>
    public static PbrBvhScene ResolveBvh(PbrSceneData scene, Func<MeshReference, MeshManifest> meshes,
        Func<BvhReference, MeshBvhData> bvhs, Func<MaterialReference, PbrMaterialData> materials,
        Func<TextureReference, PbrTextureData> textures, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(meshes); ArgumentNullException.ThrowIfNull(bvhs);
        ArgumentNullException.ThrowIfNull(materials); ArgumentNullException.ThrowIfNull(textures);
        cancellationToken.ThrowIfCancellationRequested();
        var meshValues = new Dictionary<AssetId, MeshManifest>(); var geometryIndices = new Dictionary<AssetId, int>();
        var validatedMeshes = new HashSet<AssetId>();
        var materialIndices = new Dictionary<AssetId, int>(); var textureValues = new Dictionary<AssetId, PbrTextureData>();
        var geometry = new List<MeshBvhData>(); var shading = new List<PbrMaterialAsset>(); var bindings = new List<PbrBvhBinding>();
        var materialIds = new List<AssetId>();
        var used = new bool[scene.Bindings.Length]; foreach (var instance in scene.Instances) used[instance.Binding] = true;
        var bindingIndices = new int[used.Length];

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
                index = shading.Count; shading.Add(value); materialIds.Add(reference.Value); materialIndices.Add(reference.Value, index);
            }
            return index;
        }

        for (var i = 0; i < used.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested(); if (!used[i]) continue;
            var binding = scene.Bindings[i];
            if (!meshValues.TryGetValue(binding.Mesh.Value, out var manifest)) {
                manifest = meshes(binding.Mesh) ?? throw new AssetNotFoundException("Scene Mesh inventory is not acquired.");
                meshValues.Add(binding.Mesh.Value, manifest);
            }
            var product = manifest.Bvh ?? throw new InvalidAssetException("Selected scene Mesh BVH product is absent.");
            if (!geometryIndices.TryGetValue(product.Id, out var geometryIndex)) {
                RequireRoom(geometry.Count, "geometry");
                var data = bvhs(new(product.Id)) ?? throw new AssetNotFoundException("Scene Mesh BVH is not acquired.");
                geometryIndex = geometry.Count; geometry.Add(data); geometryIndices.Add(product.Id, geometryIndex);
            }
            if (validatedMeshes.Add(binding.Mesh.Value)) ValidateBvh(manifest, product.BuildKey, geometry[geometryIndex], cancellationToken);
            ValidateSlots(manifest, binding);
            var slots = new PbrBvhMaterialSlot[binding.Materials.Length];
            for (var slot = 0; slot < slots.Length; slot++) {
                cancellationToken.ThrowIfCancellationRequested();
                var source = binding.Materials[slot]; slots[slot] = new(source.Slot, Material(source.Material));
            }
            var revision = PbrSceneTransport.ReferenceBindingIdentity(manifest.SourceHash, slots.Length,
                at => (slots[at].Slot, shading[slots[at].Material]), cancellationToken);
            bindingIndices[i] = bindings.Count; bindings.Add(new(geometryIndex, slots, manifest.SourceHash, revision));
        }
        var instances = new PbrMeshInstance[scene.Instances.Length];
        for (var i = 0; i < instances.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = scene.Instances[i]; instances[i] = instance with { Binding = bindingIndices[instance.Binding] };
        }
        var identity = PbrSceneTransport.ReferenceIdentity(instances, at => bindings[at].TransportRevision, cancellationToken);
        return new(geometry.ToArray(), shading.ToArray(), materialIds.ToArray(), bindings.ToArray(), instances, scene.Attribution, identity, cancellationToken);
    }

    private static void ValidateBvh(MeshManifest manifest, string key, MeshBvhData data, CancellationToken cancellationToken)
    {
        if (data.BuildKey != key || data.Positions.Length != manifest.VertexCount || data.Triangles.Length != manifest.TriangleCount
            || !math.all(data.Bounds.Min >= manifest.Bounds.Min) || !math.all(data.Bounds.Max <= manifest.Bounds.Max))
            throw new InvalidAssetException("BVH product does not match its Mesh inventory.");
        var slots = new HashSet<int>(); var at = 0;
        foreach (var triangle in data.Triangles) {
            if ((at++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (manifest.MaterialSlots.BinarySearch(triangle.MaterialSlot) < 0)
                throw new InvalidAssetException("BVH contains an unknown material slot.");
            slots.Add(triangle.MaterialSlot);
        }
        if (slots.Count != manifest.MaterialSlots.Length) throw new InvalidAssetException("BVH omits a Mesh material slot.");
    }
}
