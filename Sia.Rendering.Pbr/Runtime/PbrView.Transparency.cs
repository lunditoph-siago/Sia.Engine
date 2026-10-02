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
    private void Snapshot(WgpuReactiveRenderGraphPassContext context)
    {
        if (!_needsSnapshot) return;
        var source = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)_hdr.GetWgpu<WGPUTexture>().DangerousGetHandle(), Aspect = WGPUTextureAspect.All };
        var target = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)_snapshot.GetWgpu<WGPUTexture>().DangerousGetHandle(), Aspect = WGPUTextureAspect.All };
        var extent = new WGPUExtent3D { Width = _width, Height = _height, DepthOrArrayLayers = 1 };
        WgpuUnsafe.wgpuCommandEncoderCopyTextureToTexture((WGPUCommandEncoder*)context.CommandEncoder.DangerousGetHandle(), &source, &target, &extent);
    }

    private void Transparency(WgpuReactiveRenderGraphPassContext context)
    {
        if (_transparent.Count == 0) return;
        var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(_hdrKey, WGPULoadOp.Load),
            new WgpuReactiveRenderGraphDepthStencilAttachment(_frame.DepthTarget, WGPULoadOp.Undefined, WGPUStoreOp.Undefined, DepthReadOnly: true));
        Wgpu.SetBindGroup(pass, 0, _frameGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _owner.Scene.Group.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 3, _glassGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetIndexBuffer(pass, _owner.Scene.Topology.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
        for (uint plane = 0; plane < 3; plane++)
            Wgpu.SetVertexBuffer(pass, plane, _owner.Scene.Vertices.GetWgpu<WGPUBuffer>(), (ulong)plane * _owner.Scene.VertexCount * 16);
        Entity previous = default;
        foreach (var draw in _transparent) {
            var pipelines = _owner.Pipelines;
            var pipeline = _owner.Materials.Transmission[draw.Material]
                ? (draw.DoubleSided ? pipelines.TransmissionDouble : pipelines.Transmission)
                : (draw.DoubleSided ? pipelines.TransparentDouble : pipelines.Transparent);
            if (previous != pipeline) {
                Wgpu.SetRenderPipeline(pass, pipeline.GetWgpu<WGPURenderPipeline>());
                previous = pipeline;
            }
            Wgpu.SetBindGroup(pass, 2, _owner.Materials.Groups[_owner.Materials.MaterialBatches[draw.Material]].GetWgpu<WGPUBindGroup>());
            Wgpu.DrawIndexed(pass, draw.Count * 3, 1, draw.First * 3);
        }
    }
}
