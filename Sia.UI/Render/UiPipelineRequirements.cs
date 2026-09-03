using Sia.Graphics.Compatibility;

namespace Sia.UI;

internal sealed record UiPipelineRequirements(
    IReadOnlyList<GpuBufferRequirement> Buffers,
    uint VertexBufferCount,
    uint VertexAttributeCount,
    ulong VertexBufferArrayStride);
