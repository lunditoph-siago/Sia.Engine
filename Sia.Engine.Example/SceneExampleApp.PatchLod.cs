using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private VisibilityPbrFeature? _visibilityLod;
    private int _lastLodTriangles = -1;

    private void InitializePatchLod()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var build = MeshPatchBuilder.Build(TerrainGrid(0, 0, 64));
        var tree = build.Tree;
        var rootTriangles = tree.Nodes.Span[..tree.RootCount].ToArray().Sum(node => node.TriangleCount);
        Console.WriteLine($"Patch build: {build.SourceTriangleCount} source triangles, {rootTriangles} root triangles, "
            + $"{tree.RootCount} roots, {build.SimplificationCount} reductions, {build.TargetMissCount} missed targets, "
            + $"{build.UnreducedGroupCount} unreduced groups; {started.ElapsedMilliseconds} ms.");
        var instances = new List<VisibilityInstance>();
        for (var z = -1; z <= 1; z++) {
            for (var x = -1; x <= 1; x++) {
                instances.Add(new(float4x4.Translate(new float3(x * 4.5f, 0, z * 4.5f)),
                    PbrMaterial.Default with { BaseColor = new float3(0.45f, 0.8f, 0.35f), Roughness = 0.8f }));
            }
        }
        var frame = new GpuFrame(_sceneWorld!, _renderWorld!.Entities, _renderDevice, _renderQueue);
        _visibilityLod = VisibilityPbrFeature.CreateLod(in frame, tree, instances.ToArray(), CreateVisibilityChecker(),
            new(8, new(256, 1024, 18000)), _surfaceFormat);
        _renderPipeline = new RenderFeaturePipelineBuilder<RenderFrameContext>().Add(_visibilityLod).Build();
        Console.WriteLine($"Patch LOD: {tree.Nodes.Length} patches per asset, {instances.Count} instances, "
            + $"{_visibilityLod.TriangleCapacity} finest triangles; target 8 px, triangle budget 18000.");
    }

    private void ReportPatchLod(RenderView view)
    {
        if (_visibilityLod?.GetLodSelection(view) is not { } selection || selection.TriangleCount == _lastLodTriangles) { return; }
        _lastLodTriangles = selection.TriangleCount;
        Console.WriteLine($"LOD: {selection.Patches.Length} patches, {selection.TriangleCount} triangles, "
            + $"estimated error {selection.MaximumEstimatedPixelError:F1} px, "
            + $"budget limited={selection.BudgetLimited}, unreachable={selection.BudgetUnreachable}.");
    }

    private static MeshData TerrainGrid(int x, int z, int size)
    {
        var vertices = new MeshVertex[(size + 1) * (size + 1)];
        for (var j = 0; j <= size; j++) {
            for (var i = 0; i <= size; i++) { vertices[j * (size + 1) + i] = TerrainVertex(x + i, z + j); }
        }
        var indices = new List<uint>();
        for (var j = 0; j < size; j++) {
            for (var i = 0; i < size; i++) {
                var a = (uint)(j * (size + 1) + i);
                var b = a + (uint)size + 1;
                indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        }
        return new(vertices, indices.ToArray(), default);
    }

    private static MeshVertex TerrainVertex(int gx, int gz)
    {
        var x = gx / 16f - 2;
        var z = gz / 16f - 2;
        var y = 0.35f * MathF.Sin(2 * x) * MathF.Cos(2.5f * z) + 0.12f * MathF.Cos(5 * x + 3 * z);
        var dx = 0.7f * MathF.Cos(2 * x) * MathF.Cos(2.5f * z) - 0.6f * MathF.Sin(5 * x + 3 * z);
        var dz = -0.875f * MathF.Sin(2 * x) * MathF.Sin(2.5f * z) - 0.36f * MathF.Sin(5 * x + 3 * z);
        return new(new float3(x, y, z), math.normalize(new float3(-dx, 1, -dz)), new float2(gx / 8f, gz / 8f));
    }

}
