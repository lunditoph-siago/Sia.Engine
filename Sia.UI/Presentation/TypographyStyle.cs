namespace Sia.UI;

public readonly record struct TypographyStyle
{
    public FontToken Font { get; init; }

    public LayoutLength Size { get; init; }

    public FontWeight Weight { get; init; }

    public FontSlant Slant { get; init; }

    public bool Wrap { get; init; }
}
