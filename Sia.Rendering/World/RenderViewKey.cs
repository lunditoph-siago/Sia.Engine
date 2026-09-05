namespace Sia.Engine.Rendering;

public readonly record struct RenderViewKey
{
    public string Value { get; }

    public RenderViewKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public override string ToString() => Value;
}
