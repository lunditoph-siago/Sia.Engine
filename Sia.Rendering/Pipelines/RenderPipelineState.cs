namespace Sia.Engine.Rendering;

public readonly record struct RenderPipelineState(
    RenderPrimitiveTopology Topology = RenderPrimitiveTopology.TriangleList,
    RenderFrontFace FrontFace = RenderFrontFace.CounterClockwise,
    RenderCullMode CullMode = RenderCullMode.Back,
    RenderCompareFunction DepthCompare = RenderCompareFunction.LessEqual,
    bool DepthWriteEnabled = true,
    RenderBlendMode BlendMode = RenderBlendMode.Opaque,
    uint SampleCount = 1)
{
    public RenderPipelineState()
        : this(
            RenderPrimitiveTopology.TriangleList,
            RenderFrontFace.CounterClockwise,
            RenderCullMode.Back,
            RenderCompareFunction.LessEqual,
            true,
            RenderBlendMode.Opaque,
            1)
    {
    }
}
