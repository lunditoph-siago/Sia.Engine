namespace Sia.Engine.Rendering.Pbr;

internal readonly record struct PbrInitializationStatistics(
    double ValidationMilliseconds,
    double PipelinesMilliseconds,
    double MaterialsMilliseconds,
    double GeometryMilliseconds,
    double EnvironmentMilliseconds,
    double OtherMilliseconds,
    double TotalMilliseconds);
