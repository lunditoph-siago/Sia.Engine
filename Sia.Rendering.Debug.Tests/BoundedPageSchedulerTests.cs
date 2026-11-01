using Sia.Engine.Rendering;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class BoundedPageSchedulerTests
{
    private sealed record Page(int Bytes);

    private sealed class Read(bool ignoreCancellation = false)
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<Page> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<Page> Run(CancellationToken token) {
            Started.TrySetResult();
            return ignoreCancellation ? Result.Task : Result.Task.WaitAsync(token);
        }
    }

    private static StreamPageDemand Demand(string id, float priority = 1, int bytes = 8) => new(id, bytes, priority);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    [Fact]
    public async Task ReadyPageDoesNotOccupyPreparationSlotButStaysReservedUntilInstallationAcknowledged()
    {
        var reads = new Dictionary<string, Read> { ["a"] = new(), ["b"] = new(), ["c"] = new() };
        await using var scheduler = new BoundedPageScheduler<Page>((id, token) => reads[id].Run(token), p => p.Bytes, 8, 1, 24);
        StreamPageDemand[] demands = [Demand("a", 3), Demand("b", 2), Demand("c")];
        scheduler.Update(demands);
        await reads["a"].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reads["a"].Result.SetResult(new(8));
        await Until(() => scheduler.TryGetReady("a", out _));
        scheduler.Update(demands);
        await reads["b"].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(16, scheduler.Statistics.ReservedBytes);
        Assert.Equal(1, scheduler.Statistics.Preparing);
        Assert.Equal(1, scheduler.Statistics.Ready);
        reads["b"].Result.SetResult(new(8));
        await Until(() => scheduler.TryGetReady("b", out _));
        scheduler.Update(demands);
        Assert.False(reads["c"].Started.Task.IsCompleted);
        scheduler.BeginInstall("a");
        Assert.Equal(1, scheduler.Statistics.Installing);
        Assert.Equal(16, scheduler.Statistics.ReservedBytes);
        scheduler.Acknowledge("a");
        scheduler.Update([Demand("b", 2), Demand("c")]);
        await reads["c"].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scheduler.Statistics.Acknowledged);
        Assert.Equal(1, scheduler.Statistics.InstallStarted);
        Assert.Equal(16, scheduler.Statistics.ReservedBytes);
        reads["c"].Result.SetResult(new(8));
    }

    [Fact]
    public async Task CompletedReadyPagesCannotReleaseDecodedBudgetToStartMoreReads()
    {
        var reads = new Dictionary<string, Read> { ["a"] = new(), ["b"] = new(), ["c"] = new() };
        await using var scheduler = new BoundedPageScheduler<Page>((id, token) => reads[id].Run(token), p => p.Bytes, 8, 2, 16);
        StreamPageDemand[] demands = [Demand("a", 3), Demand("b", 2), Demand("c")];
        scheduler.Update(demands);
        await Task.WhenAll(reads["a"].Started.Task, reads["b"].Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
        reads["a"].Result.SetResult(new(8)); reads["b"].Result.SetResult(new(8));
        await Until(() => scheduler.TryGetReady("a", out _) && scheduler.TryGetReady("b", out _));
        scheduler.Update(demands);
        Assert.Equal(0, scheduler.Statistics.Preparing);
        Assert.Equal(2, scheduler.Statistics.Ready);
        Assert.Equal(16, scheduler.Statistics.ReservedBytes);
        Assert.False(reads["c"].Started.Task.IsCompleted);
        scheduler.Acknowledge("a");
        scheduler.Update([Demand("b", 2), Demand("c")]);
        await reads["c"].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(16, scheduler.Statistics.PeakBytes);
        reads["c"].Result.SetResult(new(8));
    }

    [Fact]
    public async Task CancellationThatIgnoresTokenRetainsSlotAndBytesAndAllDisposalsJoinIt()
    {
        var a = new Read(ignoreCancellation: true); var b = new Read();
        var scheduler = new BoundedPageScheduler<Page>((id, token) => (id == "a" ? a : b).Run(token), p => p.Bytes, 8, 1, 8);
        try {
            scheduler.Update([Demand("a")]);
            await a.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scheduler.Update([Demand("b", 10)]);
            Assert.False(b.Started.Task.IsCompleted);
            Assert.Equal(8, scheduler.Statistics.ReservedBytes);
            Assert.Equal(1, scheduler.Statistics.Cancelling);
            var first = scheduler.DisposeAsync().AsTask();
            var second = scheduler.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
            a.Result.SetResult(new(8));
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, scheduler.Statistics.ReservedBytes);
            Assert.Equal(0, scheduler.Statistics.Pending);
            Assert.Equal(1, scheduler.Statistics.Cancelled);
            Assert.False(b.Started.Task.IsCompleted);
        }
        finally { a.Result.TrySetResult(new(8)); await scheduler.DisposeAsync(); }
    }

    [Fact]
    public async Task EqualPriorityUsesOrdinalIdentityRatherThanDemandEnumerationOrder()
    {
        var a = new Read(); var b = new Read();
        await using var scheduler = new BoundedPageScheduler<Page>((id, token) => (id == "a" ? a : b).Run(token), p => p.Bytes, 8, 1, 24);
        scheduler.Update([Demand("b"), Demand("a")]);
        await a.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(b.Started.Task.IsCompleted);
        a.Result.SetResult(new(8));
        await Until(() => scheduler.TryGetReady("a", out _));
        scheduler.Update([Demand("b"), Demand("a")]);
        await b.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        b.Result.SetResult(new(8));
    }

    [Fact]
    public async Task TinySynchronouslyPreparedPagesHaveAFiniteBufferedItemLimit()
    {
        await using var scheduler = new BoundedPageScheduler<Page>((_, _) => Task.FromResult(new Page(1)), p => p.Bytes, 8, 1, 64);
        var demands = Enumerable.Range(0, 100).Select(i => Demand(i.ToString("D3"), bytes: 1)).ToArray();
        scheduler.Update(demands);
        await Until(() => scheduler.Statistics.Preparing == 0);
        scheduler.Update(demands);
        await Until(() => scheduler.Statistics.Preparing == 0);
        for (var i = 0; i < 10; i++) scheduler.Update(demands);
        Assert.Equal(2, scheduler.Statistics.Pending);
        Assert.Equal(2, scheduler.Statistics.Ready);
        Assert.Equal(2, scheduler.Statistics.Started);
        Assert.Equal(2, scheduler.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task FailedPreparationIsObservedOnceAndReservationIsReleased()
    {
        var read = new Read();
        await using var scheduler = new BoundedPageScheduler<Page>((_, token) => read.Run(token), p => p.Bytes, 8, 1, 8);
        scheduler.Update([Demand("a")]);
        await read.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        read.Result.SetException(new InvalidDataException("bad source"));
        await Until(() => scheduler.Statistics.Preparing == 0);
        scheduler.Update([Demand("a")]);
        scheduler.Update([Demand("a")]);
        Assert.Equal(1, scheduler.Statistics.Failed);
        Assert.Equal(0, scheduler.Statistics.ReservedBytes);
        Assert.Equal(0, scheduler.Statistics.Pending);
        Assert.Equal(1, scheduler.Statistics.Started);
    }

    [Fact]
    public async Task DisposingStillJoinsReadWhenCancellationCallbackThrows()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<Page>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new BoundedPageScheduler<Page>((_, token) => {
            token.Register(() => throw new InvalidOperationException("cancel observer"));
            started.SetResult(); return result.Task;
        }, p => p.Bytes, 8, 1, 8);
        scheduler.Update([Demand("a")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = scheduler.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.Equal(8, scheduler.Statistics.ReservedBytes);
        result.SetResult(new(8));
        await Assert.ThrowsAsync<AggregateException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, scheduler.Statistics.ReservedBytes);
        Assert.Equal(0, scheduler.Statistics.Pending);
    }
}
