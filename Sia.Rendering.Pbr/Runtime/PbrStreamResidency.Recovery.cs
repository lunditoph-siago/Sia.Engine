namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrStreamResidency
{
    private sealed class Recovery(Key target)
    {
        public readonly Key Target = target;
        public readonly Dictionary<Key, StreamPageAllocation> Reservations = [];
        public readonly HashSet<Key> Restoring = [];
        public readonly Dictionary<ChildRequest, float> Requests = [];
        public readonly HashSet<ChildRequest> Groups = [], Anchors = [];
        public bool Started;
        public long Failures, RestoredFrame, RestoredEpoch;
    }

    private Recovery? _recovery;
    private readonly HashSet<ChildRequest> _recoveryIncoming = [];
    private readonly Dictionary<PbrGpuSelection, (long Frame, long Epoch)> _recoveryFeedback = [];
    private long _recoveryEpoch;
    private bool _recoveryCpuInput;
    private DateTime _recoveryRetry;

    internal void ObserveGpuFeedback(PbrGpuSelection source, long frame, long epoch)
        => _recoveryFeedback[source] = (frame, epoch);

    private void TryBeginRecovery(Key target)
    {
        if (_recovery is not null || _upload is not null || _relocation is not null
            || _relocationReservation is not null || DateTime.UtcNow < _recoveryRetry) return;
        if (!_recoveryCpuInput && !_recoveryFeedback.Values.Any(feedback =>
            feedback.Frame >= 0 && _clock - feedback.Frame <= 8)) return;
        // End still owns this frame's prepared addresses. Withdraw only at the
        // next Begin, after those draws have been submitted or discarded.
        _recovery = new(target);
    }

    private void BeginRecoveryFrame()
    {
        _recoveryIncoming.Clear();
        _recoveryFeedback.Clear();
        _recoveryCpuInput = false;
        if (_recovery is not { Started: false } recovery) return;
        if (!IsAdmitted(recovery.Target) || !_wanted.ContainsKey(recovery.Target)
            || _resident.ContainsKey(recovery.Target)) { _recovery = null; return; }
        var victims = PlanRecovery(recovery.Target);
        if (victims is null || !CaptureRecoveryRequests(recovery, victims)) {
            _recovery = null;
            _recoveryRetry = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        recovery.Started = true;
        _budgetHasSignature = false;
        recovery.Failures = _requests.Statistics.Failed;
        recovery.Restoring.Add(recovery.Target);
        foreach (var key in victims)
            if (IsAdmitted(key)) recovery.Restoring.Add(key);
        foreach (var key in victims) {
            _arena.Free(_resident[key].Allocation);
            _resident.Remove(key);
            _evictions++;
            Changed(key);
        }
        try {
            // The pure plan proves one contiguous region in each pool can hold
            // the entire restoration set. Reserve all pairs before any upload;
            // installing the target first cannot consume its ancestors' space.
            ReserveRecovery(recovery.Target);
            foreach (var key in recovery.Restoring)
                if (key != recovery.Target) ReserveRecovery(key);
        }
        catch { AbortRecovery(false); throw; }
        _recoveryEpoch++;

        void ReserveRecovery(Key key)
        {
            var page = _pages[key.Page];
            if (!_arena.TryReserve((uint)page.Vertices, (uint)page.Triangles, out var allocation))
                throw new InvalidOperationException("Geometry recovery lost its complete restoration proof.");
            recovery.Reservations.Add(key, allocation);
        }
    }

    private bool CaptureRecoveryRequests(Recovery recovery, Key[] victims)
    {
        var removed = victims.ToHashSet();
        var groups = new Dictionary<ChildRequest, List<KeyValuePair<ChildRequest, float>>>();
        foreach (var pair in _budgetPreviousRequests) {
            var parent = _source.Hierarchies[pair.Key.Asset].Nodes[pair.Key.Node].Parent;
            var group = pair.Key with { Node = parent };
            if (!groups.TryGetValue(group, out var children)) groups.Add(group, children = []);
            children.Add(pair);
        }
        // Retain only previously admitted complete groups, never raw denied
        // descendants. Their union already fits both original capacities.
        var eligible = groups.Where(group => group.Value.All(child =>
            _source.Hierarchies[child.Key.Asset].Nodes[child.Key.Node].Pages.All(part =>
                IsAdmitted(PageKey(child.Key.Instance, part.Id)))))
            .ToDictionary(group => group.Key, group => group.Value);
        foreach (var pair in _budgetPreviousRequests) {
            var child = pair.Key;
            var tree = _source.Hierarchies[child.Asset];
            ChildRequest? frontier = null;
            for (var node = child.Node; tree.Nodes[node].Parent >= 0; node = tree.Nodes[node].Parent) {
                if (tree.Nodes[node].Pages.Any(part => removed.Contains(PageKey(child.Instance, part.Id))
                    || PageKey(child.Instance, part.Id) == recovery.Target)) frontier = child with { Node = node };
            }
            if (frontier is null || !Include(child with { Node = tree.Nodes[child.Node].Parent })) continue;
            recovery.Anchors.Add(frontier.Value);
        }
        var leased = recovery.Requests.Keys.SelectMany(child =>
            _source.Hierarchies[child.Asset].Nodes[child.Node].Pages.Select(part =>
                PageKey(child.Instance, part.Id))).ToHashSet();
        return recovery.Anchors.Count != 0 && leased.Contains(recovery.Target)
            && victims.Where(IsAdmitted).All(leased.Contains);

        bool Include(ChildRequest group)
        {
            if (recovery.Groups.Contains(group)) return true;
            if (!eligible.TryGetValue(group, out var children)) return false;
            var parent = _source.Hierarchies[group.Asset].Nodes[group.Node].Parent;
            if (parent >= 0 && (!_budgetPreviousRequests.ContainsKey(group)
                || !Include(group with { Node = parent }))) return false;
            recovery.Groups.Add(group);
            foreach (var child in children) recovery.Requests[child.Key] = child.Value;
            return true;
        }
    }

    private void PrepareRecoveryEnd()
    {
        if (_recovery is not { Started: true } recovery) return;
        // Raw input, not injected admission/wanted state, keeps this finite batch
        // alive. A visible coarse frontier still requests its direct children.
        if (!recovery.Anchors.Overlaps(_recoveryIncoming)
            || recovery.Restoring.Any(key => !IsAdmitted(key) || !_wanted.ContainsKey(key))) {
            var restoring = recovery.Restoring.ToArray();
            AbortRecovery(true);
            foreach (var key in restoring) _wanted.Remove(key);
            return;
        }
        if (recovery.RestoredEpoch == 0 || _clock <= recovery.RestoredFrame) return;
        // Every selection prepared in this owner frame must have observed the
        // restored mapping. Hidden/withdrawn views do not retain a stale lease.
        if (_clock - recovery.RestoredFrame <= 8 && _recoveryFeedback.Values.Any(feedback =>
            feedback.Epoch < recovery.RestoredEpoch || feedback.Frame < recovery.RestoredFrame
            || _clock - feedback.Frame > 8)) return;
        // Missing/dropped feedback cannot keep a completed batch pinned forever.
        // After the same bounded owner-frame grace, ordinary raw input takes over;
        // unreported branches retain the normal resident-parent fallback policy.
        _recovery = null;
        _budgetHasSignature = false;
    }

    private bool RecoveryWants(Key key)
        => _recovery is not { Started: true, RestoredEpoch: 0 } recovery || recovery.Restoring.Contains(key);

    private void CompleteRecoveryFrame()
    {
        if (_recovery is not { Started: true, RestoredEpoch: 0 } recovery
            || recovery.Reservations.Count != 0 || recovery.Restoring.Any(key => !_resident.ContainsKey(key))) return;
        recovery.RestoredFrame = _clock;
        recovery.RestoredEpoch = ++_recoveryEpoch;
    }

    private void MergeRecoveryRequests()
    {
        if (_recovery is not { Started: true } recovery) return;
        // Freeze only the affected complete groups for this finite batch. A new
        // sibling in the same parent group cannot enlarge its promised cost and
        // strand held ranges. Its raw request resumes after handoff; current
        // traversal safely uses the parent until the whole visible group fits.
        foreach (var child in _budgetRequests.Keys.Where(child =>
            !recovery.Requests.ContainsKey(child) && recovery.Groups.Contains(child with {
                Node = _source.Hierarchies[child.Asset].Nodes[child.Node].Parent })).ToArray())
            _budgetRequests.Remove(child);
        foreach (var pair in recovery.Requests)
            if (!_budgetRequests.TryGetValue(pair.Key, out var previous) || pair.Value > previous)
                _budgetRequests[pair.Key] = pair.Value;
    }

    private void AbortRecovery(bool cancelReads)
    {
        if (_recovery is not { } recovery) return;
        if (_upload is { } upload && recovery.Restoring.Contains(upload.Key)) {
            _arena.Abort(upload.Upload);
            _upload = null;
        }
        foreach (var allocation in recovery.Reservations.Values) _arena.Free(allocation);
        recovery.Reservations.Clear();
        _recovery = null;
        _budgetHasSignature = false;
        _recoveryEpoch++;
        _recoveryRetry = DateTime.UtcNow.AddSeconds(1);
        if (cancelReads) {
            _requests.Update([]);
            _prepared.Clear();
        }
    }
}
