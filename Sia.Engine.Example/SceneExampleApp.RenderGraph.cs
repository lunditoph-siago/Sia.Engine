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
        _renderPipeline!.ProcessFrame(renderWorld, [featureContext]);
        var props = new RenderGraphProps(
            _renderGraph!, _renderPipeline, featureContext,
            _framebufferWidth, _framebufferHeight, RenderWidth, RenderHeight, _surfaceFormat, surfaceTexture,
            PrepareGpuTiming(), _sceneRenderer, _sceneRenderer?.ResourceRevision ?? 0);

        if (_renderGraphMount is not { } mount) {
            _renderGraphMount = _renderGraphWorld!.Mount(RenderGraph, props);
            Console.WriteLine($"{_pipeline}: {_renderGraph!.PreparePlan().Graph.Passes.Count} render graph pass(es).");
            return;
        }
        mount.Update(props);
        _renderGraphWorld!.FlushReactive();
        _renderGraph!.PrepareBindings();
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

        if (props.Visibility is null)
            graph.UseTexture(
            _depthKey,
            new RenderGraphTextureDescriptor(
                "depth", RenderGraphTextureFormat.Depth32Float,
                (uint)props.RenderWidth, (uint)props.RenderHeight,
                usage: RenderGraphTextureUsage.RenderAttachment));

        var context = props.Context;
        props.Pipeline.BuildRenderGraph(ref graph, in context);
        if (props.TimingReadback.IsValid) {
            var timing = graph.UseState(static () => new TimingReadbackPass());
            timing.Visibility = props.Visibility!;
            graph.UseImportedBuffer(_gpuReadbackKey, new("example-gpu-timing", TimingBytes,
                RenderGraphBufferUsage.CopyDestination | RenderGraphBufferUsage.MapRead));
            graph.BindImportedBuffer(_gpuReadbackKey, props.TimingReadback.GetWgpu<WGPUBuffer>());
            graph.ExportBuffer(_gpuReadbackKey, RenderGraphBufferUsage.MapRead);
            graph.UseComputePass(new("example-gpu-timing"), "example-gpu-timing", timing.Declare, timing.Copy);
        }

        return graph.Complete();
    }

    private sealed class TimingReadbackPass
    {
        public Sia.Engine.Rendering.Pbr.PbrRenderer Visibility  { get; set; } = null!;

        public void Declare(RenderGraphPassDeclarationBuilder declaration) => declaration
            .Read(Visibility.GetGpuTimingsTarget(_mainViewKey), RenderGraphBufferUsage.CopySource)
            .Write(_gpuReadbackKey, RenderGraphBufferUsage.CopyDestination);

        public void Copy(WgpuReactiveRenderGraphPassContext pass)
        {
            if (Visibility.SampleGpuTiming) {
                Wgpu.CopyBufferToBuffer(pass.CommandEncoder, pass.GetBuffer(Visibility.GetGpuTimingsTarget(_mainViewKey)), 0,
                    pass.GetBuffer(_gpuReadbackKey), 0, TimingBytes);
            }
        }
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
        int RenderWidth,
        int RenderHeight,
        WGPUTextureFormat SurfaceFormat,
        WgpuHandle<WGPUTexture> SurfaceTexture,
        Entity TimingReadback, Sia.Engine.Rendering.Pbr.PbrRenderer? Visibility, ulong ResourceRevision);
}
