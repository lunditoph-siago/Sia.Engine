using Sia;

namespace Sia.Engine.Rendering.Pbr;

internal static partial class PbrShaderSource
{
    internal static string Compile(string entry, bool writableClusters = false, bool surfaceData = false, bool sceneGi = false,
        bool streamInstances = false, bool shadingWork = false, bool compatibilityUniformPadding = false, bool staticLightmap = false, bool residentInstances = false)
        => CompileWithLookup(module => module.StartsWith("rendering/", StringComparison.Ordinal)
            ? RenderingShaderSource.ReadModule(module) : Read(module), entry, writableClusters, surfaceData,
            sceneGi, streamInstances, shadingWork, compatibilityUniformPadding, staticLightmap, residentInstances);

    internal static string CompileAcquired(World world, string entry, bool writableClusters = false, bool surfaceData = false,
        bool sceneGi = false, bool streamInstances = false, bool shadingWork = false, bool compatibilityUniformPadding = false, bool staticLightmap = false, bool residentInstances = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CompileWithLookup(module => {
            var source = module.StartsWith("rendering/", StringComparison.Ordinal)
                ? RenderingShaderSource.AcquireModule(world, module) : AcquireModule(world, module);
            return source is { } entity ? entity.Get<WgslSourceResource>().Source : null;
        }, entry, writableClusters, surfaceData, sceneGi, streamInstances, shadingWork, compatibilityUniformPadding, staticLightmap, residentInstances);
    }

    private static string CompileWithLookup(Func<string, string?> read, string entry, bool writableClusters, bool surfaceData,
        bool sceneGi, bool streamInstances, bool shadingWork, bool compatibilityUniformPadding, bool staticLightmap, bool residentInstances)
    {
        if(residentInstances && (streamInstances || shadingWork)) throw new ArgumentException("Resident instances and stream work are distinct geometry inputs.");
        var definitions = new Dictionary<string, string> {
            ["RESIDENT_INSTANCES"] = residentInstances ? "true" : "false",
            ["WRITABLE_CLUSTERS"] = writableClusters ? "true" : "false",
            ["SURFACE_DATA"] = surfaceData ? "true" : "false",
            ["SCENE_GI"] = sceneGi ? "true" : "false",
            ["STATIC_LIGHTMAP"] = staticLightmap ? "true" : "false",
            ["STREAM_INSTANCES"] = streamInstances ? "true" : "false",
            ["SHADING_WORK"] = shadingWork ? "true" : "false",
            ["COMPATIBILITY_UNIFORM_PADDING"] = compatibilityUniformPadding ? "true" : "false",
            ["SHADOW_MATRIX_BASE"] = PbrView.ShadowMatrixBase.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["STREAM_WORK_BLOCK_TRIANGLES"] = PbrGpuHierarchy.WorkBlockTriangles.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var source = read("pbr/" + entry.Replace(".wgsl", "")) ?? throw new InvalidOperationException($"Missing PBR entry: {entry}");
        return RenderingShaderSource.CompileResolved(source, (module, _) => read(module), definitions);
    }

    private static string? Read(string module)
        => RenderingShaderSource.ReadEmbeddedModule(typeof(PbrShaderSource).Assembly,
            "pbr/", "Sia.Rendering.Pbr.Shaders.", module);
}
