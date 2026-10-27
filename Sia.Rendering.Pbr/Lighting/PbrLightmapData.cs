using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>
/// A stable authored instance/material receiver. Resolution describes canonical chart coordinates;
/// FirstMip declares the first stored coefficient grid without changing those coordinates or chart support.
/// </summary>
public readonly partial record struct PbrLightmapReceiver(Guid InstanceId, int MaterialSlot, string SourceHash,
    int Resolution, int Padding, int FirstChart, int ChartCount, PbrLightmapQuantization Quantization, int FirstMip = 0);

/// <summary>Receiver-local filtering ownership and intrinsic lighting; no geometry or page payload.</summary>
public readonly record struct PbrLightmapChart(int Receiver, int X, int Y, int Width, int Height, PbrLightmapCoefficients Coarse);

public readonly record struct PbrLightmapPageDescriptor(AssetId Id, string ContentHash,
    int Receiver, int Level, int X, int Y, int Edge)
{
    public int EncodedBytes => 92 + Edge * Edge * 25;
    public AssetRefer<PbrLightmapPageRecord>.Id Reference => new(Id);
    public AssetContent<PbrLightmapPageRecord> Content => new(Id, [new AssetChunk(ContentHash, EncodedBytes)]);
}

/// <summary>Lightweight static lighting manifest. Pages, mesh geometry, shading and scene instances are separate assets.</summary>
public sealed partial class PbrLightmapData
{
    public const int MaximumReceivers = 4096;
    public const int MaximumPages = 65536;
    public const int MaximumCharts = 65536;
    public const int MaximumEncodedBytes = 8 * 1024 * 1024;
    private readonly PbrLightmapReceiver[] _receivers;
    private readonly PbrLightmapPageDescriptor[] _pages;
    private readonly PbrLightmapChart[] _charts;
    public AssetId ProductId { get; }
    public string SceneIdentity { get; }
    public string BakeIdentity { get; }
    /// <summary>Exact inputs to the fixed surface-lighting integrator, retained for reference transport.</summary>
    public PbrLightmapBakeSettings BakeSettings { get; }
    public ReadOnlySpan<PbrLightmapReceiver> Receivers => _receivers;
    public ReadOnlySpan<PbrLightmapPageDescriptor> Pages => _pages;
    public ReadOnlySpan<PbrLightmapChart> Charts => _charts;
    public ulong EncodedBytes => 288ul + (ulong)_receivers.Length * 120 + (ulong)_charts.Length * 48 + (ulong)_pages.Length * 68;
    /// <summary>Array elements and hash characters; excludes object headers, temporary validation sets and external pages.</summary>
    public ulong PayloadBytes => OwnedBytes(_receivers.Length, _charts.Length, _pages.Length) + BakeSettings.EnvironmentPayloadBytes;

    private PbrLightmapData(AssetId productId, string sceneIdentity, PbrLightmapBakeSettings bakeSettings,
        PbrLightmapReceiver[] receivers, PbrLightmapChart[] charts, PbrLightmapPageDescriptor[] pages, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(bakeSettings); bakeSettings.Validate();
        ProductId = productId; SceneIdentity = sceneIdentity; BakeSettings = bakeSettings;
        _receivers = receivers; _charts = charts; _pages = pages; Validate(token);
        BakeIdentity = ComputeBakeIdentity(token);
    }

    public static PbrLightmapData Create(AssetId productId, string sceneIdentity, PbrLightmapBakeSettings bakeSettings,
        ReadOnlySpan<PbrLightmapReceiver> receivers, ReadOnlySpan<PbrLightmapChart> charts, ReadOnlySpan<PbrLightmapPageDescriptor> pages,
        ulong maximumBytes = MaximumEncodedBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bakeSettings); bakeSettings.Validate();
        Admit(receivers.Length, charts.Length, pages.Length, maximumBytes, bakeSettings.Environment is not null); cancellationToken.ThrowIfCancellationRequested();
        var copied = charts.ToArray();
        for (var at = 0; at < copied.Length; at++) {
            if ((at & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var chart = copied[at];
            if ((uint)chart.Receiver >= receivers.Length) throw new ArgumentException("Chart refers outside the receiver set.", nameof(charts));
            var quantization = receivers[chart.Receiver].Quantization; quantization.Validate();
            copied[at] = chart with { Coarse = quantization.Quantize(chart.Coarse) };
        }
        return new(productId, sceneIdentity, bakeSettings, receivers.ToArray(), copied, pages.ToArray(), cancellationToken);
    }

    public void ValidatePage(int index, PbrLightmapPageData page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if ((uint)index >= _pages.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var descriptor = _pages[index];
        if (descriptor.ContentHash != page.ContentHash || descriptor.Edge != page.Edge || descriptor.EncodedBytes != page.EncodedBytes
            || page.Quantization != _receivers[descriptor.Receiver].Quantization)
            throw new InvalidDataException("Lightmap page differs from its parent manifest.");
    }

    /// <summary>Checks receiver/source binding without acquiring any representation or page.</summary>
    public void ValidateReceiver(int index, PbrMeshInstance instance, MeshManifest mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if ((uint)index >= _receivers.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var receiver = _receivers[index];
        if (instance.Id != receiver.InstanceId || !mesh.HasBakeUvs || mesh.SourceHash != receiver.SourceHash
            || !mesh.MaterialSlots.Contains(receiver.MaterialSlot))
            throw new InvalidDataException("Lightmap receiver differs from its canonical instance/source/material binding.");
    }

    /// <summary>Returns an owned chart rectangle containing the canonical normalized bake UV, or -1.</summary>
    public int FindChart(int receiver, float2 bakeUv)
    {
        if ((uint)receiver >= _receivers.Length) throw new ArgumentOutOfRangeException(nameof(receiver));
        if (!math.all(math.isfinite(bakeUv))) throw new ArgumentException("Bake UV must be finite.", nameof(bakeUv));
        var data = _receivers[receiver]; var point = (double2)bakeUv * data.Resolution;
        for (var at = data.FirstChart; at < data.FirstChart + data.ChartCount; at++) {
            var chart = _charts[at];
            if (point.x >= chart.X && point.y >= chart.Y && point.x < chart.X + chart.Width && point.y < chart.Y + chart.Height) return at;
        }
        return -1;
    }

    /// <summary>Both bilinear taps' complete fine footprints must lie in this chart, including its border.</summary>
    public bool SupportsBilinear(int chartIndex, int level, float2 bakeUv)
    {
        if ((uint)chartIndex >= _charts.Length) throw new ArgumentOutOfRangeException(nameof(chartIndex));
        if (level is < 0 or > 12) throw new ArgumentOutOfRangeException(nameof(level));
        if (!math.all(math.isfinite(bakeUv))) throw new ArgumentException("Bake UV must be finite.", nameof(bakeUv));
        var chart = _charts[chartIndex]; var resolution = _receivers[chart.Receiver].Resolution;
        var dimension = resolution >> level; if (dimension < 2) return false;
        var cell = math.floor((double2)bakeUv * dimension - new double2(.5)); var factor = 1 << level;
        return cell.x * factor >= chart.X && cell.y * factor >= chart.Y
            && (cell.x + 2) * factor <= chart.X + chart.Width && (cell.y + 2) * factor <= chart.Y + chart.Height;
    }

    /// <summary>Logical slot identity independent of page contents; preserves IDs across recooks.</summary>
    public static AssetId PageId(AssetId productId, Guid instanceId, int materialSlot, int level, int x, int y)
    {
        if (!productId.IsValid || instanceId == Guid.Empty || materialSlot < 0 || level < 0 || x < 0 || y < 0)
            throw new ArgumentException("Invalid lightmap page slot identity.");
        Span<byte> slot = stackalloc byte[48];
        productId.Value.TryWriteBytes(slot[..16], bigEndian: true, out _);
        instanceId.TryWriteBytes(slot.Slice(16, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32LittleEndian(slot[32..], materialSlot);
        BinaryPrimitives.WriteInt32LittleEndian(slot[36..], level);
        BinaryPrimitives.WriteInt32LittleEndian(slot[40..], x);
        BinaryPrimitives.WriteInt32LittleEndian(slot[44..], y);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIALIGHTMAPSLOT"u8); hash.AppendData(slot);
        Span<byte> digest = stackalloc byte[32]; hash.GetHashAndReset(digest);
        digest[6] = (byte)((digest[6] & 15) | 128); digest[8] = (byte)((digest[8] & 63) | 128);
        return new(new Guid(digest[..16], bigEndian: true));
    }

    private static ulong OwnedBytes(int receivers, int charts, int pages) =>
        (ulong)receivers * ((ulong)Unsafe.SizeOf<PbrLightmapReceiver>() + 128)
        + (ulong)charts * (ulong)Unsafe.SizeOf<PbrLightmapChart>()
        + (ulong)pages * ((ulong)Unsafe.SizeOf<PbrLightmapPageDescriptor>() + 128) + 360;

    /// <summary>Pre-admits counts and returns the larger encoded/owned payload reservation before output exists.</summary>
    public static ulong RequiredBytes(int receivers, int charts, int pages)
        => RequiredBytes(receivers, charts, pages, false);

    public static ulong RequiredBytes(int receivers, int charts, int pages, PbrLightmapBakeSettings? settings)
        => RequiredBytes(receivers, charts, pages, settings?.Environment is not null);

    private static ulong RequiredBytes(int receivers, int charts, int pages, bool environment)
    {
        if (receivers is < 1 or > MaximumReceivers || charts < receivers || charts > MaximumCharts || pages is < 1 or > MaximumPages)
            throw new ArgumentException("Invalid lightmap manifest counts.");
        var encoded = 288ul + (ulong)receivers * 120 + (ulong)charts * 48 + (ulong)pages * 68;
        if (encoded > MaximumEncodedBytes)
            throw new ArgumentException("Lightmap manifest exceeds its encoded/owned payload budget.");
        return System.Math.Max(encoded, OwnedBytes(receivers, charts, pages) + (environment ? 256ul : 0));
    }

    private static void Admit(int receivers, int charts, int pages, ulong maximumBytes, bool environment = false)
    {
        if (RequiredBytes(receivers, charts, pages, environment) > maximumBytes)
            throw new ArgumentException("Lightmap manifest exceeds its encoded/owned payload budget.");
    }

    private static bool ValidHash(string? hash) => hash is { Length: 64 }
        && hash.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private void Validate(CancellationToken token)
    {
        if (!ProductId.IsValid || !ValidHash(SceneIdentity))
            throw new ArgumentException("Invalid lightmap manifest identity.");
        var ids = new HashSet<AssetId>(); var receivers = new HashSet<(Guid, int)>(); var pageIndex = 0; var chartIndex = 0;
        if (BakeSettings.Environment is { } environment && (environment.Id == ProductId || !ids.Add(environment.Id)))
            throw new ArgumentException("Lightmap environment aliases its product identity.");
        for (var r = 0; r < _receivers.Length; r++) {
            token.ThrowIfCancellationRequested(); var receiver = _receivers[r];
            if (receiver.InstanceId == Guid.Empty || receiver.MaterialSlot < 0 || !ValidHash(receiver.SourceHash)
                || receiver.Resolution is < 2 or > 8192 || (receiver.Resolution & (receiver.Resolution - 1)) != 0
                || receiver.Padding < 0 || receiver.Padding > (receiver.Resolution - 2) / 2
                || receiver.FirstMip is < 0 or > 12 || (receiver.Resolution >> receiver.FirstMip) < 2
                || receiver.FirstChart != chartIndex || receiver.ChartCount < 1 || receiver.ChartCount > _charts.Length - chartIndex
                || !receivers.Add((receiver.InstanceId, receiver.MaterialSlot)))
                throw new ArgumentException("Invalid or duplicate static lightmap receiver.");
            receiver.Quantization.Validate();
            ValidateCharts(r, receiver, token); chartIndex += receiver.ChartCount;
            for (var level = receiver.FirstMip; (receiver.Resolution >> level) >= 2; level++) {
                var dimension = receiver.Resolution >> level;
                var edge = System.Math.Min(PbrLightmapPageData.MaximumEdge, dimension);
                for (var y = 0; y < dimension; y += edge)
                    for (var x = 0; x < dimension; x += edge) {
                        token.ThrowIfCancellationRequested();
                        if (pageIndex >= _pages.Length) throw new ArgumentException("Lightmap manifest leaves receiver mip coverage incomplete.");
                        var page = _pages[pageIndex++];
                        if (page.Receiver != r || page.Level != level || page.X != x || page.Y != y || page.Edge != edge
                            || !ValidHash(page.ContentHash) || page.Id == ProductId || !ids.Add(page.Id)
                            || page.Id != PageId(ProductId, receiver.InstanceId, receiver.MaterialSlot, level, x, y))
                            throw new ArgumentException("Lightmap page slots must cover complete ordered receiver mip grids with stable IDs.");
                    }
            }
        }
        if (pageIndex != _pages.Length || chartIndex != _charts.Length) throw new ArgumentException("Lightmap manifest has unused page/chart descriptors.");
    }

    private void ValidateCharts(int receiverIndex, PbrLightmapReceiver receiver, CancellationToken token)
    {
        var events = new (int Y, int Change)[receiver.ChartCount * 2];
        for (var local = 0; local < receiver.ChartCount; local++) {
            token.ThrowIfCancellationRequested(); var chart = _charts[receiver.FirstChart + local];
            if (chart.Receiver != receiverIndex || chart.X < 0 || chart.Y < 0
                || chart.Width < 2 * receiver.Padding + 2 || chart.Height < 2 * receiver.Padding + 2
                || chart.X > receiver.Resolution - chart.Width || chart.Y > receiver.Resolution - chart.Height
                || !chart.Coarse.Covered)
                throw new ArgumentException("Invalid chart ownership, bounds or intrinsic coverage.");
            for (var component = 0; component < 4; component++) {
                var value = chart.Coarse.Component(component); PbrLightmapQuantization.ValidateCoefficient(value, true);
                for (var channel = 0; channel < 3; channel++) _ = receiver.Quantization.Encode(component, channel, value[channel]);
            }
            events[local * 2] = (chart.Y, local + 1); events[local * 2 + 1] = (chart.Y + chart.Height, -local - 1);
        }
        Array.Sort(events); var columns = new int[receiver.Resolution];
        foreach (var item in events) {
            token.ThrowIfCancellationRequested(); var chart = _charts[receiver.FirstChart + System.Math.Abs(item.Change) - 1];
            var occupied = columns.AsSpan(chart.X, chart.Width);
            if (item.Change < 0) { occupied.Clear(); continue; }
            foreach (var column in occupied) if (column != 0) throw new ArgumentException("Receiver chart interiors/filtering borders overlap.");
            occupied.Fill(item.Change);
        }
        // Disjoint accepted rectangles visit at most 2*resolution^2 columns; scratch is
        // 16 bytes/chart plus 4 bytes/column, not an unbounded pairwise overlap test.
    }
}
