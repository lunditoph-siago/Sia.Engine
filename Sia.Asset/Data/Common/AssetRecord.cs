namespace Sia.Asset;

using System.Collections.Immutable;

public abstract record AssetRecord : IAssetRecord
{
    [SiaIgnore] public string? Name { get; init; }
    [SiaIgnore] public ImmutableList<string> Tags { get; init; } = [];
}