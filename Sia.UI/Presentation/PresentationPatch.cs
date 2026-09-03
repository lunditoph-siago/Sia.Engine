namespace Sia.UI;

public readonly record struct PresentationPatch
{
    public LayoutStylePatch Layout { get; init; }

    public PaintStylePatch Paint { get; init; }

    public TypographyStylePatch Typography { get; init; }

    public InteractionStylePatch Interaction { get; init; }

    public StyleValue<Visibility> Visibility { get; init; }
}
