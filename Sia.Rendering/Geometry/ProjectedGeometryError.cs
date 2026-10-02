using Sia.Math;

namespace Sia.Engine.Rendering;

public static class ProjectedGeometryError
{
    public readonly record struct LodProjection(float4 EyeNear, float4 ForwardPixels);

    public static LodProjection PrepareLodProjection(in float4x4 matrix, uint width, uint height)
    {
        var x = new float3(matrix.c0.x, matrix.c1.x, matrix.c2.x);
        var y = new float3(matrix.c0.y, matrix.c1.y, matrix.c2.y);
        var w = new float3(matrix.c0.w, matrix.c1.w, matrix.c2.w);
        var length = math.length(w);
        if (!float.IsFinite(length)) return default;
        if (length == 0) {
            if (!float.IsFinite(matrix.c3.w) || matrix.c3.w <= 0) return default;
            var pixels = .5f * MathF.Max(width * math.length(x), height * math.length(y)) / matrix.c3.w;
            return float.IsFinite(pixels) ? new(default, new(0, 0, 0, pixels)) : default;
        }
        var inverse = math.inverse(matrix);
        var eye = inverse.c2.xyz / inverse.c2.w;
        var forward = w / length;
        var z = new float3(matrix.c0.z, matrix.c1.z, matrix.c2.z);
        var depthScale = math.dot(z, forward);
        var near = -(math.dot(z, eye) + matrix.c3.z) / depthScale;
        var scale = .5f * MathF.Max(width * math.length(x - forward * math.dot(x, forward)),
            height * math.length(y - forward * math.dot(y, forward))) / length;
        if (!math.all(math.isfinite(eye)) || !float.IsFinite(near) || near <= 0
            || depthScale <= 0 || !float.IsFinite(scale) || scale <= 0) return default;
        return new(new(eye, near), new(forward, scale));
    }

    public static float ProjectLodError(Aabb bounds, float spatialError, in LodProjection projection,
        in float4x4 matrix, uint width, uint height, float sphereRadius = -1)
    {
        if (projection.ForwardPixels.w == 0) return ProjectError(bounds, spatialError, matrix, width, height);
        if (spatialError == 0) return 0;
        if (math.lengthsq(projection.ForwardPixels.xyz) == 0) return spatialError * projection.ForwardPixels.w;
        var center = (bounds.Min + bounds.Max) * .5f - projection.EyeNear.xyz;
        var radius = sphereRadius >= 0 ? sphereRadius : math.length((bounds.Max - bounds.Min) * .5f);
        var distanceSquared = math.lengthsq(center);
        var z = math.dot(center, projection.ForwardPixels.xyz);
        var radial = MathF.Sqrt(MathF.Max(0, distanceSquared - z * z));
        var tangent = MathF.Sqrt(MathF.Max(0, distanceSquared - radius * radius));
        var cosine = (z * tangent - radial * radius) / MathF.Max(distanceSquared, 1e-20f);
        var near = projection.EyeNear.w;
        if (distanceSquared <= radius * radius || cosine * tangent < near) {
            var h = near - z;
            var farIntersection = radial + MathF.Sqrt(MathF.Max(0, radius * radius - h * h));
            cosine = near / MathF.Sqrt(farIntersection * farIntersection + near * near);
        }
        var edgeScale = MathF.Max(z - radius, near) * MathF.Max(cosine, 1e-6f);
        var result = spatialError * projection.ForwardPixels.w / edgeScale;
        return float.IsFinite(result) ? result : float.PositiveInfinity;
    }

    public static float ProjectError(Aabb bounds, float spatialError, in float4x4 matrix, uint width, uint height)
    {
        if (spatialError == 0) return 0;
        var minW = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        double maxX = 0, maxY = 0;
        for (var corner = 0; corner < 8; corner++) {
            var point = new float4((corner & 1) == 0 ? bounds.Min.x : bounds.Max.x,
                (corner & 2) == 0 ? bounds.Min.y : bounds.Max.y,
                (corner & 4) == 0 ? bounds.Min.z : bounds.Max.z, 1);
            var clip = math.mul(matrix, point);
            if (!math.all(math.isfinite(clip)) || clip.w <= 0) return float.PositiveInfinity;
            minW = System.Math.Min(minW, clip.w);
            minZ = System.Math.Min(minZ, clip.z);
            maxX = System.Math.Max(maxX, System.Math.Abs((double)clip.x / clip.w));
            maxY = System.Math.Max(maxY, System.Math.Abs((double)clip.y / clip.w));
        }
        var xLength = math.length(new double3(matrix.c0.x, matrix.c1.x, matrix.c2.x));
        var yLength = math.length(new double3(matrix.c0.y, matrix.c1.y, matrix.c2.y));
        var zLength = math.length(new double3(matrix.c0.z, matrix.c1.z, matrix.c2.z));
        var wLength = math.length(new double3(matrix.c0.w, matrix.c1.w, matrix.c2.w));
        var error = (double)spatialError;
        var wError = error * wLength;
        var zError = error * zLength;
        if (minW <= wError || minZ <= zError) return float.PositiveInfinity;
        var dx = error * (xLength + (maxX * wLength));
        var dy = error * (yLength + (maxY * wLength));
        return (float)(0.5 * System.Math.Max(width * dx, height * dy) / (minW - wError));
    }
}
