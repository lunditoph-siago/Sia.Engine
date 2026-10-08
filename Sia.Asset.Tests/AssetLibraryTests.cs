using Sia;
using Xunit;

namespace Sia.Asset.Tests;

public sealed record TestRecord(int Value) : AssetRecord;

public record struct TestAsset(int Value) : IAsset<TestAsset, TestRecord>
{
    public static void Construct(TestRecord record, out TestAsset asset) => asset = new(record.Value);
    public static void HandleCommandTypes(IGenericTypeHandler<ICommand<TestAsset>> handler) { }
    public static AssetBundle<TestAsset> Create(TestRecord record, AssetLife life = AssetLife.Automatic)
        => AssetBundle.Create<TestAsset, TestRecord>(record, life);
}

public class AssetLibraryTests
{
    [Fact]
    public void RecordKeysUseReferenceIdentity()
    {
        var first = new TestRecord(1);
        var second = new TestRecord(1);
        Assert.Equal(first, second);
        Assert.NotEqual(new ObjectKey<IAssetRecord>(first), new ObjectKey<IAssetRecord>(second));
        Assert.Equal(new ObjectKey<IAssetRecord>(first), new ObjectKey<IAssetRecord>(first));
        Assert.False(new ObjectKey<IAssetRecord>(first).Equals((ObjectKey<IAssetRecord>?)null));
    }

    [Fact]
    public void AcquireReusesEntityAndDestructionRemovesCacheEntry()
    {
        AssetLibrary.RegisterAsset<TestAsset, TestRecord>();
        using var world = new World();
        var library = world.AddAddon<AssetLibrary>();
        var record = new TestRecord(7);
        var first = library.AcquireEntity(record, AssetLife.Persistent);
        Assert.Equal(7, first.Get<TestAsset>().Value);
        Assert.Equal(first, library.AcquireEntity(record));
        Assert.Single(library.Entities);
        first.Destroy();
        Assert.False(library.TryGet(record, out _));
        Assert.True(library.AcquireEntity(record).IsValid);
    }

    [Fact]
    public void DestroyingOneReferrerPreservesSharedAssetUntilLastReferrer()
    {
        AssetLibrary.RegisterAsset<TestAsset, TestRecord>();
        using var world = new World();
        var library = world.AddAddon<AssetLibrary>();
        var a = library.CreateEntity(new TestRecord(1), AssetLife.Persistent);
        var b = library.CreateEntity(new TestRecord(2), AssetLife.Persistent);
        var shared = library.AcquireEntity(new TestRecord(3), a);
        b.Refer(shared);
        a.Destroy();
        Assert.True(shared.IsValid);
        Assert.Equal([b], shared.Get<AssetMetadata>().Referrers);
        b.Destroy();
        Assert.False(shared.IsValid);
    }

    [Fact]
    public void DestroyingReferencedAssetDetachesIncomingEdges()
    {
        AssetLibrary.RegisterAsset<TestAsset, TestRecord>();
        using var world = new World();
        var library = world.AddAddon<AssetLibrary>();
        var parent = library.CreateEntity(new TestRecord(1));
        var child = library.CreateEntity(new TestRecord(2), parent);
        child.Destroy();
        Assert.Empty(parent.Get<AssetMetadata>().Dependents);
        parent.Destroy();
        Assert.Empty(library.Entities);
    }

    [Fact]
    public void RemovingUnrelatedEntityDoesNotAccessAssetMetadata()
    {
        using var world = new World();
        world.AddAddon<AssetLibrary>();
        world.Create().Add(42).Destroy();
    }

    [Fact]
    public void PersistentDependencySurvivesLastReferrer()
    {
        AssetLibrary.RegisterAsset<TestAsset, TestRecord>();
        using var world = new World();
        var library = world.AddAddon<AssetLibrary>();
        var parent = library.CreateEntity(new TestRecord(1));
        var child = library.CreateEntity(new TestRecord(2), parent, AssetLife.Persistent);
        parent.Destroy();
        Assert.True(child.IsValid);
        Assert.Empty(child.Get<AssetMetadata>().Referrers);
    }

    [Fact]
    public void ExplicitRemovalBreaksAnAutomaticDependencyCycle()
    {
        AssetLibrary.RegisterAsset<TestAsset, TestRecord>();
        using var world = new World();
        var library = world.AddAddon<AssetLibrary>();
        var a = library.CreateEntity(new TestRecord(1));
        var b = library.CreateEntity(new TestRecord(2), a);
        b.Refer(a);
        a.Destroy();
        Assert.False(b.IsValid);
        Assert.Empty(library.Entities);
    }

    [Fact]
    public void EqualValuedRecordsRemainIndependentAssets()
    {
        AssetLibrary.RegisterAsset<TestAsset, TestRecord>();
        using var world = new World();
        var library = world.AddAddon<AssetLibrary>();
        var first = library.AcquireEntity(new TestRecord(5), AssetLife.Persistent);
        var second = library.AcquireEntity(new TestRecord(5), AssetLife.Persistent);
        Assert.NotEqual(first, second);
        Assert.Equal(2, library.Entities.Count);
    }
}
