using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct PbrDrawInstance(uint Instance, uint TriangleOffset);

internal static class PbrInstanceBatching
{
    internal static void Compact(List<PbrGpuScene.Draw> draws, List<PbrDrawInstance> instances,
        Comparison<PbrGpuScene.Draw> compare, bool preserveOrder = false, uint? shadowLayer = null)
    {
        if (!preserveOrder) draws.Sort(compare);
        var write = 0;
        for (var read = 0; read < draws.Count; read++) {
            var draw = draws[read];
            if (draw.InstanceCount != 1) throw new InvalidOperationException("Batching requires original per-instance draws.");
            var first = checked((uint)instances.Count);
            var id = shadowLayer is { } layer ? checked(draw.Instance * 8) | layer : draw.Instance;
            instances.Add(new(id, draw.TriangleOffset));
            if (write != 0 && compare(draws[write - 1], draw) == 0) {
                var previous = draws[write - 1];
                draws[write - 1] = previous with { InstanceCount = checked(previous.InstanceCount + 1),
                    Bounds = new(math.min(previous.Bounds.Min, draw.Bounds.Min), math.max(previous.Bounds.Max, draw.Bounds.Max)) };
            }
            else draws[write++] = draw with { Instance = first };
        }
        if (write < draws.Count) draws.RemoveRange(write, draws.Count - write);
    }
}
