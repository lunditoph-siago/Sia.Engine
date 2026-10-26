using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

internal static partial class PbrShaderSource
{
    // Authored builtin identities: preserve each ID when source contents change.
    private static readonly IReadOnlyDictionary<string, AssetId> s_Modules = new Dictionary<string, AssetId>(StringComparer.Ordinal) {
        ["pbr/bindings"] = new(new Guid("f983bf51-5a07-4139-a55e-d5b9da919b73")),
        ["pbr/clusters"] = new(new Guid("5becdfda-827d-46da-80fd-288a9630096c")),
        ["pbr/Common/reconstruction"] = new(new Guid("4da31a5a-a5ab-4b36-aabf-e21ffd978888")),
        ["pbr/Common/surface"] = new(new Guid("335e7358-4250-47e7-9a4f-bc0e5f112895")),
        ["pbr/Common/reflection_history"] = new(new Guid("8af9268e-48f8-4d78-a586-e6be93c6b0a1")),
        ["pbr/Common/reflection_storage"] = new(new Guid("489b2b0c-16a8-4d10-aeb9-216b186e26b3")),
        ["pbr/Common/triangle_debug"] = new(new Guid("b8741f75-bc35-4e3c-a04d-4676cea2ece8")),
        ["pbr/forward_background"] = new(new Guid("1ec9337d-cb88-4d1a-8c89-3e52c203efcc")),
        ["pbr/forward"] = new(new Guid("4582d499-cb95-4cf2-ad84-e0d48fe4b891")),
        ["pbr/frame_types"] = new(new Guid("47d38e36-31aa-4e8e-b24f-317ea5e854e9")),
        ["pbr/instance_types"] = new(new Guid("8121f956-3f0e-430e-a20b-717e495ac688")),
        ["pbr/lighting"] = new(new Guid("7b9aa285-19e1-49b6-859b-5486a02d9049")),
        ["pbr/output"] = new(new Guid("0a3c0407-65f6-4332-9175-b797dacc4328")),
        ["pbr/exposure"] = new(new Guid("961849e5-8c2e-421b-8dd8-bc00948a8d9c")),
        ["pbr/pbr_lighting"] = new(new Guid("c743ee26-8bd2-469a-8f61-3c8932f5a7c1")),
        ["pbr/Providers/environment"] = new(new Guid("7e9b975b-0b30-43cf-a112-09bce22cca0c")),
        ["pbr/Providers/reflection_capture"] = new(new Guid("a5e88471-1b54-4845-a815-63b9cf7d2b0f")),
        ["pbr/Providers/lightmap"] = new(new Guid("d7b83c5f-61f1-4f39-9c58-b3f5a1f144a7")),
        ["pbr/Providers/shadow_map"] = new(new Guid("17fc2a71-92d8-4737-ad2c-839ae6676fc1")),
        ["pbr/raster"] = new(new Guid("7cb391a1-28b1-4f5e-8bc4-712d441c90d7")),
        ["pbr/resolve"] = new(new Guid("25d98803-3531-43b2-86c1-d38bf39de11f")),
        ["pbr/reflection_trace"] = new(new Guid("dc624b0f-aeda-4487-a616-04b52894eb4b")),
        ["pbr/reflection_compose"] = new(new Guid("c124813b-41ae-4b25-bf3d-cc7f637bfd82")),
        ["pbr/reflection_history"] = new(new Guid("9ef9e76e-70cb-49e7-8d0b-672cad151e4d")),
        ["pbr/reflection_history_commit"] = new(new Guid("c0d0afed-5114-4d5c-bc50-9b5fd9b00fdc")),
        ["pbr/stream_expand"] = new(new Guid("78e148d3-800a-477d-aac5-6044a115d0d4")),
        ["pbr/stream_node_types"] = new(new Guid("ba71a779-7cbc-4017-aa83-c0d6947c61dc")),
        ["pbr/stream_selection_types"] = new(new Guid("220a4822-39c3-43ed-8103-417a62be3f5a")),
        ["pbr/stream_traversal"] = new(new Guid("30bba765-4d68-4531-8786-56be22b1faa3")),
        ["pbr/stream_work_types"] = new(new Guid("cdf70bee-fb9f-464e-9e7a-f9feffcfe3fe")),
        ["pbr/tiles"] = new(new Guid("220143ef-0077-470d-b2c2-31027333398b")),
        ["pbr/transparent"] = new(new Guid("160f41a6-5f26-49e4-b810-5af059591af9")),
    };

    internal static Entity? AcquireModule(World world, string module)
    {
        ArgumentNullException.ThrowIfNull(world); ArgumentNullException.ThrowIfNull(module);
        if (!s_Modules.TryGetValue(module, out var id)) return null;
        world.AcquireAddon<AssetLibrary>();
        AssetLibrary.RegisterAsset<WgslSourceResource, WgslSourceRecord>();
        var resource = "Sia.Rendering.Pbr.Shaders." + module[4..].Replace('/', '.') + ".wgsl";
        return world.AcquireAsset(id, () => EmbeddedLoader.Load<WgslSourceRecord>(resource, typeof(PbrShaderSource).Assembly));
    }
}
