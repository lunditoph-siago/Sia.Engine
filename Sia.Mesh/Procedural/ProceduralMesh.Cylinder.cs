using Sia.Math;

namespace Sia.Engine.Mesh;

public static partial class ProceduralMesh
{
    public static MeshData Cylinder(
        float radius = 0.5f,
        float height = 1.0f,
        int radialSegments = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (radialSegments < 3) {
            throw new ArgumentOutOfRangeException(nameof(radialSegments));
        }

        var ringSize = radialSegments + 1;
        var sideVertexCount = ringSize * 2;
        var capVertexCount = ringSize + 1;
        var vertices = new MeshVertex[sideVertexCount + capVertexCount * 2];
        var indices = new uint[radialSegments * 12];
        var halfHeight = height * 0.5f;

        for (var segment = 0; segment <= radialSegments; segment++) {
            var ratio = (float)segment / radialSegments;
            var angle = ratio * 2.0f * MathF.PI;
            var normal = new float3(MathF.Cos(angle), 0.0f, MathF.Sin(angle));
            var offset = normal * radius;

            vertices[segment] = new MeshVertex(
                offset + new float3(0.0f, -halfHeight, 0.0f),
                normal,
                new float2(ratio, 0.0f));
            vertices[ringSize + segment] = new MeshVertex(
                offset + new float3(0.0f, halfHeight, 0.0f),
                normal,
                new float2(ratio, 1.0f));
        }

        var bottomCenter = sideVertexCount;
        var bottomRing = bottomCenter + 1;
        var topCenter = bottomRing + ringSize;
        var topRing = topCenter + 1;
        vertices[bottomCenter] = new MeshVertex(
            new float3(0.0f, -halfHeight, 0.0f),
            new float3(0.0f, -1.0f, 0.0f),
            new float2(0.5f, 0.5f));
        vertices[topCenter] = new MeshVertex(
            new float3(0.0f, halfHeight, 0.0f),
            new float3(0.0f, 1.0f, 0.0f),
            new float2(0.5f, 0.5f));

        for (var segment = 0; segment <= radialSegments; segment++) {
            var ratio = (float)segment / radialSegments;
            var angle = ratio * 2.0f * MathF.PI;
            var x = MathF.Cos(angle);
            var z = MathF.Sin(angle);
            var offset = new float3(x * radius, 0.0f, z * radius);
            var uv = new float2(x * 0.5f + 0.5f, z * 0.5f + 0.5f);
            vertices[bottomRing + segment] = new MeshVertex(
                offset + new float3(0.0f, -halfHeight, 0.0f),
                new float3(0.0f, -1.0f, 0.0f),
                uv);
            vertices[topRing + segment] = new MeshVertex(
                offset + new float3(0.0f, halfHeight, 0.0f),
                new float3(0.0f, 1.0f, 0.0f),
                uv);
        }

        var cursor = 0;
        for (var segment = 0; segment < radialSegments; segment++) {
            var bottom = (uint)segment;
            var top = (uint)(ringSize + segment);
            indices[cursor++] = bottom;
            indices[cursor++] = top;
            indices[cursor++] = bottom + 1;
            indices[cursor++] = bottom + 1;
            indices[cursor++] = top;
            indices[cursor++] = top + 1;

            indices[cursor++] = (uint)bottomCenter;
            indices[cursor++] = (uint)(bottomRing + segment);
            indices[cursor++] = (uint)(bottomRing + segment + 1);
            indices[cursor++] = (uint)topCenter;
            indices[cursor++] = (uint)(topRing + segment + 1);
            indices[cursor++] = (uint)(topRing + segment);
        }

        return new MeshData(
            vertices,
            indices,
            new Aabb(
                new float3(-radius, -halfHeight, -radius),
                new float3(radius, halfHeight, radius)));
    }
}
