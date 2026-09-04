using Sia.UI;

namespace Sia.UI.Example;

internal readonly record struct AccentState;

[StaticStyle<AccentState, ShowcaseTheme, NoStyleInteraction, PresentationPatch>]
internal readonly partial struct AccentStyle
{
    public static PresentationPatch Resolve(
        scoped in AccentState state,
        scoped in ShowcaseTheme theme,
        scoped in NoStyleInteraction interaction) => new() {
            Paint = new() {
                Background = theme.AccentStrong,
                Border = theme.Accent,
            },
        };
}
