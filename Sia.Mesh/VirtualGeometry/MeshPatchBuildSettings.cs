namespace Sia.Engine.Mesh;

public readonly record struct MeshPatchBuildSettings(
    int MaxLeafTriangles, int MaxChildren, float ParentTriangleRatio, float NormalWeight, float UVWeight)
{
    public static MeshPatchBuildSettings Default => new(124, 4, 0.5f, 0.25f, 1);

    internal bool IsValid => MaxLeafTriangles is >= 1 and <= 512 && MaxChildren is >= 2 and <= 8
        && float.IsFinite(ParentTriangleRatio) && ParentTriangleRatio > 0 && ParentTriangleRatio < 1
        && float.IsFinite(NormalWeight) && NormalWeight >= 0 && float.IsFinite(UVWeight) && UVWeight >= 0;
}

public readonly record struct MeshPatchBuildResult(
    MeshPatchTree Tree, int SourceTriangleCount, int RemovedDegenerateTriangleCount,
    int SimplificationCount, int TargetMissCount, int UnreducedGroupCount);
