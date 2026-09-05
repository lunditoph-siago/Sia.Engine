using Sia.Math;

namespace Sia.Engine.Mesh;

public readonly record struct Meshlet(
    int VertexOffset,
    int VertexCount,
    int TriangleOffset,
    int TriangleCount,
    MeshletBounds Bounds);

public readonly record struct MeshletBounds(
    Aabb Box,
    float3 Center,
    float Radius,
    float3 ConeAxis,
    float ConeCutoff)
{
    public bool HasNormalCone => ConeCutoff < 1.0f && math.lengthsq(ConeAxis) > 0.0f;
}

public sealed record MeshletData(
    Meshlet[] Meshlets,
    uint[] VertexIndices,
    byte[] TriangleIndices,
    uint[] SourceTriangleIndices);
