namespace Sia.UI;

public readonly record struct InteractionStylePatch
{
    public StyleValue<bool> HitTestVisible { get; init; }

    public StyleValue<bool> Focusable { get; init; }

    public StyleValue<PointerCursor> Cursor { get; init; }
}
