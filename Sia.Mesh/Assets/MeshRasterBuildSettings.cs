namespace Sia.Engine.Mesh;

public readonly record struct MeshRasterBuildSettings(
    int MaximumLods, int MinimumTriangles, float TriangleRatio, float NormalWeight, float UVWeight)
{
    public static MeshRasterBuildSettings Default => new(4, 64, .5f, .25f, 1);
    internal bool IsValid => MaximumLods is >= 1 and <= 8 && MinimumTriangles >= 1
        && float.IsFinite(TriangleRatio) && TriangleRatio > 0 && TriangleRatio < 1
        && float.IsFinite(NormalWeight) && NormalWeight >= 0 && float.IsFinite(UVWeight) && UVWeight >= 0;
}

public readonly record struct MeshRasterLod(int FirstIndex, int IndexCount, float Error);
