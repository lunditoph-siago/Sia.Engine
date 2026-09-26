using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct VisibilityLodSettings(float TargetPixelError, MeshPatchBudget Budget)
{
    public VisibilityShadowLodSettings? Shadows { get; init; }
    /// <summary>Root-only rendering keeps one root in each fixed interval. This deliberately sparse mode is for extreme low-quality budgets.</summary>
    public uint RootCutStride { get; init; } = 1;
}

public readonly record struct VisibilityShadowLodSettings(float TargetPixelError, MeshPatchBudget Budget);

public sealed partial class VisibilityPbrFeature
{
    private bool RootCutOnly => _gpuLod is not null
        && _lod.Budget.MaxRefinementCandidates == 0 && _lod.Budget.MaxRefinementNodes == 0;

    private bool CpuRootCut => _staticRootCut || (_patchTree is not null && UseStaticRootCut(_lod));

    private static bool UseStaticRootCut(VisibilityLodSettings lod) => lod.RootCutStride >= 16
        && lod.Budget.MaxRefinementCandidates == 0 && lod.Budget.MaxRefinementNodes == 0;

    private static uint WorkCapacity(MeshPatchTree? tree, uint triangles, uint instances, MeshPatchBudget budget)
    {
        if (tree is null) { return checked(triangles * instances); }
        ulong roots = 0;
        foreach (var root in tree.Nodes.Span[..tree.RootCount]) { roots += (uint)root.TriangleCount; }
        var finest = (ulong)tree.FinestTriangleCount * instances;
        return checked((uint)System.Math.Min(finest, System.Math.Max(roots * instances, (uint)budget.MaxTriangles)));
    }

    public MeshPatchSelection? GetLodSelection(RenderView view)
    {
        var state = view.PersistentResources.GetRequired<ViewState>();
        if (!ReferenceEquals(state.Owner, this)) { throw new InvalidOperationException("The view belongs to another visibility feature."); }
        return state.Selection;
    }

    private void UpdateWork(ViewState view, in float4x4 viewProjection)
    {
        var count = 0;
        MeshPatchSelection? nextSelection = null;
        if (_fixedGeometry is { } geometry) {
            view.WorkCount = checked(geometry.Count * geometry.Stride);
            return;
        }
        else if (CpuRootCut) {
            if (view.WorkInitialized && view.WorkVersion == _instanceVersion) return;
            if (_instanceWorld is not null) {
                var snapshot = _preparedInstances ?? throw new InvalidOperationException("The extracted root-cut scene is unavailable.");
                for (var instance = 0; instance < snapshot.Entities.Length; instance++) {
                    if (snapshot.Entities[instance] is null) continue;
                    var asset = checked((int)snapshot.Instances[instance].Roots.w);
                    foreach (var triangle in _rootCutTrianglesByAsset[asset]) {
                        if (count == view.WorkItems.Length) throw new InvalidOperationException("The sparse root cut exceeds its reserved work list.");
                        view.WorkItems[count++] = new(triangle, (uint)instance);
                    }
                }
            } else if (_patchTree is { } rootTree) {
                for (uint instance = 0; instance < _transforms.Length; instance++) {
                    for (var root = 0; root < rootTree.RootCount; root++) {
                        if ((uint)root % _lod.RootCutStride != 0) continue;
                        var patch = rootTree.Nodes.Span[root];
                        for (var triangle = patch.TriangleOffset; triangle < patch.TriangleOffset + patch.TriangleCount; triangle++) {
                            if (count == view.WorkItems.Length) throw new InvalidOperationException("The sparse root cut exceeds its reserved work list.");
                            view.WorkItems[count++] = new((uint)triangle, instance);
                        }
                    }
                }
            } else {
                throw new InvalidOperationException("A static root cut requires a patch tree or extracted scene assets.");
            }
            var staticQueue = _queue.GetWgpu<WGPUQueue>();
            if (count > 0) Wgpu.WriteBuffer<WorkGpu>(staticQueue, view.WorkBuffer.GetWgpu<WGPUBuffer>(), 0, view.WorkItems.AsSpan(0, count));
            Wgpu.WriteBuffer<uint>(staticQueue, view.Indirect.GetWgpu<WGPUBuffer>(), 0, [checked((uint)count * 3u), 1, 0, 0]);
            Wgpu.WriteBuffer<uint>(staticQueue, view.Indirect.GetWgpu<WGPUBuffer>(), 12u * sizeof(uint), [(uint)count]);
            view.WorkCount = (uint)count;
            view.Selection = null;
            view.WorkVersion = _instanceVersion;
            view.WorkInitialized = true;
            return;
        }
        else if (_patchTree is { } tree) {
            var matrices = new float4x4[_transforms.Length];
            for (var i = 0; i < matrices.Length; i++) { matrices[i] = math.mul(viewProjection, _transforms[i]); }
            var selection = MeshPatchSelector.Select(tree, matrices, view.Width, view.Height, _lod.TargetPixelError, _lod.Budget);
            var unchanged = view.Selection is { } previous && previous.Patches.Span.SequenceEqual(selection.Patches.Span);
            if (unchanged) { view.Selection = selection; return; }
            nextSelection = selection;
            foreach (var selected in selection.Patches.Span) {
                var patch = tree.Nodes.Span[selected.Patch];
                for (var triangle = patch.TriangleOffset; triangle < patch.TriangleOffset + patch.TriangleCount; triangle++) {
                    view.WorkItems[count++] = new WorkGpu((uint)triangle, (uint)selected.Instance);
                }
            }
        }
        else {
            if (view.WorkInitialized) { return; }
            for (uint instance = 0; instance < InstanceCount; instance++) {
                for (uint triangle = 0; triangle < TriangleCount; triangle++) {
                    view.WorkItems[count++] = new WorkGpu(triangle, instance);
                }
            }
        }
        var queue = _queue.GetWgpu<WGPUQueue>();
        Wgpu.WriteBuffer<WorkGpu>(queue, view.WorkBuffer.GetWgpu<WGPUBuffer>(), 0, view.WorkItems.AsSpan(0, count));
        Wgpu.WriteBuffer<uint>(queue, view.Indirect.GetWgpu<WGPUBuffer>(), 0, [checked((uint)count * 3u), 1, 0, 0]);
        view.WorkCount = (uint)count;
        view.Selection = nextSelection;
        view.WorkInitialized = true;
    }
}
