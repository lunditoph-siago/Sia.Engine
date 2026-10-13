using Sia.Asset;
using Sia.Graphics.Text;

namespace Sia.UI.Example;

// Example resource selection and original acquisition; layout/atlas derivation stay in UI.
internal static class ShowcaseFont
{
    internal const string ResourceName = "Sia.UI.Example.NotoSans-UiSubset.ttf";
    private static readonly AssetId s_Id = new(new Guid("0bd08404-300d-4033-9205-e15fa7d1cd84"));

    internal static Entity Acquire(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.AcquireAddon<AssetLibrary>();
        AssetLibrary.RegisterAsset<UiFontResource, UiFontRecord>();
        return world.AcquireAsset(s_Id, () => EmbeddedLoader.Load<UiFontRecord>(ResourceName, typeof(ShowcaseFont).Assembly));
    }

    internal static Font Load(World world) => Acquire(world).Get<UiFontResource>().Data.Font;
}
