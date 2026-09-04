using Sia.UI;

namespace Sia.UI.Example;

internal readonly record struct ControlState(
    string Name,
    bool Checked = false,
    bool Expanded = false);

[StaticStyle<ControlState, ShowcaseTheme, ControlInteraction, PresentationPatch>]
internal readonly partial struct ControlStyle
{
    public static PresentationPatch Resolve(
        scoped in ControlState state,
        scoped in ShowcaseTheme theme,
        scoped in ControlInteraction interaction)
    {
        var background = interaction.IsPressed
            ? theme.Pressed
            : interaction.IsHovered
                ? theme.Hover
                : theme.RaisedSurface;
        var disabled = interaction.IsDisabled
            ? AccessibilityState.True
            : AccessibilityState.False;

        return new() {
            Layout = new() {
                Flow = LayoutFlow.Content,
                Height = LayoutLength.Logical(42f),
                Padding = LayoutInsets.Axes(
                LayoutLength.Logical(14f),
                LayoutLength.Logical(9f)),
                Shrink = 0f,
            },
            Paint = new() {
                Background = background,
                Foreground = interaction.IsDisabled ? theme.Muted : theme.Foreground,
                Border = interaction.IsFocused ? theme.Focus : theme.Border,
                BorderWidth = LayoutInsets.All(LayoutLength.Logical(
                    interaction.IsFocused ? 2f : 1f)),
                CornerRadius = LayoutLength.Logical(8f),
                Opacity = interaction.IsDisabled ? 0.55f : 1f,
            },
            Typography = new() {
                Size = LayoutLength.Logical(15f),
                Weight = FontWeight.Medium,
            },
            Interaction = new() {
                HitTestVisible = !interaction.IsDisabled,
                Focusable = !interaction.IsDisabled,
                Cursor = interaction.IsDisabled ? PointerCursor.Default : PointerCursor.Action,
            },
            Accessibility = new() {
                Role = AccessibilityRole.Button,
                Name = state.Name,
                Disabled = disabled,
                Checked = state.Checked
                    ? AccessibilityState.True
                    : AccessibilityState.False,
                Expanded = state.Expanded
                    ? AccessibilityState.True
                    : AccessibilityState.False,
            },
            Visibility = Visibility.Visible,
        };
    }
}
