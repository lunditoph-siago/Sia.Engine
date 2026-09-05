namespace Sia.Engine.Rendering;

public readonly record struct RenderPipelineKey(
    ShaderAssetId Shader,
    ulong ShaderRevision,
    string VertexEntryPoint,
    string FragmentEntryPoint,
    string VertexLayout,
    string ColorTargets,
    string DepthStencilTarget,
    RenderPipelineState State)
{
    public RenderPipelineKey Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Shader.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(VertexEntryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(FragmentEntryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(VertexLayout);
        ArgumentNullException.ThrowIfNull(ColorTargets);
        ArgumentNullException.ThrowIfNull(DepthStencilTarget);
        if (State.SampleCount == 0) {
            throw new ArgumentOutOfRangeException(nameof(State.SampleCount));
        }
        return this;
    }
}
