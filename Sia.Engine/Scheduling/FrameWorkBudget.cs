namespace Sia.Engine.Scheduling;

/// <summary>A shared monotonic deadline for sequential foreground work in one frame.</summary>
public readonly struct FrameWorkBudget
{
    private readonly TimeProvider? _clock;
    private readonly long _startedAt;
    private readonly TimeSpan _limit;

    private FrameWorkBudget(TimeProvider clock, TimeSpan limit)
    {
        _clock = clock;
        _startedAt = clock.GetTimestamp();
        _limit = limit;
    }

    public static FrameWorkBudget Start(TimeSpan limit, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, TimeSpan.Zero);
        return new(timeProvider ?? TimeProvider.System, limit);
    }

    private TimeSpan Elapsed => _clock?.GetElapsedTime(_startedAt) ?? TimeSpan.Zero;

    public TimeSpan Remaining
    {
        get
        {
            var remaining = _limit - Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>True at or after the deadline. A default budget is expired.</summary>
    public bool IsExpired => Remaining == TimeSpan.Zero;

    /// <summary>True after the deadline, for reporting actual overruns.</summary>
    public bool IsExceeded => Elapsed > _limit;

    /// <summary>Checks the current deadline before starting an item. The item can still overrun its estimate.</summary>
    public bool CanStart(TimeSpan estimatedCost = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(estimatedCost, TimeSpan.Zero);
        var remaining = Remaining;
        return remaining > TimeSpan.Zero && estimatedCost <= remaining;
    }
}
