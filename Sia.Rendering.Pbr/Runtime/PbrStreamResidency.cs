using Sia.Math;
using Sia.Engine.Mesh;
using System.Runtime.CompilerServices;

namespace Sia.Engine.Rendering.Pbr;

public sealed record PbrStreamingSettings
{
    public bool GpuTraversal { get; init; } = true;
    public ulong HierarchyBytes { get; init; } = 64ul * 1024 * 1024;
    public ulong DetailBytes { get; init; } = 64ul * 1024 * 1024;
    public uint UploadBytesPerFrame { get; init; } = 256 * 1024;
    public int MaximumRequests { get; init; } = 2;
    public long DecodedBytes { get; init; } = 2 * StreamGeometryPage.MaximumBytes;
    public int MaximumSelectionNodesPerView { get; init; } = 4096;
}

public readonly record struct PbrStreamingStatistics(
    ulong AllocatedBytes,
    ulong UsedBytes,
    long RootBytes,
    long StagingBytes,
    uint UploadedBytes,
    int ResidentPages,
    long Installs,
    long Evictions,
    ulong Revision,
    PageSchedulerStatistics Requests,
    int DeferredGroups);

internal sealed partial class PbrStreamResidency : IAsyncDisposable
{
    private readonly record struct Key(int Instance, int Page);

    private sealed class Resident(StreamPageAllocation allocation, bool root, long lastUsed)
    {
        public readonly StreamPageAllocation Allocation = allocation;
        public readonly bool Root = root;
        public long LastUsed = lastUsed;
    }

    private sealed record Pending(Key Key, StreamPageArena.Upload Upload);
    private sealed record LeafRoot(Key Key, Aabb Bounds, PbrGpuScene.Draw[] Draws);
    private readonly record struct View(float4x4 Projection, uint Width, uint Height, float Error);
    private sealed record Cut(
        ulong Revision,
        long Frame,
        PbrGpuScene.Draw[] Draws,
        Key[] Used,
        KeyValuePair<Key, float>[] Wanted,
        bool Complete = true);
    private readonly record struct ReadyCandidate(Key Key, float Priority, long Order);
    private readonly record struct EvictionCandidate(Key Key, Resident Page, int Order);

    private sealed class BranchGroup(int instance, int[] roots, Aabb bounds)
    {
        public readonly int Instance = instance;
        public readonly int[] Roots = roots;
        public readonly Aabb Bounds = bounds;
        public ulong Revision;
        public PbrGpuScene.Draw[] Fallback = [];
        public Key[] RootPages = [];
    }

    private readonly PbrSceneStream _source;
    private readonly PbrSceneStream.PageInfo[] _pages;
    private readonly Dictionary<string, int> _pageIndices;
    private readonly StreamPageArena _arena;
    private readonly PbrStreamingSettings _settings;
    private readonly BoundedPageScheduler _requests;
    private readonly Dictionary<Key, Resident> _resident = [];
    private readonly Dictionary<Key, float> _wanted = [];
    private readonly HashSet<Key> _used = [];
    private readonly Dictionary<int, (float Priority, int Remaining)> _missingPages = [];
    private readonly Dictionary<int, int> _readyPages = [];
    private readonly List<StreamPageDemand> _demands = [];
    private readonly List<ReadyCandidate> _ready = [];
    private readonly List<EvictionCandidate> _evictionCandidates = [];
    private readonly List<PbrGpuScene.Draw> _roots = [];
    private readonly float[] _norms;
    private readonly Aabb[][] _bounds;
    private readonly Aabb[] _instanceBounds;
    private BranchGroup[][] _branches = [];
    private LeafRoot[][] _leafRoots = [];
    private bool _cpuSelectionInitialized;
    private readonly Dictionary<View, Cut> _cuts = [];
    private readonly Dictionary<View, Dictionary<BranchGroup, Cut>> _groupCuts = [];
    private readonly Dictionary<Key, HashSet<BranchGroup>> _dependencies = [];
    private HashSet<Key>? _viewUsed;
    private Dictionary<Key, float>? _viewWanted;
    private Pending? _upload;
    private long _clock;
    private long _installs, _evictions;
    private readonly long _rootBytes;
    private uint _uploaded;
    private int _deferredGroups;
    private bool _stopped;
    private Task? _stopTask;

    public ulong Revision { get; private set; }

    public PbrGpuScene.Draw[] RootDraws => [.. _roots];

    public PbrStreamingStatistics Statistics => new(
        _arena.Bytes, _arena.UsedBytes, _rootBytes, _arena.StagingBytes, _uploaded,
        _resident.Count, _installs, _evictions, Revision, _requests.Statistics, _deferredGroups);

    internal bool IsStopped => _stopped;

    public PbrStreamResidency(PbrSceneStream source, StreamPageArena arena, PbrStreamingSettings settings, int[] materialBatches)
    {
        if (settings.UploadBytesPerFrame < 16 || settings.UploadBytesPerFrame % 16 != 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Streaming upload budget must be positive and 16-byte aligned.");
        if (settings.MaximumSelectionNodesPerView < 1024)
            throw new ArgumentOutOfRangeException(nameof(settings), "Selection must allow at least one complete packed root group.");
        (_source, _arena, _settings) = (source, arena, settings);
        _pages = [.. source.PageTable.Values];
        _pageIndices = _pages.Select((p, i) => (p.Id, Index: i)).ToDictionary(p => p.Id, p => p.Index, StringComparer.Ordinal);
        _requests = new(source.ReadPageAsync, settings.MaximumRequests, settings.DecodedBytes);
        _norms = new float[source.Instances.Length];
        for (var i = 0; i < _norms.Length; i++) _norms[i] = PbrGpuScene.TransformNorm(source.Instances.Span[i].Transform);
        _bounds = new Aabb[source.Instances.Length][];
        _instanceBounds = new Aabb[source.Instances.Length];
        var order = Enumerable.Range(0, source.Instances.Length)
            .OrderBy(i => source.Bootstrap.Materials.Span[source.Instances.Span[i].MaterialIndex].DoubleSided)
            .ThenBy(i => materialBatches[source.Instances.Span[i].MaterialIndex]);
        foreach (var i in order) {
            var instance = source.Instances.Span[i];
            var tree = source.Hierarchies![instance.AssetIndex];
            if (!settings.GpuTraversal)
                _bounds[i] = [.. tree.Nodes.Select(n => BoundsTransform.Apply(PbrSceneStream.NodeBounds(n), instance.Transform))];
            var minimum = new float3(float.PositiveInfinity);
            var maximum = new float3(float.NegativeInfinity);
            foreach (var node in tree.Nodes.AsSpan(0, tree.Roots)) {
                var bounds = BoundsTransform.Apply(PbrSceneStream.NodeBounds(node), instance.Transform);
                minimum = math.min(minimum, bounds.Min);
                maximum = math.max(maximum, bounds.Max);
            }
            _instanceBounds[i] = new(minimum, maximum);
            foreach (var node in tree.Nodes.Take(tree.Roots))
                foreach (var part in node.Pages) {
                    var id = part.Id;
                    var key = PageKey(i, id);
                    if (!_resident.ContainsKey(key)) {
                        var page = source.ResidentRoots[id];
                        if (!arena.TryReserve((uint)page.VertexCount, (uint)page.TriangleCount, out var allocation))
                            throw new InvalidOperationException("Resident roots cannot fit in the geometry arena.");
                        var upload = arena.Prepare(page, allocation,
                            settings.GpuTraversal ? float4x4.identity : instance.Transform,
                            settings.GpuTraversal ? 1 : instance.MaterialIndex + 1);
                        while (!upload.Complete) arena.Advance(upload, uint.MaxValue);
                        _resident.Add(key, new(allocation, true, 0));
                        _rootBytes += page.Bytes.Length;
                    }
                    var draw = Draw(i, node, part, _resident[key].Allocation);
                    _roots.Add(draw);
                }
        }
        if (!settings.GpuTraversal) EnsureCpuSelection();
    }

    private void EnsureCpuSelection()
    {
        if (_cpuSelectionInitialized) return;
        _branches = new BranchGroup[_source.Instances.Length][];
        _leafRoots = new LeafRoot[_source.Instances.Length][];
        for (var i = 0; i < _source.Instances.Length; i++) {
            var instance = _source.Instances.Span[i];
            var tree = _source.Hierarchies[instance.AssetIndex];
            _bounds[i] ??= [.. tree.Nodes.Select(n => BoundsTransform.Apply(PbrSceneStream.NodeBounds(n), instance.Transform))];
            _branches[i] = [.. Enumerable.Range(0, tree.Roots).Where(n => tree.Nodes[n].ChildCount > 0)
                .GroupBy(n => tree.Nodes[n].Pages[0].Id, StringComparer.Ordinal)
                .Select(g => new BranchGroup(i, [.. g], Union(g.Select(n => _bounds[i][n]))))];
            var rootGroups = _branches[i].SelectMany(g => g.Roots.Select(n => (Node: n, Group: g))).ToDictionary(p => p.Node, p => p.Group);
            var rootOf = new int[tree.Nodes.Length];
            for (var n = 0; n < tree.Nodes.Length; n++) {
                rootOf[n] = tree.Nodes[n].Parent < 0 ? n : rootOf[tree.Nodes[n].Parent];
                if (!rootGroups.TryGetValue(rootOf[n], out var group)) continue;
                foreach (var part in tree.Nodes[n].Pages) {
                    var key = PageKey(i, part.Id);
                    if (!_dependencies.TryGetValue(key, out var groups)) _dependencies.Add(key, groups = []);
                    groups.Add(group);
                }
            }
            var leaves = new Dictionary<Key, List<PbrGpuScene.Draw>>();
            foreach (var node in tree.Nodes.AsSpan(0, tree.Roots)) {
                if (node.ChildCount != 0) continue;
                foreach (var part in node.Pages) {
                    var key = PageKey(i, part.Id);
                    if (!leaves.TryGetValue(key, out var list)) leaves.Add(key, list = []);
                    list.Add(Draw(i, node, part, _resident[key].Allocation));
                }
            }
            _leafRoots[i] = [.. leaves.Select(p => new LeafRoot(p.Key, Union(p.Value.Select(d => d.Bounds)), Merge(p.Value)))];
            foreach (var group in _branches[i]) {
                group.RootPages = [.. group.Roots.SelectMany(n => tree.Nodes[n].Pages).Select(p => PageKey(i, p.Id)).Distinct()];
                group.Fallback = Merge([.. group.Roots.SelectMany(n => tree.Nodes[n].Pages.Select(p => Draw(i, tree.Nodes[n], p,
                    _resident[PageKey(i, p.Id)].Allocation, _bounds[i][n])))]);
            }
        }
        _cpuSelectionInitialized = true;
    }

    private static Aabb Union(IEnumerable<Aabb> bounds)
    {
        var min = new float3(float.PositiveInfinity);
        var max = new float3(float.NegativeInfinity);
        foreach (var b in bounds) { min = math.min(min, b.Min); max = math.max(max, b.Max); }
        return new(min, max);
    }

    private static PbrGpuScene.Draw[] Merge(List<PbrGpuScene.Draw> draws)
    {
        var result = new List<PbrGpuScene.Draw>();
        foreach (var draw in draws.OrderBy(d => d.First)) {
            if (result.Count > 0 && result[^1].First + result[^1].Count == draw.First)
                result[^1] = result[^1] with { Count = result[^1].Count + draw.Count };
            else result.Add(draw);
        }
        return [.. result];
    }

    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_stopTask is not null, this);
        _clock++;
        _wanted.Clear();
        _used.Clear();
        _uploaded = 0;
        _deferredGroups = 0;
    }

    public void Select(float4x4 vp, uint width, uint height, float error, FrustumCuller culler,
        List<PbrGpuScene.Draw> output)
    {
        EnsureCpuSelection();
        var view = new View(vp, width, height, error);
        if (_cuts.TryGetValue(view, out var cached) && cached.Complete && cached.Revision == Revision) {
            foreach (var key in cached.Used) Touch(key);
            foreach (var pair in cached.Wanted) Demand(pair.Key, pair.Value);
            output.AddRange(cached.Draws);
            _cuts[view] = cached with { Frame = _clock };
            return;
        }
        var start = output.Count;
        var lodProjection = ProjectedGeometryError.PrepareLodProjection(vp, width, height);
        var visited = 0;
        var deferred = false;
        var groupIncomplete = false;
        var cacheGroups = _cuts.ContainsKey(view);
        _viewUsed = cacheGroups ? [] : null;
        _viewWanted = cacheGroups ? [] : null;
        Dictionary<BranchGroup, Cut>? groupCuts = null;
        if (cacheGroups && !_groupCuts.TryGetValue(view, out groupCuts)) _groupCuts.Add(view, groupCuts = []);
        for (var i = 0; i < _source.Instances.Length; i++) {
            if (!culler.Intersects(_instanceBounds[i])) continue;
            var instance = _source.Instances.Span[i];
            var tree = _source.Hierarchies![instance.AssetIndex];
            foreach (var leaf in _leafRoots[i])
                if (culler.Intersects(leaf.Bounds)) { Touch(leaf.Key); output.AddRange(leaf.Draws); }
            foreach (var group in _branches[i]) {
                if (!culler.Intersects(group.Bounds)) continue;
                if (groupCuts is not null && groupCuts.TryGetValue(group, out var cut) && cut.Revision == group.Revision) {
                    foreach (var key in cut.Used) Touch(key);
                    foreach (var pair in cut.Wanted) Demand(pair.Key, pair.Value);
                    output.AddRange(cut.Draws);
                    continue;
                }
                if (visited + group.Roots.Length > _settings.MaximumSelectionNodesPerView) {
                    foreach (var key in group.RootPages) Touch(key);
                    output.AddRange(group.Fallback);
                    _deferredGroups++;
                    deferred = true;
                    continue;
                }
                if (!cacheGroups) {
                    groupIncomplete = false;
                    foreach (var n in group.Roots) Visit(i, tree, n);
                    if (groupIncomplete) _deferredGroups++;
                    continue;
                }
                var allUsed = _viewUsed!;
                var allWanted = _viewWanted;
                _viewUsed = [];
                _viewWanted = [];
                var groupStart = output.Count;
                groupIncomplete = false;
                foreach (var n in group.Roots) Visit(i, tree, n);
                if (!groupIncomplete)
                    groupCuts![group] = new(group.Revision, _clock, [.. output.Skip(groupStart)], [.. _viewUsed], [.. _viewWanted]);
                else _deferredGroups++;
                allUsed.UnionWith(_viewUsed);
                var wanted = _viewWanted;
                _viewUsed = allUsed;
                _viewWanted = allWanted;
                foreach (var pair in wanted) Demand(pair.Key, pair.Value);
            }
        }
        if (!_cuts.ContainsKey(view) && _cuts.Count >= 8) {
            var oldest = _cuts.MinBy(p => p.Value.Frame).Key;
            _cuts.Remove(oldest);
            _groupCuts.Remove(oldest);
        }
        _cuts[view] = cacheGroups
            ? new(Revision, _clock, output.Skip(start).ToArray(), _viewUsed!.ToArray(), _viewWanted!.ToArray(), !deferred)
            : new(Revision, _clock, [], [], [], false);
        _viewUsed = null;
        _viewWanted = null;
        void Visit(int instanceIndex, PbrSceneStream.HierarchyInfo tree, int index)
        {
            var instance = _source.Instances.Span[instanceIndex];
            var node = tree.Nodes[index];
            var bounds = _bounds[instanceIndex][index];
            if (!culler.Intersects(bounds)) return;
            foreach (var part in node.Pages) Touch(PageKey(instanceIndex, part.Id));
            if (++visited > _settings.MaximumSelectionNodesPerView) {
                deferred = true;
                groupIncomplete = true;
                foreach (var part in node.Pages) output.Add(Draw(instanceIndex, node, part, _resident[PageKey(instanceIndex, part.Id)].Allocation, bounds));
                return;
            }
            var raw = node.ChildCount == 0 ? 0
                : tree.ErrorMetric == MeshPatchErrorMetric.Quadric
                    ? ProjectedGeometryError.ProjectLodError(bounds, node.Error * _norms[instanceIndex],
                        lodProjection, vp, width, height,
                        math.length(PbrSceneStream.NodeBounds(node).Max - PbrSceneStream.NodeBounds(node).Min)
                            * .5f * _norms[instanceIndex])
                    : ProjectedGeometryError.ProjectError(bounds, node.Error * _norms[instanceIndex], vp, width, height);
            var projected = float.IsFinite(raw) ? raw : float.MaxValue;
            if (node.ChildCount > 0 && (error == 0 || projected > error)) {
                var complete = true;
                for (var c = node.Children; c < node.Children + node.ChildCount; c++)
                    foreach (var part in tree.Nodes[c].Pages) {
                        var key = PageKey(instanceIndex, part.Id);
                        Demand(key, projected);
                        if (_resident.ContainsKey(key)) Touch(key);
                        else complete = false;
                    }
                if (complete) {
                    for (var c = node.Children; c < node.Children + node.ChildCount; c++) Visit(instanceIndex, tree, c);
                    return;
                }
            }
            foreach (var part in node.Pages) output.Add(Draw(instanceIndex, node, part, _resident[PageKey(instanceIndex, part.Id)].Allocation, bounds));
        }
    }

    private Key PageKey(int instance, string id) => new(_settings.GpuTraversal ? -1 : instance, _pageIndices[id]);

    private void Touch(Key key)
    {
        _viewUsed?.Add(key);
        if (!_used.Add(key)) return;
        if (_resident.TryGetValue(key, out var r)) r.LastUsed = _clock;
    }

    private void Demand(Key key, float priority)
    {
        if (!_wanted.TryGetValue(key, out var old) || priority > old) _wanted[key] = priority;
        if (_viewWanted is { } wanted && (!wanted.TryGetValue(key, out old) || priority > old)) wanted[key] = priority;
    }

    private PbrGpuScene.Draw Draw(int index, PbrSceneStream.NodeInfo node, PbrSceneStream.PagePart part, StreamPageAllocation allocation, Aabb? bounds = null)
    {
        var instance = _source.Instances.Span[index];
        return new(allocation.Triangles.Offset + (uint)part.First, (uint)part.Count, instance.MaterialIndex,
            bounds ?? BoundsTransform.Apply(PbrSceneStream.NodeBounds(node), instance.Transform),
            _source.Bootstrap.Materials.Span[instance.MaterialIndex].DoubleSided,
            (allocation.Triangles.Offset + (uint)part.First) * 3, (uint)index, 0, 0, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void EndFrame()
    {
        if (_stopped) return;
        if (_upload is { } pending && !_wanted.ContainsKey(pending.Key)) {
            _arena.Abort(pending.Upload);
            _upload = null;
        }
        _missingPages.Clear();
        _demands.Clear();
        foreach (var pair in _wanted) {
            if (_resident.ContainsKey(pair.Key)) continue;
            if (_missingPages.TryGetValue(pair.Key.Page, out var group))
                _missingPages[pair.Key.Page] = (System.Math.Max(group.Priority, pair.Value), group.Remaining + 1);
            else _missingPages.Add(pair.Key.Page, (pair.Value, 1));
        }
        foreach (var pair in _missingPages)
            _demands.Add(new(_pages[pair.Key].Id, _pages[pair.Key].Bytes, pair.Value.Priority));
        _requests.Update(_demands);
        // Sort only the bounded ready set, rather than every demanded page each upload.
        _ready.Clear();
        _readyPages.Clear();
        foreach (var id in _requests.ReadyIds) _readyPages.Add(_pageIndices[id], _readyPages.Count);
        uint order = 0;
        if (_readyPages.Count != 0) {
            foreach (var pair in _wanted) {
                if (_readyPages.TryGetValue(pair.Key.Page, out var pageOrder) && !_resident.ContainsKey(pair.Key))
                    _ready.Add(new(pair.Key, pair.Value, ((long)pageOrder << 32) | order));
                order++;
            }
        }
        _ready.Sort(static (a, b) => {
            var priority = b.Priority.CompareTo(a.Priority);
            if (priority != 0) return priority;
            var instance = a.Key.Instance.CompareTo(b.Key.Instance);
            return instance != 0 ? instance : a.Order.CompareTo(b.Order);
        });
        var readyIndex = 0;
        while (_uploaded < _settings.UploadBytesPerFrame) {
            if (_upload is null) {
                while (readyIndex < _ready.Count) {
                    var candidate = _ready[readyIndex++];
                    if (_resident.ContainsKey(candidate.Key) || !_requests.TryGetReady(_pages[candidate.Key.Page].Id, out var page))
                        continue;
                    if (!Reserve(page!, out var allocation)) continue;
                    var transform = float4x4.identity;
                    var material = 1;
                    if (!_settings.GpuTraversal) {
                        var instance = _source.Instances.Span[candidate.Key.Instance];
                        transform = instance.Transform;
                        material = instance.MaterialIndex + 1;
                    }
                    _upload = new(candidate.Key, _arena.Prepare(page!, allocation, transform, material));
                    break;
                }
                if (_upload is null) break;
            }
            var active = _upload;
            _uploaded += _arena.Advance(active.Upload, _settings.UploadBytesPerFrame - _uploaded);
            if (!active.Upload.Complete) break;
            _resident.Add(active.Key, new(active.Upload.Allocation, false, _clock));
            _installs++;
            Changed(active.Key);
            _upload = null;
            var (Priority, Remaining) = _missingPages[active.Key.Page];
            _missingPages[active.Key.Page] = (Priority, Remaining - 1);
            if (Remaining == 1) _requests.Acknowledge(_pages[active.Key.Page].Id);
        }
    }

    private bool Reserve(StreamGeometryPage page, out StreamPageAllocation allocation)
    {
        if (_arena.TryReserve((uint)page.VertexCount, (uint)page.TriangleCount, out allocation)) return true;
        _evictionCandidates.Clear();
        foreach (var pair in _resident)
            if (!pair.Value.Root && !_used.Contains(pair.Key) && !_wanted.ContainsKey(pair.Key))
                _evictionCandidates.Add(new(pair.Key, pair.Value, _evictionCandidates.Count));
        _evictionCandidates.Sort(static (a, b) => {
            var age = a.Page.LastUsed.CompareTo(b.Page.LastUsed);
            return age != 0 ? age : a.Order.CompareTo(b.Order);
        });
        var reserved = false;
        foreach (var candidate in _evictionCandidates) {
            _arena.Free(candidate.Page.Allocation);
            _resident.Remove(candidate.Key);
            _evictions++;
            Changed(candidate.Key);
            if (_arena.TryReserve((uint)page.VertexCount, (uint)page.TriangleCount, out allocation)) {
                reserved = true;
                break;
            }
        }
        _evictionCandidates.Clear();
        return reserved;
    }

    private void Changed(Key key)
    {
        if (_gpuSlots is not null && _gpuSlots.TryGetValue(key, out var slot)) _gpuDirty.Add(slot);
        Revision++;
        if (_dependencies.TryGetValue(key, out var groups)) foreach (var group in groups) group.Revision++;
    }

    public ValueTask DisposeAsync() => new(_stopTask ??= StopAsync());

    private async Task StopAsync()
    {
        await _requests.DisposeAsync();
        if (_upload is { } p) _arena.Abort(p.Upload);
        _upload = null;
        _stopped = true;
    }
}
