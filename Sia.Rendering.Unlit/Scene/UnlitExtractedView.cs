using Sia.Math;

namespace Sia.Engine.Rendering.Unlit;

internal sealed record UnlitExtractedView(
    float4x4 ViewProjection,
    ReadOnlyMemory<UnlitInstance> Instances,
    UnlitDrawItem[] Items);
