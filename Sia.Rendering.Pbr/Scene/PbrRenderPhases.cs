using Sia.Engine.Rendering;

namespace Sia.Engine.Rendering.Pbr;

public static class PbrRenderPhases
{
    public static RenderPhaseKey Opaque { get; } = new("pbr-opaque");
}
