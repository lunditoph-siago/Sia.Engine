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
    private void Output(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(_frame.ColorTarget, WGPULoadOp.Clear, Cacheable: false));
        Wgpu.SetRenderPipeline(pass, _owner.Pipelines.Output.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, _outputGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }
}
