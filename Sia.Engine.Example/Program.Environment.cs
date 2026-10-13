using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Sia;
using Sia.Asset;
using Sia.Engine.Rendering;

namespace Sia.Engine.Example;

// Application IO and acquisition composition; Rendering owns the record/value codecs.
internal static class LightingFiles
{
    internal sealed record Prepared(IblEnvironmentRecord? Environment, DiffuseProbeRecord? Probes);

    internal static async Task<Prepared> PrepareAsync(string? environment, string? probes, HttpClient http,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http); cancellationToken.ThrowIfCancellationRequested();
        var preparedEnvironment = environment is null ? null
            : await ReadAsync<IblEnvironmentRecord>(environment, http, IblEnvironmentAsset.EncodedBytes, exact: true, cancellationToken);
        const int maximumProbeBytes = 76 + DiffuseProbeAsset.MaximumProbes * 9 * 16 + 32;
        var preparedProbes = probes is null ? null
            : await ReadAsync<DiffuseProbeRecord>(probes, http, maximumProbeBytes, exact: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(preparedEnvironment, preparedProbes);
    }

    private static async Task<TRecord> ReadAsync<TRecord>(string path, HttpClient http, int maximumBytes,
        bool exact, CancellationToken cancellationToken) where TRecord : ILoadable<TRecord>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = await AssetInput.OpenAsync(path, http, cancellationToken);
        ReadOnlyMemory<byte> bytes = exact ? await AssetInput.ReadExactAsync(input, maximumBytes, cancellationToken)
            : await AssetInput.ReadBoundedAsync(input, maximumBytes, cancellationToken);
        if (!MemoryMarshal.TryGetArray(bytes, out var segment))
            throw new InvalidOperationException("Asset input must return an array-backed payload.");
        TRecord Decode(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            using var payload = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
            return TRecord.Load(payload, path);
        }
#if BROWSER
        return await Program.BrowserOwner.RunCpuAsync(Decode, cancellationToken);
#else
        return Decode(cancellationToken);
#endif
    }

    // Synchronous owner-context operation. The caller retains this root until renderers are disposed
    // and destroys it on partial initialization failure. Shared assets keep their existing lifetimes.
    internal static (Entity Environment, Entity Probes) Acquire(World world, Prepared prepared, Entity owner)
    {
        ArgumentNullException.ThrowIfNull(world); ArgumentNullException.ThrowIfNull(prepared);
        if (!owner.IsValid || !ReferenceEquals(owner.Host.World, world)
            || Unsafe.IsNullRef(ref owner.GetOrNullRef<AssetMetadata>()))
            throw new ArgumentException("Lighting owner must be an asset entity in the target World.", nameof(owner));
        _ = world.GetAddon<AssetLibrary>();
        AssetLibrary.RegisterAsset<IblEnvironmentResource, IblEnvironmentRecord>();
        AssetLibrary.RegisterAsset<DiffuseProbeResource, DiffuseProbeRecord>();
        var environment = prepared.Environment is { } e ? world.AcquireAsset(e, owner) : default;
        var probes = prepared.Probes is { } p ? world.AcquireAsset(p, owner) : default;
        return (environment, probes);
    }
}
