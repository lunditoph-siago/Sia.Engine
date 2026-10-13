using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Debug;

internal static class DebugShaderSource
{
    private const string ResourceName = "Sia.Rendering.Debug.Shaders.debug_overlay.wgsl";
    private static readonly AssetId s_SourceId = new(new Guid("138f9c2c-3e5c-45cf-8c52-457e393f72b7"));

    internal static Entity Acquire(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.AcquireAddon<AssetLibrary>();
        AssetLibrary.RegisterAsset<WgslSourceResource, WgslSourceRecord>();
        return world.AcquireAsset(s_SourceId,
            () => EmbeddedLoader.Load<WgslSourceRecord>(ResourceName, typeof(DebugShaderSource).Assembly));
    }
}
