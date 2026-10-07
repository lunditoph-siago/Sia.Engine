using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Sia.Engine.Example;
using Sia.Engine.Rendering;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class SceneInputsTests
{
    private static readonly SceneInputs Inputs = new("geometry/scene.siapbr", "geometry/Bistro.siastream",
        "environment.siaenv", "lightmaps.sialmst", "probes.siaprobe");

    [Theory]
    [InlineData(RenderQuality.Low, null, "geometry/scene.siapbr")]
    [InlineData(RenderQuality.Medium, null, "geometry/Bistro.siastream")]
    [InlineData(RenderQuality.High, null, "geometry/Bistro.siastream")]
    [InlineData(RenderQuality.Low, false, "geometry/scene.siapbr")]
    [InlineData(RenderQuality.Medium, false, "geometry/scene.siapbr")]
    [InlineData(RenderQuality.High, false, "geometry/scene.siapbr")]
    [InlineData(RenderQuality.Low, true, "geometry/scene.siapbr")]
    [InlineData(RenderQuality.Medium, true, "geometry/scene.siapbr")]
    [InlineData(RenderQuality.High, true, "geometry/scene.siapbr")]
    public void TierSelectionPreservesExplicitLod(RenderQuality quality, bool? lod, string expected)
        => Assert.Equal(expected, Inputs.SelectScene(quality, lod));

    [Fact]
    public void UnsupportedQualityIsRejectedEvenWithLodOverride()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Inputs.SelectScene((RenderQuality)123, true));

    [Fact]
    public void OptionalCaptureAndResidentLightmapsRoundTripAndResolveFromTheDescriptionDirectory()
    {
        var inputs = Inputs with { Reflections = "captures/room.siarefl", Lightmaps = "surface.sialmap" };
        var restored = SceneInputs.Decode(inputs.Encode());
        Assert.Equal(inputs, restored);
        var parent = Path.GetFullPath(Path.Combine(".work", "scene-input-test"));
        var resolved = restored.Resolve(Path.Combine(parent, "scene.siascene"));
        Assert.Equal(Path.Combine(parent, "geometry", "scene.siapbr"), resolved.SelectScene(RenderQuality.Low));
        Assert.Equal(Path.Combine(parent, "geometry", "Bistro.siastream"), resolved.SelectScene(RenderQuality.High));
        Assert.Equal(Path.Combine(parent, "captures", "room.siarefl"), resolved.Reflections);
    }

    [Fact]
    public async Task HttpLoadPreservesDescriptionQueryAndResolvesReferencesWithoutReadingAssets()
    {
        using var handler = new Handler(Inputs.Encode());
        using var http = new HttpClient(handler);
        const string address = "https://example.invalid/scenes/v1/scene.siascene?revision=2";
        var loaded = await SceneInputs.LoadAsync(address, http);
        Assert.Equal(address, Assert.Single(handler.Addresses).AbsoluteUri);
        Assert.Equal("https://example.invalid/scenes/v1/geometry/scene.siapbr", loaded.SelectScene(RenderQuality.Low));
        Assert.Equal("https://example.invalid/scenes/v1/geometry/Bistro.siastream", loaded.SelectScene(RenderQuality.Medium));
        Assert.Equal("https://example.invalid/scenes/v1/environment.siaenv", loaded.Environment);
        Assert.Equal("https://example.invalid/scenes/v1/lightmaps.sialmst", loaded.Lightmaps);
        Assert.Equal("https://example.invalid/scenes/v1/probes.siaprobe", loaded.Probes);
        Assert.Null(loaded.Reflections);
    }

    [Theory]
    [InlineData("../scene.siapbr")]
    [InlineData("/scene.siapbr")]
    [InlineData("https://elsewhere.invalid/scene.siapbr")]
    [InlineData("C:/scene.siapbr")]
    [InlineData("nested\\scene.siapbr")]
    [InlineData("%2e%2e/scene.siapbr")]
    [InlineData("scene.siapbr?token=value")]
    [InlineData("scene.siapbr#fragment")]
    [InlineData("nested//scene.siapbr")]
    [InlineData("nested/./scene.siapbr")]
    [InlineData("scene.siastream")]
    [InlineData("")]
    public void InvalidReferencesAreRejectedByBothReaderAndWriter(string reference)
    {
        Assert.Throws<InvalidDataException>(() => (Inputs with { Resident = reference }).Encode());
        var json = JsonNode.Parse(Inputs.Encode())!;
        json["Resident"] = reference;
        Assert.Throws<InvalidDataException>(() => SceneInputs.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData("Version", "2")]
    [InlineData("Version", "\"1\"")]
    [InlineData("Version", "null")]
    [InlineData("Resident", "null")]
    [InlineData("Stream", "123")]
    [InlineData("Environment", "[]")]
    [InlineData("Probes", "true")]
    [InlineData("Reflections", "null")]
    [InlineData("Unknown", "\"value\"")]
    public void InvalidFieldsFailInsteadOfSilentlyFallingBack(string field, string value)
    {
        var json = JsonNode.Parse(Inputs.Encode())!;
        json[field] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => SceneInputs.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("Resident")]
    [InlineData("Stream")]
    [InlineData("Environment")]
    [InlineData("Lightmaps")]
    [InlineData("Probes")]
    public void RequiredFieldsCannotBeOmitted(string field)
    {
        var json = JsonNode.Parse(Inputs.Encode())!.AsObject();
        Assert.True(json.Remove(field));
        Assert.Throws<InvalidDataException>(() => SceneInputs.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public async Task DuplicateMalformedAndOversizedDescriptionsFail()
    {
        var json = Encoding.UTF8.GetString(Inputs.Encode());
        var duplicate = json[..^1] + ",\"Resident\":\"other.siapbr\"}";
        foreach (var invalid in new[] { duplicate, "[]", "{", json + "{}" })
            Assert.Throws<InvalidDataException>(() => SceneInputs.Decode(Encoding.UTF8.GetBytes(invalid)));
        var oversized = new byte[SceneInputs.MaximumBytes + 1];
        Assert.Throws<InvalidDataException>(() => SceneInputs.Decode(oversized));
        using var handler = new Handler(oversized);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => SceneInputs.LoadAsync("https://example.invalid/scene.siascene", http));
    }

    private sealed class Handler(byte[] payload) : HttpMessageHandler
    {
        internal List<Uri> Addresses = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Addresses.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}
