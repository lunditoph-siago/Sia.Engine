using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>One static, box-projected reflection region. Dynamic instances are excluded from its source domain.</summary>
public sealed class PbrReflectionCaptureAsset
{
    public ReadOnlyMemory<byte> SceneIdentity { get; }
    public ReadOnlyMemory<byte> EnvironmentIdentity { get; }
    public float3 Position { get; }
    public Aabb Bounds { get; }
    public IblEnvironmentAsset Environment { get; }
    private const int k_PayloadBytes = 8 + 64 + 36 + IblEnvironmentAsset.EncodedBytes;
    public const int EncodedBytes = k_PayloadBytes + 32;

    internal PbrReflectionCaptureAsset(ReadOnlySpan<byte> identity, ReadOnlySpan<byte> environmentIdentity,
        float3 position, Aabb bounds, IblEnvironmentAsset environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ValidateRegion(position, bounds);
        if (identity.Length != 32 || environmentIdentity.Length != 32)
            throw new InvalidDataException("Capture requires static scene and source environment identities.");
        SceneIdentity = identity.ToArray();
        EnvironmentIdentity = environmentIdentity.ToArray();
        (Position, Bounds, Environment) = (position, bounds, environment);
    }

    internal static byte[] HashEnvironment(IblEnvironmentAsset environment)
    {
        using var bytes = new MemoryStream(IblEnvironmentAsset.EncodedBytes);
        environment.Write(bytes);
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
    }

    internal static void ValidateRegion(float3 position, Aabb bounds)
    {
        if (!math.all(math.isfinite(position)) || !math.all(math.isfinite(bounds.Min))
            || !math.all(math.isfinite(bounds.Max)) || !math.all(bounds.Min < position & position < bounds.Max)
            || !math.all(math.isfinite(bounds.Max - bounds.Min)))
            throw new ArgumentException("Capture point must be finite and strictly inside a finite nonempty box.");
    }

    public void Write(Stream destination) => ChecksummedAsset.Write<PbrReflectionCaptureAsset, Codec>(destination, this);

    public static PbrReflectionCaptureAsset Read(Stream source) => ChecksummedAsset.Read<PbrReflectionCaptureAsset, Codec>(source);

    private readonly struct Codec : IChecksummedAssetCodec<PbrReflectionCaptureAsset>
    {
        public static int HeaderBytes => 0;
        public static int GetReadSize(ReadOnlySpan<byte> header) => k_PayloadBytes;
        public static int GetWriteSize(PbrReflectionCaptureAsset value) => k_PayloadBytes;

        public static void WritePayload(BinaryWriter writer, PbrReflectionCaptureAsset value)
        {
            writer.Write("SIAREFL1"u8);
            writer.Write(value.SceneIdentity.Span);
            writer.Write(value.EnvironmentIdentity.Span);
            void Vector(float3 v) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
            Vector(value.Position); Vector(value.Bounds.Min); Vector(value.Bounds.Max);
            value.Environment.Write(writer.BaseStream);
        }

        public static PbrReflectionCaptureAsset ReadPayload(BinaryReader reader)
        {
            if (!reader.ReadBytes(8).AsSpan().SequenceEqual("SIAREFL1"u8)) throw new InvalidDataException("Unsupported reflection capture version.");
            var identity = reader.ReadBytes(32);
            var environmentIdentity = reader.ReadBytes(32);
            float3 Vector() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var position = Vector(); var bounds = new Aabb(Vector(), Vector());
            return new(identity, environmentIdentity, position, bounds, IblEnvironmentAsset.Read(reader.BaseStream));
        }
    }
}
