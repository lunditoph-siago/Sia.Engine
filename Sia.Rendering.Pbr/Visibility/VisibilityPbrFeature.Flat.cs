using Sia;
using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private sealed partial class ViewState
    {
        private Entity _flatLayout, _flatPipeline, _flatGroup;
        private WgpuHandle<WGPUTextureView> _flatId;

        public void PrepareFlatOutput()
        {
            if (_flatPipeline.IsValid) return;
            var acquired = new List<Entity>();
            var device = Owner._device.GetWgpu<WGPUDevice>();
            try {
                var layout = Layout(Owner._world, device, [
                    TextureLayout(0, WGPUTextureSampleType.Uint, WGPUShaderStage.Fragment),
                    BufferLayout(2, WGPUBufferBindingType.ReadOnlyStorage, 80, WGPUShaderStage.Fragment),
                    BufferLayout(3, WGPUBufferBindingType.Uniform, 16, WGPUShaderStage.Fragment)], acquired);
                var pipelineLayout = PipelineLayout(Owner._world, device, [Owner._geometryLayout, layout], acquired);
                var shader = Own(Owner._world, Wgpu.CreateWgslShaderModule(device,
                    PbrShaderSource.LoadVisibilityFlat(), "visibility-flat"), acquired);
                var pipeline = Own(Owner._world, PbrIblPrecomputePipelines.CreateFullscreenPipeline(device,
                    shader.GetWgpu<WGPUShaderModule>(), pipelineLayout.GetWgpu<WGPUPipelineLayout>(), Owner._output.Format), acquired);
                _flatLayout = layout;
                _flatPipeline = pipeline;
            }
            catch {
                for (var i = acquired.Count - 1; i >= 0; i--) acquired[i].Destroy();
                throw;
            }
        }

        public void DeclareFlatOutput(RenderGraphPassDeclarationBuilder declaration)
        {
            declaration.Read(s_CameraKey, RenderGraphBufferUsage.Uniform).Read(s_OutputKey, RenderGraphBufferUsage.Uniform)
                .Read(s_WorkKey, RenderGraphBufferUsage.Storage).Read(s_GeometryKeys[3], RenderGraphBufferUsage.Storage)
                .Read(s_MaterialParametersKey, RenderGraphBufferUsage.Storage)
                .Read(Owner.VisibilityTarget, RenderGraphTextureUsage.TextureBinding)
                .Write(Frame.ColorTarget, RenderGraphTextureUsage.RenderAttachment);
            if (Timing is not null) declaration.Write(Owner.GpuTimingsTarget, RenderGraphBufferUsage.CopyDestination);
        }

        public void FlatOutput(WgpuReactiveRenderGraphPassContext context)
        {
            var id = context.GetTextureView(Owner.VisibilityTarget);
            if (!_flatGroup.IsValid || id != _flatId) {
                var next = Owner.OwnTextureBindGroup(_flatLayout,
                    [TextureEntry(0, id), BufferEntry(2, Owner._materialParameters), BufferEntry(3, OutputUniform)], id);
                if (_flatGroup.IsValid) _flatGroup.Destroy();
                _flatGroup = next; _flatId = id;
            }
            var pass = TimingActive ? BeginTimedRender(context, Frame.ColorTarget, Frame.ColorLoadOp, false)
                : context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(Frame.ColorTarget, Frame.ColorLoadOp, Cacheable: Frame.ColorCacheable));
            try {
                Wgpu.SetRenderPipeline(pass, _flatPipeline.GetWgpu<WGPURenderPipeline>());
                Wgpu.SetBindGroup(pass, 0, Group.GetWgpu<WGPUBindGroup>());
                Wgpu.SetBindGroup(pass, 1, _flatGroup.GetWgpu<WGPUBindGroup>());
                Wgpu.Draw(pass, 3);
            } finally { if (TimingActive) { Wgpu.EndRenderPass(pass); Wgpu.Release(ref pass); } }
            if (TimingActive) ResolveTiming(context);
        }
    }
}
