using Sia;

namespace Sia.UI;

internal sealed class UiInteractionState : IAddon
{
    public Entity? Hovered;
    public Entity? Pressed;
    public Point LastPosition;
    public bool WasButtonDown;
}
