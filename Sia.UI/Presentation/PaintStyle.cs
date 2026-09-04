namespace Sia.UI;

public readonly record struct PaintStyle
{
    public Color Background { get; init; }

    public Color Foreground { get; init; }

    public Color Border { get; init; }

    public LayoutInsets BorderWidth { get; init; }

    public LayoutLength CornerRadius { get; init; }

    public float Opacity { get; init; }
}
