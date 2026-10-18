using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    internal static Task<Prepared<MeshBvhRecord>> PrepareBvhAsync(string path, HttpClient client,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default)
        => PrepareAsync(path, client, static m => m.Bvh?.Id ?? throw new InvalidDataException("Selected Mesh has no BVH product."),
            static (MeshBvhRecord _) => 1, MeshBvhRecord.Load,
            maximumFileBytes, maximumTotalBytes, cancellationToken);

    internal static (Entity Root, PbrBvhScene Scene) AcquireBvh(World world, Prepared<MeshBvhRecord> prepared)
    {
        AssetLibrary.RegisterAsset<MeshBvhAsset, MeshBvhRecord>();
        return Acquire<MeshBvhRecord, PbrBvhScene>(world, prepared, static (w, s) => {
            var scene = PbrSceneComposition.ResolveBvh(s,
                r => r.Find(w)?.Get<MeshManifestAsset>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<MeshBvhAsset>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<PbrMaterialResource>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<PbrTextureAsset>().Data ?? throw new AssetNotFoundException());
            return scene;
        });
    }
}
