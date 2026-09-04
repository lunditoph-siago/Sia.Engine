using Sia.UI;

namespace Sia.UI.Example;

internal readonly record struct CanvasState;

[StaticStyle<CanvasState, ShowcaseTheme, NoStyleInteraction, PresentationPatch>]
internal readonly partial struct CanvasStyle
{
    public static PresentationPatch Resolve(
        scoped in CanvasState state,
        scoped in ShowcaseTheme theme,
        scoped in NoStyleInteraction interaction) => new() {
            Layout = new() {
                Flow = LayoutFlow.Vertical,
                Width = LayoutLength.Percent(100f),
                Height = LayoutLength.Percent(100f),
                Padding = LayoutInsets.All(LayoutLength.Logical(28f)),
                Gap = LayoutLength.Logical(16f),
                CrossAlignment = LayoutAlignment.Center,
            },
            Paint = new() {
                Background = theme.Canvas,
            },
            Accessibility = new() {
                Role = AccessibilityRole.Region,
                Name = "Reactive presentation showcase",
            },
            Visibility = Visibility.Visible,
        };
}
