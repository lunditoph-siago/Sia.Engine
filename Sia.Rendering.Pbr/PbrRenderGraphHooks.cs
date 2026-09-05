using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia;
using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public static class PbrRenderGraphHooks
{
    private static readonly RenderGraphBufferKey _clusterConfigKey = new("pbr-cluster-config");
    private static readonly RenderGraphBufferKey _clusteredLightsKey = new("pbr-clustered-lights");
    private static readonly RenderGraphBufferKey _lightGridKey = new("pbr-light-grid");
    private static readonly RenderGraphBufferKey _lightIndexListKey = new("pbr-light-index-list");
    private static readonly RenderGraphTextureKey _shadowAtlasKey = new("pbr-shadow-atlas");
    private static readonly RenderGraphTextureKey _iblPrefilteredKey = new("pbr-ibl-prefiltered");
    private static readonly RenderGraphTextureKey _iblBrdfLutKey = new("pbr-ibl-brdf-lut");

    public static void UseShadowPasses(
        ref RenderGraphBuildContext graph,
        PbrRenderer renderer,
        PbrViewState viewState,
        ShadowAtlasConfig shadowConfig,
        IReadOnlyList<PbrDrawItem> items)
    {
        var atlasDescriptor = new RenderGraphTextureDescriptor(
            "shadow-atlas", RenderGraphTextureFormat.Depth32Float,
            shadowConfig.TileResolution, shadowConfig.TileResolution,
            depthOrArrayLayers: (uint)shadowConfig.LayerCount,
            usage: RenderGraphTextureUsage.RenderAttachment | RenderGraphTextureUsage.TextureBinding);
        graph.UseImportedTexture(_shadowAtlasKey, atlasDescriptor);
        graph.BindImportedTexture(
            _shadowAtlasKey, viewState.ShadowAtlas.Texture.GetWgpu<WGPUTexture>());

        for (var layer = 0; layer < shadowConfig.LayerCount; layer++) {
            var pass = new RenderGraphPassKey($"pbr-shadow-layer-{layer}");
            var state = graph.UseState(() => new ShadowLayerState(layer));
            state.Update(renderer, viewState, items);
            graph.UsePass(pass, state.Name, state.Declare, state.Render);
        }
    }

    public static void UseIblPrecomputePasses(
        ref RenderGraphBuildContext graph,
        PbrRenderer renderer,
        PbrViewState viewState)
    {
        var prefilteredDescriptor = new RenderGraphTextureDescriptor(
            "ibl-prefiltered", RenderGraphTextureFormat.RGBA16Float,
            IblEnvironmentGpuStore.PrefilteredResolution, IblEnvironmentGpuStore.PrefilteredResolution,
            depthOrArrayLayers: 6,
            mipLevelCount: IblEnvironmentGpuStore.PrefilteredMipCount,
            usage: RenderGraphTextureUsage.RenderAttachment | RenderGraphTextureUsage.TextureBinding);
        graph.UseImportedTexture(_iblPrefilteredKey, prefilteredDescriptor);
        graph.BindImportedTexture(
            _iblPrefilteredKey, viewState.Ibl.PrefilteredTexture.GetWgpu<WGPUTexture>());

        var brdfLutDescriptor = new RenderGraphTextureDescriptor(
            "ibl-brdf-lut", RenderGraphTextureFormat.RG16Float,
            IblEnvironmentGpuStore.BrdfLutResolution, IblEnvironmentGpuStore.BrdfLutResolution,
            usage: RenderGraphTextureUsage.RenderAttachment | RenderGraphTextureUsage.TextureBinding);
        graph.UseImportedTexture(_iblBrdfLutKey, brdfLutDescriptor);
        graph.BindImportedTexture(
            _iblBrdfLutKey, viewState.Ibl.BrdfLutTexture.GetWgpu<WGPUTexture>());

        for (var face = 0; face < 6; face++) {
            for (var mip = 0; mip < IblEnvironmentGpuStore.PrefilteredMipCount; mip++) {
                var pass = new RenderGraphPassKey($"pbr-ibl-prefilter-{face}-{mip}");
                var state = graph.UseState(() => new IblPrefilterState(face, mip));
                state.Update(renderer, viewState);
                graph.UsePass(pass, state.Name, state.Declare, state.Render);
            }
        }

        var lutPass = new RenderGraphPassKey("pbr-ibl-brdf-lut");
        var lutState = graph.UseState(() => new IblBrdfLutState());
        lutState.Update(renderer);
        graph.UsePass(lutPass, "pbr-ibl-brdf-lut", lutState.Declare, lutState.Render);
    }

    public static void UseClusterLightCullingPass(
        ref RenderGraphBuildContext graph,
        PbrRenderer renderer,
        PbrViewState viewState,
        ClusterGridConfig clusterConfig,
        RenderGraphPassKey pass)
    {
        graph.UseImportedBuffer(
            _clusterConfigKey,
            new RenderGraphBufferDescriptor("cluster-config", ClusterConfigGpu.Stride, RenderGraphBufferUsage.Uniform));
        graph.BindImportedBuffer(
            _clusterConfigKey, viewState.ClusterBuffers.ConfigBuffer.GetWgpu<WGPUBuffer>());

        graph.UseImportedBuffer(
            _clusteredLightsKey,
            new RenderGraphBufferDescriptor(
                "clustered-lights", viewState.Lights.ClusteredCapacity, RenderGraphBufferUsage.Storage));
        graph.BindImportedBuffer(
            _clusteredLightsKey, viewState.Lights.ClusteredBuffer.GetWgpu<WGPUBuffer>());

        graph.UseImportedBuffer(
            _lightGridKey,
            new RenderGraphBufferDescriptor(
                "light-grid", viewState.ClusterBuffers.LightGridSize, RenderGraphBufferUsage.Storage));
        graph.BindImportedBuffer(
            _lightGridKey, viewState.ClusterBuffers.LightGridBuffer.GetWgpu<WGPUBuffer>());

        graph.UseImportedBuffer(
            _lightIndexListKey,
            new RenderGraphBufferDescriptor(
                "light-index-list", viewState.ClusterBuffers.LightIndexListCapacity, RenderGraphBufferUsage.Storage));
        graph.BindImportedBuffer(
            _lightIndexListKey, viewState.ClusterBuffers.LightIndexListBuffer.GetWgpu<WGPUBuffer>());

        var state = graph.UseState(() => new ClusterCullingState());
        state.Update(renderer, viewState, clusterConfig);
        graph.UseComputePass(pass, "pbr-cluster-culling", state.Declare, state.Render);
    }

    public static void UseDepthPrepass(
        ref RenderGraphBuildContext graph,
        PbrRenderer renderer,
        PbrViewState viewState,
        RenderPhase<PbrDrawItem> phase,
        RenderGraphPassKey pass,
        RenderGraphTextureKey depth)
    {
        var state = graph.UseState(() => new DepthPrepassState(depth));
        state.Update(renderer, viewState, phase, depth);
        graph.UsePass(pass, "pbr-depth-prepass", state.Declare, state.Render);
    }

    public static void UseForwardPbrPass(
        ref RenderGraphBuildContext graph,
        PbrRenderer renderer,
        PbrViewState viewState,
        RenderPhase<PbrDrawItem> phase,
        RenderGraphPassKey pass,
        RenderGraphTextureKey color,
        RenderGraphTextureKey depth,
        WGPULoadOp colorLoadOp = WGPULoadOp.Clear,
        bool colorCacheable = true)
    {
        var state = graph.UseState(() => new ForwardPbrState(color, depth));
        state.Update(renderer, viewState, phase, color, depth, colorLoadOp, colorCacheable);
        graph.UsePass(pass, "pbr-forward-opaque", state.Declare, state.Render);
    }

    private sealed class ClusterCullingState
    {
        private PbrRenderer? _renderer;
        private PbrViewState? _viewState;
        private ClusterGridConfig? _clusterConfig;

        public void Update(
            PbrRenderer renderer,
            PbrViewState viewState,
            ClusterGridConfig clusterConfig)
        {
            _renderer = renderer;
            _viewState = viewState;
            _clusterConfig = clusterConfig;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration
                .Read(_clusterConfigKey, RenderGraphBufferUsage.Uniform)
                .Read(_clusteredLightsKey, RenderGraphBufferUsage.Storage)
                .Write(_lightGridKey, RenderGraphBufferUsage.Storage)
                .Write(_lightIndexListKey, RenderGraphBufferUsage.Storage);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var computePass = context.GetOrBeginComputePass();
            _renderer!.EncodeClusterLightCulling(
                _viewState!, _clusterConfig!, computePass);
            Wgpu.EndComputePass(computePass);
            Wgpu.Release(ref computePass);
        }
    }

    private sealed class DepthPrepassState(RenderGraphTextureKey depth)
    {
        private PbrRenderer? _renderer;
        private PbrViewState? _viewState;
        private RenderPhase<PbrDrawItem>? _phase;

        public RenderGraphTextureKey Depth { get; private set; } = depth;

        public void Update(
            PbrRenderer renderer,
            PbrViewState viewState,
            RenderPhase<PbrDrawItem> phase,
            RenderGraphTextureKey depth)
        {
            _renderer = renderer;
            _viewState = viewState;
            _phase = phase;
            Depth = depth;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration.Write(Depth, RenderGraphTextureUsage.RenderAttachment);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphDepthStencilAttachment(Depth, WGPULoadOp.Clear));
            _renderer!.EncodeDepthPrepass(
                _viewState!, _phase!.Items, renderPass);
        }
    }

    private sealed class ForwardPbrState(RenderGraphTextureKey color, RenderGraphTextureKey depth)
    {
        private PbrRenderer? _renderer;
        private PbrViewState? _viewState;
        private RenderPhase<PbrDrawItem>? _phase;
        private WGPULoadOp _colorLoadOp;
        private bool _colorCacheable = true;

        public RenderGraphTextureKey Color { get; private set; } = color;
        public RenderGraphTextureKey Depth { get; private set; } = depth;

        public void Update(
            PbrRenderer renderer,
            PbrViewState viewState,
            RenderPhase<PbrDrawItem> phase,
            RenderGraphTextureKey color,
            RenderGraphTextureKey depth,
            WGPULoadOp colorLoadOp,
            bool colorCacheable)
        {
            _renderer = renderer;
            _viewState = viewState;
            _phase = phase;
            Color = color;
            Depth = depth;
            _colorLoadOp = colorLoadOp;
            _colorCacheable = colorCacheable;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration
                .Write(Color, RenderGraphTextureUsage.RenderAttachment)
                .Write(Depth, RenderGraphTextureUsage.RenderAttachment)
                .Read(_clusterConfigKey, RenderGraphBufferUsage.Uniform)
                .Read(_clusteredLightsKey, RenderGraphBufferUsage.Storage)
                .Read(_lightGridKey, RenderGraphBufferUsage.Storage)
                .Read(_lightIndexListKey, RenderGraphBufferUsage.Storage)
                .Read(_shadowAtlasKey, RenderGraphTextureUsage.TextureBinding)
                .Read(_iblPrefilteredKey, RenderGraphTextureUsage.TextureBinding)
                .Read(_iblBrdfLutKey, RenderGraphTextureUsage.TextureBinding);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphColorAttachment(Color, _colorLoadOp, Cacheable: _colorCacheable),
                new WgpuReactiveRenderGraphDepthStencilAttachment(Depth, WGPULoadOp.Load));
            _renderer!.EncodeForwardPbr(
                _viewState!, _phase!.Items, renderPass);
        }
    }

    private sealed class ShadowLayerState(int layer)
    {
        private PbrRenderer? _renderer;
        private PbrViewState? _viewState;
        private IReadOnlyList<PbrDrawItem>? _items;

        public string Name { get; } = $"pbr-shadow-layer-{layer}";

        public void Update(
            PbrRenderer renderer,
            PbrViewState viewState,
            IReadOnlyList<PbrDrawItem> items)
        {
            _renderer = renderer;
            _viewState = viewState;
            _items = items;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration.Write(
                _shadowAtlasKey, RenderGraphTextureUsage.RenderAttachment,
                new RenderGraphTextureSubresourceRange(0, 1, (uint)layer, 1));

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphDepthStencilAttachment(
                    _shadowAtlasKey, WGPULoadOp.Clear,
                    Subresources: new RenderGraphTextureSubresourceRange(0, 1, (uint)layer, 1),
                    Cacheable: false));
            _renderer!.EncodeShadowLayer(
                _viewState!, _items!, layer, renderPass);
        }
    }

    private sealed class IblPrefilterState(int face, int mip)
    {
        private PbrRenderer? _renderer;
        private PbrViewState? _viewState;

        public string Name { get; } = $"pbr-ibl-prefilter-{face}-{mip}";

        public void Update(
            PbrRenderer renderer,
            PbrViewState viewState)
        {
            _renderer = renderer;
            _viewState = viewState;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration.Write(
                _iblPrefilteredKey, RenderGraphTextureUsage.RenderAttachment,
                new RenderGraphTextureSubresourceRange((uint)mip, 1, (uint)face, 1));

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphColorAttachment(
                    _iblPrefilteredKey, WGPULoadOp.Clear,
                    Subresources: new RenderGraphTextureSubresourceRange((uint)mip, 1, (uint)face, 1),
                    Cacheable: false));
            _renderer!.EncodeIblPrefilter(
                _viewState!,
                face,
                mip,
                renderPass);
        }
    }

    private sealed class IblBrdfLutState
    {
        private PbrRenderer? _renderer;

        public void Update(PbrRenderer renderer) => _renderer = renderer;

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration.Write(_iblBrdfLutKey, RenderGraphTextureUsage.RenderAttachment);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphColorAttachment(_iblBrdfLutKey, WGPULoadOp.Clear, Cacheable: false));
            _renderer!.EncodeIblBrdfLut(renderPass);
        }
    }
}
