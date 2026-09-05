namespace Sia.Engine.Rendering;

public sealed class ShaderAsset
{
    public ShaderAsset(
        ShaderAssetId id,
        string wgsl,
        ulong revision = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(wgsl);
        Id = id;
        Wgsl = wgsl;
        Revision = revision;
    }

    public ShaderAssetId Id { get; }

    public string Wgsl { get; }

    public ulong Revision { get; }

    public ShaderAsset WithWgsl(string wgsl) => new(Id, wgsl, checked(Revision + 1));
}
