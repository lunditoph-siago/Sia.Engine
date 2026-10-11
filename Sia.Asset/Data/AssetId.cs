namespace Sia.Asset;

public readonly record struct AssetId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static AssetId New() => new(Guid.NewGuid());
}
