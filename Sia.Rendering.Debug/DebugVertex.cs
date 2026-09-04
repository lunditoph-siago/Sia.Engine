using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Debug;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct DebugVertex(float3 Position, float3 Normal, float4 Color)
{
    public const int Stride = 48;
    public const int PositionOffset = 0;
    public const int NormalOffset = 16;
    public const int ColorOffset = 32;

    public bool IsFinite =>
        float.IsFinite(Position.x) &&
        float.IsFinite(Position.y) &&
        float.IsFinite(Position.z) &&
        float.IsFinite(Normal.x) &&
        float.IsFinite(Normal.y) &&
        float.IsFinite(Normal.z) &&
        float.IsFinite(Color.x) &&
        float.IsFinite(Color.y) &&
        float.IsFinite(Color.z) &&
        float.IsFinite(Color.w);
}
