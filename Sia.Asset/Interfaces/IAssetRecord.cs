namespace Sia.Asset;

using System.Collections.Immutable;

public interface IAssetRecord
{
    string? Name { get; }
    ImmutableList<string> Tags { get; }
}