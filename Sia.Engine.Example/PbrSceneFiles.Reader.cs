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
            => Read(id, Location(id), (stream, name) => decode(stream, (ulong)_maximumFileBytes, name));

        private async Task<T> Read<T>(AssetId id, string location, Func<Stream, string, T> decode) where T : class, IAssetRecord
        {
            Claim<T>(id);
            var records = await ReadBatch([id], _ => location, (stream, _, name) => decode(stream, name!));
            return records[id];
        }

        private string Location(AssetId id) => _uri is not null
            ? new Uri(_uri, id.Value.ToString("N") + ".siaasset").AbsoluteUri
            : Path.Combine(_root!, id.Value.ToString("N") + ".siaasset");

        private void Claim<T>(AssetId id) where T : class, IAssetRecord
        {
            _token.ThrowIfCancellationRequested();
            if (_claims.TryGetValue(id, out var kind) && kind != typeof(T))
                throw new InvalidDataException("One logical asset ID cannot identify different record kinds.");
            _claims[id] = typeof(T);
        }

        internal Task<Dictionary<AssetId, T>> ReadMany<T>(IEnumerable<AssetId> ids,
            Func<Stream, ulong, string?, T> decode) where T : class, IAssetRecord
        {
            var selected = new List<AssetId>(); var unique = new HashSet<AssetId>();
            foreach (var id in ids) {
                Claim<T>(id);
                if (unique.Add(id)) selected.Add(id);
            }
            return ReadBatch(selected, Location, decode);
        }

        private async Task<Dictionary<AssetId, T>> ReadBatch<T>(IReadOnlyList<AssetId> ids,
            Func<AssetId, string> location, Func<Stream, ulong, string?, T> decode) where T : class, IAssetRecord
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_token);
            var token = cancellation.Token;
            var window = new ReadWindow(_maximumFileBytes, _maximumTotalBytes - EncodedBytes);
            var pending = new Queue<Task<EncodedInput>>(4);
            var records = new Dictionary<AssetId, T>();
            var next = 0; var consuming = -1;
            var failureGate = new object(); Exception? failure = null; var failedIndex = int.MaxValue;
            void Remember(int index, Exception error)
            {
                if (error is OperationCanceledException && token.IsCancellationRequested) return;
                lock (failureGate) {
                    // Cancelled reads may report IO/disposal errors; preserve the initiating failure.
                    if (token.IsCancellationRequested && failure is not null) return;
                    if (index < failedIndex) { failedIndex = index; failure = error; }
                }
            }
            async Task<EncodedInput> Fetch(int index)
            {
                try { return await ReadEncoded(location(ids[index]), index, window, token).ConfigureAwait(false); }
                catch (Exception error) {
                    Remember(index, error);
                    try { await cancellation.CancelAsync().ConfigureAwait(false); }
                    catch (Exception cancelError) { Remember(index, cancelError); }
                    throw;
                }
            }
            void Fill()
            {
                while (pending.Count < 4 && next < ids.Count) {
                    var index = next++;
                    pending.Enqueue(Fetch(index));
                }
            }
            try {
                Fill();
                for (var index = 0; index < ids.Count; index++) {
                    consuming = index;
                    using var input = await pending.Dequeue();
                    Fill();
                    T Decode(CancellationToken cpuToken)
                    {
                        cpuToken.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                        if (!MemoryMarshal.TryGetArray(input.Bytes, out var segment)) throw new InvalidOperationException("Expected owned encoded array.");
                        using var encoded = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
                        return decode(encoded, (ulong)_maximumFileBytes, input.Location);
                    }
#if BROWSER
                    var record = await Program.BrowserOwner.RunCpuAsync(Decode, token);
#else
                    var record = Decode(token);
#endif
                    token.ThrowIfCancellationRequested();
                    records.Add(ids[index], record);
                    EncodedBytes += input.Bytes.Length;
                }
                return records;
            }
            catch (Exception error) {
                // Cancel and drain prefetched payloads before propagating the initiating failure.
                Remember(consuming, error);
                try { await cancellation.CancelAsync(); }
                catch (Exception cancelError) { Remember(consuming, cancelError); }
                while (pending.TryDequeue(out var task)) {
                    try { using var abandoned = await task; }
                    catch { /* Keep the initiating error, not secondary cancellation. */ }
                }
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }
        }

        private async Task<EncodedInput> ReadEncoded(string location, int index, ReadWindow window, CancellationToken token)
        {
            HttpResponseMessage? response = null; Stream? stream = null;
            ReadWindow.Reservation? reservation = null;
            EncodedInput? result = null;
            try {
                long? length;
                if (AssetInput.TryGetHttpUri(location, out var uri)) {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    // Encoded and unknown bodies require exclusive admission; wire length is not decoded length.
                    length = response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase))
                        ? null : response.Content.Headers.ContentLength;
                    reservation = await window.Reserve(index, length, token).ConfigureAwait(false);
                    stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                }
                else {
                    stream = await AssetInput.OpenAsync(location, _client, token).ConfigureAwait(false);
                    length = stream.CanSeek ? stream.Length - stream.Position : null;
                    reservation = await window.Reserve(index, length, token).ConfigureAwait(false);
                }
                ReadOnlyMemory<byte> bytes = length is { } exact
                    ? await AssetInput.ReadExactAsync(stream, checked((int)exact), token).ConfigureAwait(false)
                    : await AssetInput.ReadBoundedAsync(stream, reservation.Value.Limit, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                window.Complete(reservation.Value, bytes.Length);
                result = new EncodedInput(bytes, location, window, reservation.Value);
                reservation = null;
                return result;
            }
            catch {
                if (reservation is { } held) window.Abort(held);
                throw;
            }
            finally {
                try {
                    try { stream?.Dispose(); }
                    finally { response?.Dispose(); }
                }
                catch { result?.Dispose(); throw; }
            }
        }

        private sealed class EncodedInput(ReadOnlyMemory<byte> bytes, string location, ReadWindow window, ReadWindow.Reservation reservation) : IDisposable
        {
            private ReadWindow? _window = window;
            internal ReadOnlyMemory<byte> Bytes { get; private set; } = bytes;
            internal string Location { get; } = location;
            public void Dispose()
            {
                Interlocked.Exchange(ref _window, null)?.Release(reservation);
                Bytes = default;
            }
        }

        // At most four responses are open. Ordered byte admission prevents a
        // later small payload from pinning space needed by an earlier large one.
        // This counts owned encoded inputs, not transport buffers or GC garbage.
        private sealed class ReadWindow(int maximumFileBytes, long remainingBytes)
        {
            private const long PayloadBytes = 16L * 1024 * 1024;
            private readonly Lock _gate = new();
            private TaskCompletionSource _changed = NewSignal();
            private int _next;
            private long _pending, _total;
            private bool _exclusive;
            internal readonly record struct Reservation(int Limit, long Bytes, bool Exclusive);
            private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
            private void Pulse() { var prior = _changed; _changed = NewSignal(); prior.TrySetResult(); }

            internal async Task<Reservation> Reserve(int index, long? length, CancellationToken token)
            {
                while (true) {
                    Task wait;
                    lock (_gate) {
                        token.ThrowIfCancellationRequested();
                        if (length is < 0 || length > maximumFileBytes)
                            throw new InvalidDataException("Asset payload exceeds its encoded file size limit.");
                        if (index == _next) {
                            var limit = (int)System.Math.Min(maximumFileBytes, remainingBytes - _total);
                            if (length is < 0 || length > limit)
                                throw new InvalidDataException("Asset payload exceeds its file or aggregate encoded size limit.");
                            var bytes = length ?? limit;
                            var exclusive = length is null || bytes > PayloadBytes;
                            if (!_exclusive && (exclusive ? _pending == 0 : _pending + bytes <= PayloadBytes)) {
                                _pending += bytes; _total += bytes; _exclusive = exclusive; _next++;
                                Pulse();
                                return new(limit, bytes, exclusive);
                            }
                        }
                        wait = _changed.Task;
                    }
                    await wait.WaitAsync(token).ConfigureAwait(false);
                }
            }

            internal void Complete(Reservation held, int actual)
            {
                lock (_gate) { _total -= held.Bytes - actual; Pulse(); }
            }
            internal void Abort(Reservation held)
            {
                lock (_gate) { _total -= held.Bytes; Release(held); }
            }
            internal void Release(Reservation held)
            {
                lock (_gate) {
                    _pending -= held.Bytes;
                    if (held.Exclusive) _exclusive = false;
                    Pulse();
                }
            }
        }

        internal async Task<PreparedReferences> References()
        {
            var scene = await Read(Id, _path, (s, n) => PbrSceneRecord.Load(s, (ulong)_maximumFileBytes, n));
            var meshIds = new List<AssetId>(); var materialIds = new List<AssetId>();
            var uniqueMeshes = new HashSet<AssetId>(); var uniqueMaterials = new HashSet<AssetId>();
            var used = new bool[scene.Data.Bindings.Length]; foreach (var instance in scene.Data.Instances) used[instance.Binding] = true;
            for (var i = 0; i < used.Length; i++) {
                if (!used[i]) continue;
                _token.ThrowIfCancellationRequested(); var binding = scene.Data.Bindings[i];
                if (uniqueMeshes.Add(binding.Mesh.Value)) meshIds.Add(binding.Mesh.Value);
                for (var at = 0; at < binding.Materials.Length; at++) {
                    var id = binding.Materials[at].Material.Value;
                    if (uniqueMaterials.Add(id)) materialIds.Add(id);
                    if (materialIds.Count > 4096) throw new InvalidDataException("Resolved material table limit exceeded.");
                }
            }
            var meshes = await ReadMany<MeshManifestRecord>(meshIds, static (s, _, n) => MeshManifestRecord.Load(s, n));
            var materials = await ReadMany<PbrMaterialRecord>(materialIds, static (s, _, n) => PbrMaterialRecord.Load(s, n));
            var textureIds = new List<AssetId>(); var uniqueTextures = new HashSet<AssetId>();
            foreach (var material in materials.Values)
                foreach (var reference in material.Data.TextureReferences) {
                    if (uniqueTextures.Add(reference.Value)) textureIds.Add(reference.Value);
                    if (textureIds.Count > 4096) throw new InvalidDataException("Resolved texture table limit exceeded.");
                }
            var textures = await ReadMany<PbrTextureRecord>(textureIds, PbrTextureRecord.Load);
            _token.ThrowIfCancellationRequested();
            return new(Id, scene, meshes, materials, textures) { EncodedBytes = EncodedBytes };
        }
    }
}
