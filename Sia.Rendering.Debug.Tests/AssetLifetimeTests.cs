using Sia;
using Sia.Asset;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class AssetLifetimeTests
{
    private sealed record Record(int Value = 0) : AssetRecord;

    private readonly record struct Asset(int Value) : IAsset<Asset, Record>
    {
        public static void Construct(Record record, out Asset component) => component = new(record.Value);
        public static void HandleCommandTypes(IGenericTypeHandler<ICommand<Asset>> handler) { }
        public static AssetBundle<Asset> Create(Record record, AssetLife life = AssetLife.Automatic)
            => AssetBundle.Create<Asset, Record>(record, life);
    }

    private static World CreateWorld()
    {
        AssetLibrary.RegisterAsset<Asset, Record>();
        var world = new World();
        world.AcquireAddon<AssetLibrary>();
        return world;
    }

    private static Entity Owner(World world) => world.CreateAsset(new Record(), AssetLife.Persistent);

    [Fact]
    public void ForeignReferenceFailsBeforeEitherGraphChanges()
    {
        using var first = CreateWorld();
        using var second = CreateWorld();
        var owner = Owner(first);
        var target = Owner(second);

        Assert.Throws<ArgumentException>(() => owner.Refer(target));

        Assert.Empty(owner.Get<AssetMetadata>().Dependents);
        Assert.Empty(target.Get<AssetMetadata>().Referrers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignOwnerFailsBeforeRecordIsCreatedOrIndexed(bool create)
    {
        using var first = CreateWorld();
        using var second = CreateWorld();
        var owner = Owner(second);
        var record = new Record(7);
        var initialCount = first.Count;

        Assert.Throws<ArgumentException>(() => {
            if (create) first.CreateAsset(record, owner);
            else first.AcquireAsset(record, owner);
        });

        Assert.Equal(initialCount, first.Count);
        Assert.False(first.TryGetAsset(record, out _));
        Assert.Empty(owner.Get<AssetMetadata>().Dependents);
    }

    [Fact]
    public void NonAssetOwnerFailsBeforeRecordIsCreated()
    {
        using var world = CreateWorld();
        var owner = world.Create();
        var record = new Record();
        var initialCount = world.Count;

        Assert.Throws<ArgumentException>(() => world.AcquireAsset(record, owner));

        Assert.Equal(initialCount, world.Count);
        Assert.False(world.TryGetAsset(record, out _));
    }

    [Fact]
    public void DeadTargetAndNonAssetOwnerAreRejectedWithoutAddingEdges()
    {
        using var world = CreateWorld();
        var owner = Owner(world);
        var nonAsset = world.Create();
        var target = Owner(world);
        target.Destroy();

        Assert.Throws<ArgumentException>(() => owner.Refer(target));
        Assert.Throws<ArgumentException>(() => nonAsset.Refer(owner));

        Assert.Empty(owner.Get<AssetMetadata>().Dependents);
        Assert.Empty(owner.Get<AssetMetadata>().Referrers);
    }

    [Fact]
    public void LastUnreferReclaimsAutomaticAssetAndItsDependencies()
    {
        using var world = CreateWorld();
        var owner = Owner(world);
        var childId = AssetId.New();
        var child = world.AcquireAsset(childId, () => new Record(1), owner);
        var leafId = AssetId.New();
        var leaf = world.AcquireAsset(leafId, () => new Record(2), child);
        var notifiedWhileAlive = false;
        world.Dispatcher.Listen<AssetMetadata.OnUnreferred>((Entity target, in AssetMetadata.OnUnreferred _) => {
            if (target == child) notifiedWhileAlive = target.IsValid;
            return false;
        });

        owner.Unrefer(child);

        Assert.True(notifiedWhileAlive);
        Assert.False(child.IsValid);
        Assert.False(leaf.IsValid);
        Assert.False(world.TryGetAsset<Record>(childId, out _));
        Assert.False(world.TryGetAsset<Record>(leafId, out _));
        Assert.Empty(owner.Get<AssetMetadata>().Dependents);
    }

    [Fact]
    public void ReferenceRestoredDuringNotificationPreventsReclamation()
    {
        using var world = CreateWorld();
        var first = Owner(world);
        var second = Owner(world);
        var asset = world.AcquireAsset(new Record(), first);
        var restored = false;
        world.Dispatcher.Listen<AssetMetadata.OnUnreferred>((Entity target, in AssetMetadata.OnUnreferred _) => {
            if (target == asset && !restored) {
                restored = true;
                second.Refer(asset);
            }
            return false;
        });

        first.Unrefer(asset);

        Assert.True(asset.IsValid);
        Assert.Equal(new[] { second }, asset.Get<AssetMetadata>().Referrers);
        second.Unrefer(asset);
        Assert.False(asset.IsValid);
    }

    [Fact]
    public void SharedAssetSurvivesUntilLastOwnerIsDestroyed()
    {
        using var world = CreateWorld();
        var first = Owner(world);
        var second = Owner(world);
        var id = AssetId.New();
        var asset = world.AcquireAsset(id, () => new Record(), first);
        Assert.Equal(asset, world.AcquireAsset<Record>(id, () => throw new InvalidOperationException(), second));

        first.Destroy();
        Assert.True(asset.IsValid);
        second.Destroy();
        Assert.False(asset.IsValid);
        Assert.False(world.TryGetAsset<Record>(id, out _));
    }

    [Fact]
    public void CycleAndSelfReferenceAreRejectedWithoutChangingGraph()
    {
        using var world = CreateWorld();
        var owner = Owner(world);
        var child = world.AcquireAsset(new Record(1), owner);
        var leaf = world.AcquireAsset(new Record(2), child);

        Assert.Throws<InvalidOperationException>(() => leaf.Refer(owner));
        Assert.Throws<InvalidOperationException>(() => leaf.Refer(leaf));

        Assert.Empty(leaf.Get<AssetMetadata>().Dependents);
        Assert.Empty(owner.Get<AssetMetadata>().Referrers);
        Assert.Equal(new[] { child }, owner.Get<AssetMetadata>().Dependents);
        owner.Unrefer(child);
        Assert.False(child.IsValid);
        Assert.False(leaf.IsValid);
    }

    [Fact]
    public void PersistentTargetSurvivesLastUnrefer()
    {
        using var world = CreateWorld();
        var owner = Owner(world);
        var asset = world.AcquireAsset(new Record(), owner, AssetLife.Persistent);

        owner.Unrefer(asset);

        Assert.True(asset.IsValid);
        Assert.Empty(asset.Get<AssetMetadata>().Referrers);
    }

    [Fact]
    public void ReacquisitionPreservesFirstLifetimeAndRecord()
    {
        using var world = CreateWorld();
        var owner = Owner(world);
        var id = AssetId.New();
        var asset = world.AcquireAsset(id, () => new Record(1), owner);
        var factories = 0;

        var again = world.AcquireAsset(id, () => { factories++; return new Record(2); }, AssetLife.Persistent);

        Assert.Equal(asset, again);
        Assert.Equal(0, factories);
        Assert.Equal(1, again.Get<Asset>().Value);
        Assert.Equal(AssetLife.Automatic, again.Get<AssetMetadata>().AssetLife);
        owner.Unrefer(asset);
        Assert.False(again.IsValid);
    }

    [Fact]
    public void DuplicateReferenceIsIdempotent()
    {
        using var world = CreateWorld();
        var owner = Owner(world);
        var asset = world.AcquireAsset(new Record(), owner);

        owner.Refer(asset);
        Assert.Single(asset.Get<AssetMetadata>().Referrers);
        owner.Unrefer(asset);

        Assert.False(asset.IsValid);
    }

    [Fact]
    public void UnloadedIdentityCanAcquireANewRecord()
    {
        using var world = CreateWorld();
        var owner = Owner(world);
        var id = AssetId.New();
        var first = world.AcquireAsset(id, () => new Record(1), owner);
        owner.Unrefer(first);

        var second = world.AcquireAsset(id, () => new Record(2), owner);

        Assert.False(first.IsValid);
        Assert.NotEqual(first, second);
        Assert.Equal(2, second.Get<Asset>().Value);
        Assert.True(world.TryGetAsset<Record>(id, out var indexed));
        Assert.Equal(second, indexed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateRemoveNotificationCannotCreateDanglingReferences(bool dyingIsOwner)
    {
        using var world = CreateWorld();
        var survivor = Owner(world);
        var dying = Owner(world);
        var notified = false;
        Exception? rejection = null;
        // Register after AssetLibrary: its own dependency cleanup has returned when
        // this listener runs, but the entity has not yet been invalidated by World.
        world.Dispatcher.Listen<WorldEvents.Remove>((Entity target, in WorldEvents.Remove _) => {
            if (target != dying) return false;
            notified = true;
            Assert.True(target.IsValid);
            rejection = Xunit.Record.Exception(() => {
                if (dyingIsOwner) target.Refer(survivor);
                else survivor.Refer(target);
            });
            return false;
        });

        dying.Destroy();

        Assert.True(notified);
        Assert.False(dying.IsValid);
        Assert.Empty(survivor.Get<AssetMetadata>().Dependents);
        Assert.Empty(survivor.Get<AssetMetadata>().Referrers);
        Assert.IsType<InvalidOperationException>(rejection);
    }

    [Fact]
    public void FailedRemoveNotificationKeepsAssetClosedToNewReferencesButAllowsDestroyRetry()
    {
        using var world = CreateWorld();
        var owner = Owner(world);
        var dependent = Owner(world);
        var record = new Record(7);
        var dying = world.CreateAsset(record, AssetLife.Persistent);
        owner.Refer(dying); dying.Refer(dependent);
        var error = new InvalidOperationException("late remove callback");
        var attempts = 0;
        world.Dispatcher.Listen<WorldEvents.Remove>((Entity target, in WorldEvents.Remove _) => {
            if (target == dying && ++attempts == 1) throw error;
            return false;
        });

        Assert.Same(error, Assert.Throws<InvalidOperationException>(dying.Destroy));

        Assert.True(dying.IsValid);
        Assert.Empty(dying.Get<AssetMetadata>().Dependents);
        Assert.Empty(dying.Get<AssetMetadata>().Referrers);
        Assert.Empty(owner.Get<AssetMetadata>().Dependents);
        Assert.Empty(dependent.Get<AssetMetadata>().Referrers);
        Assert.Throws<InvalidOperationException>(() => owner.Refer(dying));
        Assert.Throws<InvalidOperationException>(() => dying.Refer(dependent));

        var count = world.Count;
        var createdRecord = new Record(8);
        var acquiredRecord = new Record(9);
        var id = AssetId.New();
        var factories = 0;
        Assert.Throws<InvalidOperationException>(() => world.CreateAsset(createdRecord, dying));
        Assert.Throws<InvalidOperationException>(() => world.AcquireAsset(acquiredRecord, dying));
        Assert.Throws<InvalidOperationException>(() => world.AcquireAsset(id, () => {
            factories++;
            return new Record(10);
        }, dying));
        Assert.Equal(count, world.Count);
        Assert.Equal(0, factories);
        Assert.False(world.TryGetAsset(createdRecord, out _));
        Assert.False(world.TryGetAsset(acquiredRecord, out _));
        Assert.False(world.TryGetAsset<Record>(id, out _));
        Assert.Empty(dying.Get<AssetMetadata>().Dependents);
        Assert.Empty(dying.Get<AssetMetadata>().Referrers);

        dying.Destroy();

        Assert.Equal(2, attempts);
        Assert.False(dying.IsValid);
        Assert.False(world.TryGetAsset(record, out _));
        Assert.Empty(owner.Get<AssetMetadata>().Dependents);
        Assert.Empty(dependent.Get<AssetMetadata>().Referrers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestroyRetryDoesNotRemoveAReplacementAssetIndex(bool identified)
    {
        using var world = CreateWorld();
        var originalRecord = new Record(7);
        var replacementRecord = identified ? new Record(8) : originalRecord;
        var id = AssetId.New();
        var original = identified
            ? world.AcquireAsset(id, () => originalRecord)
            : world.AcquireAsset(originalRecord);
        var error = new InvalidOperationException("host release callback after library removal");
        var attempts = 0;
        // QuerySubscription has already attached AssetLibrary.OnEntityRemoved to
        // this host. This later callback fails after the index removal but before
        // ordinary World release invalidates the entity's storage.
        ((IReactiveEntityHost)original.Host).OnEntityReleased += entity => {
            if (entity == original && ++attempts == 1) throw error;
        };

        Assert.Same(error, Assert.Throws<InvalidOperationException>(original.Destroy));

        Assert.True(original.IsValid);
        Assert.False(world.TryGetAsset(originalRecord, out _));
        if (identified) Assert.False(world.TryGetAsset<Record>(id, out _));
        var replacement = identified
            ? world.AcquireAsset(id, () => replacementRecord)
            : world.AcquireAsset(replacementRecord);
        Assert.NotEqual(original, replacement);
        Assert.True(world.TryGetAsset(replacementRecord, out var indexedRecord));
        Assert.Equal(replacement, indexedRecord);
        if (identified) {
            Assert.True(world.TryGetAsset<Record>(id, out var indexedId));
            Assert.Equal(replacement, indexedId);
        }

        original.Destroy();

        Assert.Equal(2, attempts);
        Assert.False(original.IsValid);
        Assert.True(replacement.IsValid);
        Assert.True(world.TryGetAsset(replacementRecord, out indexedRecord));
        Assert.Equal(replacement, indexedRecord);
        if (identified) {
            Assert.True(world.TryGetAsset<Record>(id, out var indexedId));
            Assert.Equal(replacement, indexedId);
        }
        Assert.Equal(replacement, identified
            ? world.AcquireAsset<Record>(id, () => throw new InvalidOperationException("replacement identity was lost"))
            : world.AcquireAsset(replacementRecord));
        replacement.Destroy();
        Assert.Empty(world.GetAddon<AssetLibrary>().Entities);
        Assert.Equal(0, world.Count);
    }
}
