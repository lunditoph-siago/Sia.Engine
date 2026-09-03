namespace Sia.UI;

public readonly record struct PaintStylePatch
{
    public StyleValue<Color> Background { get; init; }

    public StyleValue<Color> Foreground { get; init; }

    public StyleValue<Color> Border { get; init; }

    public StyleValue<LayoutInsets> BorderWidth { get; init; }

    public StyleValue<LayoutLength> CornerRadius { get; init; }

    public StyleValue<float> Opacity { get; init; }
}
