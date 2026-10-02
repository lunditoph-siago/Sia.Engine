using Sia;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.GLFW;
using Sia.Input;
using Sia.Math;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private const int BunnyColumns = 15;
    private const int BunnyRows = 9;
    private const int BunnyInstanceCount = BunnyColumns * BunnyRows;
    private const float BunnyPixelError = .125f;
    private readonly MeshPatchAsset? _patchAsset;
    private Aabb _patchBounds;
    private Aabb _patchSceneBounds;
    private readonly VisibilityDebugMode _patchDebugMode;
    private float _patchDistance;
    private float _patchTourPhase;
    private bool _patchTour;
#if !BROWSER
    private uint _patchKeys;
#endif
    private (int Distance, bool Touring, VisibilityDebugMode Mode, uint Triangles)? _patchStatus;

    internal static PbrSceneAsset CreateBunnyScene(MeshPatchAsset asset)
    {
        var bounds = asset.Build.Tree.CopyGeometry().Geometry.Bounds;
        var spacing = math.max(bounds.Max - bounds.Min, new float3(.1f)) * 1.2f;
        var instances = new PbrSceneInstance[BunnyInstanceCount];
        var index = 0;
        for (var row = -(BunnyRows / 2); row <= BunnyRows / 2; row++) {
            for (var column = -(BunnyColumns / 2); column <= BunnyColumns / 2; column++) {
                instances[index++] = new(0, 0, float4x4.Translate(new float3(column * spacing.x, row * spacing.y, 0)));
            }
        }
        return PbrSceneAsset.Create([asset],
            [new(PbrMaterial.Default with { BaseColor = new float3(.8f, .75f, .65f), Roughness = .8f })],
            instances, "Stanford Bunny · Stanford University Computer Graphics Laboratory; https://graphics.stanford.edu/data/3Dscanrep/");
    }

    private unsafe void InitializePatchLod()
    {
        var build = (_patchAsset ?? throw new InvalidOperationException("A cooked patch asset is required.")).Build;
        var tree = build.Tree;
        var rootTriangles = tree.Nodes.Span[..tree.RootCount].ToArray().Sum(node => node.TriangleCount);
        Console.WriteLine($"Cooked patch: {build.SourceTriangleCount} source triangles, {rootTriangles} root triangles, "
            + $"{tree.RootCount} roots, {build.SimplificationCount} reductions, {build.TargetMissCount} missed targets, "
            + $"{build.UnreducedGroupCount} unreduced groups.");
        _patchBounds = tree.RootCount == 0 ? default : tree.Nodes.Span[0].Bounds;
        foreach (var node in tree.Nodes.Span[..tree.RootCount]) {
            _patchBounds = new(math.min(_patchBounds.Min, node.Bounds.Min), math.max(_patchBounds.Max, node.Bounds.Max));
        }
        var stream = _materialStream ?? throw new InvalidOperationException("Bunny requires its complete LOD stream.");
        _patchSceneBounds = stream.Bounds;
        var frame = new GpuFrame(_sceneWorld!, _renderWorld!.Entities, _renderDevice, _renderQueue);
        var settings = new PbrRendererSettings {
            TargetPixelError = BunnyPixelError,
            ShadowTexelError = BunnyPixelError,
            GpuTiming = _gpuTimingEnabled,
            Streaming = new() {
                GpuTraversal = Program.GpuTraversal,
                DetailBytes = 8ul * 1024 * 1024,
                MaximumSelectionNodesPerView = 32768
            }
        };
        _sceneRenderer = new PbrRenderer(in frame, stream, _surfaceFormat, settings) { DebugMode = _patchDebugMode };
        _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>().Add(_sceneRenderer).Build();
        Console.WriteLine($"{(Program.GpuTraversal ? "GPU" : "CPU")} Bunny LOD: {BunnyInstanceCount} instances; target {BunnyPixelError} px; detail budget {settings.Streaming.DetailBytes} bytes.");
        InitializeInspectionControls();
        Console.WriteLine($"Bunny wall: {BunnyColumns} x {BunnyRows} bunnies, {(long)build.SourceTriangleCount * BunnyInstanceCount:N0} source triangles, one shared geometry asset.");
    }

    private unsafe void InitializeInspectionControls()
    {
#if !BROWSER
        GlfwUnsafe.SetKeyCallback((WindowHandle*)_window.Handle, (_, key, _, action, _) => {
            if (action == InputAction.Press) {
                if (_pipeline == ScenePipeline.Pbr) {
                    _cameraPressed.Add(key);
                }
                _patchKeys |= key switch {
                    Key.Space when _pipeline == ScenePipeline.Bunny => 1u,
                    Key.M => 2u,
                    Key.R => 4u,
                    Key.S or Key.Down when _pipeline == ScenePipeline.Bunny => 8u,
                    Key.W or Key.Up when _pipeline == ScenePipeline.Bunny => 16u,
                    _ => 0u
                };
            }
        });
        Console.WriteLine(_pipeline == ScenePipeline.Pbr
            ? "WASD: move. Q/E: down/up. Right drag or arrows: look. Shift: fast. C: slow. R: reset. M: material."
            : "W/S or Up/Down: near/far. Space: pause/resume tour. M: triangles/shaded. R: return near.");
#endif
    }

    private void UpdatePatchInspection(float deltaTime)
    {
#if BROWSER
        var pressed = (uint)_browserCommands;
        _cameraFocused = (pressed & 128) != 0;
        if ((pressed & 256) != 0) {
            var distance = _browserDistance;
            if (_pipeline == ScenePipeline.Bunny && double.IsFinite(distance)) {
                _patchDistance = (float)System.Math.Clamp(distance, 0, 1);
                _patchTour = false;
            }
        }
#else
        bool Down(Key key) => Glfw.GetKey(_window, key) != InputAction.Release;
        var pressed = _patchKeys;
        _patchKeys = 0;
#endif
        if (_pipeline == ScenePipeline.Bunny && (pressed & 1) != 0) {
            _patchTour = !_patchTour;
            _patchTourPhase = MathF.Acos(1 - 2 * _patchDistance);
        }
        if ((pressed & 2) != 0) {
            _sceneRenderer!.DebugMode = _sceneRenderer.DebugMode == VisibilityDebugMode.Triangles
                ? VisibilityDebugMode.Shaded : VisibilityDebugMode.Triangles;
        }
        if ((pressed & 4) != 0) {
            if (_pipeline == ScenePipeline.Pbr) {
                _cameraEye = null;
            }
            else {
                _patchDistance = 0;
            }
            _patchTour = false;
        }
#if !BROWSER
        if (_pipeline == ScenePipeline.Bunny) {
            var direction = (Down(Key.S) || Down(Key.Down) ? 1 : 0) - (Down(Key.W) || Down(Key.Up) ? 1 : 0);
            var step = ((pressed & 8) != 0 ? 1 : 0) - ((pressed & 16) != 0 ? 1 : 0);
            if (direction != 0 || step != 0) {
                _patchTour = false;
                _patchDistance = System.Math.Clamp(_patchDistance + direction * deltaTime * 0.25f + step * 0.01f, 0, 1);
            }
        }
#endif
        if (_patchTour) {
            _patchTourPhase = (_patchTourPhase + deltaTime * (MathF.Tau / 36)) % MathF.Tau;
            _patchDistance = (1 - MathF.Cos(_patchTourPhase)) * 0.5f;
        }
        var triangles = _pipeline == ScenePipeline.Bunny ? _sceneRenderer!.FrameStatistics.Triangles : 0;
        var status = ((int)(_patchDistance * 100), _patchTour, _sceneRenderer!.DebugMode, triangles);
        if (_patchStatus != status) {
            _patchStatus = status;
            var scene = _pipeline == ScenePipeline.Bunny ? $"{BunnyInstanceCount} bunnies | {(Program.GpuTraversal ? "GPU" : "CPU")} LOD | {triangles:N0} triangles | target {BunnyPixelError} px"
                : $"{_materialInstanceCount} instances | {(_finest ? "Finest geometry" : "Automatic LOD")}";
            var camera = _pipeline == ScenePipeline.Pbr ? "Free camera"
                : $"Near 0 -- {(int)(_patchDistance * 100)} -- 100 Far | {(_patchTour ? "Tour" : "Paused")}";
            Glfw.SetTitle(_window, $"Sia.Engine - {scene} | {_sceneRenderer.DebugMode} | {camera}");
#if BROWSER
            _browserInspection = (_patchDistance, _patchTour, _sceneRenderer.DebugMode == VisibilityDebugMode.Triangles, scene);
#endif
        }
    }

    private float3 PatchEye(float aspect, float3 target)
    {
        var half = (_patchBounds.Max - _patchBounds.Min) * 0.5f;
        var radius = System.Math.Max(0.001f, System.Math.Max(half.y, half.x / aspect));
        var near = half.z + radius * 1.05f / MathF.Tan(MathF.PI / 6);
        var wallHalf = (_patchSceneBounds.Max - _patchSceneBounds.Min) * 0.5f;
        var far = half.z + System.Math.Max(wallHalf.y, wallHalf.x / aspect) * 1.3f / MathF.Tan(MathF.PI / 6);
        return target + new float3(0, 0, near * MathF.Pow(far / near, _patchDistance));
    }
}
