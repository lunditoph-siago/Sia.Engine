namespace Sia.Engine.Rendering;

public sealed class RenderFeaturePipeline<TContext>
{
    private readonly IRenderFeature[] _features;

    internal RenderFeaturePipeline(IRenderFeature[] features)
    {
        _features = features;
    }

    public IReadOnlyList<IRenderFeature> Features => _features;

    public void Extract(in RenderFeatureContext<TContext> context)
    {
        foreach (var feature in _features) {
            if (feature is IExtractRenderFeature<TContext> extract) {
                extract.Extract(in context);
            }
        }
    }

    public void Prepare(in RenderFeatureContext<TContext> context)
    {
        foreach (var feature in _features) {
            if (feature is IPrepareRenderFeature<TContext> prepare) {
                prepare.Prepare(in context);
            }
        }
    }

    public void Queue(in RenderFeatureContext<TContext> context)
    {
        foreach (var feature in _features) {
            if (feature is IQueueRenderFeature<TContext> queue) {
                queue.Queue(in context);
            }
        }
    }

    public void BuildRenderGraph(
        ref RenderGraphBuildContext graph,
        in RenderFeatureContext<TContext> context)
    {
        foreach (var feature in _features) {
            if (feature is IRenderGraphContributor<TContext> contributor) {
                contributor.BuildRenderGraph(ref graph, in context);
            }
        }
    }
}
