using Sia.Math;

namespace Sia.Engine.Mesh;

public static partial class ProceduralMesh
{
    public static MeshData Capsule(
        float radius = 0.5f,
        float halfLength = 0.5f,
        int hemisphereSegments = 8,
        int radialSegments = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfNegative(halfLength);
        if (hemisphereSegments < 1) {
            throw new ArgumentOutOfRangeException(nameof(hemisphereSegments));
        }
        if (radialSegments < 3) {
            throw new ArgumentOutOfRangeException(nameof(radialSegments));
        }

        var ringSize = radialSegments + 1;
        var ringCount = (hemisphereSegments + 1) * 2;
        var vertices = new MeshVertex[ringSize * ringCount];

        for (var hemisphere = 0; hemisphere < 2; hemisphere++) {
            var centerY = hemisphere == 0 ? -halfLength : halfLength;
            for (var latitude = 0; latitude <= hemisphereSegments; latitude++) {
                var ratio = (float)latitude / hemisphereSegments;
                var angle = hemisphere == 0
                    ? -MathF.PI * 0.5f + ratio * MathF.PI * 0.5f
                    : ratio * MathF.PI * 0.5f;
                var radial = MathF.Cos(angle);
                var vertical = MathF.Sin(angle);
                var ring = hemisphere * (hemisphereSegments + 1) + latitude;

                for (var longitude = 0; longitude <= radialSegments; longitude++) {
                    var longitudeRatio = (float)longitude / radialSegments;
                    var longitudeAngle = longitudeRatio * 2.0f * MathF.PI;
                    var normal = new float3(
                        radial * MathF.Cos(longitudeAngle),
                        vertical,
                        radial * MathF.Sin(longitudeAngle));
                    var position = normal * radius + new float3(0.0f, centerY, 0.0f);
                    var vertex = ring * ringSize + longitude;
                    vertices[vertex] = new MeshVertex(
                        position,
                        normal,
                        new float2(
                            longitudeRatio,
                            (position.y + halfLength + radius) / (2.0f * (halfLength + radius))));
                }
            }
        }

        var indices = new uint[(ringCount - 1) * radialSegments * 6];
        var cursor = 0;
        for (var ring = 0; ring < ringCount - 1; ring++) {
            for (var longitude = 0; longitude < radialSegments; longitude++) {
                var current = (uint)(ring * ringSize + longitude);
                var next = current + 1;
                var above = (uint)((ring + 1) * ringSize + longitude);
                var aboveNext = above + 1;
                indices[cursor++] = current;
                indices[cursor++] = above;
                indices[cursor++] = next;
                indices[cursor++] = next;
                indices[cursor++] = above;
                indices[cursor++] = aboveNext;
            }
        }

        var halfHeight = halfLength + radius;
        return new MeshData(
            vertices,
            indices,
            new Aabb(
                new float3(-radius, -halfHeight, -radius),
                new float3(radius, halfHeight, radius)));
    }
}
