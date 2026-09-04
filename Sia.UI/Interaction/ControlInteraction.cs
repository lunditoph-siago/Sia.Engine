namespace Sia.UI;

public readonly record struct ControlInteraction
{
    public bool IsHovered { get; init; }

    public bool IsPressed { get; init; }

    public bool IsFocused { get; init; }

    public bool IsDisabled { get; init; }
}
