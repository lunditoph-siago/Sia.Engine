using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>CPU one-bounce producer over borrowed acquired BVHs. No IO, asset mutation, GPU resources or final-HDR dependency.</summary>
public sealed class PbrReflectionLighting
{
    private readonly record struct Surface(float3 Albedo, float3 Emission, bool DoubleSided);
    private readonly PbrBvhTrace _trace;
    private readonly PbrReflectionSettings _settings;
    private readonly Surface[] _surfaces;
    private readonly bool[] _opaque;

    public PbrReflectionLighting(PbrBvhTrace trace, PbrReflectionSettings settings, ulong maximumWorkingBytes = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(trace); ArgumentNullException.ThrowIfNull(settings); settings.Validate();
        var bytes = (ulong)trace.Scene.Materials.Length * ((ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<Surface>() + 1);
        if (bytes > maximumWorkingBytes) throw new ArgumentException("Reflection material scratch exceeds admission.", nameof(maximumWorkingBytes));
        _trace = trace; _settings = settings; _surfaces = new Surface[trace.Scene.Materials.Length]; _opaque = new bool[_surfaces.Length];
        for (var at = 0; at < _surfaces.Length; at++) {
            var value = trace.Scene.Materials[at]; var surface = PbrSceneTransport.Surface(value);
            RequireColor(surface.Albedo.xyz, 1); RequireColor(surface.Emission.xyz, 1e18f);
            _surfaces[at] = new(surface.Albedo.xyz, surface.Emission.xyz, value.DoubleSided); _opaque[at] = !value.AlphaBlend;
        }
    }

    public PbrReflectionSample Trace(float3 origin, float3 direction, ReadOnlySpan<PbrReflectionDirectionalLight> directionals,
        ReadOnlySpan<PbrReflectionLocalLight> locals, Func<float3, float3, float3> irradiance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(irradiance); cancellationToken.ThrowIfCancellationRequested();
        if (directionals.Length > 4 || locals.Length > 256) throw new ArgumentException("Reflection light tables exceed renderer admission.");
        foreach (var light in directionals) {
            RequireColor(light.Radiance, 1e18f); RequireDirection(light.TowardLight);
        }
        foreach (var light in locals) {
            RequireColor(light.Radiance, 1e18f);
            if (!math.all(math.isfinite(light.Position)) || !float.IsFinite(light.Range) || light.Range < 0)
                throw new ArgumentException("Invalid local reflection light.");
            if (light.Spot) {
                RequireDirection(light.Direction);
                if (!float.IsFinite(light.InnerCosine) || !float.IsFinite(light.OuterCosine)
                    || light.InnerCosine > 1 || light.OuterCosine < -1 || light.InnerCosine <= light.OuterCosine)
                    throw new ArgumentException("Invalid reflection spot cone.");
            }
        }
        RequireDirection(direction); direction = math.normalize(direction);
        var query = _trace.Query(origin, direction, _settings.MaximumSteps, _settings.RayBias, _settings.MaximumDistance,
            cancellationToken, _opaque);
        if (!query.Complete || !query.HasHit) return new(default, default, false, query.Complete);
        var hit = query.Hit; var surface = _surfaces[hit.Material];
        if (!hit.FrontFace && !surface.DoubleSided) return new(default, hit, true, true);
        if (!math.any(surface.Albedo > 0)) return new(surface.Emission, hit, true, true);
        var normal = hit.FrontFace ? hit.GeometricNormal : -hit.GeometricNormal;
        var position = origin + direction * (float)hit.Parameter; var biased = position + normal * _settings.RayBias;
        var indirect = irradiance(position, normal);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Color(indirect, 1e18f)) return new(default, default, false, false);
        var radiance = surface.Emission + surface.Albedo * indirect / MathF.PI;
        foreach (var light in directionals) {
            var toward = math.normalize(light.TowardLight); var cosine = MathF.Max(0, math.dot(normal, toward));
            if (cosine == 0 || !math.any(light.Radiance > 0) || !math.any(surface.Albedo > 0)) continue;
            var shadow = _trace.Query(biased, toward, _settings.MaximumShadowSteps, _settings.RayBias, _settings.MaximumDistance,
                cancellationToken, _opaque);
            if (!shadow.Complete) return new(default, default, false, false);
            if (!shadow.HasHit) radiance += surface.Albedo * light.Radiance * (cosine / MathF.PI);
        }
        Span<int> selected = stackalloc int[8]; Span<float> energies = stackalloc float[8];
        Span<float3> contributions = stackalloc float3[8]; selected.Fill(-1); energies.Clear();
        for (var at = 0; _settings.MaximumLocalLights > 0 && at < locals.Length; at++) {
            var contribution = LocalContribution(locals[at], position, normal, surface.Albedo);
            var energy = math.cmax(contribution);
            if (energy <= 0) continue;
            for (var slot = 0; slot < _settings.MaximumLocalLights; slot++) {
                if (energy <= energies[slot]) continue;
                for (var move = _settings.MaximumLocalLights - 1; move > slot; move--) {
                    selected[move] = selected[move - 1]; energies[move] = energies[move - 1]; contributions[move] = contributions[move - 1];
                }
                selected[slot] = at; energies[slot] = energy; contributions[slot] = contribution; break;
            }
        }
        for (var slot = 0; slot < _settings.MaximumLocalLights; slot++) {
            if (selected[slot] < 0) continue;
            var delta = locals[selected[slot]].Position - biased; var distance = math.length(delta);
            if (distance <= 2 * _settings.RayBias) continue;
            var shadow = _trace.Query(biased, delta / distance, _settings.MaximumShadowSteps, _settings.RayBias, distance - _settings.RayBias,
                cancellationToken, _opaque);
            if (!shadow.Complete) return new(default, default, false, false);
            if (!shadow.HasHit) radiance += contributions[slot];
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Color(radiance, 1e18f) ? new(radiance, hit, true, true) : new(default, default, false, false);
    }

    private float3 LocalContribution(PbrReflectionLocalLight light, float3 position, float3 normal, float3 albedo)
    {
        var delta = light.Position - position; var distance = math.length(delta);
        if (!float.IsFinite(distance) || distance <= 2 * _settings.RayBias || (light.Range > 0 && distance >= light.Range) || distance > _settings.MaximumDistance) return default;
        var toward = delta / distance; var cosine = MathF.Max(0, math.dot(normal, toward));
        var window = light.Range == 0 ? 1 : System.Math.Clamp(1 - MathF.Pow(distance / light.Range, 4), 0, 1);
        var attenuation = window * window / MathF.Max(distance * distance, 1e-4f);
        if (light.Spot) {
            var cone = System.Math.Clamp((math.dot(-toward, math.normalize(light.Direction)) - light.OuterCosine)
                / MathF.Max(light.InnerCosine - light.OuterCosine, 1e-4f), 0, 1); attenuation *= cone * cone;
        }
        return albedo * light.Radiance * (cosine * attenuation / MathF.PI);
    }

    /// <summary>Signed replacement before final clipping. The baseline and weight are exported from current material shading.</summary>
    public static float3 Compose(float3 opaque, float3 baseline, float3 weight, PbrReflectionSample current)
    {
        if (!math.all(math.isfinite(opaque)) || !Color(baseline, float.MaxValue) || !Color(weight, float.MaxValue))
            throw new ArgumentException("Reflection composition inputs must be finite with nonnegative baseline/weight.");
        return current.Valid && current.Complete && Color(current.Radiance, float.MaxValue)
            ? math.max(opaque + current.Radiance * weight - baseline, float3.zero) : math.max(opaque, float3.zero);
    }

    private static bool Color(float3 value, float maximum) => math.all(math.isfinite(value)) && math.all(value >= 0 & value <= maximum);
    private static void RequireColor(float3 value, float maximum) { if (!Color(value, maximum)) throw new ArgumentException("Invalid bounded reflection radiance/albedo."); }
    private static void RequireDirection(float3 value)
    {
        if (!math.all(math.isfinite(value)) || !float.IsFinite(math.lengthsq(value)) || math.lengthsq(value) <= 1e-12f)
            throw new ArgumentException("Reflection direction must be finite and nonzero.");
    }
}
