using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>An independent typed lighting product; byte integrity is checked by the original content loader.</summary>
public readonly record struct PbrLightmapProductReference(AssetId Id, string ContentHash, int EncodedBytes, string BakeIdentity)
{
    public AssetRefer<PbrLightmapRecord>.Id Reference => new(Id);
    public AssetContent<PbrLightmapRecord> Content => new(Id, [new AssetChunk(ContentHash, EncodedBytes)]);
}

/// <summary>Stable binding and bounded support while its independent chart product is unread.</summary>
public readonly record struct PbrLightmapReceiverReference(PbrLightmapReceiver Receiver, int Product, int ProductReceiver,
    PbrLightmapCoefficients Coarse);

/// <summary>Lightweight scene lighting entry. Contains references/bindings, never charts or coefficient page payloads.</summary>
public sealed partial class PbrLightmapSetData
{
    public const int MaximumProducts = 4096;
    public const int MaximumReceivers = 4096;
    public const int MaximumEncodedBytes = 1024 * 1024;
    public const ulong MaximumPayloadBytes = 4ul * 1024 * 1024;
    private readonly PbrLightmapProductReference[] _products;
    private readonly PbrLightmapReceiverReference[] _receivers;
    private readonly (int Receivers, int Charts, int Pages)[] _counts;
    public AssetId ProductId { get; }
    public string SceneIdentity { get; }
    public string BakeIdentity { get; }
    public PbrLightmapBakeSettings BakeSettings { get; }
    public ReadOnlySpan<PbrLightmapProductReference> Products => _products;
    public ReadOnlySpan<PbrLightmapReceiverReference> Receivers => _receivers;
    public ulong EncodedBytes => 284ul + (ulong)_products.Length * 84 + (ulong)_receivers.Length * 152;
    /// <summary>Owned array values, hash characters and settings; excludes object headers and validation scratch.</summary>
    public ulong PayloadBytes => OwnedBytes(_products.Length, _receivers.Length) + BakeSettings.EnvironmentPayloadBytes;

    private PbrLightmapSetData(AssetId id, string scene, PbrLightmapBakeSettings settings,
        PbrLightmapProductReference[] products, PbrLightmapReceiverReference[] receivers, CancellationToken token)
    {
        ProductId=id; SceneIdentity=scene; BakeSettings=settings; _products=products; _receivers=receivers;
        _counts=new (int,int,int)[products.Length];
        Validate(token); BakeIdentity=ComputeBakeIdentity(token);
    }

    public static PbrLightmapSetData Create(AssetId id, string scene, PbrLightmapBakeSettings settings,
        ReadOnlySpan<PbrLightmapProductReference> products, ReadOnlySpan<PbrLightmapReceiverReference> receivers,
        ulong maximumBytes=MaximumPayloadBytes, CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate(); cancellationToken.ThrowIfCancellationRequested();
        Admit(products.Length,receivers.Length,maximumBytes,settings.Environment is not null);
        var copied=receivers.ToArray();
        for (var at=0;at<copied.Length;at++) {
            cancellationToken.ThrowIfCancellationRequested(); var entry=copied[at]; entry.Receiver.Quantization.Validate();
            copied[at]=entry with { Coarse=entry.Receiver.Quantization.Quantize(entry.Coarse) };
        }
        return new(id,scene,settings,products.ToArray(),copied,cancellationToken);
    }

    public static ulong RequiredBytes(int products,int receivers)
        => RequiredBytes(products,receivers,false);

    public static ulong RequiredBytes(int products,int receivers,PbrLightmapBakeSettings? settings)
        => RequiredBytes(products,receivers,settings?.Environment is not null);

    private static ulong RequiredBytes(int products,int receivers,bool environment)
    {
        if (products is <1 or >MaximumProducts || receivers is <1 or >MaximumReceivers)
            throw new ArgumentException("Lighting set product/receiver counts exceed their admission.");
        var encoded=284ul+(ulong)products*84+(ulong)receivers*152;
        if (encoded>MaximumEncodedBytes) throw new ArgumentException("Lighting set exceeds its encoded admission.");
        return System.Math.Max(encoded,OwnedBytes(products,receivers)+(environment?256ul:0));
    }

    private static ulong OwnedBytes(int products,int receivers)
        => 360ul+(ulong)products*((ulong)Unsafe.SizeOf<PbrLightmapProductReference>()+256+(ulong)Unsafe.SizeOf<(int,int,int)>())
            +(ulong)receivers*((ulong)Unsafe.SizeOf<PbrLightmapReceiverReference>()+128);

    private static void Admit(int products,int receivers,ulong maximumBytes,bool environment=false)
    {
        if (RequiredBytes(products,receivers,environment)>maximumBytes)
            throw new ArgumentException("Lighting set exceeds its encoded/owned payload budget.");
    }

    private static bool ValidHash(string? value) => value is { Length:64 }
        && value.All(static c=>c is >= '0' and <= '9' or >= 'A' and <= 'F');

    /// <summary>Stable receiver product identity, independent of lighting output and source revisions.</summary>
    public static AssetId ReceiverProductId(AssetId setId,Guid instance,int materialSlot)
    {
        if (!setId.IsValid || instance==Guid.Empty || materialSlot<0) throw new ArgumentException("Invalid independent receiver product slot.");
        Span<byte> bytes=stackalloc byte[36]; setId.Value.TryWriteBytes(bytes[..16],bigEndian:true,out _);
        instance.TryWriteBytes(bytes.Slice(16,16),bigEndian:true,out _); BinaryPrimitives.WriteInt32LittleEndian(bytes[32..],materialSlot);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("SIALIGHTMAP-RECEIVER"u8); hash.AppendData(bytes);
        var digest=hash.GetHashAndReset(); return new(new Guid(digest.AsSpan(0,16),bigEndian:true));
    }

    private void Validate(CancellationToken token)
    {
        if (!ProductId.IsValid || !ValidHash(SceneIdentity)) throw new ArgumentException("Invalid lighting set identity.");
        BakeSettings.Validate(); var productIds=new HashSet<AssetId>();
        if (BakeSettings.Environment is { } environment && environment.Id==ProductId)
            throw new ArgumentException("Lighting environment aliases its set identity.");
        foreach (var product in _products) {
            token.ThrowIfCancellationRequested();
            if (!product.Id.IsValid || product.Id==ProductId || product.Id==BakeSettings.Environment?.Id || !productIds.Add(product.Id)
                || !ValidHash(product.ContentHash) || !ValidHash(product.BakeIdentity)
                || product.EncodedBytes is <288 or >PbrLightmapData.MaximumEncodedBytes)
                throw new ArgumentException("Invalid or aliased independent lighting product.");
        }
        var instances=new HashSet<(Guid,int)>(); var rows=new Dictionary<(int,int),PbrLightmapReceiver>();
        foreach (var entry in _receivers) {
            token.ThrowIfCancellationRequested(); var receiver=entry.Receiver;
            if ((uint)entry.Product>=_products.Length || entry.ProductReceiver is <0 or >=PbrLightmapData.MaximumReceivers
                || !rows.TryAdd((entry.Product,entry.ProductReceiver),receiver) || !instances.Add((receiver.InstanceId,receiver.MaterialSlot))
                || receiver.InstanceId==Guid.Empty || receiver.MaterialSlot<0 || !ValidHash(receiver.SourceHash)
                || receiver.Resolution is <2 or >8192 || (receiver.Resolution&(receiver.Resolution-1))!=0
                || receiver.Padding<0 || receiver.Padding>(receiver.Resolution-2)/2 || receiver.FirstChart<0
                || receiver.FirstMip is <0 or >12 || (receiver.Resolution>>receiver.FirstMip)<2
                || receiver.ChartCount is <1 or >PbrLightmapData.MaximumCharts
                || receiver.FirstChart>PbrLightmapData.MaximumCharts-receiver.ChartCount || !entry.Coarse.Covered)
                throw new ArgumentException("Invalid lighting receiver binding/support.");
            receiver.Quantization.Validate(); _=receiver.Quantization.Quantize(entry.Coarse);
            ref var counts=ref _counts[entry.Product]; counts.Receivers++; counts.Charts+=receiver.ChartCount;
            for (var dimension=receiver.Resolution>>receiver.FirstMip;dimension>=2;dimension>>=1) {
                var grid=(dimension+63)/64; counts.Pages+=grid*grid;
            }
        }
        for (var at=0;at<_counts.Length;at++) {
            token.ThrowIfCancellationRequested(); var counts=_counts[at];
            _=PbrLightmapData.RequiredBytes(counts.Receivers,counts.Charts,counts.Pages,BakeSettings);
            var firstChart=0;
            for (var row=0;row<counts.Receivers;row++) {
                if (!rows.TryGetValue((at,row),out var receiver) || receiver.FirstChart!=firstChart)
                    throw new ArgumentException("Lighting member receiver rows and chart ranges must be complete and ordered.");
                firstChart+=receiver.ChartCount;
            }
            var encoded=288ul+(ulong)counts.Receivers*120+(ulong)counts.Charts*48+(ulong)counts.Pages*68;
            if (encoded!=(ulong)_products[at].EncodedBytes)
                throw new ArgumentException("Lighting member encoded size differs from its declared receiver shape.");
        }
    }

    /// <summary>Exact independent metadata shape, available before product IO. Does not include coefficient page payloads.</summary>
    public (int Receivers,int Charts,int Pages) ProductCounts(int index)
    {
        if ((uint)index>=_counts.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _counts[index];
    }

    /// <summary>Checks a typed decoded product without IO/acquisition. Its content hash must already be verified by the loader.</summary>
    public void ValidateProduct(int index,PbrLightmapData product,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(product);
        if ((uint)index>=_products.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var expected=_products[index];
        if (product.ProductId!=expected.Id || product.SceneIdentity!=SceneIdentity || product.BakeIdentity!=expected.BakeIdentity
            || product.EncodedBytes!=(ulong)expected.EncodedBytes || !SettingsEqual(BakeSettings,product.BakeSettings))
            throw new InvalidDataException("Lighting product differs from its declared set dependency.");
        var count=0;
        foreach (var entry in _receivers) {
            cancellationToken.ThrowIfCancellationRequested(); if (entry.Product!=index) continue; count++;
            if ((uint)entry.ProductReceiver>=product.Receivers.Length || entry.Receiver!=product.Receivers[entry.ProductReceiver])
                throw new InvalidDataException("Lighting product receiver differs from its declared binding.");
        }
        if (count!=product.Receivers.Length) throw new InvalidDataException("Lighting product contains undeclared receiver rows.");
    }

    public bool SameRevision(PbrLightmapSetData other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ProductId==other.ProductId && SceneIdentity==other.SceneIdentity && SettingsEqual(BakeSettings,other.BakeSettings)
            && _products.AsSpan().SequenceEqual(other._products) && _receivers.AsSpan().SequenceEqual(other._receivers);
    }

    private static bool SettingsEqual(PbrLightmapBakeSettings a,PbrLightmapBakeSettings b)
        => a.SameInputs(b);

    private string ComputeBakeIdentity(CancellationToken token)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIALIGHTMAP-SET-INPUT3"u8);
        Span<byte> bytes=stackalloc byte[PbrLightmapBakeSettings.InputBytes];
        _=Convert.FromHexString(SceneIdentity,bytes[..32],out _,out _); hash.AppendData(bytes[..32]);
        BakeSettings.Encode(bytes); hash.AppendData(bytes);
        var order=Enumerable.Range(0,_receivers.Length).OrderBy(at=>_receivers[at].Receiver.InstanceId)
            .ThenBy(at=>_receivers[at].Receiver.MaterialSlot);
        foreach (var at in order) {
            token.ThrowIfCancellationRequested(); var entry=_receivers[at]; var receiver=entry.Receiver;
            receiver.InstanceId.TryWriteBytes(bytes[..16],bigEndian:true,out _);
            _=Convert.FromHexString(receiver.SourceHash,bytes.Slice(16,32),out _,out _);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[48..],receiver.MaterialSlot);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[52..],receiver.Resolution);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[56..],receiver.Padding);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[60..],receiver.FirstMip); hash.AppendData(bytes[..64]);
            _=Convert.FromHexString(_products[entry.Product].BakeIdentity,bytes[..32],out _,out _); hash.AppendData(bytes[..32]);
        }
        token.ThrowIfCancellationRequested(); return Convert.ToHexString(hash.GetHashAndReset());
    }
}
