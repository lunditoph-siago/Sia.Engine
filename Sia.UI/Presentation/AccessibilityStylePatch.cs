namespace Sia.UI;

public readonly record struct AccessibilityStylePatch
{
    public StyleValue<AccessibilityRole> Role { get; init; }

    public StyleValue<string?> Name { get; init; }

    public StyleValue<string?> Description { get; init; }

    public StyleValue<AccessibilityState> Disabled { get; init; }

    public StyleValue<AccessibilityState> ReadOnly { get; init; }

    public StyleValue<AccessibilityState> Selected { get; init; }

    public StyleValue<AccessibilityState> Checked { get; init; }

    public StyleValue<AccessibilityState> Expanded { get; init; }

    public StyleValue<int> HeadingLevel { get; init; }
}
