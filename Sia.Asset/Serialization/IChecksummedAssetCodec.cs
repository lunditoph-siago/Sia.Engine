namespace Sia.Asset;

/// <summary>
/// Format rules for one bounded payload followed by SHA256. Implementations must
/// validate untrusted prefix lengths before returning an allocation size.
/// </summary>
public interface IChecksummedAssetCodec<T>
{
    /// <summary>Bytes required to determine payload size; zero for fixed-size formats.</summary>
    static abstract int HeaderBytes { get; }
    /// <summary>Total payload size, including the prefix and excluding SHA256.</summary>
    static abstract int GetReadSize(ReadOnlySpan<byte> header);
    static abstract int GetWriteSize(T value);
    static abstract T ReadPayload(BinaryReader reader);
    static abstract void WritePayload(BinaryWriter writer, T value);
}
