using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct VisibilityInstance(float4x4 Transform, PbrMaterial Material)
{
    public int AssetIndex { get; init; }
    public int MaterialIndex { get; init; }

    public VisibilityInstance(float4x4 transform, int materialIndex)
        : this(transform, new PbrMaterial(float3.one, 1, 1, float3.one, 1))
    {
        MaterialIndex = materialIndex;
    }
}

public enum VisibilityDebugMode
{
    Shaded,
    Normals,
    UV,
    Albedo,
    Triangles
}

public sealed record VisibilityAlbedo(uint Width, uint Height, ReadOnlyMemory<byte>[] MipLevels);
