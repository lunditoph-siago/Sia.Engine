namespace Sia.UI;

public interface IStaticStyle<TState, TTheme, TInteraction, TPresentation>
    where TState : struct
    where TTheme : struct
    where TInteraction : struct
    where TPresentation : struct
{
    public static abstract TPresentation Resolve(
        scoped in TState state,
        scoped in TTheme theme,
        scoped in TInteraction interaction);
}
