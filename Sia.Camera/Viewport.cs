using Sia;

namespace Sia.Engine.Camera;

public sealed class Viewport : IAddon
{
    public ViewportSize Value { get; set; } = new(1, 1);
}
