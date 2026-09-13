using System.Runtime.InteropServices;
using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe class PbrScreenReflections
{
    private readonly GpuFrame _frame;
    private readonly Entity _layout, _pipeline, _camera;
    private Entity _group;
    private WgpuHandle<WGPUTextureView>[] _sources = [];
    private static readonly RenderGraphTextureKey Snapshot = new("pbr-reflection-source");
    private static readonly RenderGraphBufferKey Camera = new("pbr-reflection-camera");
    private RenderGraphTextureKey _color, _depth;
    private VisibilityPbrFeature _visibility = null!;
    private PbrViewState _lighting = null!;
    private uint _width, _height;

    public PbrScreenReflections(in GpuFrame frame)
    {
        _frame = frame;
        var device = frame.Device.GetWgpu<WGPUDevice>();
        var world = frame.ResourceWorld;
        var acquired = new List<Entity>();
        Entity Own<T>(WgpuHandle<T> value) where T : unmanaged
        {
            var entity = world.OwnWgpu(value);
            acquired.Add(entity);
            return entity;
        }
        try {
            var entries = new WGPUBindGroupLayoutEntry[6];
            for (uint i = 0; i < 6; i++) {
                entries[i] = WGPUBindGroupLayoutEntry.Default;
                entries[i].Binding = i;
                entries[i].Visibility = WGPUShaderStage.Fragment;
                if (i == 0) {
                    entries[i].Buffer.Type = WGPUBufferBindingType.Uniform;
                    entries[i].Buffer.MinBindingSize = 144;
                } else {
                    entries[i].Texture.ViewDimension = WGPUTextureViewDimension._2D;
                    entries[i].Texture.SampleType = i == 2 ? WGPUTextureSampleType.Depth : WGPUTextureSampleType.Float;
                }
            }
            fixed (WGPUBindGroupLayoutEntry* ptr = entries) {
                _layout = Own(Wgpu.CreateBindGroupLayout(device, new WGPUBindGroupLayoutDescriptor { EntryCount = 6, Entries = ptr }));
            }
            var light = Own(PbrLightingBindGroupLayout.Create(device));
            var ibl = Own(PbrIblBindGroupLayout.Create(device));
            var layouts = stackalloc WGPUBindGroupLayout*[3];
            layouts[0] = (WGPUBindGroupLayout*)_layout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            layouts[1] = (WGPUBindGroupLayout*)light.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            layouts[2] = (WGPUBindGroupLayout*)ibl.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            var pipelineLayout = Own(Wgpu.CreatePipelineLayout(device, new WGPUPipelineLayoutDescriptor { BindGroupLayoutCount = 3, BindGroupLayouts = layouts }));
            var shader = Own(Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadScreenReflections(), "pbr-screen-reflections"));
            _pipeline = Own(PbrIblPrecomputePipelines.CreateFullscreenPipeline(device,
                shader.GetWgpu<WGPUShaderModule>(), pipelineLayout.GetWgpu<WGPUPipelineLayout>(), PbrOutputPipelines.HdrFormat));
            _camera = Own(Wgpu.CreateBuffer(device, new WGPUBufferDescriptor { Size = 144, Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst }));
        } catch {
            for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); }
            throw;
        }
    }

    public void Prepare(in GpuFrame frame, PbrExtractedView view)
    {
        if (frame.Device != _frame.Device || frame.Queue != _frame.Queue || frame.ResourceWorld != _frame.ResourceWorld) {
            throw new InvalidOperationException("Reflection resources belong to another device, queue or world.");
        }
        var camera = view.CameraMatrices;
        Wgpu.WriteBuffer<CameraData>(frame.Queue.GetWgpu<WGPUQueue>(), _camera.GetWgpu<WGPUBuffer>(), 0,
            [new(camera.ViewProj, new(camera.WorldPosition, 1), math.inverse(camera.ViewProj))]);
    }

    public void BuildGraph(ref RenderGraphBuildContext graph, RenderGraphTextureKey color, RenderGraphTextureKey depth,
        VisibilityPbrFeature visibility, PbrViewState lighting, PbrExtractedView view)
    {
        _color = color; _depth = depth; _visibility = visibility; _lighting = lighting;
        _width = (uint)view.Viewport.Width; _height = (uint)view.Viewport.Height;
        graph.UseTexture(Snapshot, new RenderGraphTextureDescriptor("pbr-reflection-source", RenderGraphTextureFormat.RGBA16Float, _width, _height));
        graph.UseImportedBuffer(Camera, new RenderGraphBufferDescriptor("pbr-reflection-camera", 144, RenderGraphBufferUsage.Uniform));
        graph.BindImportedBuffer(Camera, _camera.GetWgpu<WGPUBuffer>());
        // A non-render pass ends the lighting attachment before the encoder copy.
        graph.UseComputePass(new("pbr-reflection-snapshot"), "pbr-reflection-snapshot",
            d => d.Read(_color, RenderGraphTextureUsage.CopySource).Write(Snapshot, RenderGraphTextureUsage.CopyDestination), Copy);
        graph.UsePass(new("pbr-screen-reflections"), "pbr-screen-reflections", Declare, Render);
    }

    private void Declare(RenderGraphPassDeclarationBuilder d) => d.Read(Camera, RenderGraphBufferUsage.Uniform)
        .Read(Snapshot, RenderGraphTextureUsage.TextureBinding).Read(_depth, RenderGraphTextureUsage.TextureBinding)
        .Read(_visibility.BaseColorRoughnessTarget, RenderGraphTextureUsage.TextureBinding)
        .Read(_visibility.NormalMetallicTarget, RenderGraphTextureUsage.TextureBinding)
        .Read(_visibility.EmissiveOcclusionTarget, RenderGraphTextureUsage.TextureBinding)
        .Read(new RenderGraphTextureKey("pbr-ibl-prefiltered"), RenderGraphTextureUsage.TextureBinding)
        .Read(new RenderGraphTextureKey("pbr-ibl-brdf-lut"), RenderGraphTextureUsage.TextureBinding)
        .Write(_color, RenderGraphTextureUsage.RenderAttachment);

    private void Copy(WgpuReactiveRenderGraphPassContext c)
    {
        var from = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)c.GetTexture(_color).DangerousGetHandle(), Aspect = WGPUTextureAspect.All };
        var to = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)c.GetTexture(Snapshot).DangerousGetHandle(), Aspect = WGPUTextureAspect.All };
        var size = new WGPUExtent3D { Width = _width, Height = _height, DepthOrArrayLayers = 1 };
        WgpuUnsafe.wgpuCommandEncoderCopyTextureToTexture((WGPUCommandEncoder*)c.CommandEncoder.DangerousGetHandle(), &from, &to, &size);
    }

    private void Render(WgpuReactiveRenderGraphPassContext c)
    {
        WgpuHandle<WGPUTextureView>[] sources = [c.GetTextureView(Snapshot), c.GetTextureView(_depth),
            c.GetTextureView(_visibility.BaseColorRoughnessTarget), c.GetTextureView(_visibility.NormalMetallicTarget),
            c.GetTextureView(_visibility.EmissiveOcclusionTarget)];
        if (!_group.IsValid || _sources.Length != sources.Length || sources.Where((v, i) => v.DangerousGetHandle() != _sources[i].DangerousGetHandle()).Any()) {
            var entries = new WGPUBindGroupEntry[6];
            entries[0] = new() { Binding = 0, Buffer = (WGPUBuffer*)_camera.GetWgpu<WGPUBuffer>().DangerousGetHandle(), Size = 144 };
            for (uint i = 0; i < 5; i++) { entries[i + 1] = new() { Binding = i + 1, TextureView = (WGPUTextureView*)sources[i].DangerousGetHandle() }; }
            var next = PbrTextureBindGroups.Create(_frame.ResourceWorld, _frame.Device.GetWgpu<WGPUDevice>(), _layout, entries, sources);
            if (_group.IsValid) { _group.Destroy(); }
            _group = next; _sources = sources;
        }
        var pass = c.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(_color, WGPULoadOp.Clear));
        Wgpu.SetRenderPipeline(pass, _pipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, _group.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _lighting.ForwardLightingBindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 2, _lighting.IblBindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CameraData(float4x4 Projection, float4 Eye, float4x4 Inverse);
}
