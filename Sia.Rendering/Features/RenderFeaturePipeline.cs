namespace Sia.Engine.Rendering;

public sealed class RenderFeaturePipeline<TContext>
{
    private readonly IRenderFeature[] _features;

    public IReadOnlyList<IRenderFeature> Features => _features;

    internal RenderFeaturePipeline(IRenderFeature[] features)
    {
        _features = features;
    }

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

    public void PrepareFrame(RenderWorld world, ReadOnlySpan<RenderFeatureContext<TContext>> contexts)
    {
        ArgumentNullException.ThrowIfNull(world);
        var views = new HashSet<RenderView>();
        foreach (ref readonly var context in contexts) {
            if (!ReferenceEquals(context.RenderWorld, world) || !world.Views.Contains(context.View))
                throw new ArgumentException("All contexts must belong to the supplied render world.", nameof(contexts));
            if (!views.Add(context.View))
                throw new ArgumentException("Each view may be prepared only once per batch.", nameof(contexts));
        }
        foreach (var feature in _features)
            if (feature is IPrepareRenderFeature<TContext> prepare) prepare.PrepareFrame(world, contexts);
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
