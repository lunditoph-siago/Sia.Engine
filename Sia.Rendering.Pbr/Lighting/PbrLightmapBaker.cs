using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed record PbrLightmapBakeSettings
{
    public ProceduralSky Sky { get; init; } = new();
    public float3 TowardLight { get; init; } = new(0, 1, 0);
    public float3 LightRadiance { get; init; }
    public int Samples { get; init; } = 128;
    public float MaximumDistance { get; init; } = 1000;
    public float RayBias { get; init; } = .002f;

    internal void Validate()
    {
        static bool Color(float3 v) => math.all(math.isfinite(v) & (v >= 0));
        ArgumentNullException.ThrowIfNull(Sky);
        if (!Color(Sky.Horizon) || !Color(Sky.Zenith) || !Color(Sky.Ground) || !Color(Sky.SunRadiance)
            || !math.all(math.isfinite(Sky.SunDirection)) || math.lengthsq(Sky.SunDirection) < 1e-8f
            || !float.IsFinite(math.lengthsq(Sky.SunDirection)) || !float.IsFinite(Sky.SunExponent) || Sky.SunExponent < 1
            || !float.IsFinite(Sky.Intensity) || Sky.Intensity < 0 || !Color(LightRadiance)
            || !math.all(math.isfinite(TowardLight)) || !float.IsFinite(math.lengthsq(TowardLight)) || math.lengthsq(TowardLight) < 1e-8f
            || Samples is < 16 or > 1024 || !float.IsFinite(MaximumDistance) || MaximumDistance <= .001f
            || !float.IsFinite(RayBias) || RayBias <= 0 || RayBias >= MaximumDistance)
            throw new ArgumentException("Invalid surface bake lighting or integration settings.");
    }
}

public static partial class PbrLightmapBaker
{
    /// <summary>Offline CPU BVH integration: sky visibility, emission and one visibility-tested directional bounce.</summary>
    public static PbrLightmapAsset Bake(PbrLightmapInput input, PbrLightmapBakeSettings? settings = null,
        ulong maximumTraceBytes = 128ul * 1024 * 1024, ulong maximumCoefficientBytes = 32ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var options = settings ?? new();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if ((ulong)input.Texels.Length * 4 * 16 > maximumCoefficientBytes)
            throw new InvalidOperationException("Surface coefficients exceed the configured CPU integration budget.");
        var tracing = PbrSceneTransport.BuildStatic(input.Scene, maximumTraceBytes, finest: true).Packed.ToArray();
        var coefficients = Integrate(input, options, tracing, Directions(options), cancellationToken);
        return new(input.Resolution, input.Receivers.Span, PbrLightmapAsset.Identity(input.Scene), input.SurfaceIdentity.Span, options, coefficients, input.Charts.Span);
    }

    private static float3[] Directions(PbrLightmapBakeSettings options)
    {
        var directions = new float3[options.Samples];
        for (var i = 0; i < directions.Length; i++) {
            var y = 1 - 2 * (i + .5f) / options.Samples;
            var radius = MathF.Sqrt(MathF.Max(0, 1 - y * y));
            var theta = 2.39996323f * i;
            directions[i] = new(MathF.Cos(theta) * radius, y, MathF.Sin(theta) * radius);
        }
        return directions;
    }

    private static float4[] Integrate(PbrLightmapInput input, PbrLightmapBakeSettings options, float4[] tracing,
        float3[] directions, CancellationToken cancellationToken)
    {
        var coefficients = new float4[input.Texels.Length * 4];
        var light = math.normalize(options.TowardLight);
        var scale = 4 * MathF.PI / options.Samples;
        Parallel.For(0, input.Texels.Length, new ParallelOptions { CancellationToken = cancellationToken }, index => {
            var texel = input.Texels.Span[index];
            if (!texel.Covered) return;
            float3 c0 = default, cy = default, cz = default, cx = default;
            for (var sample = 0; sample < directions.Length; sample++) {
                if ((sample & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                var direction = directions[sample];
                // A direction offset avoids hitting the receiver itself on either face.
                var origin = texel.Position + direction * options.RayBias;
                var hit = Trace(tracing, origin, direction, options.MaximumDistance);
                var radiance = options.Sky.Evaluate(direction);
                if (hit.Triangle >= 0) {
                    var indices = tracing[(int)tracing[0].y + hit.Triangle];
                    var start = (int)tracing[1].x;
                    var a = tracing[start + (int)indices.x].xyz;
                    var b = tracing[start + (int)indices.y].xyz;
                    var c = tracing[start + (int)indices.z].xyz;
                    var material = (int)tracing[0].w + (int)indices.w * 2;
                    if (!hit.Front && tracing[material].w < .5f) radiance = default;
                    else {
                        var normal = math.normalize(math.cross(b - a, c - a));
                        if (!hit.Front) normal = -normal;
                        var point = origin + direction * hit.Distance + normal * options.RayBias;
                        var cosine = MathF.Max(0, math.dot(normal, light));
                        radiance = tracing[material + 1].xyz;
                        if (cosine > 0 && Trace(tracing, point, light, options.MaximumDistance).Triangle < 0)
                            radiance += tracing[material].xyz * options.LightRadiance * (cosine / MathF.PI);
                    }
                }
                c0 += radiance * .2820948f;
                cy += radiance * (.4886025f * direction.y);
                cz += radiance * (.4886025f * direction.z);
                cx += radiance * (.4886025f * direction.x);
            }
            coefficients[index * 4] = new(c0 * scale, 1);
            coefficients[index * 4 + 1] = new(cy * scale, 0);
            coefficients[index * 4 + 2] = new(cz * scale, 0);
            coefficients[index * 4 + 3] = new(cx * scale, 0);
        });
        for (var i = 0; i < input.FilterSources.Length; i++) {
            if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var source = input.FilterSources.Span[i];
            if (source >= 0 && source != i) coefficients.AsSpan(source * 4, 4).CopyTo(coefficients.AsSpan(i * 4, 4));
        }
        return coefficients;
    }

    internal readonly record struct Hit(float Distance, int Triangle, bool Front);

    internal static Hit Trace(float4[] data, float3 origin, float3 direction, float maximum)
    {
        var hit = new Hit(maximum, -1, true);
        var inverse = new float3(1 / Safe(direction.x), 1 / Safe(direction.y), 1 / Safe(direction.z));
        for (var node = 0; node < (int)data[0].x;) {
            var lo = data[2 + node * 3];
            var hi = data[3 + node * 3];
            var a = (lo.xyz - origin) * inverse;
            var b = (hi.xyz - origin) * inverse;
            var near = math.min(a, b);
            var far = math.max(a, b);
            if (MathF.Max(MathF.Max(near.x, near.y), MathF.Max(near.z, .001f))
                > MathF.Min(MathF.Min(far.x, far.y), MathF.Min(far.z, hit.Distance))) { node = (int)lo.w; continue; }
            for (var i = 0; i < (int)data[4 + node * 3].x; i++) {
                var triangle = (int)hi.w + i;
                var indices = data[(int)data[0].y + triangle];
                var start = (int)data[1].x;
                var position = data[start + (int)indices.x].xyz;
                var e1 = data[start + (int)indices.y].xyz - position;
                var e2 = data[start + (int)indices.z].xyz - position;
                var p = math.cross(direction, e2);
                var determinant = math.dot(e1, p);
                if (MathF.Abs(determinant) < 1e-8f) continue;
                var t = origin - position;
                var u = math.dot(t, p) / determinant;
                var q = math.cross(t, e1);
                var v = math.dot(direction, q) / determinant;
                var distance = math.dot(e2, q) / determinant;
                if (u >= 0 && v >= 0 && u + v <= 1 && distance > .001f && distance < hit.Distance)
                    hit = new(distance, triangle, determinant > 0);
            }
            node++;
        }
        return hit;
        static float Safe(float v) => MathF.Abs(v) < 1e-20f ? (v < 0 ? -1e-20f : 1e-20f) : v;
    }
}
