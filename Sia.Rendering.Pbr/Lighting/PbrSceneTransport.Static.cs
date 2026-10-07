using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    /// <summary>Static instances and their referenced geometry/materials, independent of dynamic-only assets and table offsets.</summary>
    public static byte[] StaticIdentity(PbrSceneAsset scene, bool finest = false)
    {
        var hash = new SceneIdentityHash();
        hash.AppendData("SIASTATIC1"u8);
        hash.AppendData(Identity(StaticSource(scene), finest));
        return hash.Finish();
    }

    private static PbrSceneAsset StaticSource(PbrSceneAsset scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var geometry = new List<MeshPatchAsset>();
        var materials = new List<PbrMaterialAsset>();
        var instances = new List<PbrSceneInstance>();
        var geometryMap = new Dictionary<int, int>();
        var materialMap = new Dictionary<int, int>();
        foreach (var instance in scene.Instances.Span) {
            if (instance.Dynamic) continue;
            if (!geometryMap.TryGetValue(instance.Geometry, out var g)) {
                geometryMap.Add(instance.Geometry, g = geometry.Count);
                geometry.Add(scene.Geometry.Span[instance.Geometry]);
            }
            if (!materialMap.TryGetValue(instance.Material, out var m)) {
                materialMap.Add(instance.Material, m = materials.Count);
                materials.Add(scene.Materials.Span[instance.Material]);
            }
            instances.Add(instance with { Geometry = g, Material = m });
        }
        // Values remain owned by the immutable source; only reference tables are remapped.
        return PbrSceneAsset.Create(geometry.ToArray(), materials.ToArray(), instances.ToArray());
    }
}
