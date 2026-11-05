using Sia.Engine.Example;
using Xunit;

namespace Sia.Engine.Rendering.Debug.Tests;

public class ScenePrefetchTests
{
    [Fact]
    public async Task Required_reads_overlap_with_a_bounded_window_and_ordered_results()
    {
        var gates = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var started = new List<int>();
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = PbrSceneFiles.ReadRequiredAsync(new[] { 0, 1, 2, 3 }, (id, token) => {
            started.Add(id);
            if (id == 2) third.SetResult();
            if (id == 3) fourth.SetResult();
            return gates[id].Task.WaitAsync(token);
        }, CancellationToken.None);
        Assert.Equal(new[] { 0, 1 }, started);
        gates[1].SetResult(11);
        Assert.Equal(new[] { 0, 1 }, started);
        gates[0].SetResult(10);
        await third.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fourth.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gates[3].SetResult(13);
        Assert.False(work.IsCompleted);
        gates[2].SetResult(12);
        Assert.Equal(new[] { 10, 11, 12, 13 }, await work);
    }

    [Fact]
    public async Task Failure_cancels_and_joins_prefetched_work_before_returning()
    {
        var fail = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = false;
        var expected = new InvalidDataException("bad required page");
        async Task<int> Read(int id, CancellationToken token)
        {
            if (id == 0) return await fail.Task;
            try { await Task.Delay(Timeout.Infinite, token); return id; }
            finally { joined = true; }
        }
        var work = PbrSceneFiles.ReadRequiredAsync(new[] { 0, 1, 2 }, Read, CancellationToken.None);
        fail.SetException(expected);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidDataException>(() => work));
        Assert.True(joined);
    }

    [Fact]
    public async Task Later_failure_cancels_an_earlier_blocked_read_without_waiting_for_its_success()
    {
        var fail = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = false;
        var expected = new InvalidDataException("second page failed");
        async Task<int> Read(int id, CancellationToken token)
        {
            if (id == 1) return await fail.Task;
            try { await Task.Delay(Timeout.Infinite, token); return id; }
            finally { joined = true; }
        }
        var work = PbrSceneFiles.ReadRequiredAsync(new[] { 0, 1, 2 }, Read, CancellationToken.None);
        fail.SetException(expected);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidDataException>(() => work.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.True(joined);
    }

    [Fact]
    public async Task External_cancellation_is_joined_and_does_not_start_the_remaining_set()
    {
        using var cancellation = new CancellationTokenSource();
        var started = 0; var joined = 0;
        async Task<int> Read(int id, CancellationToken token)
        {
            Interlocked.Increment(ref started);
            try { await Task.Delay(Timeout.Infinite, token); return id; }
            finally { Interlocked.Increment(ref joined); }
        }
        var work = PbrSceneFiles.ReadRequiredAsync(new[] { 0, 1, 2, 3 }, Read, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(2, started);
        Assert.Equal(2, joined);
    }
}
