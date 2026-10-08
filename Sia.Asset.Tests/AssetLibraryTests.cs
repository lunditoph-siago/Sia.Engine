using Xunit;

namespace Sia.Asset.Tests;

public sealed class AssetLibraryTests
{
    private sealed record Mesh(ReadOnlyMemory<byte> Bytes);
    private sealed record Texture(ReadOnlyMemory<byte> Bytes);
    private sealed class MeshCodec(long? ownedBytes = null) : IAssetCodec<Mesh>
    {
        public ushort Kind => 7;
        public int Decodes;
        public AssetDecodePlan Inspect(ReadOnlySpan<byte> payload) => new(ownedBytes ?? 0, true);
        public Mesh Decode(ReadOnlyMemory<byte> payload) { Decodes++; return new(payload); }
    }
    private sealed class TextureCodec : IAssetCodec<Texture>
    {
        public ushort Kind => 8;
        public AssetDecodePlan Inspect(ReadOnlySpan<byte> payload) => new(0, true);
        public Texture Decode(ReadOnlyMemory<byte> payload) => new(payload);
    }
    private static readonly AssetLibraryLimits Limits = new(4096, 4096, 4096);

    [Fact]
    public async Task EveryRegisteredKindUsesOneOwnerAndCache()
    {
        var meshBytes = AssetContainer.Encode(7, [1, 2, 3]);
        var textureBytes = AssetContainer.Encode(8, [4, 5]);
        var meshRef = AssetContainer.GetReference<Mesh>(meshBytes, 7);
        var textureRef = AssetContainer.GetReference<Texture>(textureBytes, 8);
        var reads = 0;
        await using var assets = new AssetLibrary((id, _) => {
            Interlocked.Increment(ref reads);
            return ValueTask.FromResult<Stream>(new MemoryStream(id == meshRef.Id ? meshBytes : textureBytes));
        }, Limits).Register(new MeshCodec()).Register(new TextureCodec());
        using var mesh = await assets.AcquireAsync(meshRef);
        using var texture = await assets.AcquireAsync(textureRef);
        using var duplicate = await assets.AcquireAsync(meshRef);
        Assert.Same(mesh.Value, duplicate.Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, mesh.Value.Bytes.ToArray());
        Assert.Equal(new byte[] { 4, 5 }, texture.Value.Bytes.ToArray());
        Assert.Equal(2, reads);
        Assert.Equal(5, assets.Statistics.ResidentBytes);
        Assert.Equal(1, assets.Statistics.CacheHits);
        Assert.Equal(0, assets.Statistics.EncodedBytes);
        Assert.Equal(0, assets.Statistics.DecodedBytes);
        mesh.Dispose();
        mesh.Dispose();
        Assert.Throws<ObjectDisposedException>(() => mesh.Value);
        Assert.Equal(3, duplicate.Value.Bytes.Length);
    }

    [Fact]
    public async Task CoalescesRequestsAndOneBorrowerCancellationDoesNotCancelOthers()
    {
        var bytes = AssetContainer.Encode(7, [1, 2, 3]);
        var reference = AssetContainer.GetReference<Mesh>(bytes, 7);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken sourceToken = default;
        var codec = new MeshCodec();
        await using var assets = new AssetLibrary(async (_, token) => {
            sourceToken = token;
            entered.SetResult();
            await finish.Task;
            return new MemoryStream(bytes);
        }, Limits).Register(codec);
        using var cancellation = new CancellationTokenSource();
        var first = assets.AcquireAsync(reference, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = assets.AcquireAsync(reference).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(sourceToken.IsCancellationRequested);
        Assert.Equal(68, assets.Statistics.EncodedBytes);
        finish.SetResult();
        using var retained = await second;
        Assert.Equal(1, codec.Decodes);
        Assert.Equal(1, assets.Statistics.Reads);
        Assert.Equal(1, assets.Statistics.CoalescedRequests);
    }

    [Fact]
    public async Task AllBorrowersCanceledRetainReservationsUntilReadIsTerminal()
    {
        var bytes = AssetContainer.Encode(7, [1, 2, 3]);
        var reference = AssetContainer.GetReference<Mesh>(bytes, 7);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken sourceToken = default;
        await using var assets = new AssetLibrary(async (_, token) => {
            sourceToken = token;
            entered.SetResult();
            await finish.Task;
            return new MemoryStream(bytes);
        }, Limits).Register(new MeshCodec());
        using var cancellation = new CancellationTokenSource();
        var loading = assets.AcquireAsync(reference, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.True(sourceToken.IsCancellationRequested);
        Assert.Equal(68, assets.Statistics.EncodedBytes);
        Assert.Equal(3, assets.Statistics.DecodedBytes);
        var shutdown = assets.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        finish.SetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, assets.Statistics.PendingAssets);
        Assert.Equal(0, assets.Statistics.EncodedBytes);
        Assert.Equal(0, assets.Statistics.DecodedBytes);
    }

    [Theory]
    [InlineData(67, 4096, AssetBudgetKind.Encoded)]
    [InlineData(4096, 2, AssetBudgetKind.Decoded)]
    public async Task AdmissionPrecedesSourceEffects(long encoded, long decoded, AssetBudgetKind expected)
    {
        var reference = AssetContainer.GetReference<Mesh>(AssetContainer.Encode(7, [1, 2, 3]), 7);
        var reads = 0;
        await using var assets = new AssetLibrary((_, _) => {
            reads++;
            throw new InvalidOperationException("Must not open.");
        }, new(encoded, decoded, 4096)).Register(new MeshCodec());
        var error = await Assert.ThrowsAsync<AssetBudgetExceededException>(() => assets.AcquireAsync(reference).AsTask());
        Assert.Equal(expected, error.Kind);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task ResidentAdmissionPrecedesDecodeAndFailureReleasesEveryReservation()
    {
        var bytes = AssetContainer.Encode(7, [1, 2, 3]);
        var reference = AssetContainer.GetReference<Mesh>(bytes, 7);
        var codec = new MeshCodec(4096);
        await using var assets = new AssetLibrary((_, _) => ValueTask.FromResult<Stream>(new MemoryStream(bytes)), Limits).Register(codec);
        var error = await Assert.ThrowsAsync<AssetBudgetExceededException>(() => assets.AcquireAsync(reference).AsTask());
        Assert.Equal(AssetBudgetKind.Resident, error.Kind);
        Assert.Equal(0, codec.Decodes);
        Assert.Equal(0, assets.Statistics.ResidentBytes);
        Assert.Equal(0, assets.Statistics.PendingAssets);
        Assert.Equal(0, assets.Statistics.EncodedBytes);
        Assert.Equal(0, assets.Statistics.DecodedBytes);
    }

    [Fact]
    public async Task PinnedDataIsNotEvictedAndUnpinnedDataCanBeReplaced()
    {
        var firstBytes = AssetContainer.Encode(7, [1, 2, 3]);
        var secondBytes = AssetContainer.Encode(7, [4, 5, 6]);
        var firstRef = AssetContainer.GetReference<Mesh>(firstBytes, 7);
        var secondRef = AssetContainer.GetReference<Mesh>(secondBytes, 7);
        await using var assets = new AssetLibrary((id, _) => ValueTask.FromResult<Stream>(
            new MemoryStream(id == firstRef.Id ? firstBytes : secondBytes)), Limits with { ResidentBytes = 3 }).Register(new MeshCodec());
        using var pinned = await assets.AcquireAsync(firstRef);
        await Assert.ThrowsAsync<AssetBudgetExceededException>(() => assets.AcquireAsync(secondRef).AsTask());
        Assert.Equal(new byte[] { 1, 2, 3 }, pinned.Value.Bytes.ToArray());
        pinned.Dispose();
        using var next = await assets.AcquireAsync(secondRef);
        Assert.Equal(new byte[] { 4, 5, 6 }, next.Value.Bytes.ToArray());
        Assert.Equal(1, assets.Statistics.ResidentAssets);
        Assert.Equal(3, assets.Statistics.ResidentBytes);
    }

    [Fact]
    public async Task CorruptContentNeverReachesDomainCodec()
    {
        var bytes = AssetContainer.Encode(7, [1, 2, 3]);
        var reference = AssetContainer.GetReference<Mesh>(bytes, 7);
        bytes[^1] ^= 255;
        var codec = new MeshCodec();
        await using var assets = new AssetLibrary((_, _) => ValueTask.FromResult<Stream>(new MemoryStream(bytes)), Limits).Register(codec);
        await Assert.ThrowsAsync<InvalidDataException>(() => assets.AcquireAsync(reference).AsTask());
        Assert.Equal(0, codec.Decodes);
        Assert.Equal(0, assets.Statistics.ResidentAssets);
    }

    [Fact]
    public async Task ReadConcurrencyAndPendingCountAreIndependentBounds()
    {
        var firstBytes = AssetContainer.Encode(7, [1]);
        var secondBytes = AssetContainer.Encode(7, [2]);
        var thirdBytes = AssetContainer.Encode(7, [3]);
        var firstRef = AssetContainer.GetReference<Mesh>(firstBytes, 7);
        var secondRef = AssetContainer.GetReference<Mesh>(secondBytes, 7);
        var thirdRef = AssetContainer.GetReference<Mesh>(thirdBytes, 7);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var assets = new AssetLibrary(async (id, _) => {
            Interlocked.Increment(ref reads);
            if (id == firstRef.Id) { entered.SetResult(); await finish.Task; }
            return new MemoryStream(id == firstRef.Id ? firstBytes : secondBytes);
        }, Limits with { ConcurrentReads = 1, PendingAssets = 2 }).Register(new MeshCodec());
        try {
            var first = assets.AcquireAsync(firstRef).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = assets.AcquireAsync(secondRef).AsTask();
            Assert.Equal(1, Volatile.Read(ref reads));
            Assert.Equal(2, assets.Statistics.PendingAssets);
            var error = await Assert.ThrowsAsync<AssetBudgetExceededException>(() => assets.AcquireAsync(thirdRef).AsTask());
            Assert.Equal(AssetBudgetKind.Requests, error.Kind);
            finish.SetResult();
            using var firstLease = await first;
            using var secondLease = await second;
            Assert.Equal(2, reads);
        }
        finally { finish.TrySetResult(); }
    }

    [Fact]
    public async Task ANewBorrowerWaitsForCanceledReadThenStartsFresh()
    {
        var bytes = AssetContainer.Encode(7, [1]);
        var reference = AssetContainer.GetReference<Mesh>(bytes, 7);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var assets = new AssetLibrary(async (_, _) => {
            if (Interlocked.Increment(ref reads) == 1) { entered.SetResult(); await finish.Task; }
            return new MemoryStream(bytes);
        }, Limits).Register(new MeshCodec());
        try {
            using var cancellation = new CancellationTokenSource();
            var first = assets.AcquireAsync(reference, cancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            var second = assets.AcquireAsync(reference).AsTask();
            Assert.False(second.IsCompleted);
            finish.SetResult();
            using var lease = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, reads);
            Assert.Equal(1, lease.Value.Bytes.Length);
        }
        finally { finish.TrySetResult(); }
    }

    [Fact]
    public async Task ResidentCountBoundsCacheMetadataSeparatelyFromPayloadBytes()
    {
        var firstBytes = AssetContainer.Encode(7, [1]);
        var secondBytes = AssetContainer.Encode(7, [2]);
        var firstRef = AssetContainer.GetReference<Mesh>(firstBytes, 7);
        var secondRef = AssetContainer.GetReference<Mesh>(secondBytes, 7);
        await using var assets = new AssetLibrary((id, _) => ValueTask.FromResult<Stream>(
            new MemoryStream(id == firstRef.Id ? firstBytes : secondBytes)), Limits with { ResidentAssets = 1 }).Register(new MeshCodec());
        using (await assets.AcquireAsync(firstRef)) { }
        using (await assets.AcquireAsync(secondRef)) { }
        using var reread = await assets.AcquireAsync(firstRef);
        Assert.Equal(3, assets.Statistics.Reads);
        Assert.Equal(1, assets.Statistics.ResidentAssets);
        Assert.Equal(1, assets.Statistics.ResidentBytes);
    }

    [Fact]
    public async Task ShutdownCancelsEveryReadAndDrainsDespiteCallbackFailure()
    {
        var bytes = new[] { AssetContainer.Encode(7, [1]), AssetContainer.Encode(7, [2]) };
        var references = bytes.Select(b => AssetContainer.GetReference<Mesh>(b, 7)).ToArray();
        var entered = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
        var canceled = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assets = new AssetLibrary(async (id, token) => {
            var index = id == references[0].Id ? 0 : 1;
            using var registration = token.Register(() => {
                canceled[index].TrySetResult();
                if (index == 0) throw new InvalidOperationException("Cancellation callback failed.");
            });
            entered[index].SetResult();
            await finish.Task;
            return new MemoryStream(bytes[index]);
        }, Limits).Register(new MeshCodec());
        try {
            var requests = references.Select(r => assets.AcquireAsync(r).AsTask()).ToArray();
            await Task.WhenAll(entered.Select(t => t.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = assets.DisposeAsync().AsTask();
            await Task.WhenAll(canceled.Select(t => t.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(shutdown.IsCompleted);
            finish.SetResult();
            var error = await Assert.ThrowsAnyAsync<Exception>(() => shutdown.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("Cancellation callback failed", error.ToString());
            foreach (var request in requests) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(0, assets.Statistics.PendingAssets);
            Assert.Equal(0, assets.Statistics.EncodedBytes);
            Assert.Equal(0, assets.Statistics.DecodedBytes);
        }
        finally {
            finish.TrySetResult();
            try { await assets.DisposeAsync(); } catch { /* The same checked shutdown failure is retained. */ }
        }
    }

    [Fact]
    public async Task RegistrationFreezesAndShutdownInvalidatesOutstandingLeases()
    {
        var bytes = AssetContainer.Encode(7, [1, 2, 3]);
        var reference = AssetContainer.GetReference<Mesh>(bytes, 7);
        await using var assets = new AssetLibrary((_, _) => ValueTask.FromResult<Stream>(new MemoryStream(bytes)), Limits).Register(new MeshCodec());
        Assert.Throws<ArgumentException>(() => assets.Register(new MeshCodec()));
        using var lease = await assets.AcquireAsync(reference);
        Assert.Throws<InvalidOperationException>(() => assets.Register(new TextureCodec()));
        await assets.DisposeAsync();
        await assets.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => lease.Value);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => assets.AcquireAsync(reference).AsTask());
        Assert.Equal(0, assets.Statistics.ResidentBytes);
    }
}
