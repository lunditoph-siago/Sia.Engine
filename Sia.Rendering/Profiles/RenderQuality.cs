namespace Sia.Engine.Rendering;

public enum RenderQuality
{
    Low,
    Medium,
    High
}

public readonly record struct RenderCapabilities(
    ulong MaxBufferBytes,
    ulong MaxStorageBufferBytes,
    uint MaxTextureDimension2D,
    uint MaxTextureArrayLayers,
    bool TimestampQueries);
