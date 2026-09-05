namespace Sia.Engine.Rendering;

public interface IExtractRenderFeature<TContext> : IRenderFeature
{
    public void Extract(in RenderFeatureContext<TContext> context);
}
