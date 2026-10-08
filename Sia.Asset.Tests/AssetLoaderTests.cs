using Xunit;

namespace Sia.Asset.Tests;

public class AssetLoaderTests
{
    private sealed record Loaded(Stream Input) : ILoadable<Loaded>, ILoadable<Loaded, bool>
    {
        public static Stream? LastInput;
        public static Loaded Load(Stream stream, string? name = null) => new(stream);
        public static Loaded Load(Stream stream, bool fail, string? name = null)
        {
            LastInput = stream;
            if (fail) throw new InvalidDataException("fixture failure");
            return new(stream);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmbeddedLoaderClosesOwnedStream(bool fail)
    {
        var assembly = typeof(AssetLoaderTests).Assembly;
        if (fail) Assert.Throws<InvalidDataException>(() => EmbeddedLoader.Load<Loaded, bool>("sample.txt", true, assembly));
        else EmbeddedLoader.Load<Loaded, bool>("sample.txt", false, assembly);
        Assert.False(Loaded.LastInput!.CanRead);
    }

    [Fact]
    public void EmbeddedLoaderWithoutOptionsClosesOwnedStream()
        => Assert.False(EmbeddedLoader.Load<Loaded>("sample.txt", typeof(AssetLoaderTests).Assembly).Input.CanRead);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileLoaderClosesOwnedStream(bool fail)
    {
        var directory = Path.GetDirectoryName(typeof(AssetLoaderTests).Assembly.Location)!;
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, "fixture");
        try {
            if (fail) Assert.Throws<InvalidDataException>(() => FileLoader.Load<Loaded, bool>(path, true));
            else FileLoader.Load<Loaded, bool>(path, false);
            Assert.False(Loaded.LastInput!.CanRead);
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally {
            Loaded.LastInput?.Dispose();
            File.Delete(path);
        }
    }
}
