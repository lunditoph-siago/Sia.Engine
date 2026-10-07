using System.Security.Cryptography;

namespace Sia.Engine.Mesh;

public sealed partial class MeshPatchAsset
{
    public MeshPatchBuildResult Build { get; }
    public MeshPatchBuildSettings Settings { get; }
    public string SourceHash { get; }
    /// <summary>At least one retained vertex has nonzero bake coordinates; does not certify chart validity.</summary>
    public bool HasLightmapUV { get; }

    private MeshPatchAsset(MeshPatchBuildResult build, MeshPatchBuildSettings settings, string sourceHash)
    {
        Build = build;
        Settings = settings;
        SourceHash = sourceHash;
        foreach (var vertex in build.Tree.Vertices)
            if (vertex.LightmapUV.x != 0 || vertex.LightmapUV.y != 0) { HasLightmapUV = true; break; }
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
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new MeshData([.. source.Vertices], [.. source.Indices], source.Bounds);
        var options = settings ?? MeshPatchBuildSettings.Default;
        var build = MeshPatchBuilder.Build(snapshot, options, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lightmap = snapshot.Vertices.Any(v => v.LightmapUV.x != 0 || v.LightmapUV.y != 0);
        Span<byte> buffer = stackalloc byte[lightmap ? 56 : 48];
        if (lightmap) hash.AppendData("SIAMESHUV1"u8);
        var writer = new Writer(buffer);
        writer.Int(snapshot.Vertices.Length);
        writer.Int(snapshot.Indices.Length);
        hash.AppendData(buffer[..8]);
        foreach (var vertex in snapshot.Vertices) {
            cancellationToken.ThrowIfCancellationRequested();
            writer = new(buffer);
            writer.Vertex(vertex, lightmap);
            hash.AppendData(buffer);
        }
        foreach (var index in snapshot.Indices) {
            cancellationToken.ThrowIfCancellationRequested();
            writer = new(buffer);
            writer.UInt(index);
            hash.AppendData(buffer[..4]);
        }
        return new(build, options, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
