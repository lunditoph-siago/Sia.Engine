namespace Sia.Engine.Rendering;

public interface IPrepareRenderFeature<TContext> : IRenderFeature
{
    public void Prepare(in RenderFeatureContext<TContext> context);

    public void PrepareFrame(RenderWorld world, ReadOnlySpan<RenderFeatureContext<TContext>> contexts)
    {
        foreach (ref readonly var context in contexts) Prepare(in context);
    }
}
