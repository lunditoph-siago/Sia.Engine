namespace Sia.Engine.Example;

// Delayed GPU feedback only; CPU/RAF cadence must never drive this controller.
internal sealed class RenderResolutionController
{
    private readonly double targetMilliseconds;
    private readonly float maximumScale;
    public RenderResolutionController(double targetMilliseconds, float maximumScale) {
        if (!double.IsFinite(targetMilliseconds) || targetMilliseconds <= 0 || !float.IsFinite(maximumScale) || maximumScale is < .0625f or > 1)
            throw new ArgumentOutOfRangeException(nameof(maximumScale));
        this.targetMilliseconds = targetMilliseconds; this.maximumScale = maximumScale; Scale = maximumScale;
    }
    public float Scale { get; private set; }
    public int Changes { get; private set; }
    private int _lastSequence = -1, _changedAt = -1000, _over, _under;

    public void Observe(int sequence, int submittedSequence, float sampleScale, double milliseconds)
    {
        if (sequence <= _lastSequence) return;
        _lastSequence = sequence;
        if (sequence <= _changedAt || sampleScale != Scale || !double.IsFinite(milliseconds) || milliseconds <= 0) return;
        _over = milliseconds > targetMilliseconds ? _over + 1 : 0;
        _under = milliseconds < targetMilliseconds * .65 ? _under + 1 : 0;
        if (submittedSequence - _changedAt < 8) return;
        var next = Scale;
        if (_over >= 2) {
            var proposed = Scale * System.Math.Sqrt(targetMilliseconds * .9 / milliseconds);
            next = (float)(System.Math.Floor(proposed / maximumScale * 16) / 16 * maximumScale);
        } else if (_under >= 32) next = Scale + maximumScale / 16;
        next = System.Math.Clamp(next, System.Math.Min(.0625f, maximumScale), maximumScale);
        if (next == Scale) return;
        Scale = next; _changedAt = submittedSequence; _over = _under = 0; Changes++;
    }
}
