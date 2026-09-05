namespace Sia.Engine.Rendering;

public readonly record struct ShaderAssetId
{
    public string Value { get; }

    public ShaderAssetId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public override string ToString() => Value;
}
