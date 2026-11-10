using System.Runtime.InteropServices;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrStreamResidency
{
    internal readonly record struct BudgetPage(int Instance, int Page, float Priority);
    internal readonly record struct BudgetCounts(int Denied, int MissingAncestors, int VertexDenied, int TriangleDenied, long PageVisits);

    // Numeric scratch belongs to the residency owner. Planning has no GPU/IO
    // effects; only the completed result is copied into its publication map.
    internal sealed class BudgetWorkspace
    {
        private readonly record struct CachedPage(int Page, int Visits);
        private sealed class Group(ChildRequest key, int slot, int parent)
        {
            internal readonly ChildRequest Key = key;
            internal readonly int Slot = slot, ParentNode = parent;
            internal readonly List<ChildRequest> Children = [];
            internal ChildRequest[] CachedChildren = [];
            internal CachedPage[] Pages = [];
            internal int TrailingVisits;
            internal Group? Parent;
            internal long Frame;
            internal float Priority;
            internal bool MainView;
            internal bool Recovery;
        }

        private readonly PbrGeometryAdmissionLayout _layout;
        private readonly PbrSceneStream.PageInfo[] _pages;
        private readonly bool _shared;
        private readonly Group?[] _sharedGroups;
        private readonly Dictionary<ChildRequest, Group> _groups = [];
        private List<Group> _order = [], _frameGroups = [];
        private readonly List<Group> _closure = [];
        private readonly List<BudgetPage> _result = [], _extra = [];
        private readonly List<CachedPage> _cacheScratch = [];
        private readonly HashSet<ChildRequest> _selected = [];
        private readonly HashSet<(int Instance, int Page)> _planKeys = [], _groupKeys = [];
        private readonly PbrAdmissionEpochSet _requested, _selectedNodes, _planPages, _groupPages, _cachePages;
        private readonly float[] _priorities;
        private bool _denseSignature;
        private static readonly Comparison<Group> s_Compare = Compare;

        internal long PageCacheBuilds { get; private set; }
        internal long Sorts { get; private set; }
        internal long CachedPageVisits { get; private set; }
        internal ReadOnlySpan<BudgetPage> Result => CollectionsMarshal.AsSpan(_result);

        internal BudgetWorkspace(PbrSceneStream.HierarchyInfo[] hierarchies, ReadOnlySpan<int> canonicalPages,
            PbrSceneStream.PageInfo[] pages, bool shared)
        {
            _layout = new(hierarchies, canonicalPages); _pages = pages; _shared = shared;
            var nodes = shared ? _layout.NodeCount : 0;
            _sharedGroups = new Group?[nodes]; _priorities = new float[nodes];
            _requested = new(nodes); _selectedNodes = new(nodes);
            _planPages = new(shared ? pages.Length : 0); _groupPages = new(shared ? pages.Length : 0);
            _cachePages = new(pages.Length);
        }

        internal bool SameRequests(Dictionary<ChildRequest, float> requests, Dictionary<ChildRequest, float> previous)
        {
            if (requests.Count != previous.Count) return false;
            foreach (var pair in requests) {
                if (_denseSignature) {
                    if (pair.Key.Instance != -1) return false;
                    var slot = _layout.Node(pair.Key.Asset, pair.Key.Node);
                    if (!_requested.Contains(slot) || pair.Value != _priorities[slot]) return false;
                }
                else if (!previous.TryGetValue(pair.Key, out var priority) || pair.Value != priority) return false;
            }
            return true;
        }

        internal BudgetCounts Plan(Dictionary<ChildRequest, float> requests, ReadOnlySpan<BudgetPage> roots,
            ulong rootVertices, ulong rootTriangles, uint vertexCapacity, uint triangleCapacity, long frame,
            HashSet<ChildRequest>? recoveryGroups = null, IReadOnlySet<ChildRequest>? mainRequests = null)
        {
            if (rootVertices > vertexCapacity || rootTriangles > triangleCapacity)
                throw new InvalidOperationException("Budget roots and transparent bootstrap exceed the geometry arena.");
            _result.Clear(); _frameGroups.Clear();
            _denseSignature = _shared;
            if (_shared) { _requested.Begin(); _selectedNodes.Begin(); _planPages.Begin(); }
            else { _selected.Clear(); _planKeys.Clear(); }
            foreach (var root in roots) {
                _result.Add(root with { Priority = 0 });
                if (_shared) _planPages.Add(root.Page); else _planKeys.Add((root.Instance, root.Page));
            }
            foreach (var pair in requests) {
                var child = pair.Key;
                if (_shared) {
                    var slot = _layout.Node(child.Asset, child.Node);
                    _requested.Add(slot); _priorities[slot] = pair.Value;
                    _denseSignature &= child.Instance == -1;
                }
                var parent = _layout.ParentLocal(child.Asset, child.Node);
                if (parent < 0) throw new InvalidDataException("A geometry child request must have a parent.");
                var group = Find(child with { Node = parent }, true)!;
                if (group.Frame != frame) {
                    group.Frame = frame; group.Priority = 0;
                    group.MainView = false;
                    group.Recovery = recoveryGroups?.Contains(group.Key) ?? false;
                    group.Children.Clear(); _frameGroups.Add(group);
                }
                group.Children.Add(child);
                var main = mainRequests?.Contains(child) ?? false;
                if (main && !group.MainView) { group.MainView = true; group.Priority = pair.Value; }
                else if (main == group.MainView) group.Priority = System.Math.Max(group.Priority, pair.Value);
            }
            // A visible descendant can only replace its fallback after its
            // complete observed ancestor groups are admitted too. Promote that
            // dependency closure before ranking unrelated shadow-only groups.
            foreach (var group in _frameGroups) {
                if (!group.MainView) continue;
                var ancestor = group;
                while (ancestor.ParentNode >= 0) {
                    var observed = _denseSignature ? _requested.Contains(ancestor.Slot) : requests.ContainsKey(ancestor.Key);
                    if (!observed) break;
                    var dependency = ancestor.Parent ??= Find(ancestor.Key with { Node = ancestor.ParentNode });
                    if (dependency is null || dependency.Frame != frame) break;
                    if (!dependency.MainView) { dependency.MainView = true; dependency.Priority = group.Priority; }
                    else dependency.Priority = System.Math.Max(dependency.Priority, group.Priority);
                    ancestor = dependency;
                }
            }
            foreach (var group in _frameGroups) CachePages(group);
            if (!CanReuseOrder(frame)) {
                _frameGroups.Sort(s_Compare); Sorts++;
                (_order, _frameGroups) = (_frameGroups, _order);
            }
            var vertices = rootVertices; var triangles = rootTriangles;
            var denied = 0; var missing = 0; var vertexDenied = 0; var triangleDenied = 0; long visits = 0;
            foreach (var group in _order) {
                if (Selected(group)) continue;
                _closure.Clear();
                var ancestor = group; var complete = true;
                while (!Selected(ancestor)) {
                    _closure.Add(ancestor);
                    if (ancestor.ParentNode < 0) break;
                    var dependency = ancestor.Parent ??= Find(ancestor.Key with { Node = ancestor.ParentNode });
                    var observed = _denseSignature ? _requested.Contains(ancestor.Slot) : requests.ContainsKey(ancestor.Key);
                    if (!observed || dependency is null || dependency.Frame != frame) { complete = false; break; }
                    ancestor = dependency;
                }
                if (!complete) { denied++; missing++; continue; }
                if (!Collect(vertexCapacity - vertices, triangleCapacity - triangles,
                    out var extraVertices, out var extraTriangles, ref visits)) {
                    denied++;
                    if (extraVertices > vertexCapacity - vertices) vertexDenied++;
                    if (extraTriangles > triangleCapacity - triangles) triangleDenied++;
                    continue;
                }
                vertices += extraVertices; triangles += extraTriangles;
                foreach (var page in _extra) {
                    _result.Add(page with { Priority = group.Priority });
                    if (_shared) _planPages.Add(page.Page); else _planKeys.Add((page.Instance, page.Page));
                }
                foreach (var dependency in _closure)
                    if (_shared) _selectedNodes.Add(dependency.Slot); else _selected.Add(dependency.Key);
            }
            return new(denied, missing, vertexDenied, triangleDenied, visits);
        }

        private Group? Find(ChildRequest key, bool create = false)
        {
            var slot = _layout.Node(key.Asset, key.Node);
            if (_shared) {
                var group = _sharedGroups[slot];
                if (group is null && create)
                    _sharedGroups[slot] = group = new(key, slot, _layout.ParentLocal(key.Asset, key.Node));
                return group;
            }
            if (!_groups.TryGetValue(key, out var found) && create)
                _groups.Add(key, found = new(key, slot, _layout.ParentLocal(key.Asset, key.Node)));
            return found;
        }

        private bool Selected(Group group) => _shared ? _selectedNodes.Contains(group.Slot) : _selected.Contains(group.Key);

        private static int Compare(Group left, Group right)
        {
            var recovery = right.Recovery.CompareTo(left.Recovery); if (recovery != 0) return recovery;
            var main = right.MainView.CompareTo(left.MainView); if (main != 0) return main;
            var priority = right.Priority.CompareTo(left.Priority); if (priority != 0) return priority;
            var instance = left.Key.Instance.CompareTo(right.Key.Instance); if (instance != 0) return instance;
            var asset = left.Key.Asset.CompareTo(right.Key.Asset); return asset != 0 ? asset : left.Key.Node.CompareTo(right.Key.Node);
        }

        private bool CanReuseOrder(long frame)
        {
            if (_order.Count != _frameGroups.Count) return false;
            for (var i = 0; i < _order.Count; i++)
                if (_order[i].Frame != frame || (i != 0 && Compare(_order[i - 1], _order[i]) > 0)) return false;
            return true;
        }

        private void CachePages(Group group)
        {
            var children = CollectionsMarshal.AsSpan(group.Children);
            if (children.SequenceEqual(group.CachedChildren)) return;
            group.CachedChildren = children.ToArray();
            _cacheScratch.Clear(); _cachePages.Begin(); var visits = 0;
            foreach (var child in children)
                foreach (var page in _layout.Pages(child.Asset, child.Node)) {
                    visits++;
                    if (!_cachePages.Add(page)) continue;
                    _cacheScratch.Add(new(page, visits)); visits = 0;
                }
            group.Pages = _cacheScratch.ToArray(); group.TrailingVisits = visits; PageCacheBuilds++;
        }

        private bool Collect(ulong vertexLimit, ulong triangleLimit, out ulong vertices, out ulong triangles, ref long visits)
        {
            _extra.Clear();
            if (_shared) _groupPages.Begin(); else _groupKeys.Clear();
            vertices = triangles = 0;
            foreach (var group in _closure) {
                foreach (var cached in group.Pages) {
                    // Visit weights preserve the original logical counter and
                    // its early-failure prefix despite cached duplicate removal.
                    visits += cached.Visits; CachedPageVisits++;
                    var page = cached.Page; var instance = _shared ? -1 : group.Key.Instance;
                    if (_shared) { if (_planPages.Contains(page) || !_groupPages.Add(page)) continue; }
                    else if (_planKeys.Contains((instance, page)) || !_groupKeys.Add((instance, page))) continue;
                    _extra.Add(new(instance, page, 0));
                    vertices = checked(vertices + (ulong)_pages[page].Vertices);
                    triangles = checked(triangles + (ulong)_pages[page].Triangles);
                    if (vertices > vertexLimit || triangles > triangleLimit) return false;
                }
                visits += group.TrailingVisits;
            }
            return true;
        }
    }
}
