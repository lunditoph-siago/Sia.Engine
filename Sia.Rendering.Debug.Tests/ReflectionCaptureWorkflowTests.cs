using System.Net;
using Sia.Engine.Example;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class ReflectionCaptureWorkflowTests
{
    private static string WorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".work")) && File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;
        return AppContext.BaseDirectory; // Standalone checkout/CI also owns a local .work area.
    }

    [Fact]
    public async Task BakePublishesCheckedAssetLoadsFromFileAndRefusesOverwrite()
    {
        var directory = Path.Combine(WorkspaceRoot(), ".work", "rendering-tier-implementation", "reflection-workflow-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var scenePath = Path.Combine(directory, "scene.siapbr");
            var environmentPath = Path.Combine(directory, "sky.siaenv");
            var output = Path.Combine(directory, "capture.siarefl");
            var quad = MeshPatchAsset.Cook(new([
                new(new(-1, -1, 1), new(0, 0, -1), new(0, 0)),
                new(new(1, -1, 1), new(0, 0, -1), new(1, 0)),
                new(new(1, 1, 1), new(0, 0, -1), new(1, 1)),
                new(new(-1, 1, 1), new(0, 0, -1), new(0, 1))],
                [0, 2, 1, 0, 3, 2], new(new(-1, -1, 1), new(1, 1, 1))));
            var scene = PbrSceneAsset.Create([quad], [new(new() { BaseColor = float3.zero, EmissiveColor = new(1, 0, 0), EmissiveStrength = .25f }, DoubleSided: true)],
                [new(0, 0, float4x4.identity), new(0, 0, float4x4.identity) { Dynamic = true }]);
            await File.WriteAllBytesAsync(scenePath, scene.Encode());
            using (var file = File.Create(environmentPath)) ReflectionCaptureTests.Environment().Write(file);
            string[] args = ["--bake-reflections", scenePath, "--output", output, "--capture-region", "0,0,0,-2,-2,-2,2,2,2", "--environment", environmentPath];
            await Program.BakeReflectionCaptureAsync(args);
            using var http = new HttpClient();
            var capture = await Program.LoadReflectionCaptureAsync(output, http);
            Assert.Equal(PbrReflectionCaptureAsset.EncodedBytes, new FileInfo(output).Length);
            Assert.Equal(PbrSceneTransport.StaticIdentity(scene), capture.SceneIdentity.ToArray());
            Assert.Equal(float3.zero, capture.Position);
            Assert.Contains(capture.Environment.Cube.ToArray(), v => v == (Half).25f);
            Assert.Equal(3, Directory.GetFiles(directory).Length); // No staging file survives publication.
            var bytes = await File.ReadAllBytesAsync(output);
            await Assert.ThrowsAsync<IOException>(() => Program.BakeReflectionCaptureAsync(args));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
        }
        finally {
            // Only files created by this test live in this unique workspace directory.
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("0,0,0,-1,-1,-1,1,1", "128")]
    [InlineData("NaN,0,0,-1,-1,-1,1,1,1", "128")]
    [InlineData("1,0,0,-1,-1,-1,1,1,1", "128")]
    [InlineData("0,0,0,-1,-1,-1,1,1,1", "0")]
    public async Task InvalidBakeInputsFailBeforeOpeningAnySource(string region, string budget)
        => await Assert.ThrowsAsync<ArgumentException>(() => Program.BakeReflectionCaptureAsync([
            "--bake-reflections", "missing-scene", "--environment", "missing-sky", "--output", "missing-output",
            "--capture-region", region, "--trace-budget-mib", budget]));

    private sealed class PayloadHandler(byte[] bytes) : HttpMessageHandler
    {
        internal Uri? Requested;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    [Fact]
    public async Task HttpLoaderPreservesQueryAndRejectsCorruptionTruncationAndTrailingData()
    {
        var capture = new PbrReflectionCaptureAsset(new byte[32], new byte[32], float3.zero,
            new(new(-1), new(1)), ReflectionCaptureTests.Environment());
        using var payload = new MemoryStream(); capture.Write(payload);
        var bytes = payload.ToArray();
        using var valid = new PayloadHandler(bytes);
        using var http = new HttpClient(valid);
        var uri = "https://example.invalid/capture.siarefl?sha256=abc";
        var restored = await Program.LoadReflectionCaptureAsync(uri, http);
        Assert.Equal(uri, valid.Requested!.AbsoluteUri);
        Assert.Equal(capture.Bounds, restored.Bounds);
        var corrupt = bytes.ToArray(); corrupt[80] ^= 1;
        foreach (var (expected, invalid) in new (Type, byte[])[] {
            (typeof(InvalidDataException), corrupt),
            (typeof(EndOfStreamException), bytes[..100]),
            (typeof(InvalidDataException), [.. bytes, (byte)0]) }) {
            using var handler = new PayloadHandler(invalid);
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync(expected, () => Program.LoadReflectionCaptureAsync(uri, client));
        }
    }
}
