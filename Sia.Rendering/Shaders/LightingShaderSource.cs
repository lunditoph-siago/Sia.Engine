using Sia.Graphics.Wgsl;

namespace Sia.Engine.Rendering;

public static class LightingShaderSource
{
    public static string ClusteredForwardModule { get; } = Read("clustered_forward");

    public static string LoadClusterLightCulling()
    {
        var result = WgslPreprocessor.Process(Read("cluster_light_culling"), null,
            (path, _) => path == "rendering::clustered_forward" ? ClusteredForwardModule : null);
        if (result.HasErrors) throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
        return result.CombinedSource;
    }

    private static string Read(string name)
    {
        using var stream = typeof(LightingShaderSource).Assembly.GetManifestResourceStream($"Sia.Rendering.Shaders.{name}.wgsl")
            ?? throw new InvalidOperationException($"Missing shared lighting shader: {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
