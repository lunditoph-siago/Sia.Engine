using System.Reflection;
using Sia.Engine.Example;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

// Link only the production private session-policy partial, without the Example executable/GPU.
public sealed class ExampleGpuErrorStateTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type App = typeof(SceneExampleApp);
    private static object Begin() => App.GetMethod("BeginGpuErrorSession", Static)!.Invoke(null, null)!;
    private static nint Generation(object session) => (nint)session.GetType().GetField("Generation", Instance)!.GetValue(session)!;
    private static object? Find(nint generation) => App.GetMethod("FindGpuErrorSession", Static)!.Invoke(null, [generation]);
    private static object? Snapshot() => App.GetProperty("GpuDeviceLossSnapshot", Static)!.GetValue(null);
    private static object? Loss(object session, string reason, bool destroyed = false, long timestamp = 123) =>
        session.GetType().GetMethod("TryRecordLoss", Instance)!.Invoke(session,
            [reason, destroyed, "native original message", DateTimeOffset.UnixEpoch, timestamp]);
    private static void ExpectDestroy(object session) => session.GetType().GetMethod("ExpectDestroy", Instance)!.Invoke(session, null);
    private static object? Member(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    [Fact]
    public void RecreationRejectsLateCallbackToken()
    {
        var old = Begin(); var current = Begin();
        Assert.NotEqual(Generation(old), Generation(current));
        Assert.Null(Find(Generation(old)));
        Assert.Same(current, Find(Generation(current)));
    }

    [Fact]
    public void CallbackAlreadyHoldingOldSessionCannotPoisonNewApplication()
    {
        var capturedOldSession = Begin();
        var current = Begin();
        Assert.NotNull(Loss(capturedOldSession, "Unknown"));
        Assert.Null(Snapshot());
        Assert.Same(current, Find(Generation(current)));
    }

    [Fact]
    public void ExplicitCurrentDestroyDoesNotRecordLoss()
    {
        var session = Begin(); ExpectDestroy(session);
        Assert.Null(Loss(session, "Destroyed", destroyed: true));
        Assert.Null(Snapshot());
    }

    [Fact]
    public void UnexpectedDestroyRecordsOriginalMetadata()
    {
        var session = Begin(); var loss = Loss(session, "Destroyed", destroyed: true, timestamp: 987)!;
        Assert.Same(loss, Snapshot());
        Assert.Equal("Destroyed", Member(loss, "Reason"));
        Assert.Equal("native original message", Member(loss, "Message"));
        Assert.Equal(DateTimeOffset.UnixEpoch, Member(loss, "Utc"));
        Assert.Equal(987L, Member(loss, "StopwatchTimestamp"));
        Assert.Equal((long)Generation(session), Member(loss, "SessionGeneration"));
    }

    [Fact]
    public void OldExpectedDestroyDoesNotSuppressNewUnexpectedDestroy()
    {
        var old = Begin(); ExpectDestroy(old);
        var current = Begin();
        Assert.NotNull(Loss(current, "Destroyed", destroyed: true));
    }

    [Fact]
    public void FirstLossAndTimestampSurviveLaterNotifications()
    {
        var session = Begin(); var first = Loss(session, "Unknown", timestamp: 11);
        Assert.Null(Loss(session, "Destroyed", destroyed: true, timestamp: 22));
        Assert.Same(first, Snapshot());
        Assert.Equal("Unknown", Member(Snapshot()!, "Reason"));
        Assert.Equal(11L, Member(Snapshot()!, "StopwatchTimestamp"));
    }

    [Fact]
    public void ConcurrentLossNotificationsPublishOnlyOneOriginalRecord()
    {
        var session = Begin(); var winners = 0;
        Parallel.For(0, 32, i => {
            if (Loss(session, "loss-" + i, timestamp: i) is not null) Interlocked.Increment(ref winners);
        });
        Assert.Equal(1, winners);
        Assert.NotNull(Snapshot());
    }

    [Fact]
    public void FirstErrorSinkIsSessionLocalAndPreservesEarlierCause()
    {
        var old = Begin();
        var method = old.GetType().GetMethod("TryRecordError", Instance)!;
        Assert.True((bool)method.Invoke(old, ["first native error"])!);
        Assert.False((bool)method.Invoke(old, ["later device error"])!);
        Assert.Equal("first native error", old.GetType().GetProperty("Error", Instance)!.GetValue(old));
        var current = Begin();
        Assert.Null(current.GetType().GetProperty("Error", Instance)!.GetValue(current));
    }
}
