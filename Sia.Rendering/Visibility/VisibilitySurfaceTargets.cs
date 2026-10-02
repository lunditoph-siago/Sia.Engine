using Sia.Graphics.Reactive;

namespace Sia.Engine.Rendering;

public readonly record struct VisibilitySurfaceTargets(
    RenderGraphTextureKey Visibility,
    RenderGraphTextureKey Depth,
    RenderGraphTextureKey NormalRoughness,
    RenderGraphTextureKey BaseColorMetallic)
{
    public const uint AttributeBytesPerPixel = 12;
}
