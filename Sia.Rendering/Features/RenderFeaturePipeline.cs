namespace Sia.Engine.Rendering;

public sealed class RenderFeaturePipeline<TContext>
{
    private readonly IExtractRenderFeature<TContext>[] _extract;
    private readonly IPrepareRenderFeature<TContext>[] _prepare;
    private readonly IQueueRenderFeature<TContext>[] _queue;
    private readonly IRenderGraphContributor<TContext>[] _graph;

    public IReadOnlyList<IRenderFeature> Features { get; }

    internal RenderFeaturePipeline(IRenderFeature[] features)
    {
        Features = Array.AsReadOnly(features);
        _extract = Select<IExtractRenderFeature<TContext>>();
        _prepare = Select<IPrepareRenderFeature<TContext>>();
        _queue = Select<IQueueRenderFeature<TContext>>();
        _graph = Select<IRenderGraphContributor<TContext>>();

        TRole[] Select<TRole>() where TRole : IRenderFeature => features.OfType<TRole>().ToArray();
    }

    public void Extract(in RenderFeatureContext<TContext> context)
    {
        foreach (var feature in _extract) feature.Extract(in context);
    }

    public void Prepare(in RenderFeatureContext<TContext> context)
    {
        foreach (var feature in _prepare) feature.Prepare(in context);
    }

    public void Queue(in RenderFeatureContext<TContext> context)
    {
        foreach (var feature in _queue) feature.Queue(in context);
    }

    public void PrepareFrame(RenderWorld world, ReadOnlySpan<RenderFeatureContext<TContext>> contexts)
    {
        ValidateBatch(world, contexts);
        PrepareBatch(world, contexts);
    }

    /// <summary>
    /// Validates the full batch, extracts all views, prepares each feature once,
    /// then queues all views. The caller owns BeginFrame and graph construction.
    /// </summary>
    public void ProcessFrame(RenderWorld world, ReadOnlySpan<RenderFeatureContext<TContext>> contexts)
    {
        ValidateBatch(world, contexts);
        foreach (ref readonly var context in contexts) Extract(in context);
        PrepareBatch(world, contexts);
        foreach (ref readonly var context in contexts) Queue(in context);
    }

    private void PrepareBatch(RenderWorld world, ReadOnlySpan<RenderFeatureContext<TContext>> contexts)
    {
        foreach (var feature in _prepare) feature.PrepareFrame(world, contexts);
    }

    private static void ValidateBatch(RenderWorld world, ReadOnlySpan<RenderFeatureContext<TContext>> contexts)
    {
        ArgumentNullException.ThrowIfNull(world);
        var views = new HashSet<RenderView>();
        foreach (ref readonly var context in contexts) {
            if (!ReferenceEquals(context.RenderWorld, world) || !world.Views.Contains(context.View))
                throw new ArgumentException("All contexts must belong to the supplied render world.", nameof(contexts));
            if (!views.Add(context.View))
                throw new ArgumentException("Each view may be prepared only once per batch.", nameof(contexts));
        }
    }

    public void BuildRenderGraph(
        ref RenderGraphBuildContext graph,
        in RenderFeatureContext<TContext> context)
    {
        foreach (var feature in _graph) feature.BuildRenderGraph(ref graph, in context);
    }
}
