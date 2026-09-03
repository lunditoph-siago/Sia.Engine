namespace Sia.UI;

internal static class UiPresentationMapper
{
    public static Node Node(scoped in Presentation presentation)
    {
        var layout = presentation.Layout;
        return new() {
            Display = presentation.Visibility == Visibility.Visible
                ? Display(layout.Flow)
                : global::Sia.UI.Display.None,
            PositionType = layout.Positioning == LayoutPositioning.Flow
                ? PositionType.Relative
                : PositionType.Absolute,
            Overflow = new(
                Overflow(layout.InlineOverflow),
                Overflow(layout.BlockOverflow)),
            Left = Value(layout.Offset.Start),
            Right = Value(layout.Offset.End),
            Top = Value(layout.Offset.Before),
            Bottom = Value(layout.Offset.After),
            Width = Value(layout.Width),
            Height = Value(layout.Height),
            MinWidth = Value(layout.MinWidth),
            MinHeight = Value(layout.MinHeight),
            MaxWidth = Value(layout.MaxWidth),
            MaxHeight = Value(layout.MaxHeight),
            Margin = Rect(layout.Margin),
            Padding = Rect(layout.Padding),
            Border = Rect(presentation.Paint.BorderWidth),
            BorderRadius = BorderRadius.All(Value(presentation.Paint.CornerRadius, Val.Zero)),
            AlignItems = Items(layout.CrossAlignment),
            AlignSelf = Items(layout.SelfAlignment),
            JustifyContent = Content(layout.MainAlignment),
            RowGap = Value(layout.Gap, Val.Zero),
            ColumnGap = Value(layout.Gap, Val.Zero),
            FlexDirection = layout.Flow == LayoutFlow.Vertical
                ? FlexDirection.Column
                : FlexDirection.Row,
            FlexWrap = layout.Wrap ? FlexWrap.Wrap : FlexWrap.NoWrap,
            FlexGrow = layout.Grow,
            FlexBasis = Value(layout.Basis),
        };
    }

    public static Color Color(Color color, float opacity)
    {
        var alpha = opacity == 0f ? color.A : color.A * System.Math.Clamp(opacity, 0f, 1f);
        return color with { A = alpha };
    }

    private static Display Display(LayoutFlow flow) => flow switch {
        LayoutFlow.Content => global::Sia.UI.Display.Block,
        LayoutFlow.Overlay => global::Sia.UI.Display.Grid,
        _ => global::Sia.UI.Display.Flex,
    };

    private static OverflowAxis Overflow(OverflowPolicy overflow) => overflow switch {
        OverflowPolicy.Clip => OverflowAxis.Hidden,
        OverflowPolicy.Scroll => OverflowAxis.Scroll,
        _ => OverflowAxis.Visible,
    };

    private static AlignItems Items(LayoutAlignment alignment) => alignment switch {
        LayoutAlignment.Start => AlignItems.Start,
        LayoutAlignment.Center => AlignItems.Center,
        LayoutAlignment.End => AlignItems.End,
        LayoutAlignment.Stretch => AlignItems.Stretch,
        _ => AlignItems.Default,
    };

    private static AlignContent Content(LayoutAlignment alignment) => alignment switch {
        LayoutAlignment.Start => AlignContent.Start,
        LayoutAlignment.Center => AlignContent.Center,
        LayoutAlignment.End => AlignContent.End,
        LayoutAlignment.Stretch => AlignContent.Stretch,
        LayoutAlignment.SpaceBetween => AlignContent.SpaceBetween,
        LayoutAlignment.SpaceAround => AlignContent.SpaceAround,
        _ => AlignContent.Default,
    };

    private static UiRect Rect(LayoutInsets insets) => new(
        Value(insets.Start, Val.Zero),
        Value(insets.End, Val.Zero),
        Value(insets.Before, Val.Zero),
        Value(insets.After, Val.Zero));

    private static Val Value(LayoutLength length, Val? unspecified = null)
        => length.Kind switch {
            LayoutLengthKind.Logical => Val.Px(length.Value),
            LayoutLengthKind.Percent => Val.Percent(length.Value),
            _ => unspecified ?? Val.Auto,
        };
}
