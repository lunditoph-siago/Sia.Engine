using Sia.Graphics.Reactive;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    private void Reflections(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(_reflectionRadianceKey, WGPULoadOp.Clear));
        if (_owner.DebugMode != VisibilityDebugMode.Shaded) return;
        Wgpu.SetRenderPipeline(pass, _owner.Pipelines.Reflection.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, _reflectionFrameGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _reflectionGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }

    private void CompositeReflections(WgpuReactiveRenderGraphPassContext context)
    {
        if (_owner.DebugMode != VisibilityDebugMode.Shaded) return;
        var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(_hdrKey, WGPULoadOp.Load));
        Wgpu.SetRenderPipeline(pass, _owner.Pipelines.ReflectionComposite.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, _reflectionFrameGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _reflectionCompositeGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }
}
