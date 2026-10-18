using Sia;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private PbrSceneStream? _materialStream;
    private PbrRasterScene? _rasterScene;
    private PbrBvhScene? _traceScene;
    private int _materialInstanceCount;
    private PbrSceneFiles.Prepared? _referenceScene;
    private Entity _assetRoot;
    private Aabb _materialBounds;

    private void AcquireReferenceScene()
    {
        if (_referenceScene is not { } prepared) return;
        switch (prepared) {
            case PbrSceneFiles.PreparedMixed mixed: {
                var acquired = PbrSceneFiles.AcquireMixed(_renderWorld!.Entities, mixed);
                _assetRoot = acquired.Root; _materialStream = acquired.Stream; _traceScene = acquired.Trace;
                _materialInstanceCount = acquired.Stream.SourceInstanceCount;
                break;
            }
            case PbrSceneFiles.Prepared<MeshRasterRecord> raster: {
                var acquired = PbrSceneFiles.AcquireRaster(_renderWorld!.Entities, raster);
                _assetRoot = acquired.Root; _rasterScene = acquired.Scene;
                _materialInstanceCount = acquired.Scene.Instances.Length;
                break;
            }
            case PbrSceneFiles.PreparedStreaming virtuals: {
                var acquired = PbrSceneFiles.AcquireStreaming(_renderWorld!.Entities, virtuals);
                _assetRoot = acquired.Root; _materialStream = acquired.Stream;
                _materialInstanceCount = acquired.Stream.SourceInstanceCount;
                break;
            }
            default: throw new InvalidOperationException("Unsupported prepared scene product.");
        }
        AcquireLightmapAssets(prepared.Scene.Data);
        _referenceScene = null;
        Console.WriteLine(prepared.Scene.Data.Attribution);
        Console.WriteLine($"Reference PBR scene: {prepared.Scene.Data.Instances.Length} Mesh instances; {_materialInstanceCount} section instances.");
    }

    private void BuildMaterialScene()
    {
        if (_materialStream is null && _rasterScene is null) throw new InvalidOperationException("A PBR scene input is required.");
        var world = _sceneWorld!;
        _materialBounds = new(new float3(float.PositiveInfinity), new float3(float.NegativeInfinity));
        if (_rasterScene is { } raster) _materialBounds = raster.Bounds;
        else _materialBounds = _materialStream!.Bounds;
        var shadows = world.AcquireAddon<ShadowAtlasConfig>();
        shadows.TileResolution = _qualitySettings.ShadowResolution;
        shadows.CascadeCount = 3;
        shadows.MaxShadowedSpotLights = 1;
        shadows.ShadowDistance = 30;
        world.AcquireAddon<EnvironmentLighting>().Sky = _bakedEnvironment?.Sky ?? _lightmapInput?.Set.BakeSettings.Sky ?? new ProceduralSky { Intensity = .75f };
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
        var settings = preset with {
            BakedEnvironment = _bakedEnvironment,
            ReflectionCaptures = _captureInputs ?? [],
            Lightmaps = _lightmapInput,
            BakedProbes = _bakedProbes,
            TraceScene = _traceScene,
            TargetPixelError = _pipeline == ScenePipeline.Bunny && _options.Quality != RenderQuality.Low ? BunnyPixelError : preset.TargetPixelError == 0 ? .25f : preset.TargetPixelError,
            ShadowTexelError = _pipeline == ScenePipeline.Bunny && _options.Quality != RenderQuality.Low ? BunnyPixelError : preset.ShadowTexelError == 0 ? .25f : preset.ShadowTexelError,
        };
        _sceneRenderer = _rasterScene is { } raster ? new PbrRenderer(in frame, raster, _surfaceFormat, settings) { DebugMode = _patchDebugMode }
            : new PbrRenderer(in frame, _materialStream!, _surfaceFormat, settings) { DebugMode = _patchDebugMode };
        InitializeInspectionControls();
        Console.WriteLine($"PBR workflow: {_options.Quality}; {settings.OpaquePath}; pixel error {settings.TargetPixelError}; shadow texel error {settings.ShadowTexelError}; shadow size {settings.ShadowResolution}; environment {(settings.BakedEnvironment is null ? "procedural" : "baked")}. Independent selected geometry.");
        Console.WriteLine($"Static reflection regions: {settings.ReflectionCaptures.Length}.");
        Console.WriteLine($"Live reflections: {settings.Reflections is not null}.");
        Console.WriteLine($"Scene diffuse GI: {(settings.Lightmaps is null ? "environment/probes" : "static surface lightmaps")}; dynamic probes {settings.DynamicSceneGi}; updates {settings.ProbeUpdates}; samples {(settings.DynamicSceneGi && settings.Lightmaps is { } bake ? bake.Set.BakeSettings.Samples : settings.ProbeSamples)}; signed correction {settings.DynamicSceneGi && settings.Lightmaps is not null}. Multiple bounces and temporal reconstruction are pending.");
        _materialStream = null;
        _rasterScene = null;
        _traceScene = null;
        _lightmapInput = null;
    }

    public ValueTask StopSceneStreamingAsync() => _sceneRenderer?.StopStreamingAsync() ?? ValueTask.CompletedTask;
}
