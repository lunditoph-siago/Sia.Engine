namespace Sia.Asset;

/// <summary>A stable logical asset identity, independent of content and build revisions.</summary>
public readonly record struct AssetId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static AssetId New() => new(Guid.NewGuid());
}
