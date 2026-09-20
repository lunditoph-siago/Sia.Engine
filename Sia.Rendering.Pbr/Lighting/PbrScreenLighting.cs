using System.Runtime.InteropServices;
using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe class PbrScreenLighting
{
    private readonly GpuFrame _frame;
    private readonly Entity _camera;
    private readonly Entity[] _layouts = new Entity[2], _pipelines = new Entity[2], _groups = new Entity[2];
    private readonly WgpuHandle<WGPUTextureView>[][] _sources = [[], []];
    private readonly WgpuHandle<WGPUTextureView>[] _scratch = new WgpuHandle<WGPUTextureView>[7];
    private readonly bool _indirect;
    private static readonly RenderGraphTextureKey Output = new("pbr-screen-output");
    private static readonly RenderGraphTextureKey Gather = new("pbr-screen-gather");
    private static readonly RenderGraphTextureKey GatherSurface = new("pbr-screen-surface");
    private static readonly RenderGraphBufferKey Camera = new("pbr-screen-camera");
    private RenderGraphTextureKey _color, _depth;
    private VisibilityPbrFeature _visibility = null!;
    private PbrViewState _lighting = null!;

    public PbrScreenLighting(in GpuFrame frame, bool reflections, bool indirect)
    {
        _frame = frame; _indirect = indirect;
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
            var light = Own(PbrLightingBindGroupLayout.Create(device));
            var ibl = Own(PbrIblBindGroupLayout.Create(device));
            for (var stage = 0; stage < (indirect ? 2 : 1); stage++) {
                var count = stage == 0 && indirect ? 8 : 6;
                var entries = new WGPUBindGroupLayoutEntry[count];
                for (uint i = 0; i < count; i++) {
                    entries[i] = WGPUBindGroupLayoutEntry.Default;
                    entries[i].Binding = i;
                    entries[i].Visibility = WGPUShaderStage.Fragment;
                    if (i == 0) {
                        entries[i].Buffer.Type = WGPUBufferBindingType.Uniform;
                        entries[i].Buffer.MinBindingSize = 144;
                    } else {
                        entries[i].Texture.ViewDimension = WGPUTextureViewDimension._2D;
                        entries[i].Texture.SampleType = i is 2 or 7 ? WGPUTextureSampleType.UnfilterableFloat : WGPUTextureSampleType.Float;
                    }
                }
                fixed (WGPUBindGroupLayoutEntry* ptr = entries) {
                    _layouts[stage] = Own(Wgpu.CreateBindGroupLayout(device, new WGPUBindGroupLayoutDescriptor { EntryCount = (nuint)count, Entries = ptr }));
                }
                var layouts = new WGPUBindGroupLayout*[3];
                layouts[0] = (WGPUBindGroupLayout*)_layouts[stage].GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
                layouts[1] = (WGPUBindGroupLayout*)light.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
                layouts[2] = (WGPUBindGroupLayout*)ibl.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
                Entity pipelineLayout;
                fixed (WGPUBindGroupLayout** ptr = layouts) {
                    pipelineLayout = Own(Wgpu.CreatePipelineLayout(device, new WGPUPipelineLayoutDescriptor { BindGroupLayoutCount = 3, BindGroupLayouts = ptr }));
                }
                var label = stage == 0 ? "pbr-screen-lighting" : "pbr-screen-gather";
                var shader = Own(Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadScreenLighting(stage == 1, reflections, indirect), label));
                _pipelines[stage] = Own(PbrIblPrecomputePipelines.CreateFullscreenPipeline(device,
                    shader.GetWgpu<WGPUShaderModule>(), pipelineLayout.GetWgpu<WGPUPipelineLayout>(), PbrOutputPipelines.HdrFormat,
                    stage == 1 ? WGPUTextureFormat.RGBA32Float : WGPUTextureFormat.Undefined));
            }
            _camera = Own(Wgpu.CreateBuffer(device, new WGPUBufferDescriptor { Size = 144, Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst }));
        } catch {
            for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); }
            throw;
        }
    }

    public void Prepare(in GpuFrame frame, PbrExtractedView view)
    {
        if (frame.Device != _frame.Device || frame.Queue != _frame.Queue || frame.ResourceWorld != _frame.ResourceWorld) {
            throw new InvalidOperationException("Screen lighting resources belong to another device, queue or world.");
        }
        var camera = view.CameraMatrices;
        Wgpu.WriteBuffer<CameraData>(frame.Queue.GetWgpu<WGPUQueue>(), _camera.GetWgpu<WGPUBuffer>(), 0,
            [new(camera.ViewProj, new(camera.WorldPosition, 1), math.inverse(camera.ViewProj))]);
    }

    public RenderGraphTextureKey BuildGraph(ref RenderGraphBuildContext graph, RenderGraphTextureKey color, RenderGraphTextureKey depth,
        VisibilityPbrFeature visibility, PbrViewState lighting, PbrExtractedView view)
    {
        if (Output == color || Output == depth || Output == visibility.HdrTarget || Output == visibility.VisibilityTarget
            || Output == visibility.BaseColorRoughnessTarget
            || Output == visibility.NormalMetallicTarget || Output == visibility.EmissiveOcclusionTarget) {
            throw new InvalidOperationException("Screen lighting requires a distinct output target.");
        }
        _color = color; _depth = depth; _visibility = visibility; _lighting = lighting;
        var width = (uint)view.Viewport.Width;
        var height = (uint)view.Viewport.Height;
        graph.UseTexture(Output, new RenderGraphTextureDescriptor("pbr-screen-output", RenderGraphTextureFormat.RGBA16Float, width, height));
        graph.UseImportedBuffer(Camera, new RenderGraphBufferDescriptor("pbr-screen-camera", 144, RenderGraphBufferUsage.Uniform));
        graph.BindImportedBuffer(Camera, _camera.GetWgpu<WGPUBuffer>());
        if (_indirect) {
            graph.UseTexture(Gather, new RenderGraphTextureDescriptor("pbr-screen-gather", RenderGraphTextureFormat.RGBA16Float,
                (width + 7) / 8, (height + 7) / 8));
            graph.UseTexture(GatherSurface, new RenderGraphTextureDescriptor("pbr-screen-surface", RenderGraphTextureFormat.RGBA32Float,
                (width + 7) / 8, (height + 7) / 8));
            graph.UsePass(new("pbr-screen-gather"), "pbr-screen-gather", d => Declare(d, true), c => Render(c, 1));
        }
        graph.UsePass(new("pbr-screen-lighting"), "pbr-screen-lighting", d => Declare(d, false), c => Render(c, 0));
        // Keep hook order stable when the debug view changes, but bypass its output.
        return visibility.DebugMode == VisibilityDebugMode.Shaded ? Output : color;
    }

    private void Declare(RenderGraphPassDeclarationBuilder d, bool gather)
    {
        if (_visibility.DebugMode != VisibilityDebugMode.Shaded) { return; }
        d.Read(Camera, RenderGraphBufferUsage.Uniform)
        .Read(_color, RenderGraphTextureUsage.TextureBinding).Read(_depth, RenderGraphTextureUsage.TextureBinding)
        .Read(_visibility.BaseColorRoughnessTarget, RenderGraphTextureUsage.TextureBinding)
        .Read(_visibility.NormalMetallicTarget, RenderGraphTextureUsage.TextureBinding)
        .Read(_visibility.EmissiveOcclusionTarget, RenderGraphTextureUsage.TextureBinding)
        .Read(new RenderGraphTextureKey("pbr-ibl-prefiltered"), RenderGraphTextureUsage.TextureBinding)
        .Read(new RenderGraphTextureKey("pbr-ibl-brdf-lut"), RenderGraphTextureUsage.TextureBinding)
        .Write(gather ? Gather : Output, RenderGraphTextureUsage.RenderAttachment);
        if (gather) { d.Write(GatherSurface, RenderGraphTextureUsage.RenderAttachment); }
        if (_indirect && !gather) { d.Read(Gather, RenderGraphTextureUsage.TextureBinding).Read(GatherSurface, RenderGraphTextureUsage.TextureBinding); }
    }

    private void Render(WgpuReactiveRenderGraphPassContext c, int stage)
    {
        if (_visibility.DebugMode != VisibilityDebugMode.Shaded) { return; }
        _scratch[0] = c.GetTextureView(_color); _scratch[1] = c.GetTextureView(_depth);
        _scratch[2] = c.GetTextureView(_visibility.BaseColorRoughnessTarget);
        _scratch[3] = c.GetTextureView(_visibility.NormalMetallicTarget);
        _scratch[4] = c.GetTextureView(_visibility.EmissiveOcclusionTarget);
        var count = stage == 0 && _indirect ? 7 : 5;
        if (count == 7) { _scratch[5] = c.GetTextureView(Gather); _scratch[6] = c.GetTextureView(GatherSurface); }
        var sources = _scratch.AsSpan(0, count);
        var changed = !_groups[stage].IsValid || _sources[stage].Length != count;
        for (var i = 0; i < count && !changed; i++) {
            changed = sources[i].DangerousGetHandle() != _sources[stage][i].DangerousGetHandle();
        }
        if (changed) {
            var entries = new WGPUBindGroupEntry[count + 1];
            entries[0] = new() { Binding = 0, Buffer = (WGPUBuffer*)_camera.GetWgpu<WGPUBuffer>().DangerousGetHandle(), Size = 144 };
            for (uint i = 0; i < count; i++) { entries[i + 1] = new() { Binding = i + 1, TextureView = (WGPUTextureView*)sources[(int)i].DangerousGetHandle() }; }
            var next = PbrTextureBindGroups.Create(_frame.ResourceWorld, _frame.Device.GetWgpu<WGPUDevice>(), _layouts[stage], entries, sources);
            if (_groups[stage].IsValid) { _groups[stage].Destroy(); }
            _groups[stage] = next; _sources[stage] = sources.ToArray();
        }
        var pass = stage == 0
            ? c.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(Output, WGPULoadOp.Clear))
            : c.GetOrBeginRenderPass([new(Gather, WGPULoadOp.Clear), new(GatherSurface, WGPULoadOp.Clear)]);
        Wgpu.SetRenderPipeline(pass, _pipelines[stage].GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, _groups[stage].GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _lighting.ForwardLightingBindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 2, _lighting.IblBindGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CameraData(float4x4 Projection, float4 Eye, float4x4 Inverse);
}
