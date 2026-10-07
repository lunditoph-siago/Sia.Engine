using Sia;
using Sia.Graphics.Reactive;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    private void Prepass(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = context.GetOrBeginRenderPass(null, new WgpuReactiveRenderGraphDepthStencilAttachment(_frame.DepthTarget, WGPULoadOp.Clear));
        DrawIndexedOpaque(pass, true);
    }

    private void AlphaCoverage(WgpuReactiveRenderGraphPassContext context)
    {
        // A refraction snapshot must retain opaque color even behind foliage.
        if (_needsSnapshot || _transparent.Count == 0) return;
        var pass = context.GetOrBeginRenderPass(null, new WgpuReactiveRenderGraphDepthStencilAttachment(_frame.DepthTarget, WGPULoadOp.Load));
        Wgpu.SetBindGroup(pass, 0, _rasterGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _owner.Scene.Group.GetWgpu<WGPUBindGroup>());
        Wgpu.SetIndexBuffer(pass, _owner.Scene.Topology.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
        for (uint plane = 0; plane < _owner.Scene.VertexPlanes; plane++)
            Wgpu.SetVertexBuffer(pass, plane, _owner.Scene.Vertices.GetWgpu<WGPUBuffer>(), (ulong)plane * _owner.Scene.VertexCount * 16);
        foreach (var draw in _transparent) {
            Wgpu.SetRenderPipeline(pass, (draw.DoubleSided ? _owner.Pipelines.CoverageDouble : _owner.Pipelines.Coverage).GetWgpu<WGPURenderPipeline>());
            Wgpu.SetBindGroup(pass, 2, _owner.Materials.Groups[_owner.Materials.MaterialBatches[draw.Material]].GetWgpu<WGPUBindGroup>());
            Wgpu.DrawIndexed(pass, draw.Count * 3, 1, draw.First * 3, firstInstance: draw.Instance);
        }
    }

    private void Forward(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(_hdrKey, WGPULoadOp.Load),
            new WgpuReactiveRenderGraphDepthStencilAttachment(_frame.DepthTarget, WGPULoadOp.Undefined, WGPUStoreOp.Undefined, DepthReadOnly: true));
        DrawIndexedOpaque(pass, false);
    }

    private void DrawIndexedOpaque(WgpuHandle<WGPURenderPassEncoder> pass, bool depthOnly)
    {
        Wgpu.SetBindGroup(pass, 0, (depthOnly ? _rasterGroup : _frameGroup).GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _owner.Scene.Group.GetWgpu<WGPUBindGroup>());
        Wgpu.SetIndexBuffer(pass, _owner.Scene.Topology.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
        for (uint plane = 0; plane < (depthOnly ? 1u : _owner.Scene.VertexPlanes); plane++)
            Wgpu.SetVertexBuffer(pass, plane, _owner.Scene.Vertices.GetWgpu<WGPUBuffer>(), (ulong)plane * _owner.Scene.VertexCount * 16);
        bool? sided = null;
        var batch = -1;
        foreach (var draw in _opaque) {
            if (sided != draw.DoubleSided) {
                var pipeline = depthOnly ? (draw.DoubleSided ? _owner.Pipelines.PrepassDouble : _owner.Pipelines.Prepass)
                    : (draw.DoubleSided ? _owner.Pipelines.ForwardDouble : _owner.Pipelines.Forward);
                Wgpu.SetRenderPipeline(pass, pipeline.GetWgpu<WGPURenderPipeline>());
                sided = draw.DoubleSided;
            }
            var nextBatch = _owner.Materials.MaterialBatches[draw.Material];
            if (!depthOnly && nextBatch != batch) {
                Wgpu.SetBindGroup(pass, 2, _owner.Materials.Groups[nextBatch].GetWgpu<WGPUBindGroup>());
                batch = nextBatch;
            }
            Wgpu.DrawIndexed(pass, draw.Count * 3, 1, draw.FirstIndex, firstInstance: draw.Instance);
        }
    }

    private void ForwardBackground(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = Wgpu.BeginComputePass(context.CommandEncoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetBindGroup(pass, 0, _backgroundGroup.GetWgpu<WGPUBindGroup>());
            Wgpu.SetComputePipeline(pass, _owner.Pipelines.ForwardBackground.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, (_width + 7) / 8, (_height + 7) / 8);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }
}
