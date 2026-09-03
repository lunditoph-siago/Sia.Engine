namespace Sia.UI;

public readonly record struct LayoutLength
{
    public static readonly LayoutLength Unspecified = default;
    public static readonly LayoutLength Zero = Logical(0f);
    public static readonly LayoutLength Fill = Fraction(1f);

    public LayoutLengthKind Kind { get; }

    public float Value { get; }

    private LayoutLength(LayoutLengthKind kind, float value)
    {
        if (!float.IsFinite(value)) {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (kind == LayoutLengthKind.Fraction && value < 0f) {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        Kind = kind;
        Value = value;
    }

    public static LayoutLength Logical(float value) => new(LayoutLengthKind.Logical, value);

    public static LayoutLength Percent(float value) => new(LayoutLengthKind.Percent, value);

    public static LayoutLength Fraction(float value) => new(LayoutLengthKind.Fraction, value);
}
