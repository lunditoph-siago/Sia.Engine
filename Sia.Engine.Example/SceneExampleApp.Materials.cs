using Sia;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private readonly PbrSceneAsset? _materialScene;
    private readonly List<(Entity Entity, float4x4 Transform)> _materialInstances = [];
    private float _materialTime;

    private void BuildMaterialScene()
    {
        var scene = _materialScene ?? throw new InvalidOperationException("A cooked PBR scene is required.");
        var world = _sceneWorld!;
        foreach (var instance in scene.Instances.Span) {
            var entity = world.Create(HList.From(new VisibilityInstance(instance.Transform, instance.Material) { AssetIndex = instance.Geometry }));
            if (instance.Material <= 2) { _materialInstances.Add((entity, instance.Transform)); }
        }
        var shadows = world.AcquireAddon<ShadowAtlasConfig>();
        shadows.TileResolution = 512; shadows.CascadeCount = 3; shadows.MaxShadowedSpotLights = 1; shadows.ShadowDistance = 30;
        world.AcquireAddon<EnvironmentLighting>().Sky = new ProceduralSky { Intensity = .5f };
        var sun = quaternion.LookRotation(math.normalize(new float3(.4f, 1, .3f)), new(0, 1, 0));
        world.Create(HList.From(new DirectionalLight(), new ShadowCaster(), new LightColor(new(1, .96f, .9f), 2),
            new GlobalTransform(new AffineTransform(float3.zero, sun))));
        CreatePointLight(world, new(-5, 4, -2), new(.3f, .5f, 1), 24, 15);
        CreatePointLight(world, new(5, 4, 2), new(1, .5f, .25f), 24, 15);
        var spot = quaternion.LookRotation(math.normalize(new float3(0, 1, -.3f)), new(0, 0, 1));
        world.Create(HList.From(new SpotLight(12, .3f, .6f), new ShadowCaster(), new LightColor(new(.4f, .7f, 1), 18),
            new GlobalTransform(new AffineTransform(new(0, 6, 2), spot))));
    }

    private void InitializeMaterialRendering()
    {
        var frame = new GpuFrame(_sceneWorld!, _renderWorld!.Entities, _renderDevice, _renderQueue);
        var settings = new VisibilityLodSettings(4, new MeshPatchBudget(8192, 16384, 262144) {
            MaxRefinementCandidates = 4096, MaxRefinementNodes = 16384
        });
        _visibilityLod = VisibilityPbrFeature.CreateGpuScene(in frame, _materialScene!, 32, settings, _surfaceFormat, _patchDebugMode);
        InitializeInspectionControls();
        Console.WriteLine("PBR: nine textured cameras, shared geometry, dynamic instances, scene lights, shadows and IBL. A: toggle atmosphere.");
    }

    private void UpdateMaterialScene(float deltaTime)
    {
        _materialTime = (_materialTime + deltaTime) % 3600;
        var rotation = float4x4.RotateY(MathF.Sin(_materialTime * .35f) * .18f);
        foreach (var (entity, transform) in _materialInstances) {
            entity.Get<VisibilityInstance>() = entity.Get<VisibilityInstance>() with { Transform = math.mul(rotation, transform) };
        }
    }
}
