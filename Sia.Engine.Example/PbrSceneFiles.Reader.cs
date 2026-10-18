using System.Runtime.InteropServices;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    private sealed record PreparedReferences(AssetId Id, PbrSceneRecord Scene,
        IReadOnlyDictionary<AssetId, MeshManifestRecord> Meshes,
        IReadOnlyDictionary<AssetId, PbrMaterialRecord> Materials,
        IReadOnlyDictionary<AssetId, PbrTextureRecord> Textures) : Prepared(Id, Scene, Meshes, Materials, Textures);

    // Application-owned location, cross-kind claims and aggregate encoded admission; streams are scoped per read.
    private sealed class Reader
    {
        private readonly string _path;
        private readonly string? _root;
        private readonly Uri? _uri;
        private readonly HttpClient _client;
        private readonly CancellationToken _token;
        private readonly Dictionary<AssetId, Type> _claims = [];
        private readonly int _maximumFileBytes;
        private readonly long _maximumTotalBytes;
        internal AssetId Id { get; }
        internal long EncodedBytes { get; private set; }

        internal Reader(string path, HttpClient client, int maximumFileBytes, long maximumTotalBytes, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(client);
            ArgumentOutOfRangeException.ThrowIfNegative(maximumFileBytes); ArgumentOutOfRangeException.ThrowIfNegative(maximumTotalBytes);
            token.ThrowIfCancellationRequested();
            var http = AssetInput.TryGetHttpUri(path, out var uri);
            var filename = Path.GetFileNameWithoutExtension(http ? uri!.AbsolutePath : path);
            if (!IsReferenceScene(path) || !Guid.TryParseExact(filename, "N", out var guid) || guid == Guid.Empty)
                throw new ArgumentException("Reference scenes require a nonempty Guid in N format and .siascene suffix.", nameof(path));
            Id = new(guid); _path = path; _client = client; _token = token;
            _root = http ? null : Path.GetDirectoryName(Path.GetFullPath(path)); _uri = http ? uri : null;
            _maximumFileBytes = maximumFileBytes; _maximumTotalBytes = maximumTotalBytes;
        }

        internal Task<T> Read<T>(AssetId id, Func<Stream, ulong, string?, T> decode) where T : class, IAssetRecord
            => Read(id, _uri is not null ? new Uri(_uri, id.Value.ToString("N") + ".siaasset").AbsoluteUri
                : Path.Combine(_root!, id.Value.ToString("N") + ".siaasset"), (stream, name) => decode(stream, (ulong)_maximumFileBytes, name));

        private async Task<T> Read<T>(AssetId id, string location, Func<Stream, string, T> decode) where T : class, IAssetRecord
        {
            _token.ThrowIfCancellationRequested();
            if (_claims.TryGetValue(id, out var kind) && kind != typeof(T))
                throw new InvalidDataException("One logical asset ID cannot identify different record kinds.");
            _claims[id] = typeof(T);
            using var input = await AssetInput.OpenAsync(location, _client, _token);
            var limit = (int)System.Math.Min(_maximumFileBytes, _maximumTotalBytes - EncodedBytes);
            var bytes = await AssetInput.ReadBoundedAsync(input, limit, _token); EncodedBytes += bytes.Length;
            _token.ThrowIfCancellationRequested();
            T Decode(CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); _token.ThrowIfCancellationRequested();
                if (!MemoryMarshal.TryGetArray(bytes, out var segment)) throw new InvalidOperationException("Expected owned encoded array.");
                using var encoded = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
                return decode(encoded, location);
            }
#if BROWSER
            return await Program.BrowserOwner.RunCpuAsync(Decode, _token);
#else
            return Decode(_token);
#endif
        }

        internal async Task<PreparedReferences> References()
        {
            var scene = await Read(Id, _path, (s, n) => PbrSceneRecord.Load(s, (ulong)_maximumFileBytes, n));
            var meshes = new Dictionary<AssetId, MeshManifestRecord>(); var materials = new Dictionary<AssetId, PbrMaterialRecord>();
            var textures = new Dictionary<AssetId, PbrTextureRecord>();
            var used = new bool[scene.Data.Bindings.Length]; foreach (var instance in scene.Data.Instances) used[instance.Binding] = true;
            for (var i = 0; i < used.Length; i++) {
                if (!used[i]) continue;
                _token.ThrowIfCancellationRequested(); var binding = scene.Data.Bindings[i];
                if (!meshes.ContainsKey(binding.Mesh.Value)) meshes.Add(binding.Mesh.Value, await Read<MeshManifestRecord>(binding.Mesh.Value, static (s, _, n) => MeshManifestRecord.Load(s, n)));
                for (var at = 0; at < binding.Materials.Length; at++) {
                    var id = binding.Materials[at].Material.Value;
                    if (materials.ContainsKey(id)) continue;
                    if (materials.Count == 4096) throw new InvalidDataException("Resolved material table limit exceeded.");
                    var material = await Read<PbrMaterialRecord>(id, static (s, _, n) => PbrMaterialRecord.Load(s, n)); materials.Add(id, material);
                    foreach (var reference in material.Data.TextureReferences) {
                        if (textures.ContainsKey(reference.Value)) continue;
                        if (textures.Count == 4096) throw new InvalidDataException("Resolved texture table limit exceeded.");
                        textures.Add(reference.Value, await Read<PbrTextureRecord>(reference.Value, PbrTextureRecord.Load));
                    }
                }
            }
            _token.ThrowIfCancellationRequested();
            return new(Id, scene, meshes, materials, textures) { EncodedBytes = EncodedBytes };
        }
    }
}
