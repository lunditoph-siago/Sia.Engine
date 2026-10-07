using Sia;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private PbrSceneAsset? _materialScene;
    private readonly PbrSceneStream? _materialStream;
    private readonly int _materialInstanceCount;
    private readonly bool _finest;
    private Aabb _materialBounds;

    private void BuildMaterialScene()
    {
        var scene = _materialScene ?? throw new InvalidOperationException("A cooked PBR scene is required.");
        var world = _sceneWorld!;
        _materialBounds = new(new float3(float.PositiveInfinity), new float3(float.NegativeInfinity));
        foreach (var instance in scene.Instances.Span) {
            var tree = scene.Geometry.Span[instance.Geometry].Build.Tree;
            foreach (var node in tree.Nodes.Span[..tree.RootCount]) {
                for (var corner = 0; corner < 8; corner++) {
                    var p = math.mul(instance.Transform, new float4(new float3(
                        (corner & 1) == 0 ? node.Bounds.Min.x : node.Bounds.Max.x,
                        (corner & 2) == 0 ? node.Bounds.Min.y : node.Bounds.Max.y,
                        (corner & 4) == 0 ? node.Bounds.Min.z : node.Bounds.Max.z), 1)).xyz;
                    _materialBounds = new(math.min(_materialBounds.Min, p), math.max(_materialBounds.Max, p));
                }
            }
        }
        if (_materialStream is { } stream) {
            _materialBounds = new(math.min(_materialBounds.Min, stream.Bounds.Min), math.max(_materialBounds.Max, stream.Bounds.Max));
        }
        var shadows = world.AcquireAddon<ShadowAtlasConfig>();
        shadows.TileResolution = _qualitySettings.ShadowResolution;
        shadows.CascadeCount = 3;
        shadows.MaxShadowedSpotLights = 1;
        shadows.ShadowDistance = 30;
        world.AcquireAddon<EnvironmentLighting>().Sky = Program.BakedEnvironment?.Sky ?? new ProceduralSky { Intensity = .75f };
        var sun = quaternion.LookRotation(math.normalize(new float3(-.8f, 1, .4f)), new(0, 1, 0));
        world.Create(HList.From(new DirectionalLight(), new ShadowCaster(), new LightColor(new(1, .96f, .9f), 3),
            new GlobalTransform(new AffineTransform(float3.zero, sun))));
        CreatePointLight(world, new(3, 4, -4), new(1, .85f, .65f), 40, 15);
        CreatePointLight(world, new(5, 4, -8), new(1, .85f, .65f), 24, 15);
        var spot = quaternion.LookRotation(math.normalize(new float3(0, 1, -.3f)), new(0, 0, 1));
        world.Create(HList.From(new SpotLight(12, .3f, .6f), new ShadowCaster(), new LightColor(new(1, .9f, .75f), 18),
            new GlobalTransform(new AffineTransform(new(2, 6, -5), spot))));
    }

    private void InitializeMaterialRendering()
    {
        var frame = new GpuFrame(_sceneWorld!, _renderWorld!.Entities, _renderDevice, _renderQueue);
        var preset = _qualitySettings;
        var opaquePath = Program.OpaquePath ?? preset.OpaquePath;
        var settings = preset with {
            Streaming = preset.Streaming with {
                GpuTraversal = Program.GpuTraversal ?? (opaquePath == PbrOpaquePath.Visibility && preset.Streaming.GpuTraversal)
            },
            BakedEnvironment = Program.BakedEnvironment,
            BakedReflections = Program.BakedReflections,
            ExportSurfaceData = Program.ExportSurfaceData,
            BakedProbes = Program.BakedProbes,
            BakedLightmaps = Program.BakedLightmaps,
            StreamedLightmaps = Program.StreamedLightmaps,
            DynamicSceneGi = Program.DynamicSceneGi ?? preset.DynamicSceneGi,
            OpaquePath = opaquePath,
            TargetPixelError = _finest ? 0 : preset.TargetPixelError == 0 ? .25f : preset.TargetPixelError,
            ShadowTexelError = _finest ? 0 : preset.ShadowTexelError == 0 ? .25f : preset.ShadowTexelError,
            GpuTiming = _gpuTimingEnabled
        };
        _sceneRenderer = _materialStream is { } stream
            ? new PbrRenderer(in frame, stream, _surfaceFormat, settings) { DebugMode = _patchDebugMode }
            : new PbrRenderer(in frame, _materialScene!, _surfaceFormat, settings) { DebugMode = _patchDebugMode };
        _workflow = new(settings.OpaquePath.ToString(), settings.BakedEnvironment is not null, _finest,
            settings.TargetPixelError, settings.ShadowTexelError, settings.ShadowResolution, settings.ExportSurfaceData,
            settings.DynamicSceneGi, settings.BakedProbes is not null, settings.BakedLightmaps is not null || settings.StreamedLightmaps is not null,
            settings.StreamedLightmaps is not null, settings.DynamicSceneGi ? settings.ProbeUpdates : 0,
            settings.DynamicSceneGi ? settings.ProbeSamples : 0, settings.BakedReflections is not null);
        InitializeInspectionControls();
        Console.WriteLine($"PBR workflow: {Program.Quality}; {settings.OpaquePath}; pixel error {settings.TargetPixelError}; shadow texel error {settings.ShadowTexelError}; shadow size {settings.ShadowResolution}; environment {(settings.BakedEnvironment is null ? "procedural" : "baked")}. Geometry {(_materialStream is null ? "resident" : "hierarchical stream")}.");
        Console.WriteLine($"Unmapped diffuse GI: {(settings.DynamicSceneGi ? "dynamic probes" : settings.BakedProbes is not null ? "baked probes" : "environment only")}; updates {settings.ProbeUpdates}; samples {settings.ProbeSamples}. Card atlas, local-light transport, multiple bounces and temporal reconstruction are pending.");
        if (settings.BakedLightmaps is { } lightmaps)
            Console.WriteLine($"Static surface lightmaps: {lightmaps.Resolution} square, {lightmaps.Receivers.Length} receivers; replace diffuse probe/environment irradiance on mapped texels. Direct lighting remains live.");
        if (settings.StreamedLightmaps is { } pages)
            Console.WriteLine($"Paged static lightmaps: {pages.Resolution} virtual square, {pages.Receivers.Length} receivers; GPU pool {_sceneRenderer.LightmapStreamingStatistics!.Value.AllocatedBytes} bytes. Missing pages use chart means; direct lighting remains live.");
        if (settings.BakedReflections is { } capture)
            Console.WriteLine($"Static reflection capture: point {capture.Position}; box {capture.Bounds.Min} .. {capture.Bounds.Max}. Outside the region uses the environment sky.");
        _materialScene = null;
    }

    public ValueTask StopSceneStreamingAsync() => _sceneRenderer?.StopStreamingAsync() ?? ValueTask.CompletedTask;
}
