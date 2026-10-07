namespace Sia.Engine.Rendering.Pbr;

/// <summary>One derived description shared by pipeline layouts, shader variants and vertex inputs.</summary>
internal readonly record struct PbrPipelineConfiguration
{
    public PbrOpaquePath OpaquePath { get; init; }
    public bool SurfaceData { get; init; }
    public bool SceneReflections { get; init; }
    public bool TemporalReflections { get; init; }
    public bool BakedReflections { get; init; }
    public bool DynamicTrace { get; init; }
    public bool SceneGi { get; init; }
    public bool LightmapSceneGi { get; init; }
    public bool GpuStream { get; init; }
    public bool LocalInstances { get; init; }
    public bool ConventionalGeometry { get; init; }
    public bool CompactVertices { get; init; }
    public bool Lightmaps { get; init; }
    public bool QuantizedLightmaps { get; init; }
    public bool LightmapMips { get; init; }
    public bool PagedLightmaps { get; init; }
    public bool StreamLightmaps { get; init; }
    public int LightmapOwnerBits { get; init; }

    // Surface export, work buffers and raster uniforms belong to individual passes.
    internal PbrShaderOptions Shader => new() {
        SceneGi = SceneGi,
        SceneReflections = SceneReflections,
        TemporalReflections = TemporalReflections,
        BakedReflections = BakedReflections,
        DynamicTrace = DynamicTrace,
        LightmapSceneGi = LightmapSceneGi,
        LocalInstances = LocalInstances,
        CompactVertices = CompactVertices,
        Lightmaps = Lightmaps,
        QuantizedLightmaps = QuantizedLightmaps,
        LightmapMips = LightmapMips,
        PagedLightmaps = PagedLightmaps,
        StreamLightmaps = StreamLightmaps,
        LightmapOwnerBits = LightmapOwnerBits
    };

    internal static PbrPipelineConfiguration Create(PbrSceneAsset scene, PbrSceneStream? stream, PbrRendererSettings settings)
    {
        var lightmaps = settings.BakedLightmaps is not null || settings.StreamedLightmaps is not null;
        var charts = settings.BakedLightmaps?.Charts.Length ?? settings.StreamedLightmaps?.Charts.Length ?? 0;
        var markers = charts > 0 ? charts : settings.BakedLightmaps?.Receivers.Length ?? settings.StreamedLightmaps?.Receivers.Length ?? 0;
        var owners = System.Math.Max(scene.Materials.Length, checked(scene.Instances.Length + (stream?.Instances.Length ?? 0)));
        var conventional = false;
        var dynamicTrace = false;
        foreach (var instance in scene.Instances.Span)
            if (instance.Dynamic && !scene.Materials.Span[instance.Material].AlphaBlend) { dynamicTrace = true; break; }
        if (stream is not null) foreach (var instance in scene.Instances.Span)
            if (!scene.Materials.Span[instance.Material].AlphaBlend) { conventional = true; break; }
        return new() {
            OpaquePath = settings.OpaquePath,
            SurfaceData = settings.ExportSurfaceData || settings.SceneReflections,
            SceneReflections = settings.SceneReflections,
            TemporalReflections = settings.TemporalReflections,
            BakedReflections = settings.BakedReflections is not null,
            DynamicTrace = dynamicTrace,
            SceneGi = settings.DynamicSceneGi || settings.BakedProbes is not null,
            LightmapSceneGi = settings.DynamicSceneGi && lightmaps,
            GpuStream = stream is not null && settings.Streaming.GpuTraversal,
            LocalInstances = scene.HasDynamicInstances || settings.SceneReflections,
            ConventionalGeometry = conventional,
            CompactVertices = stream is null || lightmaps,
            Lightmaps = lightmaps,
            QuantizedLightmaps = settings.BakedLightmaps?.Encoding == PbrLightmapEncoding.L1Unorm8 || settings.StreamedLightmaps is not null,
            LightmapMips = settings.BakedLightmaps is { Charts.IsEmpty: false } || settings.StreamedLightmaps is not null,
            PagedLightmaps = settings.StreamedLightmaps is not null,
            StreamLightmaps = stream is not null && lightmaps,
            LightmapOwnerBits = lightmaps ? PbrResidentGeometry.PackedLightmapOwnerBits(owners, markers) : 0
        };
    }
}
