namespace Sia.UI;

public readonly record struct LayoutStyle
{
    public LayoutFlow Flow { get; init; }

    public LayoutLength Width { get; init; }

    public LayoutLength Height { get; init; }

    public LayoutLength MinWidth { get; init; }

    public LayoutLength MinHeight { get; init; }

    public LayoutLength MaxWidth { get; init; }

    public LayoutLength MaxHeight { get; init; }

    public LayoutInsets Margin { get; init; }

    public LayoutInsets Padding { get; init; }

    public LayoutLength Gap { get; init; }

    public LayoutAlignment MainAlignment { get; init; }

    public LayoutAlignment CrossAlignment { get; init; }

    public OverflowPolicy Overflow { get; init; }

    public int Layer { get; init; }
}
