using System.Collections.Concurrent;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Math;
using Xunit;

namespace Sia.Engine.Rendering.Debug.Tests;

public sealed class BoundedPageSchedulerTests
{
    private static StreamGeometryPage Page(int triangles = 1)
    {
        var vertices = new MeshVertex[triangles * 3];
        for (var i = 0; i < triangles; i++) {
            vertices[i * 3] = new(new(i, 0, 0), new(0, 0, 1), new(0));
            vertices[i * 3 + 1] = new(new(i + 1, 0, 0), new(0, 0, 1), new(1, 0));
            vertices[i * 3 + 2] = new(new(i, 1, 0), new(0, 0, 1), new(0, 1));
        }
        return StreamGeometryPage.Cook(vertices, Enumerable.Range(0, vertices.Length).Select(i => (uint)i).ToArray());
    }

    private static async Task Ready(BoundedPageScheduler owner, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!owner.TryGetReady(id, out _)) await Task.Delay(1, deadline.Token);
    }

    [Fact]
    public async Task AcknowledgeImmediatelyRefillsHighestRemainingPriorityWithoutRereadingConsumedPage()
    {
        var page = Page();
        var calls = new ConcurrentQueue<string>();
        await using var owner = new BoundedPageScheduler((id, _) => {
            calls.Enqueue(id); return Task.FromResult(page);
        }, 1);
        owner.Update([new("first", page.Bytes.Length, 10), new("third", page.Bytes.Length, 2),
            new("second", page.Bytes.Length, 4), new("second", page.Bytes.Length, 3)]);
        await Ready(owner, "first");
        owner.Acknowledge("first");
        Assert.Equal(1, owner.Statistics.Pending);
        await Ready(owner, "second");
        owner.Acknowledge("second");
        await Ready(owner, "third");
        owner.Acknowledge("third");
        Assert.Equal(new[] { "first", "second", "third" }, calls);
        Assert.Equal(3, owner.Statistics.Completed);
        Assert.Equal(0, owner.Statistics.Pending);
        Assert.Equal(0, owner.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task RefillRespectsDecodedBytesIndependentlyOfRequestCapacity()
    {
        var page = Page(500);
        Assert.True(page.Bytes.Length * 2 > StreamGeometryPage.MaximumBytes);
        await using var owner = new BoundedPageScheduler((_, _) => Task.FromResult(page), 3,
            StreamGeometryPage.MaximumBytes);
        owner.Update([new("first", page.Bytes.Length, 2), new("second", page.Bytes.Length, 1)]);
        await Ready(owner, "first");
        Assert.Equal(1, owner.Statistics.Pending);
        owner.Acknowledge("first");
        Assert.Equal(1, owner.Statistics.Pending);
        Assert.Equal(page.Bytes.Length, owner.Statistics.ReservedBytes);
        await Ready(owner, "second");
        owner.Acknowledge("second");
        Assert.Equal(page.Bytes.Length, owner.Statistics.PeakBytes);
    }

    [Fact]
    public async Task OutOfOrderAcknowledgeKeepsEarlierRunningReadAndStartsNext()
    {
        var page = Page();
        var blocked = new TaskCompletionSource<StreamGeometryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new BoundedPageScheduler((id, _) => id == "blocked" ? blocked.Task : Task.FromResult(page), 2);
        try {
            owner.Update([new("blocked", page.Bytes.Length, 3), new("ready", page.Bytes.Length, 2), new("next", page.Bytes.Length, 1)]);
            await Ready(owner, "ready");
            owner.Acknowledge("ready");
            Assert.Equal(2, owner.Statistics.Pending);
            Assert.Equal(page.Bytes.Length * 2, owner.Statistics.ReservedBytes);
            await Ready(owner, "next");
            owner.Acknowledge("next");
            Assert.Equal(1, owner.Statistics.Pending);
            Assert.False(owner.TryGetReady("blocked", out _));
            blocked.SetResult(page);
            await Ready(owner, "blocked");
            owner.Acknowledge("blocked");
            Assert.Equal(0, owner.Statistics.ReservedBytes);
        }
        finally { blocked.TrySetResult(page); }
    }

    [Fact]
    public async Task CanceledRunningReadStillOwnsItsSlotWhileAnotherSlotRefills()
    {
        var page = Page();
        var blocked = new TaskCompletionSource<StreamGeometryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new BoundedPageScheduler((id, token) => {
            if (id != "blocked") return Task.FromResult(page);
            entered.TrySetResult(token); return blocked.Task;
        }, 2);
        try {
            owner.Update([new("blocked", page.Bytes.Length, 3), new("ready", page.Bytes.Length, 2), new("next", page.Bytes.Length, 1)]);
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Ready(owner, "ready");
            owner.Update([new("ready", page.Bytes.Length, 2), new("next", page.Bytes.Length, 1)]);
            Assert.True(token.IsCancellationRequested);
            owner.Acknowledge("ready");
            Assert.Equal(2, owner.Statistics.Pending);
            Assert.Equal(page.Bytes.Length * 2, owner.Statistics.ReservedBytes);
            await Ready(owner, "next");
            owner.Acknowledge("next");
            Assert.Equal(page.Bytes.Length, owner.Statistics.ReservedBytes);
            blocked.SetResult(page);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (owner.Statistics.Pending != 0) {
                owner.Update([]);
                await Task.Delay(1, deadline.Token);
            }
            Assert.Equal(0, owner.Statistics.Pending);
            Assert.Equal(1, owner.Statistics.Cancelled);
        }
        finally { blocked.TrySetResult(page); }
    }

    [Fact]
    public async Task LatestDemandSetControlsRefillAndExplicitLaterRedemandIsAllowed()
    {
        var page = Page();
        var calls = new ConcurrentQueue<string>();
        await using var owner = new BoundedPageScheduler((id, _) => {
            calls.Enqueue(id); return Task.FromResult(page);
        }, 1);
        owner.Update([new("first", page.Bytes.Length, 3), new("obsolete", page.Bytes.Length, 2)]);
        await Ready(owner, "first");
        owner.Update([new("first", page.Bytes.Length, 3), new("new", page.Bytes.Length, 1)]);
        owner.Acknowledge("first");
        await Ready(owner, "new");
        owner.Acknowledge("new");
        owner.Update([new("first", page.Bytes.Length, 1)]);
        await Ready(owner, "first");
        owner.Acknowledge("first");
        Assert.Equal(new[] { "first", "new", "first" }, calls);
    }

    [Fact]
    public async Task PrematureAndPostDisposalAcknowledgeDoNotPublishOrAdmit()
    {
        var page = Page();
        var blocked = new TaskCompletionSource<StreamGeometryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new BoundedPageScheduler((_, _) => blocked.Task, 1);
        owner.Update([new("blocked", page.Bytes.Length, 2), new("next", page.Bytes.Length, 1)]);
        Assert.Throws<InvalidOperationException>(() => owner.Acknowledge("blocked"));
        Assert.Equal(0, owner.Statistics.Completed);
        var stop = owner.DisposeAsync().AsTask();
        Assert.False(stop.IsCompleted);
        blocked.SetResult(page);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => owner.Acknowledge("blocked"));
        Assert.Equal(0, owner.Statistics.Completed);
        Assert.Equal(0, owner.Statistics.Pending);
    }
}
