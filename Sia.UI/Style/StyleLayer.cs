namespace Sia.UI;

public readonly record struct StyleLayer(int Order) : IComparable<StyleLayer>
{
    public static readonly StyleLayer Base = new(0);
    public static readonly StyleLayer Variant = new(100);
    public static readonly StyleLayer State = new(200);
    public static readonly StyleLayer Override = new(300);

    public int CompareTo(StyleLayer other) => Order.CompareTo(other.Order);
}
