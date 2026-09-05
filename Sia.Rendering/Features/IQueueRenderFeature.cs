namespace Sia.Engine.Rendering;

public interface IQueueRenderFeature<TContext> : IRenderFeature
{
    public void Queue(in RenderFeatureContext<TContext> context);
}
