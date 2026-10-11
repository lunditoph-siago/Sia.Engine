namespace Sia.Engine.Mesh;

public sealed partial class MeshRasterData
{
    public void Save(Stream stream) => MeshGeometryCodec.Write(stream, "SIAMSHR1"u8,
        Vertices, Indices, Sections, BakeUvs, Bounds, SourceHash, BuildKey);

    public static MeshRasterData Load(Stream stream, ulong maximumBytes = 128ul * 1024 * 1024)
        => new(MeshGeometryCodec.Read(stream, "SIAMSHR1"u8, maximumBytes, raster: true));
}
