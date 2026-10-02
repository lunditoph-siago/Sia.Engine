namespace Sia.Engine.Rendering.Pbr;

internal static class PbrShaderSource
{
    internal static string Compile(string entry, bool writableClusters = false, bool surfaceData = false, bool sceneGi = false,
        bool streamInstances = false, bool shadingWork = false)
    {
        var definitions = new Dictionary<string, string> {
            ["WRITABLE_CLUSTERS"] = writableClusters ? "true" : "false",
            ["SURFACE_DATA"] = surfaceData ? "true" : "false",
            ["SCENE_GI"] = sceneGi ? "true" : "false",
            ["STREAM_INSTANCES"] = streamInstances ? "true" : "false",
            ["SHADING_WORK"] = shadingWork ? "true" : "false",
            ["STREAM_WORK_BLOCK_TRIANGLES"] = PbrGpuHierarchy.WorkBlockTriangles.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var source = Read("pbr/" + entry.Replace(".wgsl", "")) ?? throw new InvalidOperationException($"Missing PBR entry: {entry}");
        return RenderingShaderSource.Compile(source, definitions, static (module, _) => Read(module));
    }

    private static string? Read(string module)
        => RenderingShaderSource.ReadEmbeddedModule(typeof(PbrShaderSource).Assembly,
            "pbr/", "Sia.Rendering.Pbr.Shaders.", module);
}
