namespace Sia.UI;

public readonly record struct StyleContribution<TOutput>(
    StyleOwner Owner,
    StyleLayer Layer,
    TOutput Output)
    where TOutput : struct;
