using Sia;
using Sia.Engine;
using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Engine.Rendering.Unlit;
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
    private const int _gridExtent = 3;
    private const float _gridSpacing = 1.15f;

    private World? _sceneWorld;
    private SystemStage? _sceneStage;
    private PbrRenderer? _sceneRenderer;
    private RenderWorld? _renderWorld;
    private RenderFeaturePipeline<RenderFrameContext>? _renderPipeline;
    private Entity _camera;

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
        _sceneWorld.AcquireAddon<EnvironmentLighting>().Sky = new ProceduralSky {
            Intensity = 0.35f
        };

        _sceneStage = SystemChain.Empty
            .Add<TransformSystem>()
            .Add<WorldBoundsSystem>()
            .Add<CameraSystem>()
            .CreateStage(_sceneWorld);

        BuildScene(meshRegistry);

        if (_pipeline == ScenePipeline.Bunny) {
            InitializePatchLod();
            return;
        }

        if (_pipeline == ScenePipeline.Unlit) {
            var pipeline = UnlitPipeline.Create(
                _renderWorld.Entities,
                _renderDevice,
                _surfaceFormat,
                WGPUTextureFormat.Depth32Float,
                UnlitShaderSource.Load(), "unlit");
            _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>()
                .Add(new UnlitRenderFeature(new UnlitRenderer(pipeline)))
                .Build();
            return;
        }

        _sceneRenderer = new PbrRenderer(
            PbrClusterLightCullingPipeline.Create(_renderGraphWorld!, _renderDevice),
            PbrIblPrecomputePipelines.Create(_renderGraphWorld!, _renderDevice),
            PbrOutputPipelines.Create(_renderGraphWorld!, _renderDevice, _surfaceFormat));
        InitializeMaterialRendering();
        _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>()
            .Add(new PbrRenderFeature(_sceneRenderer, visibility: _visibilityLod))
            .Build();
    }

    private void BuildScene(MeshRegistry meshRegistry)
    {
        var world = _sceneWorld!;

        _camera = world.Create(HList.From(
            new CameraComponent(VerticalFovRadians: MathF.PI / 3.0f, Near: 0.1f, Far: 100.0f),
            new CameraActive(),
            new Transform(float3.zero, quaternion.identity, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null),
            CameraMatrices.Identity));
        if (_pipeline == ScenePipeline.Bunny) { return; }
        if (_pipeline == ScenePipeline.Pbr) { BuildMaterialScene(); return; }

        var groundMesh = ProceduralMesh.Plane(width: 20.0f, depth: 20.0f);
        var groundHandle = meshRegistry.Register(groundMesh);
        world.Create(HList.From(
            new Transform(new float3(0, -0.6f, 0), quaternion.identity, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null),
            new Bounds(groundMesh.Bounds),
            new WorldBounds(groundMesh.Bounds),
            new MeshComponent(groundHandle),
            new UnlitMaterial(new float4(0.12f, 0.16f, 0.22f, 1.0f)),
            new MeshRenderer()));

        var sphereMesh = ProceduralMesh.Sphere(radius: 0.45f);
        var sphereHandle = meshRegistry.Register(sphereMesh);
        var gridSteps = _gridExtent * 2;

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
                    new UnlitMaterial(new float4(
                        0.15f + 0.8f * metallic,
                        0.15f + 0.8f * roughness,
                        0.85f - 0.55f * metallic,
                        1.0f)),
                    new MeshRenderer()));
            }
        }

        var occluderMesh = ProceduralMesh.Cube(size: 1.6f);
        var occluderHandle = meshRegistry.Register(occluderMesh);
        world.Create(HList.From(
            new Transform(new float3(5.5f, 2.5f, 1.0f), quaternion.identity, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null),
            new Bounds(occluderMesh.Bounds),
            new WorldBounds(occluderMesh.Bounds),
            new MeshComponent(occluderHandle),
            new UnlitMaterial(new float4(1.0f, 0.55f, 0.12f, 1.0f)),
            new MeshRenderer()));

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
        var eye = new float3(0, _orbitHeight, _orbitRadius);
        var target = float3.zero;
        if (_pipeline == ScenePipeline.Bunny) {
            UpdatePatchInspection(deltaTime);
            target = (_patchBounds.Min + _patchBounds.Max) * 0.5f;
            var aspect = (float)_framebufferWidth / System.Math.Max(1, _framebufferHeight);
            eye = PatchEye(aspect, target);
        } else if (_pipeline == ScenePipeline.Pbr) {
            UpdatePatchInspection(deltaTime);
            var aspect = (float)_framebufferWidth / System.Math.Max(1, _framebufferHeight);
            var size = _materialBounds.Max - _materialBounds.Min;
            target = new float3(4, 3.5f, 3);
            var extent = System.Math.Max(size.z, System.Math.Max(size.y, size.x / aspect));
            eye = target + new float3(-4, 1, 17) * (.6f + .4f * _patchDistance);
            UpdateFreeCamera(deltaTime, ref eye, ref target);
            _camera.Get<CameraComponent>() = new(MathF.PI / 3, .1f, System.Math.Max(100, extent * 4));
        }
        var rotation = quaternion.LookRotation(math.normalize(eye - target), new float3(0, 1, 0));

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
        _sceneRenderer = null;
        _renderPipeline = null;
    }
}
