using System.Security.Cryptography;

namespace Sia.Engine.Mesh;

public sealed partial class MeshPatchAsset
{
    public const int FormatVersion = 2;
    public const int CurrentBuilderVersion = 3;

    public MeshPatchBuildResult Build { get; }
    public MeshPatchBuildSettings Settings { get; }
    public string SourceHash { get; }
    public int BuilderVersion { get; }

    private MeshPatchAsset(MeshPatchBuildResult build, MeshPatchBuildSettings settings, string sourceHash, int builderVersion)
    {
        Build = build;
        Settings = settings;
        SourceHash = sourceHash;
        BuilderVersion = builderVersion;
    }

    public MeshPatchAsset ExtractFinest() => new(
        new(Build.Tree.ExtractFinest(), Build.SourceTriangleCount, Build.RemovedDegenerateTriangleCount, 0, 0, 0),
        Settings, SourceHash, BuilderVersion);

    public static MeshPatchAsset Cook(MeshData source, MeshPatchBuildSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Vertices);
        ArgumentNullException.ThrowIfNull(source.Indices);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new MeshData(source.Vertices.ToArray(), source.Indices.ToArray(), source.Bounds);
        var options = settings ?? MeshPatchBuildSettings.Default;
        var build = MeshPatchBuilder.Build(snapshot, options, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[48];
        var writer = new Writer(buffer);
        writer.Int(snapshot.Vertices.Length);
        writer.Int(snapshot.Indices.Length);
        hash.AppendData(buffer[..8]);
        foreach (var vertex in snapshot.Vertices) {
            cancellationToken.ThrowIfCancellationRequested();
            writer = new(buffer);
            writer.Vertex(vertex);
            hash.AppendData(buffer);
        }
        foreach (var index in snapshot.Indices) {
            cancellationToken.ThrowIfCancellationRequested();
            writer = new(buffer);
            writer.UInt(index);
            hash.AppendData(buffer[..4]);
        }
        return new(build, options, Convert.ToHexString(hash.GetHashAndReset()), CurrentBuilderVersion);
    }
}
