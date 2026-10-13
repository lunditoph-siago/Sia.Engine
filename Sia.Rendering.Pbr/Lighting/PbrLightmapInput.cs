using ProductReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrLightmapRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>
/// Borrowed acquired lighting set and caller-owned typed member/page preparation.
/// ValidateScene before use; retain the set and transport through renderer shutdown.
/// Member acquisition runs on the owner context and returns its explicit inverse;
/// renderer retirement/disposal invokes it on that owner. Page use is short-lived.
/// </summary>
public sealed record PbrLightmapInput(PbrLightmapSetData Set,
    Func<ProductReference, CancellationToken, Task<PbrLightmapRecord>> PrepareProduct,
    Func<ProductReference, PbrLightmapRecord, (PbrLightmapData Data, Action Release)> AcquireProduct,
    Func<PbrLightmapData, int, CancellationToken, Task<PbrLightmapPageRecord>> PreparePage,
    Func<PbrLightmapData, int, PbrLightmapPageRecord, PbrLightmapPageData> AcquirePage);

public sealed record PbrLightmapStreamingSettings
{
    public int ResidentPages { get; init; } = 256;
    public int PageAddresses { get; init; } = PbrLightmapData.MaximumPages;
    public ulong GraphBytes { get; init; } = 16ul * 1024 * 1024;
    public long ProductDecodedBytes { get; init; } = 16L * 1024 * 1024;
    public int ProductRequests { get; init; } = 2;
    public ulong GpuBytes { get; init; } = 64ul * 1024 * 1024;
    /// <summary>Padded table, reusable tree scratch and resident demand arrays; excludes object bookkeeping.</summary>
    public ulong MetadataBytes { get; init; } = 32ul * 1024 * 1024;
    public long DecodedBytes { get; init; } = 2L * 1024 * 1024;
    public int MaximumRequests { get; init; } = 4;
    public uint UploadBytesPerFrame { get; init; } = 512 * 1024;
    public int IdleFrames { get; init; } = 120;
}
