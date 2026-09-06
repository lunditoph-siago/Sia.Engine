using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Rendering.Benchmarks;

internal static class Assets
{
    public static MeshData Grid(int size)
    {
        var vertices = new MeshVertex[checked((size + 1) * (size + 1))];
        var indices = new uint[checked(size * size * 6)];
        for (var y = 0; y <= size; y++) {
            for (var x = 0; x <= size; x++) {
                var px = x * 1.6f / size - 0.8f;
                var py = y * 1.6f / size - 0.8f;
                var z = 0.5f + 0.03f * MathF.Sin(px * 8) * MathF.Cos(py * 6);
                var dx = 0.24f * MathF.Cos(px * 8) * MathF.Cos(py * 6);
                var dy = -0.18f * MathF.Sin(px * 8) * MathF.Sin(py * 6);
                vertices[y * (size + 1) + x] = new(new(px, py, z), math.normalize(new float3(-dx, -dy, 1)), new((float)x / size, (float)y / size));
            }
        }
        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                var a = (uint)(y * (size + 1) + x);
                var b = a + (uint)size + 1;
                var offset = (y * size + x) * 6;
                indices[offset] = a; indices[offset + 1] = a + 1; indices[offset + 2] = b;
                indices[offset + 3] = a + 1; indices[offset + 4] = b + 1; indices[offset + 5] = b;
            }
        }
        return new(vertices, indices, default);
    }

    public static VisibilityInstance[] Instances(string scenario) => scenario switch {
        "instanced" => Enumerable.Range(0, 1025).Select(i => new VisibilityInstance(
            float4x4.Translate(new float3(i == 0 ? 0 : 3, 0, 0)), PbrMaterial.Default)).ToArray(),
        "occluded" => Enumerable.Range(0, 4).Select(i => new VisibilityInstance(
            float4x4.Translate(new float3(0, 0, i * 0.08f)), PbrMaterial.Default)).ToArray(),
        "offscreen" => Enumerable.Range(0, 4).Select(i => new VisibilityInstance(
            float4x4.Translate(new float3(i * 3, 0, 0)), PbrMaterial.Default)).ToArray(),
        "nonuniform" => [new(float4x4.Scale(new float3(0.5f, 0.9f, 0.8f)), PbrMaterial.Default)],
        _ => [new(float4x4.identity, PbrMaterial.Default)]
    };

    public static float4x4 Projection(string scenario) => scenario == "far"
        ? float4x4.Scale(new float3(0.2f, 0.2f, 1)) : float4x4.identity;
    public static MeshData Terrain(int size)
    {
        var vertices = new MeshVertex[checked((size + 1) * (size + 1))];
        for (var j = 0; j <= size; j++) {
            for (var i = 0; i <= size; i++) { vertices[j * (size + 1) + i] = TerrainVertex(i, j); }
        }
        var indices = new List<uint>(checked(size * size * 6));
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
