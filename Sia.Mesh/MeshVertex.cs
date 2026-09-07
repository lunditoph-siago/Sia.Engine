using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Mesh;

[StructLayout(LayoutKind.Explicit, Size = 64)]
public readonly record struct MeshVertex(
    [field: FieldOffset(0)] float3 Position,
    [field: FieldOffset(16)] float3 Normal,
    [field: FieldOffset(32)] float2 UV)
{
    public const int Stride = 64;
    public const int PositionOffset = 0;
    public const int NormalOffset = 16;
    public const int UVOffset = 32;
    public const int TangentOffset = 48;

    [field: FieldOffset(TangentOffset)]
    public float4 Tangent { get; init; }

    internal bool HasFiniteTangent => float.IsFinite(Tangent.x) && float.IsFinite(Tangent.y)
        && float.IsFinite(Tangent.z) && float.IsFinite(Tangent.w);
}
