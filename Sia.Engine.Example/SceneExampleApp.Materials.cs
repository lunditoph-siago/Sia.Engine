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
    private PbrRasterScene? _rasterScene;
    private int _materialInstanceCount;
    private PbrSceneFiles.Prepared? _referenceScene;
    private Entity _assetRoot;
    private Aabb _materialBounds;

    private void AcquireReferenceScene()
    {
        if (_referenceScene is not { } prepared) return;
        switch (prepared) {
            case PbrSceneFiles.Prepared<MeshRasterRecord> raster: {
                var acquired = PbrSceneFiles.AcquireRaster(_renderWorld!.Entities, raster);
                _assetRoot = acquired.Root; _rasterScene = acquired.Scene;
                _materialInstanceCount = acquired.Scene.Instances.Length;
                break;
            }
            case PbrSceneFiles.Prepared<MeshVirtualRecord> virtuals: {
                var acquired = PbrSceneFiles.AcquireVirtual(_renderWorld!.Entities, virtuals);
                _assetRoot = acquired.Root; _materialScene = acquired.Scene;
                _materialInstanceCount = acquired.Scene.Instances.Length;
                break;
            }
            default: throw new InvalidOperationException("Unsupported prepared scene product.");
        }
        _referenceScene = null;
        Console.WriteLine(prepared.Scene.Data.Attribution);
        Console.WriteLine($"Reference PBR scene: {prepared.Scene.Data.Instances.Length} Mesh instances; {_materialInstanceCount} section instances.");
    }

    private void BuildMaterialScene()
    {
        if (_materialScene is null && _rasterScene is null) throw new InvalidOperationException("A PBR scene input is required.");
        var world = _sceneWorld!;
        _materialBounds = new(new float3(float.PositiveInfinity), new float3(float.NegativeInfinity));
        if (_rasterScene is { } raster) _materialBounds = raster.Bounds;
        else foreach (var instance in _materialScene!.Instances.Span) {
            var tree = _materialScene.Geometry.Span[instance.Geometry].Build.Tree;
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
        var shadows = world.AcquireAddon<ShadowAtlasConfig>();
        shadows.TileResolution = _qualitySettings.ShadowResolution;
        shadows.CascadeCount = 3;
        shadows.MaxShadowedSpotLights = 1;
        shadows.ShadowDistance = 30;
        world.AcquireAddon<EnvironmentLighting>().Sky = _bakedEnvironment?.Sky ?? new ProceduralSky { Intensity = .75f };
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
            BakedProbes = _bakedProbes,
            TargetPixelError = _pipeline == ScenePipeline.Bunny ? BunnyPixelError : preset.TargetPixelError == 0 ? .25f : preset.TargetPixelError,
            ShadowTexelError = _pipeline == ScenePipeline.Bunny ? BunnyPixelError : preset.ShadowTexelError == 0 ? .25f : preset.ShadowTexelError,
        };
        _sceneRenderer = _rasterScene is { } raster ? new PbrRenderer(in frame, raster, _surfaceFormat, settings) { DebugMode = _patchDebugMode }
            : new PbrRenderer(in frame, _materialScene!, _surfaceFormat, settings) { DebugMode = _patchDebugMode };
        InitializeInspectionControls();
        Console.WriteLine($"PBR workflow: {_options.Quality}; {settings.OpaquePath}; pixel error {settings.TargetPixelError}; shadow texel error {settings.ShadowTexelError}; shadow size {settings.ShadowResolution}; environment {(settings.BakedEnvironment is null ? "procedural" : "baked")}.");
        Console.WriteLine($"Scene diffuse GI: {(settings.DynamicSceneGi ? "dynamic probes" : settings.BakedProbes is not null ? "baked probes" : "environment only")}; updates {settings.ProbeUpdates}; samples {settings.ProbeSamples}. Card atlas, local-light transport, multiple bounces and temporal reconstruction are pending.");
        _materialScene = null;
        _rasterScene = null;
    }
}
