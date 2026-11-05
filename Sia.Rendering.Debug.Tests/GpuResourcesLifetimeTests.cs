using System.Reflection;
using System.Runtime.CompilerServices;
using Sia;
using Sia.Engine.Rendering;
using Sia.WebGPU;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class GpuResourcesLifetimeTests
{
    private readonly struct FakeNative { }
    private readonly record struct Trigger : IEvent;

    private static Entity Resource(World world, int id, List<int> released)
        => world.OwnWgpu(new WgpuHandle<FakeNative>(id), (ref WgpuHandle<FakeNative> handle) => {
            released.Add(checked((int)handle.Handle));
            handle = default;
        });

    private static GpuResources Owner(World world, params (Entity Entity, ulong Bytes)[] resources)
    {
        // Only construction is bypassed: its device-limit query requires hardware. Cleanup uses
        // real World entities and WgpuResources, with fake handles/custom release callbacks.
        var owner = (GpuResources)RuntimeHelpers.GetUninitializedObject(typeof(GpuResources));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(GpuResources).GetField("_world", flags)!.SetValue(owner, world);
        typeof(GpuResources).GetField("_owned", flags)!.SetValue(owner, resources.Select(resource => resource.Entity).ToList());
        typeof(GpuResources).GetField("_sizes", flags)!.SetValue(owner, resources.ToDictionary(resource => resource.Entity, resource => resource.Bytes));
        typeof(GpuResources).GetProperty(nameof(GpuResources.Bytes))!.SetValue(owner,
            resources.Aggregate(0ul, (sum, resource) => checked(sum + resource.Bytes)));
        return owner;
    }

    private static void OnRemove(World world, Action<int> callback)
        => world.Dispatcher.Listen<WorldEvents.Remove<WgpuResource<FakeNative>>>(
            (Entity entity, in WorldEvents.Remove<WgpuResource<FakeNative>> _) => {
                callback(checked((int)entity.GetWgpu<FakeNative>().Handle));
                return false;
            });

    [Fact]
    public void DisposeContinuesAfterFailureAndRetriesOnlyTheLiveResource()
    {
        using var world = new World();
        var released = new List<int>(); var attempts = new List<int>();
        var first = Resource(world, 1, released); var second = Resource(world, 2, released); var third = Resource(world, 3, released);
        var owner = Owner(world, (first, 11), (second, 22), (third, 33));
        var error = new InvalidOperationException("resource three remove"); var failed = false;
        OnRemove(world, id => {
            attempts.Add(id);
            if (id == 3 && !failed) { failed = true; throw error; }
        });

        Assert.Same(error, Assert.Throws<InvalidOperationException>(owner.Dispose));
        Assert.Equal(new[] { 3, 2, 1 }, attempts);
        Assert.Equal(new[] { 2, 1 }, released);
        Assert.False(first.IsValid); Assert.False(second.IsValid); Assert.True(third.IsValid);
        Assert.Equal(33ul, owner.Bytes);
        Assert.Throws<ObjectDisposedException>(() => owner.Own(new WgpuHandle<FakeNative>(4)));
        Assert.Throws<ObjectDisposedException>(() => owner.Release(third));

        owner.Dispose(); owner.Dispose();

        Assert.Equal(new[] { 3, 2, 1, 3 }, attempts);
        Assert.Equal(new[] { 2, 1, 3 }, released);
        Assert.False(third.IsValid);
        Assert.Equal(0ul, owner.Bytes);
        Assert.Equal(0, world.GetAddon<WgpuResources>().Count);
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void DisposeAggregatesIndependentFailuresInReverseOwnershipOrder()
    {
        using var world = new World();
        var released = new List<int>(); var attempts = new List<int>();
        var first = Resource(world, 1, released); var second = Resource(world, 2, released); var third = Resource(world, 3, released);
        var owner = Owner(world, (first, 11), (second, 22), (third, 33));
        var firstError = new InvalidOperationException("resource three"); var secondError = new ArgumentException("resource one");
        var failed = new HashSet<int>();
        OnRemove(world, id => {
            attempts.Add(id);
            if (id == 3 && failed.Add(id)) throw firstError;
            if (id == 1 && failed.Add(id)) throw secondError;
        });

        var error = Assert.Throws<AggregateException>(owner.Dispose);

        Assert.Equal(new Exception[] { firstError, secondError }, error.InnerExceptions);
        Assert.Equal(new[] { 3, 2, 1 }, attempts);
        Assert.Equal(new[] { 2 }, released);
        Assert.Equal(44ul, owner.Bytes);
        Assert.True(first.IsValid); Assert.False(second.IsValid); Assert.True(third.IsValid);
        owner.Dispose(); owner.Dispose();
        Assert.Equal(new[] { 3, 2, 1, 3, 1 }, attempts);
        Assert.Equal(new[] { 2, 3, 1 }, released);
        Assert.Equal(0ul, owner.Bytes);
        Assert.Equal(0, world.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseRetainsItsAccountingWhenRemovalFails(bool duringSend)
    {
        using var world = new World();
        var released = new List<int>();
        var resource = Resource(world, 1, released); var owner = Owner(world, (resource, 11));
        var error = new InvalidOperationException("first attempt"); var attempts = 0;
        OnRemove(world, _ => { if (++attempts == 1) throw error; });

        var trigger = world.Create();
        if (duringSend) world.Dispatcher.Listen<Trigger>((Entity _, in Trigger _) => {
            owner.Release(resource);
            return false;
        });
        void Release() {
            if (duringSend) world.Send(trigger, new Trigger());
            else owner.Release(resource);
        }

        Assert.Same(error, Assert.Throws<InvalidOperationException>(Release));
        Assert.Equal(11ul, owner.Bytes);
        Assert.True(resource.IsValid);
        Assert.Empty(released);

        Release(); owner.Dispose(); trigger.Destroy();

        Assert.Equal(2, attempts);
        Assert.Equal(new[] { 1 }, released);
        Assert.Equal(0ul, owner.Bytes);
        Assert.False(resource.IsValid);
    }

    [Fact]
    public void CleanupReentryDoesNotRepeatNativeRelease()
    {
        using var world = new World();
        var released = new List<int>(); var attempts = new List<int>();
        var first = Resource(world, 1, released); var second = Resource(world, 2, released);
        var owner = Owner(world, (first, 11), (second, 22));
        OnRemove(world, id => { attempts.Add(id); owner.Dispose(); owner.Dispose(); });

        owner.Dispose(); owner.Dispose();

        Assert.Equal(new[] { 2, 1 }, attempts);
        Assert.Equal(new[] { 2, 1 }, released);
        Assert.Equal(0ul, owner.Bytes);
        Assert.Equal(0, world.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeDuringSendDefersCleanupAndPreservesDispatchErrors(bool failDispatch)
    {
        using var world = new World();
        var released = new List<int>();
        var first = Resource(world, 1, released); var second = Resource(world, 2, released);
        var owner = Owner(world, (first, 11), (second, 22));
        var cleanupError = new InvalidOperationException("remove"); var dispatchError = new ArgumentException("dispatch");
        var failCleanup = true;
        OnRemove(world, id => {
            if (id == 2 && failCleanup) { failCleanup = false; throw cleanupError; }
            owner.Dispose();
        });
        var trigger = world.Create();
        world.Dispatcher.Listen<Trigger>((Entity _, in Trigger _) => {
            owner.Dispose(); owner.Dispose();
            Assert.True(first.IsValid); Assert.True(second.IsValid);
            Assert.Equal(33ul, owner.Bytes);
            Assert.Empty(released);
            Assert.Equal(1, world.Dispatcher.PendingAfterSendActionCount);
            if (failDispatch) throw dispatchError;
            return false;
        });

        if (failDispatch) {
            var error = Assert.Throws<AggregateException>(() => world.Send(trigger, new Trigger()));
            Assert.Equal(new Exception[] { dispatchError, cleanupError }, error.InnerExceptions);
        }
        else Assert.Same(cleanupError, Assert.Throws<InvalidOperationException>(() => world.Send(trigger, new Trigger())));

        Assert.False(first.IsValid); Assert.True(second.IsValid);
        Assert.Equal(new[] { 1 }, released);
        Assert.Equal(22ul, owner.Bytes);
        Assert.Equal(0, world.Dispatcher.PendingAfterSendActionCount);
        owner.Dispose(); owner.Dispose(); trigger.Destroy();
        Assert.Equal(new[] { 1, 2 }, released);
        Assert.Equal(0ul, owner.Bytes);
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void ReleaseDuringSendKeepsBytesUntilTheResourceIsInvalid()
    {
        using var world = new World();
        var released = new List<int>();
        var resource = Resource(world, 1, released); var owner = Owner(world, (resource, 11));
        var trigger = world.Create();
        world.Dispatcher.Listen<Trigger>((Entity _, in Trigger _) => {
            owner.Release(resource);
            Assert.True(resource.IsValid);
            Assert.Equal(11ul, owner.Bytes);
            Assert.Empty(released);
            return false;
        });

        world.Send(trigger, new Trigger());

        Assert.False(resource.IsValid);
        Assert.Equal(new[] { 1 }, released);
        Assert.Equal(0ul, owner.Bytes);
        owner.Dispose(); trigger.Destroy();
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void ReleaseDuringSendRejectsDuplicateBeforeCleanup()
    {
        using var world = new World();
        var released = new List<int>();
        var resource = Resource(world, 1, released); var owner = Owner(world, (resource, 11));
        var trigger = world.Create();
        world.Dispatcher.Listen<Trigger>((Entity _, in Trigger _) => {
            owner.Release(resource);
            Assert.Throws<InvalidOperationException>(() => owner.Release(resource));
            Assert.True(resource.IsValid);
            Assert.Equal(11ul, owner.Bytes);
            Assert.Equal(1, world.Dispatcher.PendingAfterSendActionCount);
            return false;
        });

        world.Send(trigger, new Trigger());

        Assert.False(resource.IsValid);
        Assert.Equal(new[] { 1 }, released);
        Assert.Equal(0ul, owner.Bytes);
        owner.Dispose(); trigger.Destroy();
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void ReleaseRejectsReentryWhileTheResourceIsStillValid()
    {
        using var world = new World();
        var released = new List<int>();
        var resource = Resource(world, 1, released); var owner = Owner(world, (resource, 11));
        var reentered = false;
        OnRemove(world, _ => {
            if (reentered) return;
            reentered = true;
            Assert.Throws<InvalidOperationException>(() => owner.Release(resource));
            Assert.True(resource.IsValid);
            Assert.Equal(11ul, owner.Bytes);
        });

        owner.Release(resource);

        Assert.True(reentered);
        Assert.False(resource.IsValid);
        Assert.Equal(new[] { 1 }, released);
        Assert.Equal(0ul, owner.Bytes);
        owner.Dispose();
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void DisposeAfterWorldDisposalForgetsInvalidResourcesWithoutReleasingAgain()
    {
        using var world = new World();
        var released = new List<int>();
        var first = Resource(world, 1, released); var second = Resource(world, 2, released);
        var owner = Owner(world, (first, 11), (second, 22));

        world.Dispose();

        Assert.False(first.IsValid); Assert.False(second.IsValid);
        Assert.Equal(2, released.Count);
        Assert.Contains(1, released); Assert.Contains(2, released);
        Assert.Equal(33ul, owner.Bytes);

        owner.Dispose(); owner.Dispose();

        Assert.Equal(0ul, owner.Bytes);
        Assert.Equal(2, released.Count);
        Assert.True(world.IsDisposed);
    }
}
