using Sia.Engine.Rendering;
using Xunit;

namespace Sia.Engine.Rendering.Debug.Tests;

public sealed class BoundedRequestsTests
{
    [Fact]
    public async Task AdmissionReservesBeforeReadingAndEnforcesBothLimits()
    {
        BoundedRequests<string, int>? owner = null;
        var calls = 0;
        var observations = new List<(bool Contains, long Bytes)>();
        owner = new((key, _) => {
            calls++;
            observations.Add((owner!.Contains(key), owner.ReservedBytes));
            return Task.FromResult(calls);
        }, 2, 10, StringComparer.OrdinalIgnoreCase);
        await using var lifetime = owner;
        Assert.True(owner.TryStart("one", 6));
        Assert.False(owner.TryStart("ONE", 1));
        Assert.False(owner.TryStart("two", 5));
        Assert.True(owner.TryStart("two", 4));
        Assert.False(owner.TryStart("three", 1));
        Assert.Equal(2, calls);
        Assert.Equal(10, owner.ReservedBytes);
        owner.Release("one");
        Assert.True(owner.TryStart("three", 2));
        Assert.Equal(6, owner.ReservedBytes);
        Assert.Equal(10, owner.PeakBytes);
        Assert.Equal(new[] { "two", "three" }, owner.Requests.Select(r => r.Key));
        Assert.Equal(new[] { (true, 6L), (true, 10L), (true, 6L) }, observations);
    }

    [Fact]
    public async Task OutOfOrderCompletionDoesNotReleaseAnEarlierRunningRead()
    {
        var first = new TaskCompletionSource<ReadOnlyMemory<byte>>();
        var second = new TaskCompletionSource<ReadOnlyMemory<byte>>();
        await using var owner = new BoundedRequests<int, ReadOnlyMemory<byte>>(
            (key, _) => (key == 1 ? first.Task : second.Task).WaitAsync(TimeSpan.FromSeconds(5)), 2, 20);
        owner.TryStart(1, 12);
        owner.TryStart(2, 8);
        second.SetResult(new byte[] { 2 });
        Assert.Throws<InvalidOperationException>(() => owner.Release(1));
        owner.Release(2);
        Assert.Equal(12, owner.ReservedBytes);
        Assert.True(owner.Contains(1));
        first.SetResult(new byte[] { 1 });
        owner.Release(1);
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task CancellationRetainsReservationUntilReaderActuallyFinishes()
    {
        var read = new TaskCompletionSource<int>();
        CancellationToken token = default;
        await using var owner = new BoundedRequests<int, int>((_, cancellation) => {
            token = cancellation;
            return read.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }, 1, 10);
        owner.TryStart(1, 10);
        Assert.True(owner.Cancel(1));
        Assert.False(owner.Cancel(1));
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(10, owner.ReservedBytes);
        Assert.False(owner.TryStart(1, 10));
        Assert.False(owner.TryStart(2, 10));
        Assert.Throws<InvalidOperationException>(() => owner.Release(1));
        read.SetResult(42);
        owner.Release(1);
        Assert.True(owner.TryStart(1, 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderFailureStillHasAnOwnedTerminalReservation(bool returnNull)
    {
        await using var owner = new BoundedRequests<int, int>((_, _) =>
            returnNull ? null! : throw new IOException("reader failure"), 1, 10);
        Assert.True(owner.TryStart(1, 10));
        Assert.True(owner.TryGet(1, out var request));
        Assert.True(request.Read.IsFaulted);
        Assert.Equal(10, owner.ReservedBytes);
        owner.Release(1);
        Assert.Equal(0, owner.Count);
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task ShutdownCancelsAllBeforeWaitingAndRepeatedDisposalWaitsForDrain()
    {
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        var owner = new BoundedRequests<int, int>((key, token) => {
            tokens.Add(token);
            return key == 1 ? first.Task : second.Task;
        }, 2, 10);
        owner.TryStart(1, 5);
        owner.TryStart(2, 5);
        var stop = owner.DisposeAsync().AsTask();
        var repeated = owner.DisposeAsync().AsTask();
        Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
        Assert.False(stop.IsCompleted);
        Assert.False(repeated.IsCompleted);
        Assert.Equal(10, owner.ReservedBytes);
        Assert.Throws<ObjectDisposedException>(() => owner.TryStart(3, 1));
        Assert.Throws<ObjectDisposedException>(() => owner.Release(1));
        second.SetCanceled();
        Assert.False(stop.IsCompleted);
        first.SetException(new IOException("late failure"));
        await Task.WhenAll(stop, repeated).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, owner.Count);
        Assert.Equal(0, owner.ReservedBytes);
        await owner.DisposeAsync();
    }
}
