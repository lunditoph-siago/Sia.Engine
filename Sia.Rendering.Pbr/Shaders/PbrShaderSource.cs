namespace Sia.Engine.Rendering.Pbr;

/// <summary>Immutable shader definitions; compose per-pass changes with a value copy.</summary>
internal readonly record struct PbrShaderOptions
{
    public bool WritableClusters { get; init; }
    public bool SurfaceData { get; init; }
    public bool SceneGi { get; init; }
    public bool SceneReflections { get; init; }
    public bool TemporalReflections { get; init; }
    public bool BakedReflections { get; init; }
    public bool DynamicTrace { get; init; }
    public bool LightmapSceneGi { get; init; }
    public bool StreamInstances { get; init; }
    public bool ShadingWork { get; init; }
    public bool CompatibilityUniformPadding { get; init; }
    public bool LocalInstances { get; init; }
    public bool RasterFrame { get; init; }
    public bool Lightmaps { get; init; }
    public bool QuantizedLightmaps { get; init; }
    public bool LightmapMips { get; init; }
    public bool PagedLightmaps { get; init; }
    public bool CompactVertices { get; init; }
    public bool StreamLightmaps { get; init; }
    public int LightmapOwnerBits { get; init; }
}

internal static class PbrShaderSource
{
    internal static string Compile(string entry) => Compile(entry, default(PbrShaderOptions));

    // Diagnostics compile against several Engine revisions. Keep their named-argument
    // entry point while production callers compose the typed options below.
    [Obsolete("Compose PbrShaderOptions instead.")]
    internal static string Compile(string entry, bool writableClusters = false, bool surfaceData = false, bool sceneGi = false,
        bool streamInstances = false, bool shadingWork = false, bool compatibilityUniformPadding = false, bool localInstances = false,
        bool rasterFrame = false, bool lightmaps = false, bool quantizedLightmaps = false, bool lightmapMips = false,
        bool pagedLightmaps = false, bool compactVertices = false, bool streamLightmaps = false)
        => Compile(entry, new PbrShaderOptions {
            WritableClusters = writableClusters, SurfaceData = surfaceData, SceneGi = sceneGi,
            StreamInstances = streamInstances, ShadingWork = shadingWork, CompatibilityUniformPadding = compatibilityUniformPadding,
            LocalInstances = localInstances, RasterFrame = rasterFrame, Lightmaps = lightmaps, QuantizedLightmaps = quantizedLightmaps,
            LightmapMips = lightmapMips, PagedLightmaps = pagedLightmaps, CompactVertices = compactVertices, StreamLightmaps = streamLightmaps
        });

    internal static string Compile(string entry, PbrShaderOptions options)
    {
        var definitions = new Dictionary<string, string> {
            ["WRITABLE_CLUSTERS"] = options.WritableClusters ? "true" : "false",
            ["COMPACT_VERTICES"] = options.CompactVertices ? "true" : "false",
            ["STREAM_LIGHTMAPS"] = options.StreamLightmaps ? "true" : "false",
            ["PACKED_LIGHTMAP_MARKERS"] = options.LightmapOwnerBits != 0 ? "true" : "false",
            ["LIGHTMAP_OWNER_BITS"] = options.LightmapOwnerBits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SURFACE_DATA"] = options.SurfaceData ? "true" : "false",
            ["SCENE_GI"] = options.SceneGi ? "true" : "false",
            ["SCENE_REFLECTIONS"] = options.SceneReflections ? "true" : "false",
            ["TEMPORAL_REFLECTIONS"] = options.TemporalReflections ? "true" : "false",
            ["BAKED_REFLECTIONS"] = options.BakedReflections ? "true" : "false",
            ["DYNAMIC_TRACE"] = options.DynamicTrace ? "true" : "false",
            ["LIGHTMAP_SCENE_GI"] = options.LightmapSceneGi ? "true" : "false",
            ["LIGHTMAPS"] = options.Lightmaps ? "true" : "false",
            ["QUANTIZED_LIGHTMAPS"] = options.QuantizedLightmaps ? "true" : "false",
            ["LIGHTMAP_MIPS"] = options.LightmapMips ? "true" : "false",
            ["PAGED_LIGHTMAPS"] = options.PagedLightmaps ? "true" : "false",
            ["STREAM_INSTANCES"] = options.StreamInstances ? "true" : "false",
            ["LOCAL_INSTANCES"] = options.LocalInstances ? "true" : "false",
            ["HAS_INSTANCES"] = options.StreamInstances || options.LocalInstances ? "true" : "false",
            ["RASTER_FRAME"] = options.RasterFrame ? "true" : "false",
            ["SHADING_WORK"] = options.ShadingWork ? "true" : "false",
            ["COMPATIBILITY_UNIFORM_PADDING"] = options.CompatibilityUniformPadding ? "true" : "false",
            ["SHADOW_MATRIX_BASE"] = PbrView.ShadowMatrixBase.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["STREAM_WORK_BLOCK_TRIANGLES"] = PbrGpuHierarchy.WorkBlockTriangles.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var source = Read("pbr/" + entry.Replace(".wgsl", "")) ?? throw new InvalidOperationException($"Missing PBR entry: {entry}");
        return RenderingShaderSource.Compile(source, definitions, static (module, _) => Read(module));
    }

    private static string? Read(string module)
        => RenderingShaderSource.ReadEmbeddedModule(typeof(PbrShaderSource).Assembly,
            "pbr/", "Sia.Rendering.Pbr.Shaders.", module);
}
