using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private VisibilityPbrFeature? _visibilityLod;
    private readonly MeshPatchAsset? _patchAsset;

    private void InitializePatchLod()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var build = (_patchAsset ?? throw new InvalidOperationException("A cooked patch asset is required.")).Build;
        var tree = build.Tree;
        var rootTriangles = tree.Nodes.Span[..tree.RootCount].ToArray().Sum(node => node.TriangleCount);
        Console.WriteLine($"Cooked patch: {build.SourceTriangleCount} source triangles, {rootTriangles} root triangles, "
            + $"{tree.RootCount} roots, {build.SimplificationCount} reductions, {build.TargetMissCount} missed targets, "
            + $"{build.UnreducedGroupCount} unreduced groups.");
        var instances = new List<VisibilityInstance>();
        for (var z = -1; z <= 1; z++) {
            for (var x = -1; x <= 1; x++) {
                instances.Add(new(float4x4.Translate(new float3(x * 4.5f, 0, z * 4.5f)),
                    PbrMaterial.Default with { BaseColor = new float3(0.45f, 0.8f, 0.35f), Roughness = 0.8f }));
            }
        }
        var frame = new GpuFrame(_sceneWorld!, _renderWorld!.Entities, _renderDevice, _renderQueue);
        _visibilityLod = VisibilityPbrFeature.CreateGpuLod(in frame, tree, instances.ToArray(), CreateVisibilityChecker(),
            new(8, new(256, 1024, 18000)), _surfaceFormat);
        _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>().Add(_visibilityLod).Build();
        Console.WriteLine($"GPU Patch LOD: {tree.Nodes.Length} patches per asset, {instances.Count} instances, "
            + $"{_visibilityLod.TriangleCapacity} finest triangles; target 8 px, triangle budget 18000; setup/upload {started.Elapsed.TotalMilliseconds:F2} ms.");
    }

}
