using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Offline value producer over explicit acquired static inputs; owns scratch, never assets, files or GPU resources.</summary>
public static class PbrReflectionCaptureBaker
{
    private readonly record struct Surface(float3 Albedo, float3 Emission, bool DoubleSided);

    public static IblEnvironmentAsset Bake(PbrBvhScene staticScene, IblEnvironmentAsset source, float3 position, Aabb bounds,
        PbrReflectionCaptureSettings? settings = null, ulong maximumWorkingBytes = 32ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staticScene); ArgumentNullException.ThrowIfNull(source);
        var inputs = settings ?? new(); inputs.Validate(); PbrReflectionCaptureData.ValidateRegion(position, bounds);
        cancellationToken.ThrowIfCancellationRequested();
        const ulong filterBytes = 6ul * IblEnvironmentAsset.CubeSize * IblEnvironmentAsset.CubeSize * 12 + IblEnvironmentAsset.CubeTexels * 16ul;
        var materialBytes = (ulong)staticScene.Materials.Length * ((ulong)Unsafe.SizeOf<Surface>() + 1);
        if (filterBytes + materialBytes >= maximumWorkingBytes)
            throw new ArgumentException("Capture working budget cannot hold filtering and material scratch.", nameof(maximumWorkingBytes));
        var trace = PbrBvhTrace.Build(staticScene, maximumWorkingBytes - filterBytes - materialBytes, cancellationToken);
        var materials = new Surface[staticScene.Materials.Length]; var opaque = new bool[materials.Length];
        for (var at = 0; at < materials.Length; at++) {
            cancellationToken.ThrowIfCancellationRequested(); var material = staticScene.Materials[at]; var value = PbrSceneTransport.Surface(material);
            if (!math.all(math.isfinite(value.Albedo.xyz)) || math.any(value.Albedo.xyz < 0) || math.any(value.Albedo.xyz > 1)
                || !math.all(math.isfinite(value.Emission.xyz)) || math.any(value.Emission.xyz < 0) || math.any(value.Emission.xyz > 1e18f))
                throw new ArgumentException("Capture materials exceed finite diffuse/emission transport limits.", nameof(staticScene));
            materials[at] = new(value.Albedo.xyz, value.Emission.xyz, material.DoubleSided); opaque[at] = !material.AlphaBlend;
        }
        var towardLight = math.normalize(inputs.TowardLight);
        return IblEnvironmentBaker.BakeRadiance(source, (direction, background) => {
            cancellationToken.ThrowIfCancellationRequested();
            if (!trace.TryTrace(position, direction, out var hit, 0, inputs.MaximumDistance, cancellationToken, opaque)) return background;
            var material = materials[hit.Material];
            if (!hit.FrontFace && !material.DoubleSided) return float3.zero;
            var normal = hit.FrontFace ? hit.GeometricNormal : -hit.GeometricNormal;
            var point = position + direction * (float)hit.Parameter + normal * inputs.RayBias;
            var result = material.Emission + material.Albedo * math.max(IrradianceSh.Evaluate(source.Coefficients.Span, normal), float3.zero) / MathF.PI;
            var cosine = MathF.Max(0, math.dot(normal, towardLight));
            if (cosine > 0 && !trace.TryTrace(point, towardLight, out _, 0, inputs.MaximumDistance, cancellationToken, opaque))
                result += material.Albedo * inputs.LightRadiance * (cosine / MathF.PI);
            return result;
        }, inputs.FilterSamples, maximumWorkingBytes - materialBytes - trace.PayloadBytes, cancellationToken);
    }
}
