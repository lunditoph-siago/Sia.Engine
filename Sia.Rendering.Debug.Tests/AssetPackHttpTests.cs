using System.Net;
using Sia.Asset;
using Sia.Engine.Example;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class AssetPackHttpTests
{
    private static (AssetPackIndex Index, byte[] Encoded, string Hash, AssetChunk Leaf, byte[] Body) Fixture()
    {
        byte[] body = [8, 5, 9, 7]; var leaf = AssetChunk.FromBytes(body);
        // A one-file pack intentionally has the same content ID as its leaf.
        var index = new AssetPackIndex([new("one.siaasset", leaf, new(leaf, 0))]);
        using var output = new MemoryStream(); var hash = index.Save(output);
        return (index, output.ToArray(), hash, leaf, body);
    }

    [Fact]
    public async Task HttpIndexWrapperAuthenticatesTheRequestedHashAndKeepsQueryAndInputOwnership()
    {
        var f = Fixture(); var stream = new TrackingStream(f.Encoded); var requests = 0;
        using var client = new HttpClient(new Handler((request, _) => {
            requests++;
            Assert.Equal("https://assets.invalid/Content/snapshot/" + f.Hash + ".siapack?v=1", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }));

        var loaded = await PbrSceneFiles.ReadPackIndexAsync("https://assets.invalid/Content/snapshot/" + f.Hash + ".siapack?v=1", client);

        Assert.NotNull(loaded); Assert.Equal(f.Leaf.Id, loaded.Entries["one.siaasset"].Chunk.Id);
        Assert.Equal(1, requests); Assert.True(stream.Disposed);
        using var preserved = new MemoryStream(); Assert.Equal(f.Hash, loaded.Save(preserved)); Assert.Equal(f.Encoded, preserved.ToArray());
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("oversized")]
    public async Task HttpIndexWrapperRejectsInvalidBodiesAndDisposesResponseStream(string variant)
    {
        var f = Fixture();
        var body = variant switch {
            "corrupt" => f.Encoded.ToArray(), "truncated" => f.Encoded[..^1],
            "trailing" => f.Encoded.Concat(new byte[] { 0 }).ToArray(),
            _ => new byte[AssetPackIndex.MaximumEncodedBytes + 1]
        };
        if (variant == "corrupt") body[^1] ^= 1;
        var stream = new TrackingStream(body);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StreamContent(stream)
        })));

        await Assert.ThrowsAsync<InvalidDataException>(() => PbrSceneFiles.ReadPackIndexAsync("https://assets.invalid/" + f.Hash + ".siapack", client));

        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("not-a-hash.siapack")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF.siaasset")]
    public async Task InvalidIndexNameFailsBeforeHttp(string name)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) => { requests++; throw new InvalidOperationException("IO must not start"); }));
        if (name.EndsWith(".siapack", StringComparison.Ordinal))
            await Assert.ThrowsAsync<ArgumentException>(() => PbrSceneFiles.ReadPackIndexAsync("https://assets.invalid/" + name, client));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => PbrSceneFiles.ReadPackIndexAsync("https://assets.invalid/" + name, client));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task NullIndexIsTheFlatPackageCompatibilityPath()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("IO must not start")));
        Assert.Null(await PbrSceneFiles.ReadPackIndexAsync(null, client));
    }

    [Fact]
    public async Task OneFilePackLoadsByHashChunkWithExistingHttpSourceAndOnePhysicalReservation()
    {
        var f = Fixture(); var requests = 0;
        using var client = new HttpClient(new Handler((request, _) => {
            requests++; Assert.EndsWith("/" + f.Leaf.FileName, request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Body) });
        }));
        await using var cache = new AssetChunkCache(AssetChunkSources.Http(client, new Uri("https://assets.invalid/")), 5,
            resolve: f.Index.Resolve);

        using var first = await cache.AcquireAsync(f.Leaf);
        using var again = await cache.AcquireAsync(f.Leaf);

        Assert.Equal(f.Body, first.Memory.ToArray()); Assert.Equal(f.Body, again.Memory.ToArray()); Assert.Equal(1, requests);
        Assert.Equal(5, cache.Statistics.PeakReservedBytes);
        Assert.True(cache.Statistics.PhysicalReadMilliseconds >= 0);
        Assert.True(cache.Statistics.AcquireWaitMilliseconds >= 0);
    }

    [Fact]
    public async Task HttpPackCancellationPropagatesAndReleasesThePhysicalReservation()
    {
        var f = Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) => {
            entered.SetResult(); await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }));
        await using var cache = new AssetChunkCache(AssetChunkSources.Http(client, new Uri("https://assets.invalid/")), 5,
            resolve: f.Index.Resolve);
        using var cancellation = new CancellationTokenSource();
        var read = cache.AcquireAsync(f.Leaf, cancellationToken: cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await cache.DisposeAsync(); Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
