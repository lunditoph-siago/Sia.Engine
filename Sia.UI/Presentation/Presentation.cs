namespace Sia.UI;

public readonly record struct Presentation
{
    /// <summary>
    /// The starting point of every composition: fully opaque and visible, with
    /// every other field left at its unset default.
    /// </summary>
    public static readonly Presentation Neutral = new() {
        Paint = new() { Opacity = 1f },
        Visibility = Visibility.Visible,
    };

    public LayoutStyle Layout { get; init; }

    public PaintStyle Paint { get; init; }

    public TypographyStyle Typography { get; init; }

    public InteractionStyle Interaction { get; init; }

    public AccessibilityStyle Accessibility { get; init; }

    public Visibility Visibility { get; init; }
}
