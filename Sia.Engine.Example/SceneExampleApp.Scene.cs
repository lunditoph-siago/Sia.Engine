using Sia;
using Sia.Asset;
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
    private PbrRendererSettings _qualitySettings = PbrRendererSettings.ForQuality(RenderQuality.Low);

    private void InitializeScene()
    {
        if (_pipeline == ScenePipeline.Bunny) {
            _qualitySettings = PbrRendererSettings.ForQuality(_options.Quality).Resolve(WebGpuCapabilities.Read(_device));
            Console.WriteLine($"Render quality: {_options.Quality}; shadow resolution {_qualitySettings.ShadowResolution}.");
        }
        _sceneWorld = new World();
        _renderWorld = new RenderWorld();

        AssetLibrary.RegisterAsset<MeshSourceAsset, MeshSourceRecord>();
        AssetLibrary.RegisterAsset<MeshRasterAsset, MeshRasterRecord>();
        AssetLibrary.RegisterAsset<MeshManifestAsset, MeshManifestRecord>();
        _renderWorld.Entities.AcquireAddon<AssetLibrary>();
        _renderWorld.Entities.AcquireAddon<MeshRegistry>();
        _renderWorld.Entities.AcquireAddon<MeshGpuStore>();
        _sceneWorld.AcquireAddon<Viewport>().Value = new ViewportSize(InitialWidth, InitialHeight);
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

        if (_pipeline == ScenePipeline.Bunny) {
            AcquireReferenceScene();
            AcquireLightingAssets();
            AcquireCaptureAssets();
        }
        BuildScene();

        if (_pipeline == ScenePipeline.Bunny) {
            InitializeBunnyRendering();
            return;
        }

        var pipeline = UnlitPipeline.Create(
            _renderWorld.Entities,
            _renderDevice,
            _surfaceFormat,
            WGPUTextureFormat.Depth32Float,
            new ShaderAsset(new ShaderAssetId("sia:unlit"),
                UnlitShaderSource.Acquire(_renderWorld.Entities).Get<WgslSourceResource>().Source), "unlit");
        _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>()
            .Add(new UnlitRenderFeature(new UnlitRenderer(pipeline)))
            .Build();
    }

    private void BuildScene()
    {
        var world = _sceneWorld!;

        _camera = world.Create(HList.From(
            new CameraComponent(VerticalFovRadians: MathF.PI / 3.0f, Near: 0.1f, Far: 100.0f),
            new CameraActive(),
            new Transform(float3.zero, quaternion.identity, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null),
            CameraMatrices.Identity));
        if (_pipeline == ScenePipeline.Bunny) { BuildMaterialScene(); return; }

        var groundMesh = ProceduralMesh.Plane(width: 20.0f, depth: 20.0f);
        var groundMeshAsset = CreateRasterMesh(groundMesh);
        world.Create(HList.From(
            new Transform(new float3(0, -0.6f, 0), quaternion.identity, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null),
            new Bounds(groundMesh.Bounds),
            new WorldBounds(groundMesh.Bounds),
            new MeshComponent(groundMeshAsset),
            new UnlitMaterial(new float4(0.12f, 0.16f, 0.22f, 1.0f)),
            new MeshRenderer()));

        var sphereMesh = ProceduralMesh.Sphere(radius: 0.45f);
        var sphereMeshAsset = CreateRasterMesh(sphereMesh);
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
                    new MeshComponent(sphereMeshAsset),
                    new UnlitMaterial(new float4(
                        0.15f + 0.8f * metallic,
                        0.15f + 0.8f * roughness,
                        0.85f - 0.55f * metallic,
                        1.0f)),
                    new MeshRenderer()));
            }
        }

        var occluderMesh = ProceduralMesh.Cube(size: 1.6f);
        var occluderMeshAsset = CreateRasterMesh(occluderMesh);
        world.Create(HList.From(
            new Transform(new float3(5.5f, 2.5f, 1.0f), quaternion.identity, new float3(1, 1, 1)),
            GlobalTransform.Identity,
            new Node<SceneGraph>(null),
            new Bounds(occluderMesh.Bounds),
            new WorldBounds(occluderMesh.Bounds),
            new MeshComponent(occluderMeshAsset),
            new UnlitMaterial(new float4(1.0f, 0.55f, 0.12f, 1.0f)),
            new MeshRenderer()));

    }

    private void UpdateScene(float deltaTime)
    {
        if (_pipeline == ScenePipeline.Bunny) UpdateBunnyInspection(deltaTime);
        if (ApplyAuthoredCamera()) {
            _sceneStage!.Tick();
            return;
        }
        var eye = new float3(0, _orbitHeight, _orbitRadius);
        var target = float3.zero;
        if (_pipeline == ScenePipeline.Bunny) {
            target = (_bunnyBounds.Min + _bunnyBounds.Max) * 0.5f;
            var aspect = (float)_framebufferWidth / System.Math.Max(1, _framebufferHeight);
            eye = BunnyEye(aspect, target);
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
        _sceneRenderer?.Dispose();
        foreach (var root in _captureRoots) if (root.IsValid) root.Destroy();
        _captureRoots.Clear(); _captureInputs = null; _preparedCaptures = null;
        if (_assetRoot.IsValid) _assetRoot.Destroy();
        _assetRoot = default;
        _referenceScene = null;
        _sceneLighting = null;
        _preparedLighting = null;
        _bakedEnvironment = null;
        _preparedLightmaps = null;
        _lightmapInput = null;
        _materialStream = null;
        _traceScene = null;
        _rasterScene = null;
        _renderWorld?.Dispose();
        _renderWorld = null;
        _sceneRenderer = null;
        _renderPipeline = null;
    }
}
