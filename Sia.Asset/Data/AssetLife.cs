namespace Sia.Asset;

public enum AssetLife
{
    /// <summary>Destroy after the last owner reference is removed, after reference notifications finish.</summary>
    Automatic,
    /// <summary>Remain alive until explicitly destroyed or its World is disposed.</summary>
    Persistent
}
