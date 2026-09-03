using Sia;

namespace Sia.UI;

public sealed class UiFocusState : IAddon
{
    public Entity? Focused { get; internal set; }
}
