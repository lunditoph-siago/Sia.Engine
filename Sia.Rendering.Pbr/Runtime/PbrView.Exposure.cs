using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    private void InitializeExposure()
    {
        if (_owner.Settings.AutoExposure is not { } settings) return;
        _exposurePartials = _gpu.Buffer(256 * 8, WGPUBufferUsage.Storage);
        _exposureConfiguration = _gpu.Upload<float4>(
            [new(settings.TargetLuminance, settings.MinimumStops, settings.MaximumStops, 0)], WGPUBufferUsage.Uniform);
    }

    private void ImportExposure(ref RenderGraphBuildContext graph)
    {
        if (_owner.Settings.AutoExposure is null) return;
        Import(ref graph, _exposurePartialsKey, _exposurePartials, RenderGraphBufferUsage.Storage);
        Import(ref graph, _exposureConfigurationKey, _exposureConfiguration, RenderGraphBufferUsage.Uniform);
    }

    private void BuildExposure(ref RenderGraphBuildContext graph, Dependencies dependency)
    {
        if (!UseAutomaticExposure) return;
        graph.UsePass(new(_prefix + "exposure-measure"), "pbr-exposure-measure", dependency, static (in d, p) =>
            d.View.ReadTiming(p).ReadWrite(d.View._outputUniformKey, RenderGraphBufferUsage.Storage)
                .Read(d.View._hdrKey, RenderGraphTextureUsage.TextureBinding)
                .Read(d.View._exposureConfigurationKey, RenderGraphBufferUsage.Uniform)
                .Write(d.View._exposurePartialsKey, RenderGraphBufferUsage.Storage), MeasureExposure, RenderGraphPassKind.Compute);
        graph.UsePass(new(_prefix + "exposure-reduce"), "pbr-exposure-reduce", dependency, static (in d, p) =>
            p.ReadWrite(d.View._outputUniformKey, RenderGraphBufferUsage.Storage)
                .Read(d.View._hdrKey, RenderGraphTextureUsage.TextureBinding)
                .Read(d.View._exposureConfigurationKey, RenderGraphBufferUsage.Uniform)
                .Read(d.View._exposurePartialsKey, RenderGraphBufferUsage.Storage), ReduceExposure, RenderGraphPassKind.Compute);
    }

    private void MeasureExposure(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = Wgpu.BeginComputePass(context.CommandEncoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetComputePipeline(pass, _owner.Pipelines.ExposureMeasure.GetWgpu<WGPUComputePipeline>());
            Wgpu.SetBindGroup(pass, 0, _exposureGroup.GetWgpu<WGPUBindGroup>());
            Wgpu.DispatchWorkgroups(pass, System.Math.Min(16u, (_width + 7) / 8), System.Math.Min(16u, (_height + 7) / 8));
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }

    private void ReduceExposure(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = Wgpu.BeginComputePass(context.CommandEncoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetComputePipeline(pass, _owner.Pipelines.ExposureReduce.GetWgpu<WGPUComputePipeline>());
            Wgpu.SetBindGroup(pass, 0, _exposureGroup.GetWgpu<WGPUBindGroup>());
            Wgpu.DispatchWorkgroups(pass, 1);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }
}
