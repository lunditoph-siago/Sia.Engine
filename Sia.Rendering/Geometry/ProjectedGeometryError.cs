using Sia.Math;

namespace Sia.Engine.Rendering;

public static class ProjectedGeometryError
{
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
