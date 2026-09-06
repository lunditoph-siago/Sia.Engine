using Sia;
using Sia.Engine.Rendering;
using Sia.Graphics.Reactive;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;
using SiaReactive = Sia.Reactive.Reactive;

namespace Sia.Engine.Example;

internal sealed unsafe partial class SceneExampleApp
{
    private static readonly RenderGraphTextureKey _surfaceKey = new("surface");
    private static readonly RenderGraphTextureKey _depthKey = new("depth");
    private static readonly RenderViewKey _mainViewKey = new("main");
    private World? _renderGraphWorld;
    private WgpuRenderGraphRegistry? _renderGraph;
    private ReactiveMount<RenderGraphProps>? _renderGraphMount;
    private Entity _renderDevice;
    private Entity _renderQueue;

    private void InitializeRenderGraph()
    {
        _renderGraphWorld = new World();
        _renderGraph = _renderGraphWorld.ConfigureWgpuRenderGraph(_device, _queue);
        _renderDevice = _renderGraphWorld.OwnWgpu(_device, static (ref WgpuHandle<WGPUDevice> _) => { });
        _renderQueue = _renderGraphWorld.OwnWgpu(_queue, static (ref WgpuHandle<WGPUQueue> _) => { });
    }

    private void UpdateRenderGraph(WgpuHandle<WGPUTexture> surfaceTexture)
    {
        var frame = new GpuFrame(
            _sceneWorld!,
            _renderWorld!.Entities,
            _renderDevice,
            _renderQueue);
        var frameContext = new RenderFrameContext(
            frame,
            _camera,
            _surfaceKey,
            _depthKey,
            ColorCacheable: false);
        var renderWorld = _renderWorld!;
        var view = renderWorld.GetOrCreateView(_mainViewKey);
        renderWorld.BeginFrame();
        var featureContext = new RenderFeatureContext<RenderFrameContext>(
            renderWorld,
            view,
            frameContext);
        _renderPipeline!.Extract(in featureContext);
        _renderPipeline.Prepare(in featureContext);
        _renderPipeline.Queue(in featureContext);
        var props = new RenderGraphProps(
            _renderGraph!, _renderPipeline, featureContext,
            _framebufferWidth, _framebufferHeight, _surfaceFormat, surfaceTexture);

        if (_renderGraphMount is not { } mount) {
            _renderGraphMount = _renderGraphWorld!.Mount(RenderGraph, props);
            Console.WriteLine($"{_pipeline}: {_renderGraph!.PreparePlan().Graph.Passes.Count} render graph pass(es).");
            return;
        }
        if (mount.Props != props) {
            mount.Update(props);
        }
    }

    private void ExecuteRenderGraph() => _renderGraphWorld!.ExecuteWgpuRenderGraph();

    private static ReactiveNode RenderGraph(in RenderGraphProps props, ref Hooks hooks)
    {
        var graph = new RenderGraphBuildContext(ref hooks, props.Registry);

        var surfaceDescriptor = new RenderGraphTextureDescriptor(
            "surface", (RenderGraphTextureFormat)(int)props.SurfaceFormat,
            (uint)props.FramebufferWidth, (uint)props.FramebufferHeight,
            usage: RenderGraphTextureUsage.RenderAttachment);
        graph.UseImportedTexture(_surfaceKey, surfaceDescriptor);
        graph.BindImportedTexture(_surfaceKey, props.SurfaceTexture);

        graph.UseTexture(
            _depthKey,
            new RenderGraphTextureDescriptor(
                "depth", RenderGraphTextureFormat.Depth32Float,
                (uint)props.FramebufferWidth, (uint)props.FramebufferHeight,
                usage: RenderGraphTextureUsage.RenderAttachment));

        var context = props.Context;
        props.Pipeline.BuildRenderGraph(ref graph, in context);

        return SiaReactive.None;
    }

    private void DisposeRenderGraph()
    {
        if (_renderGraphMount is { } mount && mount.IsMounted) {
            mount.Unmount();
        }
        _renderGraphWorld?.Dispose();
        _renderGraphMount = null;
        _renderGraph = null;
        _renderGraphWorld = null;
    }

    private readonly record struct RenderGraphProps(
        WgpuRenderGraphRegistry Registry,
        RenderFeaturePipeline<RenderFrameContext> Pipeline,
        RenderFeatureContext<RenderFrameContext> Context,
        int FramebufferWidth,
        int FramebufferHeight,
        WGPUTextureFormat SurfaceFormat,
        WgpuHandle<WGPUTexture> SurfaceTexture);
}
