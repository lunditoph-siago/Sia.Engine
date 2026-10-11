using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Unlit;

public static class UnlitShaderSource
{
    private const string ResourceName = "Sia.Rendering.Unlit.Shaders.unlit.wgsl";
    private static readonly AssetId s_SourceId = new(new Guid("c3f2bb8e-e5da-46a3-bc60-bb74d69abc62"));

    public static ShaderAsset Load()
        => new(new ShaderAssetId("sia:unlit"), EmbeddedLoader.Load<WgslSourceRecord>(ResourceName, typeof(UnlitShaderSource).Assembly).Source);

    // CPU source identity is independent of compiled/GPU pipeline identity and revision.
    public static Entity Acquire(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.AcquireAddon<AssetLibrary>();
        AssetLibrary.RegisterAsset<WgslSourceResource, WgslSourceRecord>();
        return world.AcquireAsset(s_SourceId,
            () => EmbeddedLoader.Load<WgslSourceRecord>(ResourceName, typeof(UnlitShaderSource).Assembly));
    }
}
