namespace Sia.Asset;

/// <summary>Validates and decodes immutable managed CPU data; it performs no IO or GPU operations.</summary>
public interface IAssetCodec<T> where T : class
{
    ushort Kind { get; }
    AssetDecodePlan Inspect(ReadOnlySpan<byte> payload);
    T Decode(ReadOnlyMemory<byte> payload);
}

/// <param name="OwnedBytes">Memory allocated by Decode, excluding its input payload.</param>
/// <param name="RetainsPayload">Whether decoded data retains any part of the input buffer.</param>
public readonly record struct AssetDecodePlan(long OwnedBytes, bool RetainsPayload);

public readonly record struct AssetLibraryLimits(long EncodedBytes, long DecodedBytes,
    long ResidentBytes, int ConcurrentReads = 2, int PendingAssets = 128, int ResidentAssets = 4096)
{
    public static AssetLibraryLimits Default => new(32 * 1024 * 1024, 128 * 1024 * 1024, 256 * 1024 * 1024);
}

public enum AssetBudgetKind { Encoded, Decoded, Resident, Requests }

public sealed class AssetBudgetExceededException(AssetBudgetKind kind)
    : InvalidOperationException($"The {kind} asset budget cannot admit this request.")
{
    public AssetBudgetKind Kind { get; } = kind;
}

public readonly record struct AssetLibraryStatistics(long EncodedBytes, long DecodedBytes,
    long ResidentBytes, int PendingAssets, int ResidentAssets, long Reads, long CacheHits, long CoalescedRequests);
