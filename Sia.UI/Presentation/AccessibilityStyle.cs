namespace Sia.UI;

public readonly record struct AccessibilityStyle
{
    public AccessibilityRole Role { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public AccessibilityState Disabled { get; init; }

    public AccessibilityState ReadOnly { get; init; }

    public AccessibilityState Selected { get; init; }

    public AccessibilityState Checked { get; init; }

    public AccessibilityState Expanded { get; init; }

    public int HeadingLevel { get; init; }
}
