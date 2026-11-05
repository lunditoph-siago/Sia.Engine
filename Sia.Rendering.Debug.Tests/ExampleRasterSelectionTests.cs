using Sia.Engine.Example;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class ExampleRasterSelectionTests
{
    private static ExampleContent.Entry Entry(Guid[] instances) => new() {
        Scene = "Content/" + new string('a', 64) + "/" + new Guid("14000000-0000-0000-0000-000000000001").ToString("N") + ".siascene",
        ExplicitRasterInstances = instances
    };

    [Fact]
    public void CatalogRejectsInvalidOrRepeatedAuthoredSelections()
    {
        Assert.Throws<InvalidDataException>(() => ExampleContent.Validate(new() { Bunny = Entry([Guid.Empty]) }));
        var id = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => ExampleContent.Validate(new() { Bunny = Entry([id, id]) }));
        Assert.Throws<InvalidDataException>(() => ExampleContent.Validate(new() { Bunny = Entry(null!) }));
        var excessive = Enumerable.Range(0, 4097).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Throws<InvalidDataException>(() => ExampleContent.Validate(new() { Bunny = Entry(excessive) }));
    }

    [Fact]
    public void MappingKeepsTheSameAuthoredIdsInAnIndependentSnapshot()
    {
        var id = Guid.NewGuid(); var entry = Entry([id]);
        ExampleContent.Validate(new() { Bunny = entry });
        var mapped = ExampleContent.MapPaths(entry, path => "https://assets.invalid/" + path);
        Assert.Equal(entry.ExplicitRasterInstances, mapped.ExplicitRasterInstances);
        entry.ExplicitRasterInstances[0] = Guid.NewGuid();
        Assert.Equal(id, mapped.ExplicitRasterInstances[0]);
    }
}
