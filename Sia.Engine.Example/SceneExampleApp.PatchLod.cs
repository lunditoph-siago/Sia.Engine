using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private VisibilityPbrFeature? _visibilityLod;
    private readonly MeshPatchAsset? _patchAsset;
    private readonly PatchScene _patchScene;
    private Aabb _patchBounds;

    private void InitializePatchLod()
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
        var extent = _patchScene == PatchScene.Terrain ? 1 : 0;
        for (var z = -extent; z <= extent; z++) {
            for (var x = -extent; x <= extent; x++) {
                instances.Add(new(float4x4.Translate(new float3(x * 4.5f, 0, z * 4.5f)),
                    PbrMaterial.Default with { BaseColor = _patchScene == PatchScene.Bunny ? new float3(0.8f, 0.75f, 0.65f) : new float3(0.45f, 0.8f, 0.35f), Roughness = 0.8f }));
            }
        }
        var frame = new GpuFrame(_sceneWorld!, _renderWorld!.Entities, _renderDevice, _renderQueue);
        var albedo = _patchScene == PatchScene.Bunny
            ? new VisibilityAlbedo(1, 1, [new byte[] { 255, 255, 255, 255 }]) : CreateVisibilityChecker();
        _visibilityLod = VisibilityPbrFeature.CreateGpuLod(in frame, tree, instances.ToArray(), albedo,
            new(8, new(256, 1024, 18000)), _surfaceFormat);
        _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>().Add(_visibilityLod).Build();
        Console.WriteLine($"GPU Patch LOD: {tree.Nodes.Length} patches per asset, {instances.Count} instances, "
            + $"{_visibilityLod.TriangleCapacity} work triangles; target 8 px, triangle budget 18000; setup/upload {started.Elapsed.TotalMilliseconds:F2} ms.");
    }

}
