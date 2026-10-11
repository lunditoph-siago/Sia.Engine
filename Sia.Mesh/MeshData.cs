using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed record MeshData(MeshVertex[] Vertices, uint[] Indices, Aabb Bounds)
{
    /// <summary>Optional per-vertex lightmap coordinates, separate from the GPU vertex layout.</summary>
    public float2[] BakeUvs { get; init; } = [];
    public bool HasBakeUvs => BakeUvs.Length != 0;

    internal void ValidateBakeUvs()
    {
        ArgumentNullException.ThrowIfNull(BakeUvs);
        if (BakeUvs.Length != 0 && BakeUvs.Length != Vertices.Length)
            throw new ArgumentException("Bake UVs must be absent or have one value per vertex.", nameof(BakeUvs));
        foreach (var uv in BakeUvs) {
            if (!float.IsFinite(uv.x) || !float.IsFinite(uv.y))
                throw new ArgumentException("Bake UVs must be finite.", nameof(BakeUvs));
        }
    }
}
