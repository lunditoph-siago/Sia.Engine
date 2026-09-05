namespace Sia.Engine.Rendering;

public readonly record struct RenderPhaseKey
{
    public string Value { get; }

    public RenderPhaseKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public override string ToString() => Value;
}
