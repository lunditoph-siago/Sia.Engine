using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Unlit;

public sealed class UnlitRenderFeature :
    IExtractRenderFeature<RenderFrameContext>,
    IPrepareRenderFeature<RenderFrameContext>,
    IQueueRenderFeature<RenderFrameContext>,
    IRenderGraphContributor<RenderFrameContext>
{
    private static readonly RenderGraphBufferKey _cameraKey = new("unlit-camera");
    private static readonly RenderGraphBufferKey _instancesKey = new("unlit-instances");
    private static readonly RenderPhaseKey _phaseKey = new("unlit-opaque");
    private static readonly IComparer<UnlitDrawItem> _comparer =
        Comparer<UnlitDrawItem>.Create(static (left, right) => {
            var mesh = left.Mesh.Id.CompareTo(right.Mesh.Id);
            return mesh != 0 ? mesh : left.InstanceIndex.CompareTo(right.InstanceIndex);
        });
    private readonly UnlitRenderer _renderer;

    public RenderFeatureKey Key { get; } = new("unlit");

    public UnlitRenderFeature(UnlitRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
    }

    public void Extract(in RenderFeatureContext<RenderFrameContext> context)
    {
        var frame = context.Frame.Frame;
        context.View.Resources.Set(_renderer.Extract(in frame, context.Frame.Camera));
    }

    public void Prepare(in RenderFeatureContext<RenderFrameContext> context)
    {
        var frame = context.Frame.Frame;
        var state = context.View.PersistentResources.GetOrAdd(static () => new UnlitViewState());
        _renderer.Prepare(state, in frame, context.View.Resources.GetRequired<UnlitExtractedView>());
    }

    public void Queue(in RenderFeatureContext<RenderFrameContext> context)
    {
        var phase = context.View.Phases.GetOrAdd(_phaseKey, _comparer);
        phase.AddRange(context.View.Resources.GetRequired<UnlitExtractedView>().Items);
        phase.Sort();
    }

    public void BuildRenderGraph(
        ref RenderGraphBuildContext graph,
        in RenderFeatureContext<RenderFrameContext> context)
    {
        var state = context.View.PersistentResources.GetRequired<UnlitViewState>();
        graph.UseImportedBuffer(_cameraKey,
            new RenderGraphBufferDescriptor("unlit-camera", 64, RenderGraphBufferUsage.Uniform));
        graph.BindImportedBuffer(_cameraKey, state.CameraBuffer.GetWgpu<WGPUBuffer>());
        graph.UseImportedBuffer(_instancesKey,
            new RenderGraphBufferDescriptor("unlit-instances", state.InstanceCapacity, RenderGraphBufferUsage.Storage));
        graph.BindImportedBuffer(_instancesKey, state.InstanceBuffer.GetWgpu<WGPUBuffer>());

        var pass = graph.UseState(static () => new PassState());
        pass.Renderer = _renderer;
        pass.View = state;
        pass.Phase = context.View.Phases.GetRequired<UnlitDrawItem>(_phaseKey);
        pass.Frame = context.Frame;
        graph.UsePass(new RenderGraphPassKey("unlit-opaque"), "unlit-opaque", pass.Declare, pass.Render);
    }

    private sealed class PassState
    {
        public UnlitRenderer Renderer { get; set; } = null!;
        public UnlitViewState View { get; set; } = null!;
        public RenderPhase<UnlitDrawItem> Phase { get; set; } = null!;
        public RenderFrameContext Frame { get; set; }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration
                .Read(_cameraKey, RenderGraphBufferUsage.Uniform)
                .Read(_instancesKey, RenderGraphBufferUsage.Storage)
                .Write(Frame.ColorTarget, RenderGraphTextureUsage.RenderAttachment)
                .Write(Frame.DepthTarget, RenderGraphTextureUsage.RenderAttachment);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var pass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphColorAttachment(
                    Frame.ColorTarget, Frame.ColorLoadOp, Cacheable: Frame.ColorCacheable),
                new WgpuReactiveRenderGraphDepthStencilAttachment(Frame.DepthTarget, WGPULoadOp.Clear));
            Renderer.Encode(View, Phase, pass);
        }
    }
}
