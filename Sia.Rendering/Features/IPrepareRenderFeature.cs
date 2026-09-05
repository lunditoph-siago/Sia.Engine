namespace Sia.Engine.Rendering;

public interface IPrepareRenderFeature<TContext> : IRenderFeature
{
    public void Prepare(in RenderFeatureContext<TContext> context);
}
