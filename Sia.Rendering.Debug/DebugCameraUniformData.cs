using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Debug;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct DebugCameraUniformData(float4x4 ViewProjection)
{
    public const int Stride = 64;
}
