namespace Sia.Engine.Mesh;

public enum MeshPatchErrorMetric
{
    VertexDisplacement,
    Quadric
}

public readonly record struct MeshPatchBuildSettings(
    int MaxLeafTriangles, int MaxChildren, float ParentTriangleRatio, float NormalWeight, float UVWeight)
{
    public MeshPatchErrorMetric ErrorMetric { get; init; }

    public static MeshPatchBuildSettings Default => new(124, 4, 0.5f, 0.25f, 1);
}

public readonly record struct MeshPatchBuildResult(
    MeshPatchTree Tree, int SourceTriangleCount, int RemovedDegenerateTriangleCount,
    int SimplificationCount, int TargetMissCount, int UnreducedGroupCount);
