using System.Security.Cryptography;

namespace Sia.Engine.Mesh;

public sealed class MeshPatchAsset
{
    public MeshPatchBuildResult Build { get; }
    public MeshPatchBuildSettings Settings { get; }
    public string SourceHash { get; }

    private MeshPatchAsset(MeshPatchBuildResult build, MeshPatchBuildSettings settings, string sourceHash)
    {
        Build = build;
        Settings = settings;
        SourceHash = sourceHash;
    }

    public MeshPatchAsset ExtractFinest() => new(
        new(Build.Tree.ExtractFinest(), Build.SourceTriangleCount, Build.RemovedDegenerateTriangleCount, 0, 0, 0),
        Settings, SourceHash);

    public MeshPatchAsset ExtractRoots()
    {
        var tree = Build.Tree.ExtractRoots();
        return new(new(tree, tree.FinestTriangleCount, 0, 0, 0, 0), Settings, SourceHash);
    }

    public static MeshPatchAsset Cook(MeshData source, MeshPatchBuildSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Vertices);
        ArgumentNullException.ThrowIfNull(source.Indices);
        source.ValidateBakeUvs();
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new MeshData([.. source.Vertices], [.. source.Indices], source.Bounds) { BakeUvs = [.. source.BakeUvs] };
        var options = settings ?? MeshPatchBuildSettings.Default;
        var build = MeshPatchBuilder.Build(snapshot, options, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (snapshot.HasBakeUvs) hash.AppendData("SIAPATCHBAKE1"u8);
        Span<byte> buffer = stackalloc byte[32 * 1024];
        var writer = new MeshDataIO.Writer(Stream.Null, hash, buffer, cancellationToken);
        writer.Int(snapshot.Vertices.Length);
        writer.Int(snapshot.Indices.Length);
        foreach (var vertex in snapshot.Vertices) {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Vertex(vertex);
        }
        foreach (var index in snapshot.Indices) {
            cancellationToken.ThrowIfCancellationRequested();
            writer.UInt(index);
        }
        foreach (var uv in snapshot.BakeUvs) {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Float(uv.x); writer.Float(uv.y);
        }
        writer.Flush();
        return new(build, options, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
