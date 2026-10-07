using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Mesh;

[StructLayout(LayoutKind.Explicit, Size = 64)]
public readonly record struct MeshVertex(
    [field: FieldOffset(0)] float3 Position,
    [field: FieldOffset(16)] float3 Normal,
    float2 UV)
{
    public const int Stride = 64;
    public const int PositionOffset = 0;
    public const int NormalOffset = 16;
    public const int UVOffset = 32;
    public const int LightmapUVOffset = 40;
    public const int TangentOffset = 48;

    [field: FieldOffset(TangentOffset)]
    public float4 Tangent { get; init; }

    [FieldOffset(UVOffset)]
    private readonly Coordinates _uv = new(UV.x, UV.y);

    public float2 UV { get => new(_uv.X, _uv.Y); init => _uv = new(value.x, value.y); }

    [FieldOffset(LightmapUVOffset)]
    private readonly Coordinates _lightmapUV;

    /// <summary>Independent surface-bake coordinates. A mesh with all-zero coordinates has no usable chart.</summary>
    public float2 LightmapUV { get => new(_lightmapUV.X, _lightmapUV.Y); init => _lightmapUV = new(value.x, value.y); }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct Coordinates(float X, float Y);

    internal bool HasFiniteLightmapUV => float.IsFinite(LightmapUV.x) && float.IsFinite(LightmapUV.y);

    internal bool HasFiniteTangent => float.IsFinite(Tangent.x) && float.IsFinite(Tangent.y)
        && float.IsFinite(Tangent.z) && float.IsFinite(Tangent.w);
}
