using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private Entity _shadowRaster, _shadowBoundsRaster, _shadowBoundsBuffer;
    private static readonly RenderGraphBufferKey s_ShadowBoundsKey = new("visibility-shadow-bounds");
    private static readonly RenderGraphBufferKey s_ShadowClusterIndicesKey = new("visibility-shadow-cluster-indices");

    internal void PrepareShadows(in RenderFeatureContext<RenderFrameContext> context, PbrViewState lighting,
        ShadowAtlasConfig config)
    {
        var shadows = lighting.Shadows;
        var main = context.View.PersistentResources.GetRequired<ViewState>();
        var layers = new SortedSet<int>();
        if (shadows.HasDirectionalShadow) {
            for (var i = 0; i < shadows.CascadeCount; i++) { layers.Add(i); }
        }
        foreach (var layer in shadows.SpotShadowLayerByEntity.Values) { layers.Add(layer); }
        if (layers.Count > 0 && !_shadowRaster.IsValid) {
            var acquired = new List<Entity>();
            try {
                var indices = CreateShadowIndices(acquired);
                var lod = CreateShadowLod(acquired);
                var raster = CreateRaster(_world, _device.GetWgpu<WGPUDevice>(), _geometryLayout, acquired, shadow: true,
                    indexed: _fixedGeometry is not null || indices is not null, doubleSided: _doubleSided.Any(value => value), worldSpace: _worldSpaceGeometry);
                _shadowIndices = indices; _shadowRaster = raster; _shadowLod = lod;
            }
            catch { for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); } throw; }
        }
        if (config.ConservativeCasterBounds && !_shadowBoundsRaster.IsValid) {
            var acquired = new List<Entity>();
            try {
                var data = new float4[System.Math.Max(4, _assetBounds.Length * 2)];
                for (var i = 0; i < _assetBounds.Length; i++) if (_assetBounds[i] is { } bounds) {
                    data[i * 2] = new(bounds.Min, 0); data[i * 2 + 1] = new(bounds.Max, 0);
                }
                var device = _device.GetWgpu<WGPUDevice>();
                _shadowBoundsBuffer = Upload<float4>(_world, device, _queue.GetWgpu<WGPUQueue>(), data, WGPUBufferUsage.Storage, Wgpu.GetLimits(device), acquired);
                _shadowBoundsRaster = CreateRaster(_world, device, _geometryLayout, acquired, shadow: true, doubleSided: true, bounds: true);
            } catch { foreach (var resource in acquired) if (resource.IsValid) resource.Destroy(); throw; }
        }
        PrepareShadowLod();
        var additions = new List<ShadowView>();
        try {
            foreach (var layer in layers) {
                if (main.Shadows.ContainsKey(layer)) { continue; }
                var acquired = new List<Entity>();
                try {
                    var view = CreateView(acquired, shadow: true, lodOverride: _shadowLod, triangleCapacity: ShadowTriangleCapacity);
                    var group = _shadowIndices is { } indices ? CreateShadowIndexGroup(indices, view, acquired) : default;
                    additions.Add(new(layer, view, acquired.ToArray(), group));
                }
                catch { for (var i = acquired.Count - 1; i >= 0; i--) { if (acquired[i].IsValid) { acquired[i].Destroy(); } } throw; }
            }
        }
        catch { foreach (var item in additions) { ReleaseShadowView(item); } throw; }
        foreach (var item in additions) { main.Shadows.Add(item.Layer, item); }
        foreach (var layer in main.Shadows.Keys.ToArray()) {
            if (layers.Contains(layer)) { continue; }
            var old = main.Shadows[layer]; main.Shadows.Remove(layer); ReleaseShadowView(old);
        }
        foreach (var layer in layers) {
            var shadow = main.Shadows[layer];
            var view = shadow.View;
            var projection = shadows.LayerViewProj(layer);
            shadow.Bounds = config.ConservativeCasterBounds;
            if (shadow.Bounds && !shadow.BoundsGroup.IsValid) {
                shadow.BoundsGroup = _world.OwnWgpu(BindGroup(_geometryLayout, [BufferEntry(0, view.Uniform),
                    BufferEntry(1, _shadowBoundsBuffer), BufferEntry(3, _geometry[1]), BufferEntry(4, _geometry[2]),
                    BufferEntry(5, _geometry[3]), BufferEntry(6, view.WorkBuffer)]));
            }
            // Residency changes outside this light's clip volume do not alter its depth.
            shadow.Cacheable = (shadow.Bounds || _fixedGeometry is not null) && DebugMode == VisibilityDebugMode.Shaded;
            if (!shadow.Cacheable) { shadow.Rendered = null; }
            shadow.Pending = new(lighting.ShadowAtlas.Texture, projection, config.TileResolution, _instanceVersion, shadow.Bounds);
            shadow.Reuse = shadow.Cacheable && shadow.Rendered is { } rendered
                && rendered.Atlas == shadow.Pending.Atlas && rendered.Projection.Equals(projection)
                && rendered.Bounds == shadow.Bounds && rendered.Resolution == config.TileResolution
                && !_sceneChanges.IntersectsSince(rendered.GeometryVersion, projection);
            _frameStatistics = _frameStatistics with {
                ShadowsReused = _frameStatistics.ShadowsReused + (shadow.Reuse ? 1 : 0),
                ShadowsRendered = _frameStatistics.ShadowsRendered + (shadow.Reuse ? 0 : 1) };
            if (shadow.Reuse) { shadow.Rendered = shadow.Pending; continue; }
            view.Width = config.TileResolution; view.Height = config.TileResolution;
            Wgpu.WriteBuffer<CameraGpu>(_queue.GetWgpu<WGPUQueue>(), view.Uniform.GetWgpu<WGPUBuffer>(), 0,
                [new(projection, default, new(config.TileResolution, config.TileResolution, TriangleCount, 0), default, default,
                    RasterConfig with { w = _shadowIndices is not null ? 4u : _fixedGeometry is null ? 0u : 2u }, RasterOrigin(projection))]);
            if (!shadow.Bounds && _gpuLod is null) { UpdateWork(view, in projection); }
        }
    }

    private static void ReleaseShadowView(ShadowView view)
    {
        if (view.BoundsGroup.IsValid) view.BoundsGroup.Destroy();
        for (var i = view.Resources.Length - 1; i >= 0; i--) { if (view.Resources[i].IsValid) { view.Resources[i].Destroy(); } }
    }

    internal void BuildShadowGraph(ref RenderGraphBuildContext graph, in RenderFeatureContext<RenderFrameContext> context,
        RenderGraphTextureKey atlas)
    {
        var main = context.View.PersistentResources.GetRequired<ViewState>();
        main.ShadowAtlas = atlas;
        if (_shadowBoundsBuffer.IsValid) ImportBuffer(ref graph, s_ShadowBoundsKey, _shadowBoundsBuffer, RenderGraphBufferUsage.Storage);
        if (_fixedGeometry is { } fixedGeometry) ImportBuffer(ref graph, s_ShadowClusterIndicesKey, fixedGeometry.Indices,
            RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Index);
        if (_shadowLod is { } lod) { ImportBuffer(ref graph, new("visibility-shadow-lod-params"), lod.Parameters, RenderGraphBufferUsage.Uniform); }
        if (_shadowIndices is { } indices) {
            ImportBuffer(ref graph, new("visibility-shadow-indices"), indices.Indices, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Index);
            ImportBuffer(ref graph, new("visibility-shadow-arguments"), indices.Arguments, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Indirect);
        }
        graph.UseComputePass(new("visibility-shadows"), "visibility-shadows", main.DeclareShadows, main.RenderShadows);
    }

    private readonly record struct ShadowContent(Entity Atlas, float4x4 Projection, uint Resolution, ulong GeometryVersion, bool Bounds);

    private sealed record ShadowView(int Layer, ViewState View, Entity[] Resources, Entity IndexGroup)
    {
        public ShadowContent? Rendered { get; set; }
        public ShadowContent Pending { get; set; }
        public bool Bounds { get; set; }
        public Entity BoundsGroup { get; set; }
        public bool Cacheable { get; set; }
        public bool Reuse { get; set; }
    }

    private sealed partial class ViewState
    {
        public Dictionary<int, ShadowView> Shadows { get; } = [];
        public RenderGraphTextureKey ShadowAtlas { get; set; }

        public void DeclareShadows(RenderGraphPassDeclarationBuilder declaration)
        {
            if (Owner._shadowBoundsBuffer.IsValid) declaration.Read(s_ShadowBoundsKey, RenderGraphBufferUsage.Storage);
            declaration.ReadWrite(ShadowAtlas, RenderGraphTextureUsage.RenderAttachment);
            foreach (var key in s_GeometryKeys) { declaration.Read(key, RenderGraphBufferUsage.Storage); }
            if (Owner._shadowLod is not null) { declaration.Read(new RenderGraphBufferKey("visibility-shadow-lod-params"), RenderGraphBufferUsage.Uniform); }
            if (Owner._shadowIndices is not null) {
                declaration.ReadWrite(new RenderGraphBufferKey("visibility-shadow-indices"), RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Index)
                    .ReadWrite(new RenderGraphBufferKey("visibility-shadow-arguments"), RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Indirect);
            }
            if (Owner._fixedGeometry is not null) {
                declaration.Read(s_FixedClustersKey, RenderGraphBufferUsage.Storage)
                    .ReadWrite(s_ShadowClusterIndicesKey, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Index);
            }
            if (Owner._gpuLod is not null) {
                declaration.Read(s_PatchKey, RenderGraphBufferUsage.Storage).Read(s_LodParamsKey, RenderGraphBufferUsage.Uniform);
            }
        }

        public unsafe void RenderShadows(WgpuReactiveRenderGraphPassContext context)
        {
            if (Owner.DebugMode != VisibilityDebugMode.Shaded) { return; }
            foreach (var shadow in Shadows.Values.OrderBy(static value => value.Layer)) {
                if (shadow.Reuse) { continue; }
                var view = shadow.View;
                if (!shadow.Bounds && Owner._fixedGeometry is not null) { view.CullClusters(context); }
                if (!shadow.Bounds && Owner._gpuLod is not null) {
                    view.ProjectLod(context); view.SelectLod(context); view.CompactMain(context); view.EmitLod(context);
                }
                if (!shadow.Bounds && Owner._shadowIndices is not null) { view.EmitShadowIndices(context, shadow.IndexGroup); }
                var attachment = WGPURenderPassDepthStencilAttachment.Default;
                attachment.View = (WGPUTextureView*)context.GetTextureView(ShadowAtlas,
                    new RenderGraphTextureSubresourceRange(0, 1, (uint)shadow.Layer, 1), cacheable: false).DangerousGetHandle();
                attachment.DepthLoadOp = WGPULoadOp.Clear;
                attachment.DepthStoreOp = WGPUStoreOp.Store;
                attachment.DepthClearValue = 1;
                var descriptor = WGPURenderPassDescriptor.Default;
                descriptor.DepthStencilAttachment = &attachment;
                var pass = Wgpu.BeginRenderPass(context.CommandEncoder, in descriptor);
                try {
                    Wgpu.SetRenderPipeline(pass, (shadow.Bounds ? Owner._shadowBoundsRaster : Owner._shadowRaster).GetWgpu<WGPURenderPipeline>());
                    Wgpu.SetBindGroup(pass, 0, (shadow.Bounds ? shadow.BoundsGroup : view.Group).GetWgpu<WGPUBindGroup>());
                    if (shadow.Bounds) Wgpu.Draw(pass, 36, Owner.InstanceCapacity);
                    else if (Owner._fixedGeometry is { } geometry) {
                        Wgpu.SetIndexBuffer(pass, geometry.Indices.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
                        Wgpu.DrawIndexedIndirect(pass, view.Indirect.GetWgpu<WGPUBuffer>());
                    } else if (Owner._shadowIndices is { } indices) {
                        Wgpu.SetIndexBuffer(pass, indices.Indices.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
                        Wgpu.DrawIndexedIndirect(pass, indices.Arguments.GetWgpu<WGPUBuffer>(), 12);
                    } else { Wgpu.DrawIndirect(pass, view.Indirect.GetWgpu<WGPUBuffer>()); }
                }
                finally { Wgpu.EndRenderPass(pass); Wgpu.Release(ref pass); }
                shadow.Rendered = shadow.Cacheable ? shadow.Pending : null;
            }
        }
    }
}
