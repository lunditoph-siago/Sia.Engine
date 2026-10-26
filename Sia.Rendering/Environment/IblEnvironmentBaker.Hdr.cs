using Sia.Math;

namespace Sia.Engine.Rendering;

public static partial class IblEnvironmentBaker
{
    /// <summary>Conservative principal-array admission for RGBE input, projection, filtering and output. Excludes caller-owned stream/template and object headers.</summary>
    public static ulong HdrWorkingBytes(int width, int height)
    {
        if (width <= 0 || height <= 0 || (ulong)width * (ulong)height > int.MaxValue / 4)
            throw new ArgumentOutOfRangeException(nameof(width), "HDR dimensions exceed the packed-array limit.");
        return checked((ulong)width * (ulong)height * 4 + 6ul * IblEnvironmentAsset.CubeSize * IblEnvironmentAsset.CubeSize * 4 * 8
            + RadianceWorkingBytes + 2ul * IblEnvironmentAsset.EncodedBytes + 128 * 1024);
    }

    /// <summary>
    /// Reads a borrowed modern-RLE linear RGBE panorama (-Y/+X). Applies intensity once, integrates every
    /// source texel into physical SH and a flux-normalized 128px cube, then uses the existing GGX filter.
    /// Panorama center faces -Z, +Y is up and longitude increases toward +X. An explicit orthonormal
    /// source-to-world basis can rotate or reflect that convention. Only the template's BRDF LUT is reused.
    /// No world/GPU effects; cancellation or failure returns no asset and never closes the input stream.
    /// </summary>
    public static IblEnvironmentAsset BakeHdr(Stream source, IblEnvironmentAsset brdfSource, float intensity = 1,
        float3x3? sourceToWorld = null, int filterSamples = 256, ulong maximumInputBytes = 128ul * 1024 * 1024,
        ulong maximumWorkingBytes = 64ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(brdfSource);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead) throw new ArgumentException("HDR source must be readable.", nameof(source));
        if (!float.IsFinite(intensity) || intensity < 0) throw new ArgumentOutOfRangeException(nameof(intensity));
        if (filterSamples is < 16 or > 1024) throw new ArgumentOutOfRangeException(nameof(filterSamples));
        if (maximumInputBytes == 0) throw new ArgumentOutOfRangeException(nameof(maximumInputBytes));
        if (maximumWorkingBytes < HdrWorkingBytes(8, 1)) throw new ArgumentException("HDR fixed working arrays exceed the budget.", nameof(maximumWorkingBytes));
        var basis = sourceToWorld ?? float3x3.identity;
        if (!math.all(math.isfinite(basis.c0)) || !math.all(math.isfinite(basis.c1)) || !math.all(math.isfinite(basis.c2))
            || MathF.Abs(math.dot(basis.c0, basis.c0) - 1) > .00001f || MathF.Abs(math.dot(basis.c1, basis.c1) - 1) > .00001f
            || MathF.Abs(math.dot(basis.c2, basis.c2) - 1) > .00001f || MathF.Abs(math.dot(basis.c0, basis.c1)) > .00001f
            || MathF.Abs(math.dot(basis.c0, basis.c2)) > .00001f || MathF.Abs(math.dot(basis.c1, basis.c2)) > .00001f)
            throw new ArgumentException("HDR source-to-world basis must be finite and orthonormal.", nameof(sourceToWorld));
        var (width, height, pixels) = ReadHdr(source, maximumInputBytes, maximumWorkingBytes, cancellationToken);
        const int size = IblEnvironmentAsset.CubeSize, count = 6 * size * size;
        var bins = new double[count * 4]; var coefficients = new double[27];
        Span<double> shBasis = stackalloc double[9];
        for (var y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            var theta = System.Math.PI * (y + .5) / height;
            var sine = System.Math.Sin(theta); var cosine = System.Math.Cos(theta);
            var solidAngle = 2 * System.Math.PI / width * (System.Math.Cos(System.Math.PI * y / height) - System.Math.Cos(System.Math.PI * (y + 1) / height));
            for (var x = 0; x < width; x++) {
                var phi = System.Math.PI * (2 * (x + .5) / width - 1);
                var direction = math.mul(basis, new float3((float)(sine * System.Math.Sin(phi)), (float)cosine, (float)(-sine * System.Math.Cos(phi))));
                var rgb = DecodeHdr(pixels, (y * width + x) * 4, intensity);
                var p = Project(direction, size);
                var bx = System.Math.Clamp((int)MathF.Floor(p.X + .5f), 0, size - 1);
                var by = System.Math.Clamp((int)MathF.Floor(p.Y + .5f), 0, size - 1);
                var at = ((p.Face * size + by) * size + bx) * 4;
                bins[at] += rgb.x * solidAngle; bins[at + 1] += rgb.y * solidAngle; bins[at + 2] += rgb.z * solidAngle; bins[at + 3] += solidAngle;
                var dx = (double)direction.x; var dy = (double)direction.y; var dz = (double)direction.z;
                shBasis[0] = .282095; shBasis[1] = .488603 * dy; shBasis[2] = .488603 * dz; shBasis[3] = .488603 * dx;
                shBasis[4] = 1.092548 * dx * dy; shBasis[5] = 1.092548 * dy * dz; shBasis[6] = .315392 * (3 * dz * dz - 1);
                shBasis[7] = 1.092548 * dx * dz; shBasis[8] = .546274 * (dx * dx - dy * dy);
                for (var lm = 0; lm < 9; lm++) {
                    var weight = shBasis[lm] * solidAngle;
                    coefficients[lm * 3] += rgb.x * weight; coefficients[lm * 3 + 1] += rgb.y * weight; coefficients[lm * 3 + 2] += rgb.z * weight;
                }
            }
        }
        var radiance = new float3[count]; var cubeEnergy = new double[3];
        for (var face = 0; face < 6; face++) for (var y = 0; y < size; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < size; x++) {
                var at = (face * size + y) * size + x; var bin = at * 4; var weight = bins[bin + 3];
                var value = weight > 0 ? new float3((float)(bins[bin] / weight), (float)(bins[bin + 1] / weight), (float)(bins[bin + 2] / weight))
                    : SampleHdr(pixels, width, height, new(math.dot(basis.c0, CubeDirection(face, x, y, size)), math.dot(basis.c1, CubeDirection(face, x, y, size)), math.dot(basis.c2, CubeDirection(face, x, y, size))), intensity);
                radiance[at] = value;
                var angle = HdrCubeSolidAngle(x, y, size);
                cubeEnergy[0] += value.x * angle; cubeEnergy[1] += value.y * angle; cubeEnergy[2] += value.z * angle;
            }
        }
        // Bin footprints differ from exact cube footprints. Restore each channel's full-source L0 flux.
        var correction = new float3((float)(cubeEnergy[0] > 0 ? coefficients[0] / (.282095 * cubeEnergy[0]) : 0),
            (float)(cubeEnergy[1] > 0 ? coefficients[1] / (.282095 * cubeEnergy[1]) : 0), (float)(cubeEnergy[2] > 0 ? coefficients[2] / (.282095 * cubeEnergy[2]) : 0));
        for (var at = 0; at < count; at++) {
            if ((at & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = radiance[at] * correction;
            if (!math.all(math.isfinite(value)) || math.any(value > 1e18f)) throw new InvalidDataException("Projected HDR cube exceeds the transport range.");
            radiance[at] = value;
        }
        var sh = new float4[9];
        for (var lm = 0; lm < sh.Length; lm++) sh[lm] = new((float)coefficients[lm * 3], (float)coefficients[lm * 3 + 1], (float)coefficients[lm * 3 + 2], 0);
        var environment = new IblEnvironmentAsset(new() { Intensity = 0 }, sh, new Half[IblEnvironmentAsset.CubeTexels * 4], brdfSource.BrdfLut.ToArray());
        return FilterRadiance(environment, radiance, filterSamples, cancellationToken);
    }

    private static float3 DecodeHdr(byte[] pixels, int at, float intensity)
    {
        var exponent = pixels[at + 3];
        if (exponent == 0) return float3.zero;
        var factor = MathF.ScaleB(1, exponent - 136);
        var value = new float3(pixels[at] * factor, pixels[at + 1] * factor, pixels[at + 2] * factor) * intensity;
        if (!math.all(math.isfinite(value)) || math.any(value > 1e18f)) throw new InvalidDataException("HDR radiance exceeds the finite nonnegative transport range.");
        return value;
    }

    private static float3 SampleHdr(byte[] pixels, int width, int height, float3 direction, float intensity)
    {
        var px = (MathF.Atan2(direction.x, -direction.z) / (2 * MathF.PI) + .5f) * width - .5f;
        var py = MathF.Acos(System.Math.Clamp(direction.y, -1, 1)) / MathF.PI * height - .5f;
        var x = (int)MathF.Floor(px); var y = (int)MathF.Floor(py);
        float3 Fetch(int ix, int iy) => DecodeHdr(pixels, (System.Math.Clamp(iy, 0, height - 1) * width + (ix % width + width) % width) * 4, intensity);
        return math.lerp(math.lerp(Fetch(x, y), Fetch(x + 1, y), px - x), math.lerp(Fetch(x, y + 1), Fetch(x + 1, y + 1), px - x), py - y);
    }

    private static double HdrCubeSolidAngle(int x, int y, int size)
    {
        static double Area(double u, double v) => System.Math.Atan2(u * v, System.Math.Sqrt(u * u + v * v + 1));
        var u0 = 2.0 * x / size - 1; var u1 = 2.0 * (x + 1) / size - 1;
        var v0 = 2.0 * y / size - 1; var v1 = 2.0 * (y + 1) / size - 1;
        return Area(u1, v1) - Area(u0, v1) - Area(u1, v0) + Area(u0, v0);
    }
}
