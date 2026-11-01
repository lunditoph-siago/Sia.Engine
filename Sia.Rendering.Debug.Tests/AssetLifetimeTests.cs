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
}
