using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static class PbrReflectionCaptureBaker
{
    /// <summary>Offline static proxy capture: emission, environment diffuse and one shadowed directional light.
    /// Retains the supplied sky/SH/BRDF, and filters scene radiance into its specular cube.</summary>
    public static PbrReflectionCaptureAsset Bake(PbrSceneAsset scene, IblEnvironmentAsset environment,
        float3 position, Aabb bounds, float3 towardLight = default, float3 lightRadiance = default,
        float maximumDistance = 1000, ulong maximumTraceBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(environment);
        PbrReflectionCaptureAsset.ValidateRegion(position, bounds);
        if (!float.IsFinite(maximumDistance) || maximumDistance <= .01f
            || !math.all(math.isfinite(lightRadiance) & (lightRadiance >= 0))
            || !math.all(math.isfinite(towardLight)) || !float.IsFinite(math.lengthsq(towardLight))
            || math.any(lightRadiance > 0) && math.lengthsq(towardLight) < 1e-8f)
            throw new ArgumentException("Invalid reflection capture lighting or distance.");
        cancellationToken.ThrowIfCancellationRequested();
        var trace = PbrSceneTransport.BuildStatic(scene, maximumTraceBytes);
        var packed = trace.Packed.ToArray();
        var sh = environment.Coefficients.ToArray();
        var light = math.any(lightRadiance > 0) ? math.normalize(towardLight) : new float3(0, 1, 0);
        var captured = IblEnvironmentBaker.BakeRadiance(environment, (direction, background) => {
            var hit = PbrLightmapBaker.Trace(packed, position, direction, maximumDistance);
            if (hit.Triangle < 0) return background;
            var indices = packed[(int)packed[0].y + hit.Triangle];
            var vertices = (int)packed[1].x;
            var a = packed[vertices + (int)indices.x].xyz;
            var b = packed[vertices + (int)indices.y].xyz;
            var c = packed[vertices + (int)indices.z].xyz;
            var material = (int)packed[0].w + (int)indices.w * 2;
            var albedo = packed[material];
            if (!hit.Front && albedo.w < .5f) return background;
            var normal = math.normalize(math.cross(b - a, c - a)) * (hit.Front ? 1 : -1);
            var point = position + direction * hit.Distance + normal * .01f;
            var value = packed[material + 1].xyz
                + albedo.xyz * math.max(IrradianceSh.Evaluate(sh, normal), float3.zero) / MathF.PI;
            var cosine = MathF.Max(0, math.dot(normal, light));
            if (cosine > 0 && PbrLightmapBaker.Trace(packed, point, light, maximumDistance).Triangle < 0)
                value += albedo.xyz * lightRadiance * (cosine / MathF.PI);
            return value;
        }, cancellationToken);
        return new(trace.Identity.Span, PbrReflectionCaptureAsset.HashEnvironment(environment), position, bounds, captured);
    }
}
