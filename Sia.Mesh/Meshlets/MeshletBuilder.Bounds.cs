using Sia.Math;

namespace Sia.Engine.Mesh;

// Algorithm: https://github.com/zeux/meshoptimizer/blob/v1.2/src/meshletutils.cpp
// License: https://github.com/zeux/meshoptimizer/blob/v1.2/LICENSE.md
public static partial class MeshletBuilder
{
    private static MeshletBounds ComputeBounds(
        ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> references, ReadOnlySpan<byte> triangles)
    {
        var min = vertices[(int)references[0]].Position;
        var max = min;
        foreach (var reference in references) {
            var position = vertices[(int)reference].Position;
            min = math.min(min, position);
            max = math.max(max, position);
        }
        var center = new float3(
            (float)(((double)min.x + max.x) * 0.5),
            (float)(((double)min.y + max.y) * 0.5),
            (float)(((double)min.z + max.z) * 0.5));
        double radiusSquared = 0;
        foreach (var reference in references) {
            var position = vertices[(int)reference].Position;
            var x = (double)position.x - center.x;
            var y = (double)position.y - center.y;
            var z = (double)position.z - center.z;
            radiusSquared = System.Math.Max(radiusSquared, x * x + y * y + z * z);
        }
        var radius = radiusSquared == 0 ? 0 : MathF.BitIncrement((float)System.Math.Sqrt(radiusSquared));
        double axisX = 0, axisY = 0, axisZ = 0;
        for (var i = 0; i < triangles.Length; i += 3) {
            var normal = TriangleNormal(vertices, references, triangles.Slice(i, 3));
            axisX += normal.X;
            axisY += normal.Y;
            axisZ += normal.Z;
        }
        var axisLength = System.Math.Sqrt(axisX * axisX + axisY * axisY + axisZ * axisZ);
        var disabled = new MeshletBounds(new(min, max), center, radius, float3.zero, 1);
        if (axisLength == 0) {
            return disabled;
        }
        axisX /= axisLength;
        axisY /= axisLength;
        axisZ /= axisLength;
        var axis = new float3((float)axisX, (float)axisY, (float)axisZ);
        double minDot = 1;
        double maxSin = 0;
        for (var i = 0; i < triangles.Length; i += 3) {
            var normal = TriangleNormal(vertices, references, triangles.Slice(i, 3));
            if (normal.X == 0 && normal.Y == 0 && normal.Z == 0) {
                continue;
            }
            minDot = System.Math.Min(minDot, normal.X * axisX + normal.Y * axisY + normal.Z * axisZ);
            var crossX = normal.Y * axisZ - normal.Z * axisY;
            var crossY = normal.Z * axisX - normal.X * axisZ;
            var crossZ = normal.X * axisY - normal.Y * axisX;
            maxSin = System.Math.Max(maxSin, System.Math.Sqrt(crossX * crossX + crossY * crossY + crossZ * crossZ));
        }
        if (minDot <= 0.1 || !float.IsFinite(radius)) {
            return disabled;
        }
        var errorX = axis.x - axisX;
        var errorY = axis.y - axisY;
        var errorZ = axis.z - axisZ;
        var axisError = System.Math.Sqrt(errorX * errorX + errorY * errorY + errorZ * errorZ);
        var cutoff = System.Math.Min(1.0f, MathF.BitIncrement((float)(maxSin + axisError + 1e-6)));
        return new(new(min, max), center, radius, axis, cutoff);
    }

    private static (double X, double Y, double Z) TriangleNormal(
        ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> references, ReadOnlySpan<byte> triangle)
    {
        var a = vertices[(int)references[triangle[0]]].Position;
        var b = vertices[(int)references[triangle[1]]].Position;
        var c = vertices[(int)references[triangle[2]]].Position;
        var abX = (double)b.x - a.x;
        var abY = (double)b.y - a.y;
        var abZ = (double)b.z - a.z;
        var acX = (double)c.x - a.x;
        var acY = (double)c.y - a.y;
        var acZ = (double)c.z - a.z;
        var x = abY * acZ - abZ * acY;
        var y = abZ * acX - abX * acZ;
        var z = abX * acY - abY * acX;
        var length = System.Math.Sqrt(x * x + y * y + z * z);
        return length == 0 ? (0, 0, 0) : (x / length, y / length, z / length);
    }
}
