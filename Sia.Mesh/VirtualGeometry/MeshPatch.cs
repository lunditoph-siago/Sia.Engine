using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed record MeshPatch(MeshData Geometry, float LocalEstimatedSpatialError, MeshPatch[] Children);

public readonly record struct MeshPatchNode(
    Aabb Bounds, float EstimatedSpatialError, int Parent,
    int ChildOffset, int ChildCount, int MeshletOffset, int MeshletCount,
    int TriangleOffset, int TriangleCount);

public readonly record struct MeshPatchBudget(int MaxPatches, int MaxMeshlets, int MaxTriangles)
{
    private readonly int? _maxRefinementCandidates;

    public int MaxRefinementCandidates
    {
        get => _maxRefinementCandidates ?? int.MaxValue;
        init => _maxRefinementCandidates = value == int.MaxValue ? null : value;
    }
}

public readonly record struct SelectedMeshPatch(int Instance, int Patch);

public readonly record struct MeshPatchSelection(
    ReadOnlyMemory<SelectedMeshPatch> Patches, int MeshletCount, int TriangleCount,
    float MaximumEstimatedPixelError, bool BudgetLimited, bool BudgetUnreachable);
