using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

[StructLayout(LayoutKind.Sequential)]
internal struct PbrFrame
{
    public float4x4 ViewProjection, View, InverseProjection, InverseViewProjection;
    public float4 Eye;
    public uint4 Size, Geometry, Grid;
    public float4 Depth;
    public uint4 Counts, Shadow;
    public float4 Splits;
    public float4 Direction0, Radiance0, Direction1, Radiance1, Direction2, Radiance2, Direction3, Radiance3;
}
