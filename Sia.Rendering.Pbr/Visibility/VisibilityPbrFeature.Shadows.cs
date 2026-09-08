using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private Entity _shadowRaster;

    internal void PrepareShadows(in RenderFeatureContext<RenderFrameContext> context, ShadowGpuStore shadows, ShadowAtlasConfig config)
    {
        var main = context.View.PersistentResources.GetRequired<ViewState>();
        var layers = new SortedSet<int>();
        if (shadows.HasDirectionalShadow) {
            for (var i = 0; i < shadows.CascadeCount; i++) { layers.Add(i); }
        }
        foreach (var layer in shadows.SpotShadowLayerByEntity.Values) { layers.Add(layer); }
        if (layers.Count > 0 && !_shadowRaster.IsValid) {
            var acquired = new List<Entity>();
            try { _shadowRaster = CreateRaster(_world, _device.GetWgpu<WGPUDevice>(), _geometryLayout, acquired, shadow: true, indexed: _fixedGeometry is not null); }
            catch { for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); } throw; }
        }
        var additions = new List<ShadowView>();
        try {
            foreach (var layer in layers) {
                if (main.Shadows.ContainsKey(layer)) { continue; }
                var acquired = new List<Entity>();
                var view = CreateView(acquired, enableTiming: false);
                additions.Add(new(layer, view, acquired.ToArray()));
            }
        }
        catch { foreach (var item in additions) { ReleaseShadowView(item); } throw; }
        foreach (var item in additions) { main.Shadows.Add(item.Layer, item); }
        foreach (var layer in main.Shadows.Keys.ToArray()) {
            if (layers.Contains(layer)) { continue; }
            var old = main.Shadows[layer]; main.Shadows.Remove(layer); ReleaseShadowView(old);
        }
        foreach (var layer in layers) {
            var view = main.Shadows[layer].View;
            view.Width = config.TileResolution; view.Height = config.TileResolution;
            var projection = shadows.LayerViewProj(layer);
            Wgpu.WriteBuffer<CameraGpu>(_queue.GetWgpu<WGPUQueue>(), view.Uniform.GetWgpu<WGPUBuffer>(), 0,
                [new(projection, default, new(config.TileResolution, config.TileResolution, TriangleCount, 0), default, default,
                    RasterConfig with { w = _fixedGeometry is null ? 0u : 2u }, RasterOrigin(projection))]);
            if (_gpuLod is null) { UpdateWork(view, in projection); }
        }
    }

    private static void ReleaseShadowView(ShadowView view)
    {
        for (var i = view.Resources.Length - 1; i >= 0; i--) { if (view.Resources[i].IsValid) { view.Resources[i].Destroy(); } }
    }

    internal void BuildShadowGraph(ref RenderGraphBuildContext graph, in RenderFeatureContext<RenderFrameContext> context,
        RenderGraphTextureKey atlas)
    {
        var main = context.View.PersistentResources.GetRequired<ViewState>();
        main.ShadowAtlas = atlas;
        graph.UseComputePass(new("visibility-shadows"), "visibility-shadows", main.DeclareShadows, main.RenderShadows);
    }

    private sealed record ShadowView(int Layer, ViewState View, Entity[] Resources);

    private sealed partial class ViewState
    {
        public Dictionary<int, ShadowView> Shadows { get; } = [];
        public RenderGraphTextureKey ShadowAtlas { get; set; }

        public void DeclareShadows(RenderGraphPassDeclarationBuilder declaration)
        {
            declaration.ReadWrite(ShadowAtlas, RenderGraphTextureUsage.RenderAttachment);
            foreach (var key in s_GeometryKeys) { declaration.Read(key, RenderGraphBufferUsage.Storage); }
            if (Owner._fixedGeometry is not null) {
                declaration.Read(s_FixedClustersKey, RenderGraphBufferUsage.Storage)
                    .ReadWrite(s_ClusterIndicesKey, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Index);
            }
            if (Owner._gpuLod is not null) {
                declaration.Read(s_PatchKey, RenderGraphBufferUsage.Storage).Read(s_LodParamsKey, RenderGraphBufferUsage.Uniform);
            }
        }

        public unsafe void RenderShadows(WgpuReactiveRenderGraphPassContext context)
        {
            foreach (var shadow in Shadows.Values.OrderBy(static value => value.Layer)) {
                var view = shadow.View;
                if (Owner._fixedGeometry is not null) { view.CullClusters(context); }
                if (Owner._gpuLod is not null) {
                    view.ProjectLod(context); view.SelectLod(context); view.CompactMain(context); view.EmitLod(context);
                }
                var attachment = WGPURenderPassDepthStencilAttachment.Default;
                attachment.View = (WGPUTextureView*)context.GetTextureView(ShadowAtlas,
                    new RenderGraphTextureSubresourceRange(0, 1, (uint)shadow.Layer, 1), cacheable: false).DangerousGetHandle();
                attachment.DepthLoadOp = WGPULoadOp.Load;
                attachment.DepthStoreOp = WGPUStoreOp.Store;
                attachment.DepthClearValue = 1;
                var descriptor = WGPURenderPassDescriptor.Default;
                descriptor.DepthStencilAttachment = &attachment;
                var pass = Wgpu.BeginRenderPass(context.CommandEncoder, in descriptor);
                try {
                    Wgpu.SetRenderPipeline(pass, Owner._shadowRaster.GetWgpu<WGPURenderPipeline>());
                    Wgpu.SetBindGroup(pass, 0, view.Group.GetWgpu<WGPUBindGroup>());
                    if (Owner._fixedGeometry is { } geometry) {
                        Wgpu.SetIndexBuffer(pass, geometry.Indices.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
                        Wgpu.DrawIndexedIndirect(pass, view.Indirect.GetWgpu<WGPUBuffer>());
                    } else { Wgpu.DrawIndirect(pass, view.Indirect.GetWgpu<WGPUBuffer>()); }
                }
                finally { Wgpu.EndRenderPass(pass); Wgpu.Release(ref pass); }
            }
        }
    }
}
