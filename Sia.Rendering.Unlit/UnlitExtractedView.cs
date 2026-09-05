using Sia.Math;

namespace Sia.Engine.Rendering.Unlit;

internal sealed record UnlitExtractedView(
    float4x4 ViewProjection,
    UnlitInstance[] Instances,
    UnlitDrawItem[] Items);
