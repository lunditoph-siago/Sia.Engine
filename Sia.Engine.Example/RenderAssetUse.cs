using Sia;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

// One application render use owns its selected inputs; the optional scene is metadata only.
internal static partial class RenderAssetUse
{
    [SiaAsset]
    [SiaTemplate("Owner")]
    public partial record Record : AssetRecord
    {
        public AssetRefer<PbrSceneRecord>.Id? Scene { get; init; }
    }

    internal static Entity Create(World world, AssetRefer<PbrSceneRecord>.Id? scene = null)
    {
        AssetLibrary.RegisterAsset<Owner, Record>();
        return world.CreateAsset(new Record { Scene = scene }, AssetLife.Persistent);
    }
}
