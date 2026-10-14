using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>A real surface sample; filtering-border samples have zero physical area.</summary>
public readonly record struct PbrLightmapSurfaceTexel(float3 Position, float3 Normal, bool Covered)
{
    public int Chart { get; init; } = -1;
    public int SourceTriangle { get; init; } = -1;
    public double AreaWeight { get; init; }
}

public sealed partial record PbrLightmapBakeSettings
{
    public ProceduralSky Sky { get; init; } = new();
    public float3 TowardLight { get; init; } = new(0, 1, 0);
    public float3 LightRadiance { get; init; }
    public int Samples { get; init; } = 128;
    public float MaximumDistance { get; init; } = 1000;
    public float RayBias { get; init; } = .002f;

    internal void Validate()
    {
        static bool Color(float3 color) => math.all(math.isfinite(color)) && math.all(color >= 0) && math.all(color <= 1e10f);
        static bool Direction(float3 direction) => math.all(math.isfinite(direction)) && float.IsFinite(math.lengthsq(direction)) && math.lengthsq(direction) > 1e-8f;
        if (Sky is null || !Color(Sky.Horizon) || !Color(Sky.Zenith) || !Color(Sky.Ground) || !Color(Sky.SunRadiance)
            || !Direction(Sky.SunDirection) || !float.IsFinite(Sky.SunExponent) || Sky.SunExponent < 1
            || !float.IsFinite(Sky.Intensity) || Sky.Intensity is < 0 or > 1e5f
            || !Color(LightRadiance) || !Direction(TowardLight) || Samples is < 16 or > 1024
            || !float.IsFinite(MaximumDistance) || MaximumDistance <= 0 || !float.IsFinite(RayBias)
            || RayBias <= 0 || RayBias >= MaximumDistance)
            throw new ArgumentException("Invalid lightmap bake lighting or bounded integration settings.");
    }
}

/// <summary>
/// Serial offline producer over caller-retained local BVHs. Owns bounded reusable scratch, not assets or IO.
/// Integrates sky visibility, emission and one shadowed directional diffuse bounce using mean material colors.
/// Direct-light shading is excluded. Callers serialize BakePage and publish/discard outputs explicitly.
/// </summary>
public sealed partial class PbrLightmapBaker
{
    private readonly record struct Surface(float3 Albedo, float3 Emission, bool DoubleSided);
    private readonly PbrBvhTrace _trace;
    private readonly PbrLightmapBakeSettings _settings;
    private readonly Surface[] _surfaces;
    private readonly bool[] _opaqueMaterials;
    private readonly float3[] _directions;
    private readonly PbrLightmapCoefficients[] _scratch;
    private readonly float3 _towardLight;
    private int _busy;
    public ulong PayloadBytes { get; }

    public PbrLightmapBaker(PbrBvhTrace trace, PbrLightmapBakeSettings? settings = null,
        ulong maximumWorkingBytes = 4ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace); _settings = settings ?? new(); _settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = (ulong)trace.Scene.Materials.Length * ((ulong)Unsafe.SizeOf<Surface>() + sizeof(bool))
            + (ulong)_settings.Samples * (ulong)Unsafe.SizeOf<float3>()
            + PbrLightmapPageData.MaximumEdge * PbrLightmapPageData.MaximumEdge * (ulong)Unsafe.SizeOf<PbrLightmapCoefficients>();
        // Reserve one maximum output page as well; external surface inputs/trace and retained outputs are caller-owned.
        if (bytes + PbrLightmapPageData.MaximumEncodedBytes > maximumWorkingBytes)
            throw new ArgumentException("Lightmap producer scratch and one output page exceed the configured working budget.", nameof(maximumWorkingBytes));
        _trace = trace; PayloadBytes = bytes; _towardLight = math.normalize(_settings.TowardLight);
        _surfaces = new Surface[trace.Scene.Materials.Length]; _directions = new float3[_settings.Samples];
        _opaqueMaterials = new bool[trace.Scene.Materials.Length];
        _scratch = new PbrLightmapCoefficients[PbrLightmapPageData.MaximumEdge * PbrLightmapPageData.MaximumEdge];
        for (var index = 0; index < _surfaces.Length; index++) {
            cancellationToken.ThrowIfCancellationRequested(); var material = trace.Scene.Materials[index];
            var values = PbrSceneTransport.Surface(material);
            if (!math.all(math.isfinite(values.Albedo.xyz)) || !math.all(math.isfinite(values.Emission.xyz))
                || math.any(values.Albedo.xyz < 0) || math.any(values.Albedo.xyz > 1)
                || math.any(values.Emission.xyz < 0) || math.any(values.Emission.xyz > 1e18f))
                throw new ArgumentException("Lightmap bounce materials exceed finite diffuse/emission bounds.", nameof(trace));
            _surfaces[index] = new(values.Albedo.xyz, values.Emission.xyz, material.DoubleSided);
            _opaqueMaterials[index] = !material.AlphaBlend;
        }
        for (var index = 0; index < _directions.Length; index++) {
            var y = 1 - 2 * (index + .5f) / _directions.Length; var radius = MathF.Sqrt(MathF.Max(0, 1 - y * y));
            var angle = index * 2.39996323f;
            _directions[index] = new(radius * MathF.Cos(angle), y, radius * MathF.Sin(angle));
        }
    }

    public PbrLightmapPageData BakePage(int edge, ReadOnlySpan<PbrLightmapSurfaceTexel> surfaces,
        CancellationToken cancellationToken = default)
    {
        ValidateShape(edge, surfaces.Length);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _busy, 1) != 0) throw new InvalidOperationException("Lightmap pages must be produced serially by each baker.");
        try {
            Integrate(surfaces, cancellationToken);
            return PbrLightmapPageData.Create(edge, _scratch.AsSpan(0, surfaces.Length), cancellationToken: cancellationToken);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>
    /// Produces unquantized values into a caller-owned page buffer for receiver-wide range measurement
    /// and chart reduction. Failure/cancellation leaves that buffer unchanged; encoding and IO are explicit.
    /// </summary>
    public void BakePage(int edge, ReadOnlySpan<PbrLightmapSurfaceTexel> surfaces,
        Span<PbrLightmapCoefficients> output, CancellationToken cancellationToken = default)
    {
        ValidateShape(edge, surfaces.Length);
        if (output.Length != surfaces.Length) throw new ArgumentException("Lighting output must match the surface page.", nameof(output));
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _busy, 1) != 0) throw new InvalidOperationException("Lightmap pages must be produced serially by each baker.");
        try {
            Integrate(surfaces, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _scratch.AsSpan(0, output.Length).CopyTo(output);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static void ValidateShape(int edge, int count)
    {
        if (edge is < 2 or > PbrLightmapPageData.MaximumEdge || (edge & (edge - 1)) != 0 || count != edge * edge)
            throw new ArgumentException("Lightmap surface page shape is invalid.");
    }

    private void Integrate(ReadOnlySpan<PbrLightmapSurfaceTexel> surfaces, CancellationToken cancellationToken)
    {
        for (var index = 0; index < surfaces.Length; index++) {
                cancellationToken.ThrowIfCancellationRequested(); var surface = surfaces[index];
                if (!surface.Covered) { _scratch[index] = default; continue; }
                var length = math.lengthsq(surface.Normal);
                if (!math.all(math.isfinite(surface.Position)) || !math.all(math.isfinite(surface.Normal)) || !float.IsFinite(length) || length <= 0)
                    throw new ArgumentException("Covered lightmap surfaces require finite positions and nonzero normals.", nameof(surfaces));
                var normal = surface.Normal / MathF.Sqrt(length); var origin = surface.Position + normal * _settings.RayBias;
                float3 constant = default, cy = default, cz = default, cx = default;
                foreach (var direction in _directions) {
                    cancellationToken.ThrowIfCancellationRequested(); var radiance = _settings.Sky.Evaluate(direction);
                    if (TraceOpaque(origin, direction, out var hit, cancellationToken)) {
                        radiance = default; var material = _surfaces[hit.Material];
                        if (hit.FrontFace || material.DoubleSided) {
                            var n = hit.FrontFace ? hit.GeometricNormal : -hit.GeometricNormal;
                            var point = origin + direction * (float)hit.Parameter + n * _settings.RayBias;
                            radiance = material.Emission;
                            var cosine = MathF.Max(0, math.dot(n, _towardLight));
                            if (cosine > 0 && !TraceOpaque(point, _towardLight, out _, cancellationToken))
                                radiance += material.Albedo * _settings.LightRadiance * (cosine / MathF.PI);
                        }
                    }
                    constant += radiance * .282094792f; cy += radiance * (.488602512f * direction.y);
                    cz += radiance * (.488602512f * direction.z); cx += radiance * (.488602512f * direction.x);
                }
                var scale = 4 * MathF.PI / _directions.Length;
                _scratch[index] = new(constant * scale, cy * scale, cz * scale, cx * scale, true);
        }
    }

    private bool TraceOpaque(float3 origin, float3 direction, out PbrBvhHit hit, CancellationToken token)
        => _trace.TryTrace(origin, direction, out hit, 0, _settings.MaximumDistance, token, _opaqueMaterials);
}
