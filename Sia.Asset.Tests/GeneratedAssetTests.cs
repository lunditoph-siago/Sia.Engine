using Sia;
using Xunit;

namespace Sia.Asset.Tests;

[SiaAsset]
[SiaTemplate("GeneratedAsset")]
public partial record GeneratedRecord : AssetRecord
{
    public int Value { get; init; }
}

public class GeneratedAssetTests
{
    [Fact]
    public void OriginalGeneratorsCreateAssetBundlesThroughLibrary()
    {
        AssetLibrary.RegisterAsset<GeneratedAsset, GeneratedRecord>();
        using var world = new World();
        var library = world.AddAddon<AssetLibrary>();
        var record = new GeneratedRecord { Value = 42, Name = "generated" };
        var entity = world.AcquireAsset(record);
        Assert.Equal(42, entity.Get<GeneratedAsset>().Value);
        Assert.Same(record, entity.Get<AssetMetadata>().AssetSource);
        Assert.Equal(AssetLife.Persistent, entity.Get<AssetMetadata>().AssetLife);
        Assert.Equal(entity, library[record]);
        Assert.Equal(entity, ((AssetRefer<GeneratedRecord>)record).Find(world));
    }
}
