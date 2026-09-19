namespace Sia.Engine.Rendering.Pbr;

public readonly record struct VisibilityFrameStatistics(long UploadBytes, int Publications, int InstanceUploads,
    int ShadowsRendered, int ShadowsReused, int VisibilityCacheHits, int HistoryResets, double StreamingMilliseconds);

public sealed partial class VisibilityPbrFeature
{
    private ulong? _statisticsFrame;
    private VisibilityFrameStatistics _frameStatistics;
    public VisibilityFrameStatistics FrameStatistics => _frameStatistics;

    private void BeginStatistics(ulong frame)
    {
        if (_statisticsFrame == frame) return;
        _statisticsFrame = frame;
        _frameStatistics = default;
    }
}
