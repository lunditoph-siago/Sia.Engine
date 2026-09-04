namespace Sia.UI;

public readonly record struct LayoutStylePatch
{
    public StyleValue<LayoutFlow> Flow { get; init; }

    public StyleValue<LayoutPositioning> Positioning { get; init; }

    public StyleValue<LayoutInsets> Offset { get; init; }

    public StyleValue<LayoutLength> Width { get; init; }

    public StyleValue<LayoutLength> Height { get; init; }

    public StyleValue<LayoutLength> MinWidth { get; init; }

    public StyleValue<LayoutLength> MinHeight { get; init; }

    public StyleValue<LayoutLength> MaxWidth { get; init; }

    public StyleValue<LayoutLength> MaxHeight { get; init; }

    public StyleValue<LayoutInsets> Margin { get; init; }

    public StyleValue<LayoutInsets> Padding { get; init; }

    public StyleValue<LayoutLength> Gap { get; init; }

    public StyleValue<LayoutLength> Basis { get; init; }

    public StyleValue<float> Grow { get; init; }

    public StyleValue<float> Shrink { get; init; }

    public StyleValue<bool> Wrap { get; init; }

    public StyleValue<LayoutAlignment> MainAlignment { get; init; }

    public StyleValue<LayoutAlignment> CrossAlignment { get; init; }

    public StyleValue<LayoutAlignment> SelfAlignment { get; init; }

    public StyleValue<OverflowPolicy> InlineOverflow { get; init; }

    public StyleValue<OverflowPolicy> BlockOverflow { get; init; }

    public StyleValue<int> Layer { get; init; }
}
