using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrStreamResidency
{
    private sealed class Relocation(Key key, Key target, Resident source, StreamPageAllocation destination)
    {
        public readonly Key Key = key, Target = target;
        public readonly Resident Source = source;
        public readonly StreamPageAllocation Destination = destination;
        public StreamPageArena.Upload? Upload;
    }

    private Relocation? _relocation;
    private (Key Key, StreamPageAllocation Allocation)? _relocationReservation;

    private void BeginRelocationFrame()
    {
        if (_relocation is not { Upload.Complete: true } move) return;
        if (!RelocationRequired(move)) { AbortRelocation(); return; }
        // The preceding prepared draws have been submitted or discarded before
        // this Begin. Their old addresses remain untouched throughout End.
        _resident[move.Key] = new(move.Destination, false, move.Source.LastUsed);
        Changed(move.Key);
        _arena.Free(move.Source.Allocation);
        _relocation = null;
        var target = _pages[move.Target.Page];
        if (!_arena.TryReserve((uint)target.Vertices, (uint)target.Triangles, out var allocation))
            throw new InvalidOperationException("Geometry relocation lost its reserved free-space proof.");
        // Keep the recovered hole for its waiting page while the bounded source
        // read restarts. Other ready pages cannot consume this space first.
        _relocationReservation = (move.Target, allocation);
    }

    private bool RelocationRequired(Relocation move)
        => _resident.TryGetValue(move.Key, out var current) && ReferenceEquals(current, move.Source)
            && IsAdmitted(move.Key) && _wanted.ContainsKey(move.Key)
            && IsAdmitted(move.Target) && _wanted.ContainsKey(move.Target);

    private void CancelRelocationReservation()
    {
        if (_relocationReservation is not { } reserved) return;
        if (IsAdmitted(reserved.Key) && _wanted.ContainsKey(reserved.Key)) return;
        _arena.Free(reserved.Allocation);
        _relocationReservation = null;
    }

    private void TryBeginRelocation(Key target, StreamGeometryPage page)
    {
        if (_relocationReservation is not null) return;
        foreach (var pair in _resident) {
            if (pair.Value.Root || !IsAdmitted(pair.Key) || !_wanted.ContainsKey(pair.Key)) continue;
            if (!_arena.TryReserveRelocation(pair.Value.Allocation, (uint)page.VertexCount, (uint)page.TriangleCount,
                out var destination)) continue;
            _relocation = new(pair.Key, target, pair.Value, destination);
            // Exclusive recovery cancels ordinary ready pages as well as reads.
            // Drop their borrowed data when their scheduler reservations retire.
            _prepared.Clear();
            return;
        }
    }

    private void AdvanceRelocation()
    {
        var move = _relocation!;
        if (!RelocationRequired(move)) {
            AbortRelocation();
            _requests.Update([]);
            return;
        }
        if (move.Upload is { Complete: true }) return;
        var page = _pages[move.Key.Page];
        _demands.Clear();
        _demands.Add(new(page.Id, page.DecodedBytes, _wanted[move.Target]));
        var failures = _requests.Statistics.Failed;
        _requests.Update(_demands);
        if (_requests.Statistics.Failed != failures) {
            AbortRelocation();
            _requests.Update([]);
            return;
        }
        try {
            if (move.Upload is null) {
                if (!_requests.TryGetReady(page.Id, out var record)) return;
                var geometry = _source.UsePage(page.Id, record!);
                var transform = float4x4.identity;
                var material = 1;
                if (!SharedInstances) {
                    var instance = _source.Instances.Span[move.Key.Instance];
                    transform = instance.Transform;
                    material = instance.MaterialIndex + 1;
                }
                move.Upload = _arena.Prepare(geometry, move.Destination, transform,
                    SharedInstances ? 0u : (uint)move.Key.Instance, material);
            }
            _uploaded += _arena.Advance(move.Upload, _settings.UploadBytesPerFrame - _uploaded);
            if (move.Upload.Complete) _requests.Acknowledge(page.Id);
        }
        catch {
            AbortRelocation();
            throw;
        }
    }

    private void AbortRelocation()
    {
        if (_relocation is not { } move) return;
        if (move.Upload is { } upload) _arena.Abort(upload);
        else _arena.Free(move.Destination);
        _relocation = null;
    }
}
