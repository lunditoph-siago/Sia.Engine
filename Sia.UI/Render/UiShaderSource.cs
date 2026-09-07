using Sia.Graphics.Wgsl;

namespace Sia.UI;

public static class UiShaderSource
{
    private const string k_ResourceName = "Sia.UI.Render.Shaders.ui_node.wgsl";
    private const string k_VertexBufferResourceName =
        "Sia.UI.Render.Shaders.ui_node_vertex_buffer.wgsl";

    public static string Load(UiVertexDataMode vertexDataMode = UiVertexDataMode.StorageBuffers) =>
        LoadResource(k_ResourceName, vertexDataMode);

    public static string LoadVertexBuffer() =>
        LoadResource(k_VertexBufferResourceName, UiVertexDataMode.VertexBuffer);

    private static string LoadResource(string resourceName, UiVertexDataMode vertexDataMode)
    {
        var assembly = typeof(UiShaderSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded WGSL resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

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
