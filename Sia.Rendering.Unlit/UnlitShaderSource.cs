namespace Sia.Engine.Rendering.Unlit;

public static class UnlitShaderSource
{
    public static ShaderAsset Load()
    {
        using var stream = typeof(UnlitShaderSource).Assembly.GetManifestResourceStream(
            "Sia.Rendering.Unlit.Shaders.unlit.wgsl") ??
            throw new InvalidOperationException("The embedded Unlit shader is missing.");
        using var reader = new StreamReader(stream);
        return new ShaderAsset(new ShaderAssetId("sia:unlit"), reader.ReadToEnd());
    }
}
