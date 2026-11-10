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
        var context = PrepareRenderGraphFrame();
        UpdateRenderGraph(in context, surfaceTexture);
    }

    private RenderFeatureContext<RenderFrameContext> PrepareRenderGraphFrame()
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
        _renderPipeline!.RunFrame(renderWorld, [featureContext]);
        return featureContext;
    }

    private void UpdateRenderGraph(in RenderFeatureContext<RenderFrameContext> featureContext, WgpuHandle<WGPUTexture> surfaceTexture)
    {
        var props = new RenderGraphProps(
            _renderGraph!, _renderPipeline!, featureContext,
            _framebufferWidth, _framebufferHeight, _surfaceFormat, surfaceTexture,
            _sceneRenderer, _sceneRenderer?.ResourceRevision ?? 0);

        if (_renderGraphMount is not { } mount) {
            _renderGraphMount = _renderGraphWorld!.Mount(RenderGraph, props);
            Console.WriteLine($"{_pipeline}: {_renderGraph!.PreparePlan().Graph.Passes.Count} render graph pass(es).");
            return;
        }
        mount.Update(props);
        _renderGraphWorld!.FlushReactive();
        _renderGraph!.PrepareBindings();
    }

    private void ExecuteRenderGraph(bool presentationCopy = false)
    {
        _renderGraphWorld!.ExecuteWgpuRenderGraph();
#if !BROWSER
        // Execute has returned after queue submission. A later error must not
        // make this accepted work appear unsubmitted in the prototype ledger.
        if (presentationCopy) _presentationGraphSubmissions++;
#endif
        ThrowGpuError();
        _sceneRenderer?.NotifySubmittedFrame(_renderWorld!.FrameIndex);
#if !BROWSER
        if (presentationCopy) _presentationHistoryNotifications++;
#endif
    }

    private static ReactiveNode RenderGraph(in RenderGraphProps props, ref Hooks hooks)
    {
        var graph = new RenderGraphBuildContext(ref hooks, props.Registry);

        var surfaceDescriptor = new RenderGraphTextureDescriptor(
            "surface", (RenderGraphTextureFormat)(int)props.SurfaceFormat,
            (uint)props.FramebufferWidth, (uint)props.FramebufferHeight,
            usage: RenderGraphTextureUsage.RenderAttachment);
        graph.UseImportedTexture(_surfaceKey, surfaceDescriptor);
        graph.BindImportedTexture(_surfaceKey, props.SurfaceTexture);

        if (props.Visibility is null)
            graph.UseTexture(
            _depthKey,
            new RenderGraphTextureDescriptor(
                "depth", RenderGraphTextureFormat.Depth32Float,
                (uint)props.FramebufferWidth, (uint)props.FramebufferHeight,
                usage: RenderGraphTextureUsage.RenderAttachment));

        var context = props.Context;
        props.Pipeline.BuildRenderGraph(ref graph, in context);

        return graph.Complete();
    }

    private void DisposeRenderGraph()
    {
#if !BROWSER
        if (_presentationResources is not null && !_shutdownComplete)
            throw new InvalidOperationException("Complete scene streaming and queue shutdown before releasing the presentation output.");
#endif
        if (_renderGraphMount is { } mount && mount.IsMounted) {
            mount.Unmount();
        }
        _renderGraphWorld?.Dispose();
        _renderGraphMount = null;
        _renderGraph = null;
        _renderGraphWorld = null;
#if !BROWSER
        // The render-world resource entities outlive the graph, and both normal
        // shutdown and a scene reload dispose the graph before those entities.
        DisposePresentationOutput();
#endif
    }

    private readonly record struct RenderGraphProps(
        WgpuRenderGraphRegistry Registry,
        RenderFeaturePipeline<RenderFrameContext> Pipeline,
        RenderFeatureContext<RenderFrameContext> Context,
        int FramebufferWidth,
        int FramebufferHeight,
        WGPUTextureFormat SurfaceFormat,
        WgpuHandle<WGPUTexture> SurfaceTexture,
        Sia.Engine.Rendering.Pbr.PbrRenderer? Visibility, ulong ResourceRevision);
}
