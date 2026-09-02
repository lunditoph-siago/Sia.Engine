using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct PbrRenderInstance(
    float4x4 WorldMatrix,
    float4x4 NormalMatrix,
    float4 BaseColor,
    float4 MaterialParams,
    float4 Emissive)
{
    public const int Stride = 176;
}
