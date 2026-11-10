using Sia.Engine.Example;
using Xunit;

namespace Sia.Engine.Rendering.Debug.Tests;

public class ScenePrefetchTests
{
    [Fact]
    public async Task Required_reads_overlap_with_a_bounded_window_and_ordered_results()
    {
        var gates = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var started = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var peak = 0;
        async Task<int> Read(int id, CancellationToken token)
        {
            var current = Interlocked.Increment(ref active);
            peak = System.Math.Max(peak, current);
            started.Enqueue(id);
            if (id == 2) third.SetResult();
            if (id == 3) fourth.SetResult();
            try { return await gates[id].Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref active); }
        }
        var work = PbrSceneFiles.ReadRequiredAsync(new[] { 0, 1, 2, 3 }, Read, CancellationToken.None);
        try {
            Assert.Equal(new[] { 0, 1 }, started);
            gates[1].SetResult(11);
            await third.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(gates[0].Task.IsCompleted);
            Assert.Equal(new[] { 0, 1, 2 }, started);
            Assert.Equal(2, active);
            gates[2].SetResult(12);
            await fourth.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(gates[0].Task.IsCompleted);
            gates[3].SetResult(13);
            Assert.False(work.IsCompleted);
            gates[0].SetResult(10);
            Assert.Equal(new[] { 10, 11, 12, 13 }, await work.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, peak);
            Assert.Equal(0, active);
        }
        finally {
            for (var index = 0; index < gates.Length; index++) gates[index].TrySetResult(index + 10);
            await work.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Failure_cancels_and_joins_prefetched_work_before_returning()
    {
        var fail = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var joined = false;
        var expected = new InvalidDataException("bad required page");
        async Task<int> Read(int id, CancellationToken token)
        {
            Interlocked.Increment(ref started);
            if (id == 0) return await fail.Task;
            try { await Task.Delay(Timeout.Infinite, token); return id; }
            finally { cleanupEntered.SetResult(); await allowCleanup.Task; joined = true; }
        }
        var work = PbrSceneFiles.ReadRequiredAsync(new[] { 0, 1, 2 }, Read, CancellationToken.None);
        fail.SetException(expected);
        try {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(work.IsCompleted);
            Assert.Equal(2, started);
        }
        finally { allowCleanup.TrySetResult(); }
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
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0; var joined = 0;
        async Task<int> Read(int id, CancellationToken token)
        {
            Interlocked.Increment(ref started);
            try { await Task.Delay(Timeout.Infinite, token); return id; }
            finally {
                if (Interlocked.Increment(ref joined) == 2) cleanupEntered.SetResult();
                await allowCleanup.Task;
            }
        }
        var work = PbrSceneFiles.ReadRequiredAsync(new[] { 0, 1, 2, 3 }, Read, cancellation.Token);
        cancellation.Cancel();
        try {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(work.IsCompleted);
            Assert.Equal(2, started);
        }
        finally { allowCleanup.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(2, started);
        Assert.Equal(2, joined);
    }

    [Fact]
    public async Task Synchronous_failure_stops_filling_the_window()
    {
        var started = 0;
        var expected = new InvalidDataException("first page failed immediately");
        Task<int> Read(int id, CancellationToken token)
        {
            started++;
            throw expected;
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            PbrSceneFiles.ReadRequiredAsync(new[] { 0, 1, 2 }, Read, CancellationToken.None));

        Assert.Same(expected, error);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task External_cancellation_during_a_synchronous_read_stops_filling_the_window()
    {
        using var cancellation = new CancellationTokenSource();
        var started = 0;
        Task<int> Read(int id, CancellationToken token)
        {
            started++;
            cancellation.Cancel();
            return Task.FromResult(id);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PbrSceneFiles.ReadRequiredAsync(new[] { 0, 1, 2 }, Read, cancellation.Token));

        Assert.Equal(1, started);
    }
}
