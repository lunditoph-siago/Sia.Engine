using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering;

[StructLayout(LayoutKind.Sequential)]
// Sia float3 occupies 16 bytes; scalars share WGSL vec3's padding with the offset.
public readonly record struct GeometryClusterGpu(float MinimumX, float MinimumY, float MinimumZ, uint VertexOffset,
    float4 Maximum, float4 Sphere, float4 Cone, uint4 Work);
