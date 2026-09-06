using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.GLFW;
using Sia.Input;
using Sia.Math;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private VisibilityPbrFeature? _visibilityLod;
    private readonly MeshPatchAsset? _patchAsset;
    private readonly PatchScene _patchScene;
    private Aabb _patchBounds;
    private Aabb _patchSceneBounds;
    private readonly VisibilityDebugMode _patchDebugMode;
    private float _patchDistance;
    private float _patchTourPhase;
    private bool _patchTour;
    private uint _patchKeys;
    private string? _patchStatus;

    private unsafe void InitializePatchLod()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
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
        var instances = new List<VisibilityInstance>();
        _patchSceneBounds = _patchBounds;
        var horizontal = _patchScene == PatchScene.Bunny ? 7 : _patchScene == PatchScene.Terrain ? 1 : 0;
        var vertical = _patchScene == PatchScene.Bunny ? 4 : horizontal;
        var spacing = math.max(_patchBounds.Max - _patchBounds.Min, new float3(0.1f)) * 1.2f;
        for (var row = -vertical; row <= vertical; row++) {
            for (var column = -horizontal; column <= horizontal; column++) {
                var offset = _patchScene == PatchScene.Bunny ? new float3(column * spacing.x, row * spacing.y, 0)
                    : new float3(column * 4.5f, 0, row * 4.5f);
                _patchSceneBounds = new(math.min(_patchSceneBounds.Min, _patchBounds.Min + offset),
                    math.max(_patchSceneBounds.Max, _patchBounds.Max + offset));
                instances.Add(new(float4x4.Translate(offset),
                    PbrMaterial.Default with { BaseColor = _patchScene == PatchScene.Bunny ? new float3(0.8f, 0.75f, 0.65f) : new float3(0.45f, 0.8f, 0.35f), Roughness = 0.8f }));
            }
        }
        var frame = new GpuFrame(_sceneWorld!, _renderWorld!.Entities, _renderDevice, _renderQueue);
        var albedo = _patchScene == PatchScene.Bunny
            ? new VisibilityAlbedo(1, 1, [new byte[] { 255, 255, 255, 255 }]) : CreateVisibilityChecker();
        var settings = _patchScene == PatchScene.Bunny ? new VisibilityLodSettings(4,
            new MeshPatchBudget(4096, 8192, 262144) { MaxRefinementCandidates = 8192, MaxRefinementNodes = 32768 })
            : new VisibilityLodSettings(8, new(256, 1024, 18000));
        _visibilityLod = VisibilityPbrFeature.CreateGpuLod(in frame, tree, instances.ToArray(), albedo,
            settings, _surfaceFormat, _patchDebugMode);
        _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>().Add(_visibilityLod).Build();
        Console.WriteLine($"GPU Patch LOD: {tree.Nodes.Length} patches per asset, {instances.Count} instances, "
            + $"{_visibilityLod.TriangleCapacity} work triangles; target {settings.TargetPixelError} px, "
            + $"triangle budget {settings.Budget.MaxTriangles}; setup/upload {started.Elapsed.TotalMilliseconds:F2} ms.");
        if (_patchScene == PatchScene.Bunny) {
            GlfwUnsafe.SetKeyCallback((WindowHandle*)_window.Handle, (_, key, _, action, _) => {
                if (action == InputAction.Press) {
                    _patchKeys |= key switch {
                        Key.Space => 1u, Key.M => 2u, Key.R => 4u,
                        Key.S or Key.Down => 8u, Key.W or Key.Up => 16u, _ => 0u
                    };
                }
            });
            Console.WriteLine($"Bunny wall: 15 x 9 instances, {(long)build.SourceTriangleCount * instances.Count:N0} source triangles, one shared geometry asset.");
            Console.WriteLine("W/S or Up/Down: near/far. Space: pause/resume tour. M: triangles/shaded. R: return near.");
        }
    }

    private void UpdatePatchInspection(float deltaTime)
    {
        bool Down(Key key) => Glfw.GetKey(_window, key) != InputAction.Release;
        var pressed = _patchKeys;
        _patchKeys = 0;
        if ((pressed & 1) != 0) {
            _patchTour = !_patchTour;
            _patchTourPhase = MathF.Acos(1 - 2 * _patchDistance);
        }
        if ((pressed & 2) != 0) {
            _visibilityLod!.DebugMode = _visibilityLod.DebugMode == VisibilityDebugMode.Triangles
                ? VisibilityDebugMode.Shaded : VisibilityDebugMode.Triangles;
        }
        if ((pressed & 4) != 0) { _patchDistance = 0; _patchTour = false; }
        var direction = (Down(Key.S) || Down(Key.Down) ? 1 : 0) - (Down(Key.W) || Down(Key.Up) ? 1 : 0);
        var step = ((pressed & 8) != 0 ? 1 : 0) - ((pressed & 16) != 0 ? 1 : 0);
        if (direction != 0 || step != 0) {
            _patchTour = false;
            _patchDistance = System.Math.Clamp(_patchDistance + direction * deltaTime * 0.25f + step * 0.01f, 0, 1);
        }
        if (_patchTour) {
            _patchTourPhase = (_patchTourPhase + deltaTime * (MathF.Tau / 36)) % MathF.Tau;
            _patchDistance = (1 - MathF.Cos(_patchTourPhase)) * 0.5f;
        }
        var status = $"135 bunnies | {_visibilityLod!.DebugMode} | Near 0 -- {(int)(_patchDistance * 100)} -- 100 Far | {(_patchTour ? "Tour" : "Paused")}";
        if (_patchStatus != status) {
            _patchStatus = status;
            Glfw.SetTitle(_window, "Sia.Engine - " + status);
#if BROWSER
            SetInspectionStatus(status);
#endif
        }
    }

    private float3 PatchEye(float aspect, float3 target)
    {
        var half = (_patchBounds.Max - _patchBounds.Min) * 0.5f;
        var radius = System.Math.Max(0.001f, System.Math.Max(half.y, half.x / aspect));
        var near = half.z + radius * 1.05f / MathF.Tan(MathF.PI / 6);
        if (_patchScene != PatchScene.Bunny) { return target + new float3(0, 0, near); }
        var wallHalf = (_patchSceneBounds.Max - _patchSceneBounds.Min) * 0.5f;
        var far = half.z + System.Math.Max(wallHalf.y, wallHalf.x / aspect) * 1.3f / MathF.Tan(MathF.PI / 6);
        return target + new float3(0, 0, near * MathF.Pow(far / near, _patchDistance));
    }
}
