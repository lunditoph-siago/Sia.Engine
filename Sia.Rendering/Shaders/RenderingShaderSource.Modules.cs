using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering;

public static partial class RenderingShaderSource
{
    // Authored builtin identities: preserve each ID when source contents change.
    private static readonly IReadOnlyDictionary<string, AssetId> s_Modules = new Dictionary<string, AssetId>(StringComparer.Ordinal) {
        ["rendering/Baking/environment"] = new(new Guid("390f68df-4e39-404b-a81b-07383c682fae")),
        ["rendering/Baking/probes"] = new(new Guid("445e4db4-4745-4ae7-806e-82ef61fc1b70")),
        ["rendering/Common/color"] = new(new Guid("9868ef62-5f0e-420c-9a41-1c2450775a72")),
        ["rendering/Environment/ibl"] = new(new Guid("e454b656-d19a-4bd9-b16b-58cb83eb1f36")),
        ["rendering/Geometry/projected_error"] = new(new Guid("62672b08-c61a-46e4-9a7d-e1fd97275230")),
        ["rendering/Geometry/instance_trace"] = new(new Guid("f1549fb8-8e04-4181-b56b-9530f0e8e6eb")),
        ["rendering/Lighting/local_light"] = new(new Guid("2fda0e7f-8535-4185-a86b-3da38e7e6880")),
        ["rendering/Lighting/probe_basis"] = new(new Guid("15b04684-7f40-4acd-8f87-2c6168134fe5")),
        ["rendering/Lighting/probe_volume"] = new(new Guid("250e0a75-9830-4696-819f-32473a370817")),
        ["rendering/Lighting/shadow_map"] = new(new Guid("541dbf79-83b0-45ca-8cb0-f60075fe9476")),
        ["rendering/Visibility/surface_data"] = new(new Guid("a6273ad9-37b0-4fd0-9f14-3c51c89d7244")),
    };

    public static Entity? AcquireModule(World world, string module)
    {
        ArgumentNullException.ThrowIfNull(world); ArgumentNullException.ThrowIfNull(module);
        if (!s_Modules.TryGetValue(module, out var id)) return null;
        world.AcquireAddon<AssetLibrary>();
        AssetLibrary.RegisterAsset<WgslSourceResource, WgslSourceRecord>();
        var resource = "Sia.Rendering.Shaders." + module[10..].Replace('/', '.') + ".wgsl";
        return world.AcquireAsset(id, () => EmbeddedLoader.Load<WgslSourceRecord>(resource, typeof(RenderingShaderSource).Assembly));
    }
}
