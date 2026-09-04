namespace Sia.UI;

/// <summary>
/// Identifies the style that produced a contribution. Ordering is by assembly-
/// qualified type name so composition remains deterministic when separate
/// assemblies declare styles with the same namespace and name.
/// </summary>
public readonly record struct StyleOwner(Type Style) : IComparable<StyleOwner>
{
    public int CompareTo(StyleOwner other) => StringComparer.Ordinal.Compare(
        Identity(Style),
        Identity(other.Style));

    private static string Identity(Type style) =>
        style.AssemblyQualifiedName ?? style.FullName ?? style.Name;
}
