using Sia.Graphics.Compatibility;

namespace Sia.UI;

internal sealed record UiLegalizationPlan(
    UiVertexDataMode VertexDataMode,
    GpuLegalizationPlan BufferPlan,
    string StrategyId);
