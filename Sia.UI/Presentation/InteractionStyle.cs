namespace Sia.UI;

public readonly record struct InteractionStyle
{
    public bool HitTestVisible { get; init; }

    public bool Focusable { get; init; }

    public PointerCursor Cursor { get; init; }
}
