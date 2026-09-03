namespace Sia.UI;

public readonly record struct LayoutStyle
{
    public LayoutFlow Flow { get; init; }

    public LayoutPositioning Positioning { get; init; }

    public LayoutInsets Offset { get; init; }

    public LayoutLength Width { get; init; }

    public LayoutLength Height { get; init; }

    public LayoutLength MinWidth { get; init; }

    public LayoutLength MinHeight { get; init; }

    public LayoutLength MaxWidth { get; init; }

    public LayoutLength MaxHeight { get; init; }

    public LayoutInsets Margin { get; init; }

    public LayoutInsets Padding { get; init; }

    public LayoutLength Gap { get; init; }

    public LayoutLength Basis { get; init; }

    public float Grow { get; init; }

    public bool Wrap { get; init; }

    public LayoutAlignment MainAlignment { get; init; }

    public LayoutAlignment CrossAlignment { get; init; }

    public LayoutAlignment SelfAlignment { get; init; }

    public OverflowPolicy InlineOverflow { get; init; }

    public OverflowPolicy BlockOverflow { get; init; }

    public int Order { get; init; }

    public int Layer { get; init; }
}
