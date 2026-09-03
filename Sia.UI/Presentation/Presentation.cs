namespace Sia.UI;

public readonly record struct Presentation
{
    public LayoutStyle Layout { get; init; }

    public PaintStyle Paint { get; init; }

    public TypographyStyle Typography { get; init; }

    public InteractionStyle Interaction { get; init; }

    public Visibility Visibility { get; init; }
}
