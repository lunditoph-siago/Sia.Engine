using Sia;
using Sia.Graphics.Reactive;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
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
        BindDrawInstances(pass, _owner.Scene.VertexPlanes);
        foreach (var draw in _transparent) {
            Wgpu.SetRenderPipeline(pass, (draw.DoubleSided ? _owner.Pipelines.CoverageDouble : _owner.Pipelines.Coverage).GetWgpu<WGPURenderPipeline>());
            Wgpu.SetBindGroup(pass, 2, _owner.Materials.Groups[_owner.Materials.MaterialBatches[draw.Material]].GetWgpu<WGPUBindGroup>());
            Wgpu.DrawIndexed(pass, draw.Count * 3, draw.InstanceCount, draw.FirstIndex, firstInstance: draw.Instance);
        }
    }

    private void Forward(WgpuReactiveRenderGraphPassContext context)
    {
        // Select depth and shade together: Equal against a separate prepass does
        // not identify the winning surface when distinct primitives share depth.
        var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(_hdrKey, WGPULoadOp.Clear),
            new WgpuReactiveRenderGraphDepthStencilAttachment(_frame.DepthTarget, WGPULoadOp.Clear));
        DrawIndexedOpaque(pass);
    }

    private void DrawIndexedOpaque(WgpuHandle<WGPURenderPassEncoder> pass)
    {
        Wgpu.SetBindGroup(pass, 0, _frameGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _owner.Scene.Group.GetWgpu<WGPUBindGroup>());
        Wgpu.SetIndexBuffer(pass, _owner.Scene.Topology.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
        var planes=_owner.Scene.VertexPackingMode!=0?0u:_owner.Scene.VertexPlanes;
        for (uint plane = 0; plane < planes; plane++)
            Wgpu.SetVertexBuffer(pass, plane, _owner.Scene.Vertices.GetWgpu<WGPUBuffer>(), (ulong)plane * _owner.Scene.VertexCount * 16);
        BindDrawInstances(pass, planes);
        bool? sided = null;
        var batch = -1;
        foreach (var draw in _opaque) {
            if (sided != draw.DoubleSided) {
                var pipeline = draw.DoubleSided ? _owner.Pipelines.ForwardDouble : _owner.Pipelines.Forward;
                Wgpu.SetRenderPipeline(pass, pipeline.GetWgpu<WGPURenderPipeline>());
                sided = draw.DoubleSided;
            }
            var nextBatch = _owner.Materials.MaterialBatches[draw.Material];
            if (nextBatch != batch) {
                Wgpu.SetBindGroup(pass, 2, _owner.Materials.Groups[nextBatch].GetWgpu<WGPUBindGroup>());
                batch = nextBatch;
            }
            Wgpu.DrawIndexed(pass, draw.Count * 3, draw.InstanceCount, draw.FirstIndex, firstInstance: draw.Instance);
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
