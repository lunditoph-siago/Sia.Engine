using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal static class PbrReflectionStorage
{
    // Four byte planes preserve each float32/uint32 lane without integer storage images.
    internal const WGPUTextureFormat Format = WGPUTextureFormat.RGBA8Unorm;
    internal const uint BytesPerTexel = 4;
    internal const uint PlanesPerValue = 4;
    internal static uint Layers(uint logicalLayers = 1) => checked(logicalLayers * PlanesPerValue);
}
