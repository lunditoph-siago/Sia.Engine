using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Unlit;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct UnlitInstance(
    float4x4 World,
    float4x4 Normal,
    float4 Color)
{
    public const int Stride = 144;
}
