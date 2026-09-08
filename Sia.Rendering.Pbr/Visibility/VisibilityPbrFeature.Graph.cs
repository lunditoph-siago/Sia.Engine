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
        new("visibility-vertices"), new("visibility-indices"),
        new("visibility-triangles"), new("visibility-instances")
    ];
    private static readonly RenderGraphBufferKey s_CameraKey = new("visibility-camera");
    private static readonly RenderGraphBufferKey s_IndirectKey = new("visibility-indirect");
    private static readonly RenderGraphBufferKey s_WorkKey = new("visibility-work");
    private static readonly RenderGraphBufferKey s_OutputKey = new("visibility-output-params");

    public RenderGraphTextureKey VisibilityTarget { get; } = new("visibility-id");
    public RenderGraphTextureKey HdrTarget { get; } = new("visibility-hdr");
    public RenderGraphTextureKey BaseColorRoughnessTarget { get; } = new("visibility-base-roughness");
    public RenderGraphTextureKey NormalMetallicTarget { get; } = new("visibility-normal-metallic");
    public RenderGraphTextureKey EmissiveOcclusionTarget { get; } = new("visibility-emissive-occlusion");

    public void BuildRenderGraph(ref RenderGraphBuildContext graph, in RenderFeatureContext<RenderFrameContext> context)
        => BuildRenderGraph(ref graph, in context, true);

    internal void BuildRenderGraph(ref RenderGraphBuildContext graph, in RenderFeatureContext<RenderFrameContext> context, bool includeOutput)
    {
        var view = context.View.PersistentResources.GetRequired<ViewState>();
        graph.UseTexture(VisibilityTarget, new RenderGraphTextureDescriptor("visibility-id", RenderGraphTextureFormat.R32Uint,
            view.Width, view.Height));
        graph.UseTexture(HdrTarget, new RenderGraphTextureDescriptor("visibility-hdr", RenderGraphTextureFormat.RGBA16Float,
            view.ResolvePipeline == _resolve.Surface ? 1u : view.Width, view.ResolvePipeline == _resolve.Surface ? 1u : view.Height));
        foreach (var key in new[] { BaseColorRoughnessTarget, NormalMetallicTarget, EmissiveOcclusionTarget }) {
            graph.UseTexture(key, new RenderGraphTextureDescriptor(key.ToString(), RenderGraphTextureFormat.RGBA16Float, view.Width, view.Height));
        }
        ImportBuffer(ref graph, s_CameraKey, view.Uniform, RenderGraphBufferUsage.Uniform);
        ImportBuffer(ref graph, s_OutputKey, view.OutputUniform, RenderGraphBufferUsage.Uniform);
        ImportBuffer(ref graph, s_IndirectKey, view.Indirect, RenderGraphBufferUsage.Indirect | RenderGraphBufferUsage.CopySource
            | (_gpuLod is null && _fixedGeometry is null ? 0 : RenderGraphBufferUsage.Storage));
        ImportBuffer(ref graph, s_WorkKey, view.WorkBuffer, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.CopySource);
        ImportBuffer(ref graph, s_MaterialParametersKey, _materialParameters, RenderGraphBufferUsage.Storage);
        for (var i = 0; i < _geometry.Length; i++) {
            ImportBuffer(ref graph, s_GeometryKeys[i], _geometry[i], RenderGraphBufferUsage.Storage);
        }
        foreach (var texture in _materialTextures) {
            var info = Wgpu.GetTextureInfo(texture.Texture.GetWgpu<WGPUTexture>());
            graph.UseImportedTexture(texture.Key, new RenderGraphTextureDescriptor(texture.Key.ToString(),
                texture.Srgb ? RenderGraphTextureFormat.RGBA8UnormSrgb : RenderGraphTextureFormat.RGBA8Unorm,
                info.Size.Width, info.Size.Height, depthOrArrayLayers: info.Size.DepthOrArrayLayers,
                mipLevelCount: info.MipLevelCount, usage: RenderGraphTextureUsage.TextureBinding));
            graph.BindImportedTexture(texture.Key, texture.Texture.GetWgpu<WGPUTexture>());
        }
        foreach (var material in _materialBatches) { ImportBuffer(ref graph, material.Key, material.Uniform, RenderGraphBufferUsage.Uniform); }
        if (_gpuLod is { } lod) { BuildLodGraph(ref graph, view, lod); }
        if (_fixedGeometry is not null) { view.BuildClusterGraph(ref graph); }
        if (view.Timing is { } timing) {
            ImportBuffer(ref graph, GpuTimingsTarget, timing.Results, RenderGraphBufferUsage.QueryResolve | RenderGraphBufferUsage.CopySource | RenderGraphBufferUsage.CopyDestination);
        }
        graph.UsePass(new("visibility-raster"), "visibility-raster", view.DeclareRaster, view.Raster);
        if (_gpuLod is not null) { BuildPostOcclusionGraph(ref graph, view); }
        if (_fixedGeometry is not null) { view.BuildClusterPostGraph(ref graph); }
        view.BuildMaterialTiles(ref graph);
        graph.UseComputePass(new("visibility-resolve"), "visibility-resolve", view.DeclareResolve, view.Resolve);
        if (includeOutput) { graph.UsePass(new("visibility-output"), "visibility-output", view.DeclareOutput, view.Output); }
        else if (view.Timing is not null) { graph.UseComputePass(new("visibility-timing-resolve"), "visibility-timing-resolve", view.DeclareSurfaceTiming, view.ResolveSurfaceTiming); }
    }

    private static void ImportBuffer(ref RenderGraphBuildContext graph, RenderGraphBufferKey key, Entity entity, RenderGraphBufferUsage usage)
    {
        var buffer = entity.GetWgpu<WGPUBuffer>();
        graph.UseImportedBuffer(key, new RenderGraphBufferDescriptor(key.ToString(), Wgpu.GetBufferSize(buffer), usage));
        graph.BindImportedBuffer(key, buffer);
    }

    private ViewState CreateView(List<Entity>? resources = null, bool enableTiming = true)
    {
        var acquired = resources ?? new List<Entity>();
        var device = _device.GetWgpu<WGPUDevice>();
        var queue = _queue.GetWgpu<WGPUQueue>();
        var limits = Wgpu.GetLimits(device);
        try {
            var uniform = Upload<CameraGpu>(_world, device, queue, [default], WGPUBufferUsage.Uniform, limits, acquired);
            var outputUniform = Upload<float4>(_world, device, queue,
                [new float4(1, _output.EncodeSrgb ? 1 : 0, 0, 0)], WGPUBufferUsage.Uniform, limits, acquired);
            WorkGpu[] workItems = _fixedGeometry is not null || _gpuLod is not null ? [] : new WorkGpu[checked((int)TriangleCapacity)];
            var workBuffer = Allocate(_world, device, System.Math.Max(1u, _fixedGeometry?.Count ?? TriangleCapacity) * 8ul,
                WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc, limits, acquired);
            var indirect = Upload<uint>(_world, device, queue, _gpuLod is not null ? new uint[20] :
                _fixedGeometry is not null ? [0, 1, 0, 0, 0, 0, 0, 0] : [0, 1, 0, 0],
                WGPUBufferUsage.Indirect | WGPUBufferUsage.CopySrc | (_gpuLod is null && _fixedGeometry is null ? 0 : WGPUBufferUsage.Storage), limits, acquired);
            WGPUBindGroupEntry[] entries = [BufferEntry(0, uniform), BufferEntry(1, _geometry[0]),
                BufferEntry(3, _geometry[1]), BufferEntry(4, _geometry[2]), BufferEntry(5, _geometry[3]), BufferEntry(6, workBuffer)];
            var group = Own(_world, BindGroup(_geometryLayout, entries), acquired);
            var lodView = _gpuLod is { } lod ? CreateLodView(lod, uniform, workBuffer, indirect, limits, acquired) : (LodViewGpu?)null;
            var timing = enableTiming && _gpuLod is { EnableTiming: true } ? CreateTiming(device, limits, acquired) : (TimingGpu?)null;
            var clusters = _fixedGeometry is { } fixedGeometry ? CreateClusterView(fixedGeometry, uniform, workBuffer, indirect, limits, acquired) : (ClusterViewGpu?)null;
            var materialGroup = Own(_world, BindGroup(_materialTiles.GeometryLayout,
                [BufferEntry(0, uniform), BufferEntry(5, _geometry[3]), BufferEntry(6, workBuffer)]), acquired);
            return new(this, uniform, outputUniform, group, workBuffer, indirect, workItems, lodView) {
                Timing = timing, Clusters = clusters, MaterialGeometryGroup = materialGroup
            };
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

    private Entity OwnTextureBindGroup(Entity layout, ReadOnlySpan<WGPUBindGroupEntry> entries,
        params ReadOnlySpan<WgpuHandle<WGPUTextureView>> views)
        => PbrTextureBindGroups.Create(_world, _device.GetWgpu<WGPUDevice>(), layout, entries, views);

    private sealed partial class ViewState(VisibilityPbrFeature owner, Entity uniform, Entity outputUniform, Entity group,
        Entity workBuffer, Entity indirect, WorkGpu[] workItems, LodViewGpu? lodView)
    {
        public VisibilityPbrFeature Owner { get; } = owner;
        public Entity Uniform { get; } = uniform;
        public Entity OutputUniform { get; } = outputUniform;
        public Entity Group { get; } = group;
        public Entity WorkBuffer { get; } = workBuffer;
        public Entity Indirect { get; } = indirect;
        public WorkGpu[] WorkItems { get; } = workItems;
        public LodViewGpu? Lod { get; } = lodView;
        public uint WorkCount { get; set; }
        public bool WorkInitialized { get; set; }
        public MeshPatchSelection? Selection { get; set; }
        public RenderFrameContext Frame { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        private Entity[] _resolveGroups = [];
        public Entity ResolvePipeline { get; set; }
        private Entity _outputGroup;
        private WgpuHandle<WGPUTextureView> _idView;
        private WgpuHandle<WGPUTextureView> _hdrView;
        private WgpuHandle<WGPUTextureView>[] _surfaceViews = [];
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
            if (Owner._fixedGeometry is not null) { declaration.Read(s_ClusterIndicesKey, RenderGraphBufferUsage.Index); }
            declaration.Read(s_IndirectKey, RenderGraphBufferUsage.Indirect)
                .Write(Owner.VisibilityTarget, RenderGraphTextureUsage.RenderAttachment)
                .Write(Frame.DepthTarget, RenderGraphTextureUsage.RenderAttachment);
        }

        public void Raster(WgpuReactiveRenderGraphPassContext context) => Raster(context, false);

        private void Raster(WgpuReactiveRenderGraphPassContext context, bool post)
        {
            if (Timing is not null) { TimedRaster(context, post); return; }
            var pass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphColorAttachment(Owner.VisibilityTarget, post ? WGPULoadOp.Load : WGPULoadOp.Clear),
                new WgpuReactiveRenderGraphDepthStencilAttachment(Frame.DepthTarget, post ? WGPULoadOp.Load : WGPULoadOp.Clear));
            Wgpu.SetRenderPipeline(pass, Owner._raster.GetWgpu<WGPURenderPipeline>());
            Wgpu.SetBindGroup(pass, 0, Group.GetWgpu<WGPUBindGroup>());
            if (Owner._fixedGeometry is { } geometry) {
                Wgpu.SetIndexBuffer(pass, geometry.Indices.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
                Wgpu.DrawIndexedIndirect(pass, Indirect.GetWgpu<WGPUBuffer>());
            } else { Wgpu.DrawIndirect(pass, Indirect.GetWgpu<WGPUBuffer>(), post ? 32ul : 0ul); }
        }

        public void DeclareResolve(RenderGraphPassDeclarationBuilder declaration)
        {
            ReadGeometry(declaration);
            declaration.Read(s_MaterialParametersKey, RenderGraphBufferUsage.Storage);
            declaration.Read(s_MaterialTilesKey, RenderGraphBufferUsage.Storage)
                .Read(s_MaterialDispatchKey, RenderGraphBufferUsage.Indirect);
            declaration.Read(Owner.VisibilityTarget, RenderGraphTextureUsage.TextureBinding)
                .Write(Owner.HdrTarget, RenderGraphTextureUsage.StorageBinding)
                .Write(Owner.BaseColorRoughnessTarget, RenderGraphTextureUsage.StorageBinding)
                .Write(Owner.NormalMetallicTarget, RenderGraphTextureUsage.StorageBinding)
                .Write(Owner.EmissiveOcclusionTarget, RenderGraphTextureUsage.StorageBinding);
            foreach (var texture in Owner._materialTextures) { declaration.Read(texture.Key, RenderGraphTextureUsage.TextureBinding); }
            foreach (var material in Owner._materialBatches) { declaration.Read(material.Key, RenderGraphBufferUsage.Uniform); }
        }

        public unsafe void Resolve(WgpuReactiveRenderGraphPassContext context)
        {
            var id = context.GetTextureView(Owner.VisibilityTarget);
            var hdr = context.GetTextureView(Owner.HdrTarget);
            WgpuHandle<WGPUTextureView>[] surfaces = [context.GetTextureView(Owner.BaseColorRoughnessTarget),
                context.GetTextureView(Owner.NormalMetallicTarget), context.GetTextureView(Owner.EmissiveOcclusionTarget)];
            if (_resolveGroups.Length == 0 || !_resolveGroups[0].IsValid || id != _idView || hdr != _hdrView || !_surfaceViews.AsSpan().SequenceEqual(surfaces)) {
                var next = new Entity[Owner._materialBatches.Length];
                try {
                    for (var i = 0; i < next.Length; i++) {
                        var material = Owner._materialBatches[i];
                        var entries = new WGPUBindGroupEntry[18];
                        entries[0] = TextureEntry(0, id); entries[1] = TextureEntry(1, hdr);
                        for (uint map = 0; map < 5; map++) {
                            entries[2 + map * 2] = TextureEntry(2 + map * 2, material.Maps[map].View.GetWgpu<WGPUTextureView>());
                            var sampler = WGPUBindGroupEntry.Default;
                            sampler.Binding = 3 + map * 2;
                            sampler.Sampler = (WGPUSampler*)material.Maps[map].Sampler.GetWgpu<WGPUSampler>().DangerousGetHandle();
                            entries[3 + map * 2] = sampler;
                        }
                        entries[12] = BufferEntry(12, material.Uniform);
                        for (uint surface = 0; surface < 3; surface++) { entries[13 + surface] = TextureEntry(13 + surface, surfaces[surface]); }
                        entries[16] = BufferEntry(16, _materialTileBuffer);
                        entries[17] = BufferEntry(17, Owner._materialParameters);
                        next[i] = Owner.OwnTextureBindGroup(Owner._resolveLayout, entries, id, hdr, surfaces[0], surfaces[1], surfaces[2]);
                    }
                }
                catch { foreach (var item in next) { if (item.IsValid) { item.Destroy(); } } throw; }
                foreach (var item in _resolveGroups) { if (item.IsValid) { item.Destroy(); } }
                _resolveGroups = next;
                _idView = id;
                _hdrView = hdr;
                _surfaceViews = surfaces;
            }
            var pass = BeginCompute(context);
            try {
                Wgpu.SetComputePipeline(pass, ResolvePipeline.GetWgpu<WGPUComputePipeline>());
                Wgpu.SetBindGroup(pass, 0, Group.GetWgpu<WGPUBindGroup>());
                for (var i = 0; i < _resolveGroups.Length; i++) {
                    Wgpu.SetBindGroup(pass, 1, _resolveGroups[i].GetWgpu<WGPUBindGroup>());
                    Wgpu.DispatchWorkgroupsIndirect(pass, _materialDispatchBuffer.GetWgpu<WGPUBuffer>(), (ulong)i * 12);
                }
            }
            finally {
                Wgpu.EndComputePass(pass);
                Wgpu.Release(ref pass);
            }
        }

        public void DeclareOutput(RenderGraphPassDeclarationBuilder declaration)
        {
            declaration.Read(s_OutputKey, RenderGraphBufferUsage.Uniform)
                .Read(Owner.HdrTarget, RenderGraphTextureUsage.TextureBinding)
                .Write(Frame.ColorTarget, RenderGraphTextureUsage.RenderAttachment);
            if (Timing is not null) { declaration.Write(Owner.GpuTimingsTarget, RenderGraphBufferUsage.QueryResolve); }
        }

        public void Output(WgpuReactiveRenderGraphPassContext context)
        {
            var source = context.GetTextureView(Owner.HdrTarget);
            if (!_outputGroup.IsValid || source != _outputSource) {
                var next = Owner.OwnTextureBindGroup(Owner._output.Layout,
                    [BufferEntry(0, OutputUniform), TextureEntry(1, source)], source);
                if (_outputGroup.IsValid) { _outputGroup.Destroy(); }
                _outputGroup = next;
                _outputSource = source;
            }
            if (Timing is not null) { TimedOutput(context); return; }
            var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(
                Frame.ColorTarget, Frame.ColorLoadOp, Cacheable: Frame.ColorCacheable));
            Wgpu.SetRenderPipeline(pass, Owner._output.Pipeline.GetWgpu<WGPURenderPipeline>());
            Wgpu.SetBindGroup(pass, 0, _outputGroup.GetWgpu<WGPUBindGroup>());
            Wgpu.Draw(pass, 3);
        }
    }
}
