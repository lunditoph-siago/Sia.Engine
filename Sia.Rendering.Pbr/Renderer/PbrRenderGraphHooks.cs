using Sia;
using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrRenderGraphHooks
{
    private static readonly RenderGraphBufferKey _clusterConfigKey = new("pbr-cluster-config");
    private static readonly RenderGraphBufferKey _clusteredLightsKey = new("pbr-clustered-lights");
    private static readonly RenderGraphBufferKey _lightGridKey = new("pbr-light-grid");
    private static readonly RenderGraphBufferKey _lightIndexListKey = new("pbr-light-index-list");
    private static readonly RenderGraphTextureKey _shadowAtlasKey = new("pbr-shadow-atlas");
    private static readonly RenderGraphTextureKey _iblPrefilteredKey = new("pbr-ibl-prefiltered");
    private static readonly RenderGraphTextureKey _iblBrdfLutKey = new("pbr-ibl-brdf-lut");

    public static void UseShadowAtlas(
        ref RenderGraphBuildContext graph,
        PbrViewState viewState,
        ShadowAtlasConfig shadowConfig)
    {
        var atlasDescriptor = new RenderGraphTextureDescriptor(
            "shadow-atlas", RenderGraphTextureFormat.Depth32Float,
            shadowConfig.TileResolution, shadowConfig.TileResolution,
            depthOrArrayLayers: (uint)shadowConfig.LayerCount,
            usage: RenderGraphTextureUsage.RenderAttachment | RenderGraphTextureUsage.TextureBinding);
        graph.UseImportedTexture(_shadowAtlasKey, atlasDescriptor);
        graph.BindImportedTexture(
            _shadowAtlasKey, viewState.ShadowAtlas.Texture.GetWgpu<WGPUTexture>());
    }

    public static void UseIblPrecomputePasses(
        ref RenderGraphBuildContext graph,
        PbrRenderer renderer,
        PbrViewState viewState)
    {
        graph.UseImportedBuffer(AtmosphereGpuState.IrradianceKey,
            new RenderGraphBufferDescriptor("ibl-sh", IblShGpu.Stride, RenderGraphBufferUsage.Uniform | RenderGraphBufferUsage.Storage));
        graph.BindImportedBuffer(AtmosphereGpuState.IrradianceKey, viewState.Ibl.ShBuffer.GetWgpu<WGPUBuffer>());
        AtmosphereGpuState.BuildGraph(ref graph, viewState);
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

        var prefilterState = graph.UseState(static () => new IblPrefilterState());
        prefilterState.Update(renderer, viewState);
        graph.UsePass(new("pbr-ibl-prefilter"), "pbr-ibl-prefilter", prefilterState.Declare, prefilterState.Render);

        var lutPass = new RenderGraphPassKey("pbr-ibl-brdf-lut");
        var lutState = graph.UseState(() => new IblBrdfLutState());
        lutState.Update(renderer, viewState);
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

    private sealed class IblPrefilterState
    {
        private PbrRenderer? _renderer;
        private PbrViewState? _viewState;
        private ulong _renderedRevision;
        private Entity _texture;

        public void Update(
            PbrRenderer renderer,
            PbrViewState viewState)
        {
            if (!ReferenceEquals(_renderer, renderer) || !ReferenceEquals(_viewState, viewState)
                || _texture != viewState.Ibl.PrefilteredTexture) {
                _renderedRevision = 0;
            }
            _renderer = renderer;
            _viewState = viewState;
            _texture = viewState.Ibl.PrefilteredTexture;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration)
        {
            declaration.Write(_iblPrefilteredKey, RenderGraphTextureUsage.RenderAttachment);
            if (_viewState!.ActiveAtmosphere is not null) {
                AtmosphereGpuState.DeclareSkyRead(declaration);
            }
        }

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            if (_renderedRevision == _viewState!.EnvironmentRevision) {
                return;
            }
            // One graph dependency and cache check; all faces/mips still update together.
            for (var face = 0; face < 6; face++) {
                for (var mip = 0; mip < IblEnvironmentGpuStore.PrefilteredMipCount; mip++) {
                    var renderPass = context.GetOrBeginRenderPass(
                        new WgpuReactiveRenderGraphColorAttachment(
                            _iblPrefilteredKey, WGPULoadOp.Clear,
                            Subresources: new RenderGraphTextureSubresourceRange((uint)mip, 1, (uint)face, 1),
                            Cacheable: false));
                    _renderer!.EncodeIblPrefilter(_viewState, face, mip, renderPass);
                }
            }
            _renderedRevision = _viewState.EnvironmentRevision;
        }
    }

    private sealed class IblBrdfLutState
    {
        private PbrRenderer? _renderer;
        private PbrViewState? _viewState;
        private bool _rendered;

        public void Update(PbrRenderer renderer, PbrViewState viewState)
        {
            if (!ReferenceEquals(_renderer, renderer) || !ReferenceEquals(_viewState, viewState)) {
                _rendered = false;
            }
            _renderer = renderer;
            _viewState = viewState;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration.Write(_iblBrdfLutKey, RenderGraphTextureUsage.RenderAttachment);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            if (_rendered) {
                return;
            }
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphColorAttachment(_iblBrdfLutKey, WGPULoadOp.Clear, Cacheable: false));
            _renderer!.EncodeIblBrdfLut(renderPass);
            _rendered = true;
        }
    }
}
