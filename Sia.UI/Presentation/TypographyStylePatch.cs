namespace Sia.UI;

public readonly record struct TypographyStylePatch
{
    public StyleValue<FontToken> Font { get; init; }

    public StyleValue<LayoutLength> Size { get; init; }

    public StyleValue<FontWeight> Weight { get; init; }

    public StyleValue<bool> Wrap { get; init; }
}
