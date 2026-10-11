using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;

namespace Sia.Engine.Example;

internal sealed unsafe partial class SceneExampleApp
{
    // Procedural authoring is composed here; asset acquisition does not choose or build products.
    private AssetRefer<MeshManifestRecord> CreateRasterMesh(MeshData geometry)
    {
        var world = _renderWorld!.Entities;
        var source = MeshSource.Create(geometry);
        var sourceId = AssetId.New();
        var input = world.AcquireAsset(sourceId, () => new MeshSourceRecord { Source = source });
        Entity output = default;
        Entity inventory = default;
        var transferred = false;
        try {
            var raster = MeshRasterData.Build(input.Get<MeshSourceAsset>().Source);
            var id = AssetId.New();
            output = world.AcquireAsset(id, () => new MeshRasterRecord { Data = raster });
            var manifest = MeshManifest.Create(sourceId, source, new(id, raster.BuildKey));
            var manifestId = AssetId.New();
            inventory = world.AcquireAsset(manifestId, () => new MeshManifestRecord { Data = manifest });
            transferred = true;
            return new AssetRefer<MeshManifestRecord>.Id(manifestId);
        }
        finally {
            if (!transferred) {
                if (inventory.IsValid) inventory.Destroy();
                if (output.IsValid) output.Destroy();
            }
            input.Destroy();
        }
    }
}
