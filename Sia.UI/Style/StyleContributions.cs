using System.Collections.Immutable;

namespace Sia.UI;

/// <summary>
/// The ordered set of patches contributed to one entity, at most one per
/// owning style. Items are kept sorted on insert so composition is a plain
/// allocation-free walk.
/// </summary>
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

    /// <summary>
    /// Returns a set with <paramref name="contribution"/> replacing this
    /// owner's previous entry, or this instance unchanged when the owner
    /// already contributes an equal value.
    /// </summary>
    public StyleContributions<TOutput> Set(
        scoped in StyleContribution<TOutput> contribution)
    {
        var items = Items;
        for (var index = 0; index < items.Length; index++) {
            if (items[index].Owner != contribution.Owner) {
                continue;
            }
            if (items[index] == contribution) {
                return this;
            }
            return items[index].Layer == contribution.Layer
                ? new(items.SetItem(index, contribution))
                : new(Insert(items.RemoveAt(index), contribution));
        }
        return new(Insert(items, contribution));
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

    private static ImmutableArray<StyleContribution<TOutput>> Insert(
        ImmutableArray<StyleContribution<TOutput>> items,
        scoped in StyleContribution<TOutput> contribution)
    {
        var index = 0;
        while (index < items.Length && Precedes(items[index], contribution)) {
            index++;
        }
        return items.Insert(index, contribution);
    }

    private static bool Precedes(
        scoped in StyleContribution<TOutput> left,
        scoped in StyleContribution<TOutput> right)
    {
        var layer = left.Layer.CompareTo(right.Layer);
        return layer != 0 ? layer < 0 : left.Owner.CompareTo(right.Owner) < 0;
    }
}
