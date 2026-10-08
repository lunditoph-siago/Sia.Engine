namespace Sia.Asset;

/// <summary>Borrowed immutable CPU data, valid until the lease or owning library is disposed.</summary>
public sealed class AssetLease<T> : IDisposable where T : class
{
    private AssetLibrary? _owner;
    private readonly AssetLibrary.Entry _entry;

    internal AssetLease(AssetLibrary owner, AssetLibrary.Entry entry) { _owner = owner; _entry = entry; }
    public T Value => (Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(AssetLease<T>))).GetValue<T>(_entry);
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_entry);
}
