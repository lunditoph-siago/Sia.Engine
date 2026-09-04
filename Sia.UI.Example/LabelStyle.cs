using Sia.UI;

namespace Sia.UI.Example;

internal readonly record struct LabelState(
    float Size,
    bool Muted = false,
    bool Heading = false);

[StaticStyle<LabelState, ShowcaseTheme, NoStyleInteraction, PresentationPatch>]
internal readonly partial struct LabelStyle
{
    public static PresentationPatch Resolve(
        scoped in LabelState state,
        scoped in ShowcaseTheme theme,
        scoped in NoStyleInteraction interaction) => new() {
            Layout = new() {
                Flow = LayoutFlow.Content,
                Width = LayoutLength.Percent(100f),
                Shrink = 0f,
            },
            Paint = new() {
                Foreground = state.Muted ? theme.Muted : theme.Foreground,
            },
            Typography = new() {
                Size = LayoutLength.Logical(state.Size),
                Weight = state.Heading ? FontWeight.Bold : FontWeight.Normal,
                Wrap = true,
            },
            Accessibility = new() {
                Role = state.Heading ? AccessibilityRole.Heading : AccessibilityRole.Generic,
                HeadingLevel = state.Heading ? 1 : 0,
            },
            Visibility = Visibility.Visible,
        };
}
