using Sia;
using Sia.Engine.Mesh;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static readonly RenderGraphBufferKey[] s_GeometryKeys = [
        new("visibility-vertices"), new("visibility-meshlets"), new("visibility-indices"),
        new("visibility-triangles"), new("visibility-instances")
    ];
    private static readonly RenderGraphBufferKey s_CameraKey = new("visibility-camera");
    private static readonly RenderGraphBufferKey s_IndirectKey = new("visibility-indirect");
    private static readonly RenderGraphBufferKey s_WorkKey = new("visibility-work");
    private static readonly RenderGraphBufferKey s_OutputKey = new("visibility-output-params");
    private static readonly RenderGraphTextureKey s_AlbedoKey = new("visibility-albedo");

    public RenderGraphTextureKey VisibilityTarget { get; } = new("visibility-id");
    public RenderGraphTextureKey HdrTarget { get; } = new("visibility-hdr");

    public void BuildRenderGraph(ref RenderGraphBuildContext graph, in RenderFeatureContext<RenderFrameContext> context)
    {
        var view = context.View.PersistentResources.GetRequired<ViewState>();
        graph.UseTexture(VisibilityTarget, new RenderGraphTextureDescriptor("visibility-id", RenderGraphTextureFormat.R32Uint,
            view.Width, view.Height));
        graph.UseTexture(HdrTarget, new RenderGraphTextureDescriptor("visibility-hdr", RenderGraphTextureFormat.RGBA16Float,
            view.Width, view.Height));
        ImportBuffer(ref graph, s_CameraKey, view.Uniform, RenderGraphBufferUsage.Uniform);
        ImportBuffer(ref graph, s_OutputKey, view.OutputUniform, RenderGraphBufferUsage.Uniform);
        ImportBuffer(ref graph, s_IndirectKey, view.Indirect, RenderGraphBufferUsage.Indirect);
        ImportBuffer(ref graph, s_WorkKey, view.WorkBuffer, RenderGraphBufferUsage.Storage);
        for (var i = 0; i < _geometry.Length; i++) {
            ImportBuffer(ref graph, s_GeometryKeys[i], _geometry[i], RenderGraphBufferUsage.Storage);
        }
        var albedo = Wgpu.GetTextureInfo(_albedoTexture.GetWgpu<WGPUTexture>());
        graph.UseImportedTexture(s_AlbedoKey, new RenderGraphTextureDescriptor("visibility-albedo", RenderGraphTextureFormat.RGBA8Unorm,
            albedo.Size.Width, albedo.Size.Height, mipLevelCount: albedo.MipLevelCount, usage: RenderGraphTextureUsage.TextureBinding));
        graph.BindImportedTexture(s_AlbedoKey, _albedoTexture.GetWgpu<WGPUTexture>());
        graph.UsePass(new("visibility-raster"), "visibility-raster", view.DeclareRaster, view.Raster);
        graph.UseComputePass(new("visibility-resolve"), "visibility-resolve", view.DeclareResolve, view.Resolve);
        graph.UsePass(new("visibility-output"), "visibility-output", view.DeclareOutput, view.Output);
    }

    private static void ImportBuffer(ref RenderGraphBuildContext graph, RenderGraphBufferKey key, Entity entity, RenderGraphBufferUsage usage)
    {
        var buffer = entity.GetWgpu<WGPUBuffer>();
        graph.UseImportedBuffer(key, new RenderGraphBufferDescriptor(key.ToString(), Wgpu.GetBufferSize(buffer), usage));
        graph.BindImportedBuffer(key, buffer);
    }

    private ViewState CreateView()
    {
        var acquired = new List<Entity>();
        var device = _device.GetWgpu<WGPUDevice>();
        var queue = _queue.GetWgpu<WGPUQueue>();
        var limits = Wgpu.GetLimits(device);
        try {
            var uniform = Upload<CameraGpu>(_world, device, queue, [default], WGPUBufferUsage.Uniform, limits, acquired);
            var outputUniform = Upload<float4>(_world, device, queue,
                [new float4(1, _output.EncodeSrgb ? 1 : 0, 0, 0)], WGPUBufferUsage.Uniform, limits, acquired);
            var workItems = new uint4[checked((int)TriangleCapacity)];
            var workBuffer = Upload<uint4>(_world, device, queue, workItems, WGPUBufferUsage.Storage, limits, acquired);
            var indirect = Upload<uint>(_world, device, queue, [0, 1, 0, 0], WGPUBufferUsage.Indirect, limits, acquired);
            var entries = new WGPUBindGroupEntry[7];
            entries[0] = BufferEntry(0, uniform);
            for (var i = 0; i < _geometry.Length; i++) { entries[i + 1] = BufferEntry((uint)i + 1, _geometry[i]); }
            entries[6] = BufferEntry(6, workBuffer);
            var group = Own(_world, BindGroup(_geometryLayout, entries), acquired);
            return new(this, uniform, outputUniform, group, workBuffer, indirect, workItems);
        }
        catch {
            for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); }
            throw;
        }
    }

    private static unsafe WGPUBindGroupEntry BufferEntry(uint binding, Entity entity)
    {
        var buffer = entity.GetWgpu<WGPUBuffer>();
        var entry = WGPUBindGroupEntry.Default;
        entry.Binding = binding;
        entry.Buffer = (WGPUBuffer*)buffer.DangerousGetHandle();
        entry.Size = Wgpu.GetBufferSize(buffer);
        return entry;
    }

    private static unsafe WGPUBindGroupEntry TextureEntry(uint binding, WgpuHandle<WGPUTextureView> view)
    {
        var entry = WGPUBindGroupEntry.Default;
        entry.Binding = binding;
        entry.TextureView = (WGPUTextureView*)view.DangerousGetHandle();
        return entry;
    }

    private unsafe WgpuHandle<WGPUBindGroup> BindGroup(Entity layout, ReadOnlySpan<WGPUBindGroupEntry> entries)
    {
        fixed (WGPUBindGroupEntry* pointer = entries) {
            var descriptor = WGPUBindGroupDescriptor.Default;
            descriptor.Layout = (WGPUBindGroupLayout*)layout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            descriptor.EntryCount = (nuint)entries.Length;
            descriptor.Entries = pointer;
            return Wgpu.CreateBindGroup(_device.GetWgpu<WGPUDevice>(), descriptor);
        }
    }

    private sealed class ViewState(VisibilityPbrFeature owner, Entity uniform, Entity outputUniform, Entity group,
        Entity workBuffer, Entity indirect, uint4[] workItems)
    {
        public VisibilityPbrFeature Owner { get; } = owner;
        public Entity Uniform { get; } = uniform;
        public Entity OutputUniform { get; } = outputUniform;
        public Entity Group { get; } = group;
        public Entity WorkBuffer { get; } = workBuffer;
        public Entity Indirect { get; } = indirect;
        public uint4[] WorkItems { get; } = workItems;
        public uint WorkCount { get; set; }
        public bool WorkInitialized { get; set; }
        public MeshPatchSelection? Selection { get; set; }
        public RenderFrameContext Frame { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        private Entity _resolveGroup;
        private Entity _outputGroup;
        private WgpuHandle<WGPUTextureView> _idView;
        private WgpuHandle<WGPUTextureView> _hdrView;
        private WgpuHandle<WGPUTextureView> _outputSource;

        private static void ReadGeometry(RenderGraphPassDeclarationBuilder declaration)
        {
            declaration.Read(s_CameraKey, RenderGraphBufferUsage.Uniform);
            declaration.Read(s_WorkKey, RenderGraphBufferUsage.Storage);
            foreach (var key in s_GeometryKeys) { declaration.Read(key, RenderGraphBufferUsage.Storage); }
        }

        public void DeclareRaster(RenderGraphPassDeclarationBuilder declaration)
        {
            ReadGeometry(declaration);
            declaration.Read(s_IndirectKey, RenderGraphBufferUsage.Indirect)
                .Write(Owner.VisibilityTarget, RenderGraphTextureUsage.RenderAttachment)
                .Write(Frame.DepthTarget, RenderGraphTextureUsage.RenderAttachment);
        }

        public void Raster(WgpuReactiveRenderGraphPassContext context)
        {
            var pass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphColorAttachment(Owner.VisibilityTarget, WGPULoadOp.Clear),
                new WgpuReactiveRenderGraphDepthStencilAttachment(Frame.DepthTarget, WGPULoadOp.Clear));
            Wgpu.SetRenderPipeline(pass, Owner._raster.GetWgpu<WGPURenderPipeline>());
            Wgpu.SetBindGroup(pass, 0, Group.GetWgpu<WGPUBindGroup>());
            Wgpu.DrawIndirect(pass, Indirect.GetWgpu<WGPUBuffer>(), 0);
        }

        public void DeclareResolve(RenderGraphPassDeclarationBuilder declaration)
        {
            ReadGeometry(declaration);
            declaration.Read(Owner.VisibilityTarget, RenderGraphTextureUsage.TextureBinding)
                .Read(s_AlbedoKey, RenderGraphTextureUsage.TextureBinding)
                .Write(Owner.HdrTarget, RenderGraphTextureUsage.StorageBinding);
        }

        public unsafe void Resolve(WgpuReactiveRenderGraphPassContext context)
        {
            var id = context.GetTextureView(Owner.VisibilityTarget);
            var hdr = context.GetTextureView(Owner.HdrTarget);
            if (!_resolveGroup.IsValid || id != _idView || hdr != _hdrView) {
                var sampler = WGPUBindGroupEntry.Default;
                sampler.Binding = 3;
                sampler.Sampler = (WGPUSampler*)Owner._sampler.GetWgpu<WGPUSampler>().DangerousGetHandle();
                var next = Owner._world.OwnWgpu(Owner.BindGroup(Owner._resolveLayout, [
                    TextureEntry(0, id), TextureEntry(1, hdr), TextureEntry(2, Owner._albedoView.GetWgpu<WGPUTextureView>()), sampler
                ]));
                if (_resolveGroup.IsValid) { _resolveGroup.Destroy(); }
                _resolveGroup = next;
                _idView = id;
                _hdrView = hdr;
            }
            var pass = context.GetOrBeginComputePass();
            try {
                Wgpu.SetComputePipeline(pass, Owner._resolve.GetWgpu<WGPUComputePipeline>());
                Wgpu.SetBindGroup(pass, 0, Group.GetWgpu<WGPUBindGroup>());
                Wgpu.SetBindGroup(pass, 1, _resolveGroup.GetWgpu<WGPUBindGroup>());
                Wgpu.DispatchWorkgroups(pass, (Width + 7) / 8, (Height + 7) / 8);
            }
            finally {
                Wgpu.EndComputePass(pass);
                Wgpu.Release(ref pass);
            }
        }

        public void DeclareOutput(RenderGraphPassDeclarationBuilder declaration) => declaration
            .Read(s_OutputKey, RenderGraphBufferUsage.Uniform)
            .Read(Owner.HdrTarget, RenderGraphTextureUsage.TextureBinding)
            .Write(Frame.ColorTarget, RenderGraphTextureUsage.RenderAttachment);

        public void Output(WgpuReactiveRenderGraphPassContext context)
        {
            var source = context.GetTextureView(Owner.HdrTarget);
            if (!_outputGroup.IsValid || source != _outputSource) {
                var next = Owner._world.OwnWgpu(Owner.BindGroup(Owner._output.Layout,
                    [BufferEntry(0, OutputUniform), TextureEntry(1, source)]));
                if (_outputGroup.IsValid) { _outputGroup.Destroy(); }
                _outputGroup = next;
                _outputSource = source;
            }
            var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(
                Frame.ColorTarget, Frame.ColorLoadOp, Cacheable: Frame.ColorCacheable));
            Wgpu.SetRenderPipeline(pass, Owner._output.Pipeline.GetWgpu<WGPURenderPipeline>());
            Wgpu.SetBindGroup(pass, 0, _outputGroup.GetWgpu<WGPUBindGroup>());
            Wgpu.Draw(pass, 3);
        }
    }
}
