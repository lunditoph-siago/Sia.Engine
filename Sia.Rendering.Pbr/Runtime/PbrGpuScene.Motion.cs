using Sia.WebGPU;
using System.Runtime.CompilerServices;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrGpuScene
{
    private PbrInstanceMotion? _instanceMotion;
    private PbrInstanceMotion? _pendingInstanceMotion;
    private ulong _instanceRevision;
    internal ulong GeometryRevision => _instanceRevision + (Streaming?.Revision ?? 0);
    internal uint[] ReflectionReceiverMap(ReadOnlySpan<PbrReflectionInstancePose> poses)
        => (_instanceMotion ?? throw new NotSupportedException("Reflection history requires authored local instance rows.")).ReflectionReceiverMap(poses);
    internal bool IsInstanceEnabled(uint row) => _instanceMotion is null || (_instanceMotion.Rows[(int)row].Material.x & PbrInstanceMotion.DisabledBit) == 0;

    internal void StageVisibility(ReadOnlySpan<PbrInstanceVisibility> updates)
    {
        if (updates.IsEmpty) return;
        var current = _pendingInstanceMotion ?? _instanceMotion
            ?? throw new NotSupportedException("Runtime visibility requires shared local instance storage.");
        _pendingInstanceMotion = current.WithVisibility(updates);
    }

    internal void StageTransforms(ReadOnlySpan<PbrInstanceTransform> updates)
    {
        if (updates.IsEmpty) return;
        var current = _pendingInstanceMotion ?? _instanceMotion
            ?? throw new NotSupportedException("Runtime transforms require local shared instance storage.");
        _pendingInstanceMotion = current.WithTransforms(updates);
    }

    internal void ApplyTransforms()
    {
        if (_pendingInstanceMotion is not { } pending) return;
        var current = _instanceMotion!;
        var changed = false;
        // Queue writes precede this frame's submission. No geometry upload, allocation or range reuse.
        // Keep the pending snapshot on upload failure; a subsequent attempt rewrites all changed rows.
        for (var first = 0; first < pending.Rows.Length;) {
            if (pending.Rows[first].Equals(current.Rows[first])) { first++; continue; }
            changed = true;
            var end = first + 1;
            while (end < pending.Rows.Length && !pending.Rows[end].Equals(current.Rows[end])) end++;
            Wgpu.WriteBuffer<PbrInstanceGpu>(_gpu.Queue, Instances.GetWgpu<WGPUBuffer>(), (ulong)first * (ulong)Unsafe.SizeOf<PbrInstanceGpu>(),
                pending.Rows.AsSpan(first, end - first));
            first = end;
        }
        if (Streaming is { } streaming) {
            streaming.UpdateTransforms(pending.Rows.AsSpan(0, streaming.Source.Instances.Length),
                pending.WorldBounds.AsSpan(0, streaming.Source.Instances.Length));
            Opaque = streaming.RootDraws;
            for (var i = 0; i < Transparent.Length; i++)
                Transparent[i] = Transparent[i] with { Bounds = pending.WorldBounds[(int)Transparent[i].Instance] };
            Bounds = pending.Bounds;
        }
        else {
            foreach (var draws in new[] { Opaque, Transparent })
                for (var i = 0; i < draws.Length; i++) draws[i] = draws[i] with { Bounds = pending.WorldBounds[(int)draws[i].Instance] };
            Bounds = pending.Bounds;
        }
        _instanceMotion = pending; _pendingInstanceMotion = null;
        if (changed) _instanceRevision++;
    }
}
