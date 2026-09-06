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
        var tree = MeshPatchTree.Create([BuildTerrainPatch(0, 0, 64)]);
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

    private static MeshPatch BuildTerrainPatch(int x, int z, int size)
    {
        if (size == 16) { return new(TerrainGrid(x, z, size), 0, []); }
        var half = size / 2;
        MeshPatch[] children = [BuildTerrainPatch(x, z, half), BuildTerrainPatch(x + half, z, half),
            BuildTerrainPatch(x, z + half, half), BuildTerrainPatch(x + half, z + half, half)];
        var boundary = new List<MeshVertex>();
        for (var i = 0; i < size; i++) { boundary.Add(TerrainVertex(x + i, z)); }
        for (var i = 0; i < size; i++) { boundary.Add(TerrainVertex(x + size, z + i)); }
        for (var i = 0; i < size; i++) { boundary.Add(TerrainVertex(x + size - i, z + size)); }
        for (var i = 0; i < size; i++) { boundary.Add(TerrainVertex(x, z + size - i)); }
        var vertices = new[] { TerrainVertex(x + half, z + half) }.Concat(boundary).ToArray();
        var indices = new uint[boundary.Count * 3];
        for (var i = 0; i < boundary.Count; i++) {
            indices[i * 3] = 0;
            indices[i * 3 + 1] = (uint)((i + 1) % boundary.Count + 1);
            indices[i * 3 + 2] = (uint)i + 1;
        }
        var geometry = new MeshData(vertices, indices, default);
        return new(geometry, EstimateTerrainError(geometry, children), children);
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

    private static float EstimateTerrainError(MeshData parent, MeshPatch[] children)
    {
        var error = 0f;
        foreach (var child in children) {
            foreach (var vertex in child.Geometry.Vertices) {
                var p = vertex.Position;
                var found = false;
                for (var t = 0; t < parent.Indices.Length; t += 3) {
                    var a = parent.Vertices[parent.Indices[t]].Position;
                    var b = parent.Vertices[parent.Indices[t + 1]].Position;
                    var c = parent.Vertices[parent.Indices[t + 2]].Position;
                    var determinant = (double)(b.x - a.x) * (c.z - a.z) - (double)(b.z - a.z) * (c.x - a.x);
                    var beta = ((double)(p.x - a.x) * (c.z - a.z) - (double)(p.z - a.z) * (c.x - a.x)) / determinant;
                    var gamma = ((double)(b.x - a.x) * (p.z - a.z) - (double)(b.z - a.z) * (p.x - a.x)) / determinant;
                    if (beta < -1e-6 || gamma < -1e-6 || beta + gamma > 1 + 1e-6) { continue; }
                    var height = a.y * (1 - beta - gamma) + b.y * beta + c.y * gamma;
                    error = MathF.Max(error, (float)System.Math.Abs(p.y - height));
                    found = true;
                    break;
                }
                if (!found) { throw new InvalidOperationException("Terrain parent does not cover a child vertex."); }
            }
        }
        return error;
    }
}
