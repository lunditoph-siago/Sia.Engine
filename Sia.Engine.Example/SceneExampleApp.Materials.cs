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
    private PbrSceneAsset? _opaqueScene;
    private PbrTransparentScene? _transparency;
    private readonly int _materialInstanceCount;
    private readonly bool _finest;
    private Aabb _materialBounds;

    private void BuildMaterialScene()
    {
        var scene = _materialScene ?? throw new InvalidOperationException("A cooked PBR scene is required.");
#if BROWSER
        SetSceneAttribution(scene.Attribution);
#endif
        _opaqueScene = OpaqueScene(scene);
        var world = _sceneWorld!;
        _materialBounds = new(new float3(float.PositiveInfinity), new float3(float.NegativeInfinity));
        foreach (var instance in _opaqueScene.Instances.Span) {
            world.Create(HList.From(new VisibilityInstance(instance.Transform, instance.Material) { AssetIndex = instance.Geometry }));
            var tree = _opaqueScene.Geometry.Span[instance.Geometry].Build.Tree;
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
        shadows.TileResolution = 512; shadows.CascadeCount = 3; shadows.MaxShadowedSpotLights = 1; shadows.ShadowDistance = 30;
        world.AcquireAddon<EnvironmentLighting>().Sky = new ProceduralSky { Intensity = .75f };
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
        var source = _materialScene!;
        if (source.Instances.ToArray().Any(instance => source.Materials.Span[instance.Material].AlphaBlend)) {
            _transparency = new(in frame, source);
        }
        var scene = _opaqueScene!;
        var roots = 0; var meshlets = 0; var triangles = 0;
        foreach (var instance in scene.Instances.Span) {
            var tree = scene.Geometry.Span[instance.Geometry].Build.Tree;
            roots = checked(roots + tree.RootCount);
            foreach (var root in tree.Nodes.Span[..tree.RootCount]) {
                meshlets = checked(meshlets + root.MeshletCount);
                triangles = checked(triangles + root.TriangleCount);
            }
        }
        var settings = new VisibilityLodSettings(4, new(checked(roots + 8192), checked(meshlets + 16384), checked(triangles + 1048576)) {
            MaxRefinementCandidates = 4096, MaxRefinementNodes = 16384
        }) {
            Traversal = VisibilityLodTraversal.Parallel, MaxTraversalPasses = 48,
            Shadows = new(8, new(checked(roots + 512), checked(meshlets + 2048), checked(triangles + 131072)) {
                MaxRefinementCandidates = 512, MaxRefinementNodes = 2048
            }) { MaxTraversalPasses = 24 }
        };
        _visibilityLod = _finest
            ? VisibilityPbrFeature.CreateFixedScene(in frame, scene, scene.Instances.Span.ToArray().Select(instance =>
                new VisibilityInstance(instance.Transform, instance.Material) { AssetIndex = instance.Geometry }).ToArray(), _surfaceFormat, _patchDebugMode)
            : VisibilityPbrFeature.CreateGpuScene(in frame, scene, System.Math.Max(32, scene.Instances.Length), settings, _surfaceFormat, _patchDebugMode);
        InitializeInspectionControls();
        Console.WriteLine($"PBR: {scene.Instances.Length} static instances, {(_finest ? "fixed finest" : "automatic LOD")}, {_visibilityLod.TriangleCapacity} work triangles. B: toggle atmosphere.");
        _materialScene = null;
        _opaqueScene = null;
    }

    private static PbrSceneAsset OpaqueScene(PbrSceneAsset source)
    {
        var instances = source.Instances.ToArray().Where(instance => !source.Materials.Span[instance.Material].AlphaBlend).ToArray();
        var geometry = instances.Select(instance => instance.Geometry).Distinct().ToArray();
        var materials = instances.Select(instance => instance.Material).Distinct().ToArray();
        return PbrSceneAsset.Create(geometry.Select(index => source.Geometry.Span[index]).ToArray(),
            materials.Select(index => source.Materials.Span[index]).ToArray(),
            instances.Select(instance => instance with { Geometry = Array.IndexOf(geometry, instance.Geometry),
                Material = Array.IndexOf(materials, instance.Material) }).ToArray(), source.Attribution);
    }
}
