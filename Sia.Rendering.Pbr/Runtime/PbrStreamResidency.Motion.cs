using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrStreamResidency
{
    // Only local GPU-instance storage can move without rewriting resident geometry.
    // Called with a fully validated snapshot before any view consumes this frame.
    internal void UpdateTransforms(ReadOnlySpan<PbrInstanceGpu> rows, ReadOnlySpan<Aabb> bounds)
    {
        if (!_settings.GpuTraversal || rows.Length != _instances.Length || bounds.Length != _instances.Length)
            throw new ArgumentException("Motion requires matching local GPU instance storage.");
        var changed = false;
        for (var i = 0; i < rows.Length; i++) {
            if (_instances[i].Transform.Equals(rows[i].Transform)) continue;
            changed = true; _instances[i] = _instances[i] with { Transform = rows[i].Transform };
            _instanceBounds[i] = bounds[i];
            _norms[i] = BitConverter.UInt32BitsToSingle(rows[i].Material.y);
        }
        if (!changed) return;
        for (var i = 0; i < _roots.Count; i++)
            _roots[i] = _roots[i] with { Bounds = _instanceBounds[(int)_roots[i].Instance] };
        // Geometry remains local and residency/range ownership is unchanged.
        // CPU reference cuts and their dependent bounds must be regenerated at the new pose.
        Array.Clear(_bounds); _branches = []; _leafRoots = [];
        _cuts.Clear(); _groupCuts.Clear(); _dependencies.Clear();
        _cpuSelectionInitialized = false;
        PoseRevision++; Revision++;
    }
}
