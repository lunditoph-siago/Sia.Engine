namespace Sia.UI;

public readonly record struct ResolvedStyle<TPresentation>(TPresentation Presentation)
    where TPresentation : struct;
