using Sia;
using Sia.Engine.Rendering;
using Sia.Graphics.Reactive;
using Sia.Graphics.Rendering;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;
using SiaReactive = Sia.Reactive.Reactive;

namespace Sia.Engine.Example;

internal sealed unsafe partial class SceneExampleApp
{
    private static readonly RenderGraphTextureKey _surfaceKey = new("surface");
    private static readonly RenderGraphTextureKey _depthKey = new("depth");
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
        var frame = new GpuFrame(_sceneWorld!, _renderDevice, _renderQueue);
        var props = new RenderGraphProps(
            _renderGraph!, _renderPipeline!, frame, _camera,
            _framebufferWidth, _framebufferHeight, _surfaceFormat, surfaceTexture);

        if (_renderGraphMount is not { } mount) {
            _renderGraphMount = _renderGraphWorld!.Mount(RenderGraph, props);
            return;
        }
        if (mount.Props != props) {
            mount.Update(props);
        }
    }

    private void ExecuteRenderGraph() => _renderGraphWorld!.ExecuteWgpuRenderGraph();

    private static ReactiveNode RenderGraph(in RenderGraphProps props, ref Hooks hooks)
    {
        var registry = props.Registry;

        var surfaceDescriptor = new RenderGraphTextureDescriptor(
            "surface", (RenderGraphTextureFormat)(int)props.SurfaceFormat,
            (uint)props.FramebufferWidth, (uint)props.FramebufferHeight,
            usage: RenderGraphTextureUsage.RenderAttachment);
        hooks.UseImportedRenderGraphTexture(registry, _surfaceKey, surfaceDescriptor);
        hooks.UseImportedRenderGraphTextureBinding(registry, _surfaceKey, props.SurfaceTexture);

        hooks.UseRenderGraphTexture(
            registry, _depthKey,
            new RenderGraphTextureDescriptor(
                "depth", RenderGraphTextureFormat.Depth32Float,
                (uint)props.FramebufferWidth, (uint)props.FramebufferHeight,
                usage: RenderGraphTextureUsage.RenderAttachment));

        var frame = props.Frame;
        var context = new RenderFrameContext(
            frame,
            props.Camera,
            _surfaceKey,
            _depthKey,
            ColorCacheable: false);
        props.Pipeline.Configure(ref hooks, registry, in context);

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
        GpuFrame Frame,
        Entity Camera,
        int FramebufferWidth,
        int FramebufferHeight,
        WGPUTextureFormat SurfaceFormat,
        WgpuHandle<WGPUTexture> SurfaceTexture);
}
