using Sia;
using Sia.Engine;
using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Sia.Reactors;
using Sia.WebGPU;
using CameraComponent = Sia.Engine.Camera.Camera;
using MeshComponent = Sia.Engine.Mesh.Mesh;

namespace Sia.Engine.Example;

internal sealed unsafe partial class SceneExampleApp
{
    private const float _orbitRadius = 11.0f;
    private const float _orbitHeight = 6.0f;
    private const float _orbitSpeed = 0.35f;
    private const int _gridExtent = 3;
    private const float _gridSpacing = 1.15f;

    private World? _sceneWorld;
    private SystemStage? _sceneStage;
    private PbrDepthPrepassPipeline? _depthPipeline;
    private ForwardPbrPipeline? _forwardPipeline;
    private PipelineCache<RenderPipelineKey, ForwardPbrPipeline>? _forwardPipelineCache;
    private PbrClusterLightCullingPipeline? _cullingPipeline;
    private PbrShadowDepthPipeline? _shadowDepthPipeline;
    private PbrIblPrecomputePipelines? _iblPipelines;
    private PbrRenderer? _sceneRenderer;
    private RenderWorld? _renderWorld;
    private RenderFeaturePipeline<RenderFrameContext>? _renderPipeline;
    private Entity _camera;
    private float _orbitAngle;

    private void InitializeScene()
    {
        _sceneWorld = new World();
        _renderWorld = new RenderWorld();

        var meshRegistry = _renderWorld.Entities.AcquireAddon<MeshRegistry>();
        _renderWorld.Entities.AcquireAddon<MeshGpuStore>();
        _sceneWorld.AcquireAddon<PbrRenderCache>();
        _sceneWorld.AcquireAddon<Viewport>().Value = new ViewportSize(_initialWidth, _initialHeight);
        _sceneWorld.AcquireAddon<ClusterGridConfig>();
        _sceneWorld.AcquireAddon<ShadowAtlasConfig>();

        _sceneStage = SystemChain.Empty
            .Add<TransformSystem>()
            .Add<WorldBoundsSystem>()
            .Add<CameraSystem>()
            .CreateStage(_sceneWorld);

        BuildScene(meshRegistry);

        _depthPipeline = PbrDepthPrepassPipeline.Create(
            _renderGraphWorld!, _renderDevice, WGPUTextureFormat.Depth32Float);
        _forwardPipelineCache = new PipelineCache<RenderPipelineKey, ForwardPbrPipeline>();
        _forwardPipeline = ForwardPbrPipeline.GetOrCreate(
            _forwardPipelineCache,
            _renderGraphWorld!,
            _renderDevice,
            _surfaceFormat,
            WGPUTextureFormat.Depth32Float,
            ForwardPbrPipelineDescriptor.Default);
        _cullingPipeline = PbrClusterLightCullingPipeline.Create(_renderGraphWorld!, _renderDevice);
        _shadowDepthPipeline = PbrShadowDepthPipeline.Create(_renderGraphWorld!, _renderDevice);
        _iblPipelines = PbrIblPrecomputePipelines.Create(_renderGraphWorld!, _renderDevice);
        _sceneRenderer = new PbrRenderer(
            _depthPipeline, _forwardPipeline, _cullingPipeline, _shadowDepthPipeline, _iblPipelines);
        _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>()
            .Add(new PbrRenderFeature(_sceneRenderer))
            .Build();
    }

    private void BuildScene(MeshRegistry meshRegistry)
    {
        var world = _sceneWorld!;

        var groundMesh = ProceduralMesh.Plane(width: 20.0f, depth: 20.0f);
        var groundHandle = meshRegistry.Register(groundMesh);
        world.Create(HList.From(
            new Transform(new float3(0, -0.6f, 0), quaternion.identity, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null),
            new Bounds(groundMesh.Bounds),
            new WorldBounds(groundMesh.Bounds),
            new MeshComponent(groundHandle),
            new PbrMaterial(
                new float3(0.35f, 0.38f, 0.42f), Metallic: 0.0f, Roughness: 0.9f,
                EmissiveColor: float3.zero, EmissiveStrength: 0.0f),
            new MeshRenderer()));

        var sphereMesh = ProceduralMesh.Sphere(radius: 0.45f);
        var sphereHandle = meshRegistry.Register(sphereMesh);
        var gridSteps = _gridExtent * 2;
        var baseColor = new float3(0.8f, 0.8f, 0.8f);

        for (var gx = -_gridExtent; gx <= _gridExtent; gx++) {
            for (var gz = -_gridExtent; gz <= _gridExtent; gz++) {
                var position = new float3(gx * _gridSpacing, 0.0f, gz * _gridSpacing);
                var roughness = (gx + _gridExtent) / (float)gridSteps;
                var metallic = (gz + _gridExtent) / (float)gridSteps;

                world.Create(HList.From(
                    new Transform(position, quaternion.identity, new float3(1, 1, 1)),
                    GlobalTransform.Identity,
                    new Node<SceneGraph>(null),
                    new Bounds(sphereMesh.Bounds),
                    new WorldBounds(sphereMesh.Bounds),
                    new MeshComponent(sphereHandle),
                    new PbrMaterial(
                        baseColor, Metallic: metallic, Roughness: System.MathF.Max(roughness, 0.045f),
                        EmissiveColor: float3.zero, EmissiveStrength: 0.0f),
                    new MeshRenderer()));
            }
        }

        var sunRotation = quaternion.LookRotation(math.normalize(new float3(0.4f, 1.0f, 0.3f)), new float3(0, 1, 0));
        world.Create(HList.From(
            new DirectionalLight(),
            new ShadowCaster(),
            new LightColor(new float3(1.0f, 0.96f, 0.9f), 0.6f),
            new Transform(float3.zero, sunRotation, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null)));

        var occluderMesh = ProceduralMesh.Cube(size: 1.6f);
        var occluderHandle = meshRegistry.Register(occluderMesh);
        world.Create(HList.From(
            new Transform(new float3(5.5f, 2.5f, 1.0f), quaternion.identity, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null),
            new Bounds(occluderMesh.Bounds),
            new WorldBounds(occluderMesh.Bounds),
            new MeshComponent(occluderHandle),
            new PbrMaterial(
                new float3(0.8f, 0.8f, 0.8f), Metallic: 0.0f, Roughness: 0.7f,
                EmissiveColor: float3.zero, EmissiveStrength: 0.0f),
            new MeshRenderer()));

        CreatePointLight(world, new float3(-5.0f, 4.0f, -5.0f), new float3(1.0f, 0.25f, 0.2f), 24.0f, 16.0f);
        CreatePointLight(world, new float3(5.0f, 4.0f, -5.0f), new float3(0.2f, 1.0f, 0.3f), 24.0f, 16.0f);
        CreatePointLight(world, new float3(-5.0f, 4.0f, 5.0f), new float3(0.25f, 0.4f, 1.0f), 24.0f, 16.0f);
        CreatePointLight(world, new float3(5.0f, 4.0f, 5.0f), new float3(1.0f, 0.95f, 0.7f), 24.0f, 16.0f);

        var spotRotation = quaternion.LookRotation(math.normalize(new float3(0.0f, 1.0f, -0.15f)), new float3(0, 0, 1));
        world.Create(HList.From(
            new SpotLight(Range: 8.0f, InnerAngle: 0.25f, OuterAngle: 0.45f),
            new ShadowCaster(),
            new LightColor(new float3(0.4f, 0.7f, 1.0f), 10.0f),
            new Transform(new float3(0.0f, 3.5f, 2.2f), spotRotation, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null)));

        _camera = world.Create(HList.From(
            new CameraComponent(VerticalFovRadians: MathF.PI / 3.0f, Near: 0.1f, Far: 100.0f),
            new CameraActive(),
            new Transform(float3.zero, quaternion.identity, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null),
            CameraMatrices.Identity));
    }

    private static void CreatePointLight(World world, float3 position, float3 color, float intensity, float range) =>
        world.Create(HList.From(
            new PointLight(range),
            new LightColor(color, intensity),
            new Transform(position, quaternion.identity, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null)));

    private void UpdateScene(float deltaTime)
    {
        _orbitAngle += deltaTime * _orbitSpeed;

        var eye = new float3(0, _orbitHeight, _orbitRadius);
        var rotation = quaternion.LookRotation(math.normalize(eye - float3.zero), new float3(0, 1, 0));

        var world = _sceneWorld!;
        world.Execute(_camera, new Transform.SetPosition(eye));
        world.Execute(_camera, new Transform.SetRotation(rotation));

        _sceneStage!.Tick();
    }

    private void OnFramebufferResized()
    {
        if (_sceneWorld is not { } world) {
            return;
        }
        world.AcquireAddon<Viewport>().Value = new ViewportSize(_framebufferWidth, _framebufferHeight);
    }

    private void DisposeScene()
    {
        _sceneStage?.Dispose();
        _sceneStage = null;
        _sceneWorld?.Dispose();
        _sceneWorld = null;
        _renderWorld?.Dispose();
        _renderWorld = null;
        _depthPipeline = null;
        _forwardPipeline = null;
        _forwardPipelineCache?.Dispose();
        _forwardPipelineCache = null;
        _cullingPipeline = null;
        _shadowDepthPipeline = null;
        _sceneRenderer = null;
        _renderPipeline = null;
    }
}
