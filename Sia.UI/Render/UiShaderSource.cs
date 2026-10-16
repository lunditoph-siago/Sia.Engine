using Sia.Asset;
using Sia.Graphics.Wgsl;

namespace Sia.UI;

public static class UiShaderSource
{
    private const string k_ResourceName = "Sia.UI.Render.Shaders.ui_node.wgsl";
    private const string k_VertexBufferResourceName =
        "Sia.UI.Render.Shaders.ui_node_vertex_buffer.wgsl";
    private static readonly AssetId s_SourceId = new(new Guid("087a7db4-5fa7-4cc3-b568-dccb8f5c9eb2"));
    private static readonly AssetId s_VertexBufferSourceId = new(new Guid("d0533362-44f3-4c00-bf41-c68d30c4503f"));

    public static Entity Acquire(World world) => AcquireResource(world, s_SourceId, k_ResourceName);

    public static Entity AcquireVertexBuffer(World world) => AcquireResource(world, s_VertexBufferSourceId, k_VertexBufferResourceName);

    public static string Load(World world, UiVertexDataMode vertexDataMode = UiVertexDataMode.StorageBuffers) =>
        Compile(Acquire(world).Get<UiShaderSourceResource>().Source, k_ResourceName, vertexDataMode);

    public static string LoadVertexBuffer(World world) =>
        Compile(AcquireVertexBuffer(world).Get<UiShaderSourceResource>().Source, k_VertexBufferResourceName, UiVertexDataMode.VertexBuffer);

    private static Entity AcquireResource(World world, AssetId id, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.AcquireAddon<AssetLibrary>();
        AssetLibrary.RegisterAsset<UiShaderSourceResource, UiShaderSourceRecord>();
        return world.AcquireAsset(id, () => EmbeddedLoader.Load<UiShaderSourceRecord>(resourceName, typeof(UiShaderSource).Assembly));
    }

    private static string Compile(string source, string resourceName, UiVertexDataMode vertexDataMode)
    {
        var context = new WgslCompilationContext(new Dictionary<string, WgslValue> {
            ["PLAN_VERTEX_STORAGE"] = vertexDataMode switch {
                UiVertexDataMode.StorageBuffers => WgslValue.Boolean(true),
                UiVertexDataMode.VertexBuffer => WgslValue.Boolean(false),
                _ => throw new ArgumentOutOfRangeException(nameof(vertexDataMode))
            }
        });
        var result = WgslPreprocessor.ProcessWithContext(source, context, static (_, _) => null);
        if (result.HasErrors) {
            throw new InvalidOperationException(
                $"Failed to process '{resourceName}': " + string.Join("; ", result.Diagnostics));
        }
        return result.CombinedSource;
    }
}
