using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct VisibilityInstance(float4x4 Transform, PbrMaterial Material);

public enum VisibilityDebugMode
{
    Shaded,
    Normals,
    UV,
    Albedo,
    Triangles
}

public sealed record VisibilityAlbedo(uint Width, uint Height, ReadOnlyMemory<byte>[] MipLevels);
