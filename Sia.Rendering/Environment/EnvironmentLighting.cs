using Sia;

namespace Sia.Engine.Rendering;

public sealed class EnvironmentLighting : IAddon
{
    public ProceduralSky Sky { get; set; } = new();

    public SkyAtmosphere? Atmosphere { get; set; }
}
