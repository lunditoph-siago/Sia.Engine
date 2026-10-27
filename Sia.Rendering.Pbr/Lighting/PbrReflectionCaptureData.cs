using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Typed content dependency on a shared independent environment payload.</summary>
public readonly record struct PbrReflectionEnvironmentReference(AssetId Id, string ContentHash)
{
    public AssetRefer<IblEnvironmentRecord>.Id Reference => new(Id);
    public AssetContent<IblEnvironmentRecord> Content => new(Id, [new AssetChunk(ContentHash, IblEnvironmentAsset.EncodedBytes)]);
}

/// <summary>Frozen capture producer inputs; effects and ownership belong to the caller.</summary>
public sealed record PbrReflectionCaptureSettings
{
    public float3 TowardLight { get; init; } = new(0, 1, 0);
    public float3 LightRadiance { get; init; }
    public int FilterSamples { get; init; } = 64;
    public float MaximumDistance { get; init; } = 1000;
    public float RayBias { get; init; } = .002f;

    internal void Validate()
    {
        if (!math.all(math.isfinite(TowardLight)) || !float.IsFinite(math.lengthsq(TowardLight)) || math.lengthsq(TowardLight) <= 1e-8f
            || !math.all(math.isfinite(LightRadiance)) || math.any(LightRadiance < 0) || math.any(LightRadiance > 1e10f)
            || FilterSamples is < 16 or > 1024 || !float.IsFinite(MaximumDistance) || MaximumDistance <= 0
            || !float.IsFinite(RayBias) || RayBias <= 0 || RayBias >= MaximumDistance)
            throw new ArgumentException("Invalid bounded capture lighting/filter inputs.");
    }

    internal void Encode(Span<byte> bytes)
    {
        PbrReflectionCaptureData.Vector(bytes, TowardLight); PbrReflectionCaptureData.Vector(bytes[12..], LightRadiance);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[24..], FilterSamples);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[28..], MaximumDistance); BinaryPrimitives.WriteSingleLittleEndian(bytes[32..], RayBias);
    }
    internal static PbrReflectionCaptureSettings Decode(ReadOnlySpan<byte> bytes) => new() {
        TowardLight = PbrReflectionCaptureData.Vector(bytes), LightRadiance = PbrReflectionCaptureData.Vector(bytes[12..]),
        FilterSamples = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]),
        MaximumDistance = BinaryPrimitives.ReadSingleLittleEndian(bytes[28..]), RayBias = BinaryPrimitives.ReadSingleLittleEndian(bytes[32..])
    };
}

/// <summary>Lightweight finite region and typed dependencies. Contains no environment pixels, asset owner or publication workflow.</summary>
public sealed partial class PbrReflectionCaptureData
{
    public const int MaximumStaticInstances = 8192;
    internal const int HeaderBytes = 276;
    public const int MaximumEncodedBytes = HeaderBytes + MaximumStaticInstances * 16 + 32;
    private readonly Guid[] _staticInstances;
    public AssetId ProductId { get; }
    public AssetRefer<PbrSceneRecord>.Id Scene { get; }
    public string SceneIdentity { get; }
    public string InputIdentity { get; }
    public PbrReflectionEnvironmentReference SourceEnvironment { get; }
    public PbrReflectionEnvironmentReference Environment { get; }
    public float3 Position { get; }
    public Aabb Bounds { get; }
    public PbrReflectionCaptureSettings Settings { get; }
    public ReadOnlySpan<Guid> StaticInstances => _staticInstances;
    public int EncodedBytes => HeaderBytes + _staticInstances.Length * 16 + 32;
    /// <summary>Known retained value/array/hash bytes; excludes object headers and bounded validation scratch.</summary>
    public ulong PayloadBytes => (ulong)_staticInstances.Length * 16 + 648;

    private PbrReflectionCaptureData(AssetId id, AssetRefer<PbrSceneRecord>.Id scene, string identity,
        PbrReflectionEnvironmentReference source, PbrReflectionEnvironmentReference environment,
        float3 position, Aabb bounds, PbrReflectionCaptureSettings settings, Guid[] instances, CancellationToken token)
    {
        ProductId = id; Scene = scene; SceneIdentity = identity; SourceEnvironment = source; Environment = environment;
        Position = position; Bounds = bounds; Settings = settings; _staticInstances = instances;
        Validate(token); InputIdentity = ComputeInputIdentity(token);
    }

    public static PbrReflectionCaptureData Create(AssetId id, AssetRefer<PbrSceneRecord>.Id scene, string identity,
        PbrReflectionEnvironmentReference source, PbrReflectionEnvironmentReference environment,
        float3 position, Aabb bounds, PbrReflectionCaptureSettings settings, ReadOnlySpan<Guid> staticInstances,
        ulong maximumBytes = 1024ul * 1024, CancellationToken cancellationToken = default)
    {
        Admit(staticInstances.Length, maximumBytes); cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(scene);
        var instances = staticInstances.ToArray(); Array.Sort(instances);
        return new(id, scene, identity, source, environment, position, bounds, settings, instances, cancellationToken);
    }

    private static void Admit(int count, ulong maximumBytes)
    {
        if (count is < 1 or > MaximumStaticInstances || (ulong)count * 16 + 648 > maximumBytes)
            throw new ArgumentException("Capture static-domain metadata exceeds its shape/owned-array admission.");
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private void Validate(CancellationToken token)
    {
        Settings.Validate();
        if (!ProductId.IsValid || !Scene.Value.IsValid || !SourceEnvironment.Id.IsValid || !Environment.Id.IsValid
            || ProductId == Scene.Value || ProductId == SourceEnvironment.Id || ProductId == Environment.Id
            || Scene.Value == SourceEnvironment.Id || Scene.Value == Environment.Id || !Hash(SceneIdentity)
            || !Hash(SourceEnvironment.ContentHash) || !Hash(Environment.ContentHash)
            || SourceEnvironment.Id == Environment.Id && SourceEnvironment.ContentHash != Environment.ContentHash)
            throw new ArgumentException("Capture dependencies have invalid or aliased typed identities.");
        ValidateRegion(Position, Bounds);
        for (var at = 0; at < _staticInstances.Length; at++) {
            token.ThrowIfCancellationRequested();
            if (_staticInstances[at] == Guid.Empty || at > 0 && _staticInstances[at - 1].CompareTo(_staticInstances[at]) >= 0)
                throw new ArgumentException("Capture static instance IDs must be nonempty, unique and canonical.");
        }
    }

    internal static void ValidateRegion(float3 position, Aabb bounds)
    {
        if (!math.all(math.isfinite(position)) || !math.all(math.isfinite(bounds.Min)) || !math.all(math.isfinite(bounds.Max))
            || !math.all(bounds.Min < position & position < bounds.Max) || !math.all(math.isfinite(bounds.Max - bounds.Min)))
            throw new ArgumentException("Capture point must be strictly inside a finite nonempty region.");
    }

    public PbrBvhScene SelectReference(PbrBvhScene scene, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var selected = scene.SelectInstances(_staticInstances, maximumBytes, cancellationToken);
        if (Convert.ToHexString(selected.TransportIdentity.Span) != SceneIdentity)
            throw new InvalidDataException("Capture static source, material or transform domain is stale.");
        return selected;
    }

    private string ComputeInputIdentity(CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAPBRREFLECTIONINPUT"u8); hash.AppendData(Convert.FromHexString(SceneIdentity));
        hash.AppendData(Convert.FromHexString(SourceEnvironment.ContentHash));
        Span<byte> values = stackalloc byte[72]; Vector(values, Position); Vector(values[12..], Bounds.Min); Vector(values[24..], Bounds.Max);
        Settings.Encode(values[36..]); hash.AppendData(values);
        Span<byte> id = stackalloc byte[16];
        foreach (var instance in _staticInstances) { token.ThrowIfCancellationRequested(); instance.TryWriteBytes(id, bigEndian: true, out _); hash.AppendData(id); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void Vector(Span<byte> bytes, float3 value)
    { BinaryPrimitives.WriteSingleLittleEndian(bytes, value.x); BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], value.y); BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], value.z); }
    internal static float3 Vector(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
}
