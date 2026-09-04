using Sia.UI;

namespace Sia.UI.Example;

internal readonly record struct PanelState(bool Raised = false);

[StaticStyle<PanelState, ShowcaseTheme, NoStyleInteraction, PresentationPatch>]
internal readonly partial struct PanelStyle
{
    public static PresentationPatch Resolve(
        scoped in PanelState state,
        scoped in ShowcaseTheme theme,
        scoped in NoStyleInteraction interaction) => new() {
            Layout = new() {
                Flow = LayoutFlow.Vertical,
                Width = LayoutLength.Logical(820f),
                Padding = LayoutInsets.All(LayoutLength.Logical(20f)),
                Gap = LayoutLength.Logical(12f),
                Shrink = 0f,
            },
            Paint = new() {
                Background = state.Raised ? theme.RaisedSurface : theme.Surface,
                Border = theme.Border,
                BorderWidth = LayoutInsets.All(LayoutLength.Logical(1f)),
                CornerRadius = LayoutLength.Logical(14f),
            },
            Accessibility = new() {
                Role = AccessibilityRole.Region,
            },
            Visibility = Visibility.Visible,
        };
}
