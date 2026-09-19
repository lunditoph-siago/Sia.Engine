using Sia.Engine.Rendering;

namespace Sia.Engine.Rendering.Pbr;

public sealed record ForwardPbrPipelineDescriptor
{
    public required ShaderAsset Shader { get; init; }

    public string VertexEntryPoint { get; init; } = "vertex";

    public string FragmentEntryPoint { get; init; } = "fragment";

    public RenderPipelineState State { get; init; } = new(
        DepthWriteEnabled: false);

    public static ForwardPbrPipelineDescriptor Default => new() {
        Shader = new ShaderAsset(
            new ShaderAssetId("sia:pbr/forward"),
            PbrShaderSource.LoadForwardPbr())
    };
}
