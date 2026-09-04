using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Debug;

public sealed class DebugDrawList
{
    private readonly List<DebugVertex> _vertices;

    public DebugDrawList(int initialCapacity = 32_768)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _vertices = new List<DebugVertex>(initialCapacity);
    }

    public int VertexCount => _vertices.Count;

    public int TriangleCount => _vertices.Count / 3;

    public ReadOnlySpan<DebugVertex> Vertices => CollectionsMarshal.AsSpan(_vertices);

    public void Clear() => _vertices.Clear();

    public void AddTriangle(float3 first, float3 second, float3 third, float4 color)
    {
        var cross = math.cross(second - first, third - first);
        var lengthSquared = math.lengthsq(cross);
        if (!(lengthSquared > 1e-12f) || !math.isfinite(lengthSquared)) {
            return;
        }

        var normal = cross * math.rsqrt(lengthSquared);
        _vertices.Add(new DebugVertex(first, normal, color));
        _vertices.Add(new DebugVertex(second, normal, color));
        _vertices.Add(new DebugVertex(third, normal, color));
    }

    public void AddQuad(float3 first, float3 second, float3 third, float3 fourth, float4 color)
    {
        AddTriangle(first, second, third, color);
        AddTriangle(first, third, fourth, color);
    }

    public void AddBox(in RigidTransform transform, float3 halfExtents, float4 color)
    {
        Span<float3> vertices = [
            new(-halfExtents.x, -halfExtents.y, -halfExtents.z),
            new(halfExtents.x, -halfExtents.y, -halfExtents.z),
            new(halfExtents.x, halfExtents.y, -halfExtents.z),
            new(-halfExtents.x, halfExtents.y, -halfExtents.z),
            new(-halfExtents.x, -halfExtents.y, halfExtents.z),
            new(halfExtents.x, -halfExtents.y, halfExtents.z),
            new(halfExtents.x, halfExtents.y, halfExtents.z),
            new(-halfExtents.x, halfExtents.y, halfExtents.z),
        ];
        ReadOnlySpan<int3> triangles = [
            new(0, 2, 1), new(0, 3, 2),
            new(4, 5, 6), new(4, 6, 7),
            new(0, 1, 5), new(0, 5, 4),
            new(3, 7, 6), new(3, 6, 2),
            new(0, 4, 7), new(0, 7, 3),
            new(1, 2, 6), new(1, 6, 5),
        ];
        foreach (ref readonly var triangle in triangles) {
            AddTriangle(
                math.transform(transform, vertices[triangle.x]),
                math.transform(transform, vertices[triangle.y]),
                math.transform(transform, vertices[triangle.z]),
                color);
        }
    }

    public void AddSphere(
        in RigidTransform transform,
        float radius,
        float4 color,
        int longitudeSegments = 12,
        int latitudeSegments = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        if (longitudeSegments < 3) {
            throw new ArgumentOutOfRangeException(nameof(longitudeSegments));
        }
        if (latitudeSegments < 2) {
            throw new ArgumentOutOfRangeException(nameof(latitudeSegments));
        }

        for (var latitude = 0; latitude < latitudeSegments; latitude++) {
            var phi0 = -math.PI * 0.5f + (float)latitude / latitudeSegments * math.PI;
            var phi1 = -math.PI * 0.5f + (float)(latitude + 1) / latitudeSegments * math.PI;
            for (var longitude = 0; longitude < longitudeSegments; longitude++) {
                var theta0 = longitude * 2.0f * math.PI / longitudeSegments;
                var theta1 = (longitude + 1) * 2.0f * math.PI / longitudeSegments;
                var first = SpherePoint(phi0, theta0) * radius;
                var second = SpherePoint(phi0, theta1) * radius;
                var third = SpherePoint(phi1, theta1) * radius;
                var fourth = SpherePoint(phi1, theta0) * radius;
                if (latitude > 0) {
                    AddTriangle(
                        math.transform(transform, first),
                        math.transform(transform, third),
                        math.transform(transform, second),
                        color);
                }
                if (latitude + 1 < latitudeSegments) {
                    AddTriangle(
                        math.transform(transform, first),
                        math.transform(transform, fourth),
                        math.transform(transform, third),
                        color);
                }
            }
        }
    }

    public void AddCapsule(
        in RigidTransform transform,
        float radius,
        float halfLength,
        float4 color,
        int sideCount = 10,
        int hemisphereSegments = 6)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfNegative(halfLength);
        if (sideCount < 3) {
            throw new ArgumentOutOfRangeException(nameof(sideCount));
        }
        if (hemisphereSegments < 1) {
            throw new ArgumentOutOfRangeException(nameof(hemisphereSegments));
        }
        if (halfLength == 0.0f) {
            AddSphere(
                transform,
                radius,
                color,
                sideCount,
                hemisphereSegments * 2);
            return;
        }

        var ringCount = (hemisphereSegments + 1) * 2;
        for (var ring = 0; ring < ringCount - 1; ring++) {
            for (var side = 0; side < sideCount; side++) {
                var first = CapsulePoint(
                    in transform,
                    radius,
                    halfLength,
                    hemisphereSegments,
                    sideCount,
                    ring,
                    side);
                var second = CapsulePoint(
                    in transform,
                    radius,
                    halfLength,
                    hemisphereSegments,
                    sideCount,
                    ring + 1,
                    side);
                var third = CapsulePoint(
                    in transform,
                    radius,
                    halfLength,
                    hemisphereSegments,
                    sideCount,
                    ring,
                    side + 1);
                var fourth = CapsulePoint(
                    in transform,
                    radius,
                    halfLength,
                    hemisphereSegments,
                    sideCount,
                    ring + 1,
                    side + 1);
                AddTriangle(first, second, third, color);
                AddTriangle(third, second, fourth, color);
            }
        }
    }

    public void AddLine(float3 start, float3 end, float thickness, float4 color, int sideCount = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thickness);
        if (sideCount < 3) {
            throw new ArgumentOutOfRangeException(nameof(sideCount));
        }
        AddCylinderBetween(start, end, thickness, sideCount, color);
    }

    public void AddMarker(float3 position, float radius, float4 color) =>
        AddSphere(RigidTransform.Translate(position), radius, color, 10, 6);

    private void AddCylinderBetween(
        float3 first,
        float3 second,
        float radius,
        int sideCount,
        float4 color)
    {
        var axis = second - first;
        var lengthSquared = math.lengthsq(axis);
        if (!(lengthSquared > 1e-12f)) {
            return;
        }

        var direction = axis * math.rsqrt(lengthSquared);
        var reference = math.abs(direction.y) < 0.9f
            ? new float3(0.0f, 1.0f, 0.0f)
            : new float3(1.0f, 0.0f, 0.0f);
        var tangent = math.normalize(math.cross(direction, reference));
        var bitangent = math.cross(direction, tangent);
        for (var side = 0; side < sideCount; side++) {
            var angle0 = side * 2.0f * math.PI / sideCount;
            var angle1 = (side + 1) * 2.0f * math.PI / sideCount;
            var offset0 = (tangent * math.cos(angle0) + bitangent * math.sin(angle0)) * radius;
            var offset1 = (tangent * math.cos(angle1) + bitangent * math.sin(angle1)) * radius;
            AddQuad(
                first + offset0,
                first + offset1,
                second + offset1,
                second + offset0,
                color);
        }
    }

    private static float3 SpherePoint(float latitude, float longitude)
    {
        var radial = math.cos(latitude);
        return new float3(
            radial * math.cos(longitude),
            math.sin(latitude),
            radial * math.sin(longitude));
    }

    private static float3 CapsulePoint(
        in RigidTransform transform,
        float radius,
        float halfLength,
        int hemisphereSegments,
        int sideCount,
        int ring,
        int side)
    {
        var upper = ring > hemisphereSegments;
        var latitude = upper ? ring - hemisphereSegments - 1 : ring;
        var ratio = (float)latitude / hemisphereSegments;
        var angle = upper
            ? ratio * math.PI * 0.5f
            : -math.PI * 0.5f + ratio * math.PI * 0.5f;
        var longitude = side * 2.0f * math.PI / sideCount;
        var normal = SpherePoint(angle, longitude);
        var center = new float3(0.0f, upper ? halfLength : -halfLength, 0.0f);
        return math.transform(transform, center + normal * radius);
    }
}
