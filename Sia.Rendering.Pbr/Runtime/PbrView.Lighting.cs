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
        var at = NextLight();
        if (!float.IsFinite(light.Range) || light.Range <= 0)
            throw new ArgumentException("Point light range must be positive and finite.");
        _sceneData[at] = new(e.Get<GlobalTransform>().Affine.Translation, light.Range);
        _sceneData[at + 1] = default;
        _sceneData[at + 2] = new(color.Color * color.Intensity, 0);
        _sceneData[at + 3] = new(0, 0, -1, 0);
    }

    private void CollectSpot(Entity e)
    {
        var light = e.Get<SpotLight>();
        var color = e.Get<LightColor>();
        var transform = e.Get<GlobalTransform>().Affine;
        var at = NextLight();
        if (!float.IsFinite(light.Range) || light.Range <= .05f || !float.IsFinite(light.InnerAngle) || !float.IsFinite(light.OuterAngle)
            || light.InnerAngle < 0 || light.OuterAngle <= light.InnerAngle || light.OuterAngle >= MathF.PI / 2)
            throw new ArgumentException("Spot light requires range > .05 and 0 <= inner < outer < pi/2.");
        var layer = -1;
        if (_casters.Contains(e) && _spotCount < (uint)_frame.Frame.MainWorld.AcquireAddon<ShadowAtlasConfig>().MaxShadowedSpotLights) {
            layer = 3 + (int)_spotCount++;
            SetShadow(layer, CascadeSplitting.ComputeSpotViewProj(transform, light.OuterAngle, light.Range));
        }
        _sceneData[at] = new(transform.Translation, light.Range);
        _sceneData[at + 1] = new(-math.normalize(transform.RotationScale.c2), 1);
        _sceneData[at + 2] = new(color.Color * color.Intensity, 0);
        _sceneData[at + 3] = new(MathF.Cos(light.InnerAngle), MathF.Cos(light.OuterAngle), layer, 0);
    }

    private int NextLight()
    {
        if (_data.Counts.x >= k_MaximumLights)
            throw new ArgumentException("Resident PBR light capacity exceeded (256); lights are never silently dropped.");
        return (int)_data.Counts.x++ * 4;
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
