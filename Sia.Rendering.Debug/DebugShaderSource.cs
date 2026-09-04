namespace Sia.Engine.Rendering.Debug;

internal static class DebugShaderSource
{
    private const string ResourceName = "Sia.Rendering.Debug.Shaders.debug_overlay.wgsl";

    public static string Load()
    {
        var assembly = typeof(DebugShaderSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded WGSL resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
