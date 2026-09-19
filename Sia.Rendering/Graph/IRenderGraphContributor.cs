namespace Sia.Engine.Rendering;

public interface IRenderGraphContributor<TContext> : IRenderFeature
{
    public void BuildRenderGraph(
        ref RenderGraphBuildContext graph,
        in RenderFeatureContext<TContext> context);
}
