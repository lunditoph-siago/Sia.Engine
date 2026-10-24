using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;
using CameraComponent = Sia.Engine.Camera.Camera;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    private void CollectPoint(Entity e)
    {
        var light = e.Get<PointLight>();
        var color = e.Get<LightColor>();
        if (!float.IsFinite(light.Range) || light.Range < 0)
            throw new ArgumentException("Point light range must be finite and nonnegative; zero has no distance cutoff.");
        var position = e.Get<GlobalTransform>().Affine.Translation;
        var radiance = LightRadiance(color);
        _localRadianceBound += LocalRadianceBound(position, light.Range, radiance);
        if (math.all(radiance == 0)) return;
        var at = NextLight();
        _sceneData[at] = new(position, light.Range);
        _sceneData[at + 1] = default;
        _sceneData[at + 2] = new(radiance, 0);
        _sceneData[at + 3] = new(0, 0, -1, 0);
    }

    private void CollectSpot(Entity e)
    {
        var light = e.Get<SpotLight>();
        var color = e.Get<LightColor>();
        var transform = e.Get<GlobalTransform>().Affine;
        if (!float.IsFinite(light.Range) || light.Range < 0 || (light.Range > 0 && light.Range <= .05f) || !float.IsFinite(light.InnerAngle) || !float.IsFinite(light.OuterAngle)
            || light.InnerAngle < 0 || light.OuterAngle <= light.InnerAngle || light.OuterAngle >= MathF.PI / 2)
            throw new ArgumentException("Spot light requires range zero or > .05 and 0 <= inner < outer < pi/2.");
        var direction = LightDirection(transform.RotationScale.c2);
        var radiance = LightRadiance(color);
        _localRadianceBound += LocalRadianceBound(transform.Translation, light.Range, radiance);
        if (math.all(radiance == 0)) return;
        var at = NextLight();
        var layer = -1;
        if (_casters.Contains(e) && _spotCount < (uint)_frame.Frame.MainWorld.AcquireAddon<ShadowAtlasConfig>().MaxShadowedSpotLights) {
            var shadowRange = light.Range == 0 ? _frame.Frame.MainWorld.AcquireAddon<ShadowAtlasConfig>().ShadowDistance : light.Range;
            if (!float.IsFinite(shadowRange) || shadowRange <= .05f)
                throw new ArgumentException("Unbounded spot shadows require a finite ShadowDistance > .05.");
            layer = 3 + (int)_spotCount++;
            SetShadow(layer, CascadeSplitting.ComputeSpotViewProj(transform, light.OuterAngle, shadowRange));
        }
        _sceneData[at] = new(transform.Translation, light.Range);
        _sceneData[at + 1] = new(-direction, 1);
        _sceneData[at + 2] = new(radiance, 0);
        _sceneData[at + 3] = new(MathF.Cos(light.InnerAngle), MathF.Cos(light.OuterAngle), layer, 0);
    }

    private int NextLight()
    {
        if (_data.Counts.x >= k_MaximumLights)
            throw new ArgumentException("Resident PBR light capacity exceeded (256); lights are never silently dropped.");
        return (int)_data.Counts.x++ * 4;
    }

    private float3 LightRadiance(LightColor color)
    {
        var value = color.Color * color.Intensity;
        if (!math.all(math.isfinite(value))) throw new ArgumentException("Live light radiance must be finite.");
        if (HasReflections && (!math.all(math.isfinite(value)) || math.any(value < 0) || math.any(value > 1e18f)))
            throw new ArgumentException("Live reflection lights require finite nonnegative radiance within transport admission.");
        return value;
    }

    private static float3 LightDirection(float3 direction)
    {
        var length = math.lengthsq(direction);
        if (!math.all(math.isfinite(direction)) || !float.IsFinite(length) || length < 1e-12f)
            throw new ArgumentException("Live light direction must be finite and nonzero.");
        return math.normalize(direction);
    }

    private double LocalRadianceBound(float3 position, float range, float3 radiance)
    {
        if (!math.all(math.isfinite(position))) throw new ArgumentException("Live light position must be finite.");
        var bounds = _owner.LightingBounds;
        double squared = 0;
        for (var c = 0; c < 3; c++) {
            // Enclose CPU/GPU transform rounding before using the scene AABB.
            var padding = 32d * 1.1920928955078125e-7 * System.Math.Max(1, System.Math.Max(System.Math.Abs(position[c]), System.Math.Max(System.Math.Abs(bounds.Min[c]), System.Math.Abs(bounds.Max[c]))));
            var distance = System.Math.Max(0, System.Math.Max((double)bounds.Min[c] - position[c], (double)position[c] - bounds.Max[c]) - padding);
            squared += distance * distance;
        }
        var rangeSquared = (double)range * range;
        if (range > 0 && squared >= rangeSquared) return 0;
        var peak = math.cmax(math.abs(radiance)); _owner.ValidateDirectRadiance(peak);
        var cutoff = range == 0 ? 1 : 1 - System.Math.Pow(squared / rangeSquared, 2);
        return peak * cutoff * cutoff / System.Math.Max(squared, 1e-4);
    }

    private void Cull(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = Wgpu.BeginComputePass(context.CommandEncoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetComputePipeline(pass, _owner.Pipelines.Cluster.GetWgpu<WGPUComputePipeline>());
            Wgpu.SetBindGroup(pass, 0, _clusterGroup.GetWgpu<WGPUBindGroup>());
            Wgpu.DispatchWorkgroups(pass, (k_Cells + 63) / 64);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }
}
