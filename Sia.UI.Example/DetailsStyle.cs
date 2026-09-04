using Sia.UI;

namespace Sia.UI.Example;

internal readonly record struct DetailsState(bool IsVisible);

[StaticStyle<DetailsState, ShowcaseTheme, NoStyleInteraction, PresentationPatch>]
internal readonly partial struct DetailsStyle
{
    public static PresentationPatch Resolve(
        scoped in DetailsState state,
        scoped in ShowcaseTheme theme,
        scoped in NoStyleInteraction interaction) => new() {
            Layout = new() {
                Flow = LayoutFlow.Vertical,
                Width = LayoutLength.Percent(100f),
                Padding = LayoutInsets.All(LayoutLength.Logical(14f)),
                Gap = LayoutLength.Logical(6f),
            },
            Paint = new() {
                Background = theme.RaisedSurface,
                Border = theme.Border,
                BorderWidth = LayoutInsets.All(LayoutLength.Logical(1f)),
                CornerRadius = LayoutLength.Logical(10f),
            },
            Accessibility = new() {
                Role = AccessibilityRole.Region,
                Name = "Resolved presentation details",
                Expanded = state.IsVisible
                    ? AccessibilityState.True
                    : AccessibilityState.False,
            },
            Visibility = state.IsVisible ? Visibility.Visible : Visibility.Collapsed,
        };
}
