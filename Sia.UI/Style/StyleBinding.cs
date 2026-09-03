using Sia;

namespace Sia.UI;

public readonly record struct StyleBinding<
    TStyle,
    TState,
    TTheme,
    TInteraction,
    TPresentation>(
    TState State,
    Entity Theme)
    where TStyle : struct, IStaticStyle<TState, TTheme, TInteraction, TPresentation>
    where TState : struct
    where TTheme : struct
    where TInteraction : struct
    where TPresentation : struct;
