namespace Sia.Engine.Mesh;

public sealed partial class MeshSource
{
    public void Save(Stream stream) => MeshGeometryCodec.Write(stream, "SIAMSHS\0"u8,
        Vertices, Indices, Sections, BakeUvs, Bounds, ContentHash);

    public static MeshSource Load(Stream stream, ulong maximumBytes = 128ul * 1024 * 1024)
        => MeshGeometryCodec.Read(stream, "SIAMSHS\0"u8, maximumBytes);
}
