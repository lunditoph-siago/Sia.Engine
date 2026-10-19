using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Engine.Rendering;
using Sia.GLFW;
using Sia.Input;
using Sia.Math;
using Sia;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private const float BunnyPixelError = .125f;
    private Aabb _bunnyBounds;
    private Aabb _bunnySceneBounds;
    private readonly VisibilityDebugMode _bunnyDebugMode;
    private float _bunnyDistance;
    private float _bunnyTourPhase;
    private bool _bunnyTour;
#if !BROWSER
    private uint _bunnyKeys;
#endif
    private (int Distance, bool Touring, VisibilityDebugMode Mode, uint Triangles)? _bunnyStatus;

    private void InitializeBunnyRendering()
    {
        PrepareBunnyBounds();
        InitializeMaterialRendering();
        _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>().Add(_sceneRenderer!).Build();
        Console.WriteLine($"Bunny: {_materialInstanceCount} section instances; {_options.Quality}; pre-cooked selected geometry.");
    }

    private void PrepareBunnyBounds()
    {
        _bunnyBounds = new(new float3(float.PositiveInfinity), new float3(float.NegativeInfinity));
        void Include(Aabb bounds) => _bunnyBounds = new(math.min(_bunnyBounds.Min, bounds.Min), math.max(_bunnyBounds.Max, bounds.Max));
        if (_rasterScene is { } raster) {
            foreach (var geometry in raster.Geometry.Span) Include(geometry.Bounds);
        }
        else if (_materialStream is { } scene) {
            Include(scene.GeometryBounds);
        }
        else throw new InvalidOperationException("Bunny requires an acquired Raster or Virtual scene.");
        _bunnySceneBounds = _materialBounds;
    }

    private unsafe void InitializeInspectionControls()
    {
#if !BROWSER
        GlfwUnsafe.SetKeyCallback((WindowHandle*)_window.Handle, (_, key, _, action, _) => {
            if (action == InputAction.Press) {
                _bunnyKeys |= key switch {
                    Key.Space => 1u,
                    Key.M => 2u,
                    Key.R => 4u,
                    Key.S or Key.Down => 8u,
                    Key.W or Key.Up => 16u,
                    _ => 0u
                };
            }
        });
        Console.WriteLine("W/S or Up/Down: near/far. Space: pause/resume tour. M: triangles/shaded. R: return near.");
#endif
    }

    private void UpdateBunnyInspection(float deltaTime)
    {
#if BROWSER
        var pressed = (uint)_browserCommands;
        if ((pressed & 256) != 0) {
            var distance = _browserDistance;
            if (double.IsFinite(distance)) {
                _bunnyDistance = (float)System.Math.Clamp(distance, 0, 1);
                _bunnyTour = false;
            }
        }
#else
        bool Down(Key key) => Glfw.GetKey(_window, key) != InputAction.Release;
        var pressed = _bunnyKeys;
        _bunnyKeys = 0;
#endif
        if ((pressed & 1) != 0) {
            _bunnyTour = !_bunnyTour;
            _bunnyTourPhase = MathF.Acos(1 - 2 * _bunnyDistance);
        }
        if ((pressed & 2) != 0) {
            _sceneRenderer!.DebugMode = _sceneRenderer.DebugMode == VisibilityDebugMode.Triangles
                ? VisibilityDebugMode.Shaded : VisibilityDebugMode.Triangles;
        }
        if ((pressed & 4) != 0) {
            _bunnyDistance = 0;
            _bunnyTour = false;
        }
#if !BROWSER
        {
            var direction = (Down(Key.S) || Down(Key.Down) ? 1 : 0) - (Down(Key.W) || Down(Key.Up) ? 1 : 0);
            var step = ((pressed & 8) != 0 ? 1 : 0) - ((pressed & 16) != 0 ? 1 : 0);
            if (direction != 0 || step != 0) {
                _bunnyTour = false;
                _bunnyDistance = System.Math.Clamp(_bunnyDistance + direction * deltaTime * 0.25f + step * 0.01f, 0, 1);
            }
        }
#endif
        if (_bunnyTour) {
            _bunnyTourPhase = (_bunnyTourPhase + deltaTime * (MathF.Tau / 36)) % MathF.Tau;
            _bunnyDistance = (1 - MathF.Cos(_bunnyTourPhase)) * 0.5f;
        }
        var triangles = _sceneRenderer!.FrameStatistics.Triangles;
        var status = ((int)(_bunnyDistance * 100), _bunnyTour, _sceneRenderer!.DebugMode, triangles);
        if (_bunnyStatus != status) {
            _bunnyStatus = status;
            var scene = $"{_materialInstanceCount} instances | {_options.Quality} | {triangles:N0} triangles";
            var camera = $"Near 0 -- {(int)(_bunnyDistance * 100)} -- 100 Far | {(_bunnyTour ? "Tour" : "Paused")}";
            Glfw.SetTitle(_window, $"Sia.Engine - {scene} | {_sceneRenderer.DebugMode} | {camera}");
#if BROWSER
            _browserInspection = (_bunnyDistance, _bunnyTour, _sceneRenderer.DebugMode == VisibilityDebugMode.Triangles, scene);
#endif
        }
    }

    private float3 BunnyEye(float aspect, float3 target)
    {
        var half = (_bunnyBounds.Max - _bunnyBounds.Min) * 0.5f;
        var radius = System.Math.Max(0.001f, System.Math.Max(half.y, half.x / aspect));
        var near = half.z + radius * 1.05f / MathF.Tan(MathF.PI / 6);
        var wallHalf = (_bunnySceneBounds.Max - _bunnySceneBounds.Min) * 0.5f;
        var far = half.z + System.Math.Max(wallHalf.y, wallHalf.x / aspect) * 1.3f / MathF.Tan(MathF.PI / 6);
        return target + new float3(0, 0, near * MathF.Pow(far / near, _bunnyDistance));
    }

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
            TargetPixelError = _options.Quality == RenderQuality.Low ? preset.TargetPixelError : BunnyPixelError,
            ShadowTexelError = _options.Quality == RenderQuality.Low ? preset.ShadowTexelError : BunnyPixelError,
        };
        _sceneRenderer = _rasterScene is { } raster ? new PbrRenderer(in frame, raster, _surfaceFormat, settings) { DebugMode = _bunnyDebugMode }
            : new PbrRenderer(in frame, _materialStream!, _surfaceFormat, settings) { DebugMode = _bunnyDebugMode };
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

    private LightingFiles.Prepared? _preparedLighting;
    private IblEnvironmentAsset? _bakedEnvironment;
    private DiffuseProbeAsset? _bakedProbes;
    private LightmapFiles.Prepared? _preparedLightmaps;
    private PbrLightmapInput? _lightmapInput;
    private ReflectionCaptureFiles.Prepared[]? _preparedCaptures;
    private PbrReflectionCaptureInput[]? _captureInputs;
    private readonly List<Sia.Entity> _captureRoots = [];

    private void AcquireCaptureAssets()
    {
        if (_preparedCaptures is not { Length: > 0 } prepared) { _preparedCaptures = null; return; }
        var inputs = new PbrReflectionCaptureInput[prepared.Length];
        for (var at = 0; at < prepared.Length; at++) {
            var acquired = ReflectionCaptureFiles.Acquire(_renderWorld!.Entities, prepared[at]);
            _captureRoots.Add(acquired.Root); inputs[at] = acquired.Input;
        }
        _captureInputs = inputs; _preparedCaptures = null;
    }

    private void AcquireLightmapAssets(PbrSceneData scene)
    {
        if (_preparedLightmaps is not { } prepared) return;
        _lightmapInput = LightmapFiles.CreateInput(_renderWorld!.Entities, prepared, scene, _assetRoot);
        _preparedLightmaps = null;
    }

    private void AcquireLightingAssets()
    {
        if (_preparedLighting is not { } prepared) return;
        if (prepared.Environment is null && prepared.Probes is null) { _preparedLighting = null; return; }
        if (!_assetRoot.IsValid) _assetRoot = RenderAssetUse.Create(_renderWorld!.Entities);
        var acquired = LightingFiles.Acquire(_renderWorld!.Entities, prepared, _assetRoot);
        _bakedEnvironment = acquired.Environment.IsValid ? acquired.Environment.Get<IblEnvironmentResource>().Data : null;
        _bakedProbes = acquired.Probes.IsValid ? acquired.Probes.Get<DiffuseProbeResource>().Data : null;
        _preparedLighting = null;
    }
}
