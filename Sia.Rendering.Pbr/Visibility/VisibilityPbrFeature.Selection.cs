using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct VisibilityLodSettings(float TargetPixelError, MeshPatchBudget Budget);

public sealed partial class VisibilityPbrFeature
{
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
        if (_patchTree is { } tree) {
            var matrices = new float4x4[_transforms.Length];
            for (var i = 0; i < matrices.Length; i++) { matrices[i] = math.mul(viewProjection, _transforms[i]); }
            var selection = MeshPatchSelector.Select(tree, matrices, view.Width, view.Height, _lod.TargetPixelError, _lod.Budget);
            var unchanged = view.Selection is { } previous && previous.Patches.Span.SequenceEqual(selection.Patches.Span);
            if (unchanged) { view.Selection = selection; return; }
            nextSelection = selection;
            foreach (var selected in selection.Patches.Span) {
                var patch = tree.Nodes.Span[selected.Patch];
                for (var triangle = patch.TriangleOffset; triangle < patch.TriangleOffset + patch.TriangleCount; triangle++) {
                    view.WorkItems[count++] = new uint4((uint)triangle, (uint)selected.Instance, 0, 0);
                }
            }
        }
        else {
            if (view.WorkInitialized) { return; }
            for (uint instance = 0; instance < InstanceCount; instance++) {
                for (uint triangle = 0; triangle < TriangleCount; triangle++) {
                    view.WorkItems[count++] = new uint4(triangle, instance, 0, 0);
                }
            }
        }
        var queue = _queue.GetWgpu<WGPUQueue>();
        Wgpu.WriteBuffer<uint4>(queue, view.WorkBuffer.GetWgpu<WGPUBuffer>(), 0, view.WorkItems.AsSpan(0, count));
        Wgpu.WriteBuffer<uint>(queue, view.Indirect.GetWgpu<WGPUBuffer>(), 0, [checked((uint)count * 3u), 1, 0, 0]);
        view.WorkCount = (uint)count;
        view.Selection = nextSelection;
        view.WorkInitialized = true;
    }
}
