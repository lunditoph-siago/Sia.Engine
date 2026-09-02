namespace Sia.Asset;

public struct AssetSnapshot<TSnapshot>(in TSnapshot snapshot)
{
    public TSnapshot Snapshot = snapshot;
}