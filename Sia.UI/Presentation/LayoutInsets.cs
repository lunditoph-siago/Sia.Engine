namespace Sia.UI;

public readonly record struct LayoutInsets(
    LayoutLength Start,
    LayoutLength End,
    LayoutLength Before,
    LayoutLength After)
{
    public static readonly LayoutInsets Zero = All(LayoutLength.Zero);

    public static LayoutInsets All(LayoutLength value) => new(value, value, value, value);

    public static LayoutInsets Axes(LayoutLength inline, LayoutLength block) =>
        new(inline, inline, block, block);
}
