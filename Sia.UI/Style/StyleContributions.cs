using System.Collections.Immutable;

namespace Sia.UI;

public readonly record struct StyleContributions<TOutput>
    where TOutput : struct
{
    private readonly ImmutableArray<StyleContribution<TOutput>> _items;

    public StyleContributions(ImmutableArray<StyleContribution<TOutput>> items)
    {
        _items = items;
    }

    public ImmutableArray<StyleContribution<TOutput>> Items
        => _items.IsDefault ? [] : _items;

    public bool IsEmpty => _items.IsDefaultOrEmpty;

    public StyleContributions<TOutput> Set(
        scoped in StyleContribution<TOutput> contribution)
    {
        var items = Items;
        for (var index = 0; index < items.Length; index++) {
            if (items[index].Owner == contribution.Owner) {
                return new(items.SetItem(index, contribution));
            }
        }
        return new(items.Add(contribution));
    }

    public StyleContributions<TOutput> Remove(StyleOwner owner)
    {
        var items = Items;
        for (var index = 0; index < items.Length; index++) {
            if (items[index].Owner == owner) {
                return new(items.RemoveAt(index));
            }
        }
        return this;
    }
}
