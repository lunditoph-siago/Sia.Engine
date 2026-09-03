namespace Sia.UI;

public static class PresentationComposer
{
    public static Presentation Compose(
        ReadOnlySpan<StyleContribution<PresentationPatch>> contributions)
    {
        var ordered = contributions.ToArray();
        Array.Sort(ordered, Compare);

        var presentation = new Presentation {
            Paint = new() {
                Opacity = 1f,
            },
            Visibility = Visibility.Visible,
        };
        foreach (ref readonly var contribution in ordered.AsSpan()) {
            presentation = Apply(presentation, contribution.Output);
        }
        return presentation;
    }

    private static int Compare(
        StyleContribution<PresentationPatch> left,
        StyleContribution<PresentationPatch> right)
    {
        var layer = left.Layer.CompareTo(right.Layer);
        return layer != 0
            ? layer
            : StringComparer.Ordinal.Compare(
                left.Owner.Style.FullName,
                right.Owner.Style.FullName);
    }

    private static Presentation Apply(
        scoped in Presentation presentation,
        scoped in PresentationPatch patch) => presentation with {
        Layout = Apply(presentation.Layout, patch.Layout),
        Paint = Apply(presentation.Paint, patch.Paint),
        Typography = Apply(presentation.Typography, patch.Typography),
        Interaction = Apply(presentation.Interaction, patch.Interaction),
        Visibility = Value(patch.Visibility, presentation.Visibility),
    };

    private static LayoutStyle Apply(
        scoped in LayoutStyle style,
        scoped in LayoutStylePatch patch) => style with {
        Flow = Value(patch.Flow, style.Flow),
        Positioning = Value(patch.Positioning, style.Positioning),
        Offset = Value(patch.Offset, style.Offset),
        Width = Value(patch.Width, style.Width),
        Height = Value(patch.Height, style.Height),
        MinWidth = Value(patch.MinWidth, style.MinWidth),
        MinHeight = Value(patch.MinHeight, style.MinHeight),
        MaxWidth = Value(patch.MaxWidth, style.MaxWidth),
        MaxHeight = Value(patch.MaxHeight, style.MaxHeight),
        Margin = Value(patch.Margin, style.Margin),
        Padding = Value(patch.Padding, style.Padding),
        Gap = Value(patch.Gap, style.Gap),
        Basis = Value(patch.Basis, style.Basis),
        Grow = Value(patch.Grow, style.Grow),
        Shrink = patch.Shrink.IsSpecified ? patch.Shrink.Value : style.Shrink,
        Wrap = Value(patch.Wrap, style.Wrap),
        MainAlignment = Value(patch.MainAlignment, style.MainAlignment),
        CrossAlignment = Value(patch.CrossAlignment, style.CrossAlignment),
        SelfAlignment = Value(patch.SelfAlignment, style.SelfAlignment),
        InlineOverflow = Value(patch.InlineOverflow, style.InlineOverflow),
        BlockOverflow = Value(patch.BlockOverflow, style.BlockOverflow),
        Order = Value(patch.Order, style.Order),
        Layer = Value(patch.Layer, style.Layer),
    };

    private static PaintStyle Apply(
        scoped in PaintStyle style,
        scoped in PaintStylePatch patch) => style with {
        Background = Value(patch.Background, style.Background),
        Foreground = Value(patch.Foreground, style.Foreground),
        Border = Value(patch.Border, style.Border),
        BorderWidth = Value(patch.BorderWidth, style.BorderWidth),
        CornerRadius = Value(patch.CornerRadius, style.CornerRadius),
        Opacity = Value(patch.Opacity, style.Opacity),
    };

    private static TypographyStyle Apply(
        scoped in TypographyStyle style,
        scoped in TypographyStylePatch patch) => style with {
        Font = Value(patch.Font, style.Font),
        Size = Value(patch.Size, style.Size),
        Weight = Value(patch.Weight, style.Weight),
        Wrap = Value(patch.Wrap, style.Wrap),
    };

    private static InteractionStyle Apply(
        scoped in InteractionStyle style,
        scoped in InteractionStylePatch patch) => style with {
        HitTestVisible = Value(patch.HitTestVisible, style.HitTestVisible),
        Focusable = Value(patch.Focusable, style.Focusable),
        Cursor = Value(patch.Cursor, style.Cursor),
    };

    private static T Value<T>(StyleValue<T> patch, T current)
        where T : struct => patch.IsSpecified ? patch.Value : current;
}
