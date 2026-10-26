#if !BROWSER
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Example;

internal static partial class MeshCook
{
    // The host selects the executable explicitly. Recipes describe source policy,
    // never an executable or a command line. The private files belong to this call.
    internal static MeshSource GenerateTangents(MeshSource source, string toolPath, string scratchParent,
        ulong maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchParent);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(toolPath))
            throw new ArgumentException("The reference tangent tool requires an explicit absolute path.", nameof(toolPath));
        toolPath = Path.GetFullPath(toolPath);
        if (!File.Exists(toolPath)) throw new FileNotFoundException("Reference tangent tool was not found.", toolPath);
        scratchParent = Path.GetFullPath(scratchParent);
        if (!Directory.Exists(scratchParent)) throw new DirectoryNotFoundException("Tangent scratch parent must already exist.");
        if (!BitConverter.IsLittleEndian)
            throw new NotSupportedException("The reference tangent tool requires a little-endian float32 host.");

        var corners = source.Indices.Length;
        var outputBytes = checked(16ul + (ulong)corners * 16);
        RequireTangentSourceBudget(source.Vertices.Length, corners, source.Sections.Length, source.HasBakeUvs, maximumBytes);
        if (outputBytes > maximumBytes || (ulong)corners * sizeof(uint) > maximumBytes
            || (ulong)source.Vertices.Length > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Tangent corner output or reconstruction arrays exceed the byte limit.");

        var directory = Path.Combine(scratchParent, ".mesh-tangents-" + Guid.NewGuid().ToString("N"));
        if (Path.Exists(directory)) throw new IOException("Tangent scratch path already exists.");
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "source.siaasset");
        var outputPath = Path.Combine(directory, "tangents.bin");
        var inputCreated = false;
        Process? process = null;
        var started = false;
        var tasks = new List<Task>(3);
        using var pipes = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? failure = null;
        try {
            RequireTangentDirectory(directory);
            using (var file = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                inputCreated = true;
                using var bounded = new OutputStream(file, maximumBytes, cancellationToken);
                source.Save(bounded);
            }
            cancellationToken.ThrowIfCancellationRequested();
            RequireTangentDirectory(directory);
            var start = new ProcessStartInfo(toolPath) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add(inputPath);
            start.ArgumentList.Add(outputPath);
            start.ArgumentList.Add(maximumBytes.ToString(CultureInfo.InvariantCulture));
            process = new Process { StartInfo = start };
            started = process.Start();
            if (!started) throw new IOException("Reference tangent tool did not start.");
            tasks.Add(DrainTangentPipeAsync(process.StandardOutput.BaseStream, pipes.Token));
            tasks.Add(DrainTangentPipeAsync(process.StandardError.BaseStream, pipes.Token));
            tasks.Add(process.WaitForExitAsync(pipes.Token));
            AwaitTangentProcessAsync(tasks).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidDataException($"Reference tangent tool failed with exit code {process.ExitCode}.");

            RequireTangentDirectory(directory);
            var attributes = File.GetAttributes(outputPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Reference tangent result must be a regular owned file.");
            using var input = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if ((ulong)input.Length != outputBytes)
                throw new InvalidDataException("Reference tangent result length does not match the complete corner table.");
            Span<byte> bytes = stackalloc byte[16];
            input.ReadExactly(bytes);
            if (!bytes[..8].SequenceEqual("MIKKTG01"u8)
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != (uint)corners
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0)
                throw new InvalidDataException("Unsupported reference tangent corner result.");

            // Retain unreferenced records as well as the oriented indexed surface.
            // These arrays and the final owned/encoded source shape are admitted
            // separately; the limit does not describe native scratch or peak RSS.
            var used = new bool[source.Vertices.Length];
            foreach (var index in source.Indices) {
                cancellationToken.ThrowIfCancellationRequested();
                used[(int)index] = true;
            }
            var unused = 0;
            foreach (var value in used) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!value) unused++;
            }
            RequireTangentSourceBudget(checked(1 + unused), corners, source.Sections.Length, source.HasBakeUvs, maximumBytes);
            var indices = new uint[corners];
            var vertices = new List<MeshVertex>();
            var bakeUvs = source.HasBakeUvs ? new List<float2>() : null;
            var remap = new Dictionary<TangentVertexKey, uint>();
            for (var corner = 0; corner < corners; corner++) {
                cancellationToken.ThrowIfCancellationRequested();
                input.ReadExactly(bytes);
                var tangent = new float4(BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]));
                if (!float.IsFinite(tangent.x) || !float.IsFinite(tangent.y) || !float.IsFinite(tangent.z)
                    || tangent.w is not (1f or -1f))
                    throw new InvalidDataException("Reference tangent corners require finite values and a unit handedness sign.");
                var original = (int)source.Indices[corner];
                var vertex = source.Vertices[original] with { Tangent = tangent };
                var bake = source.HasBakeUvs ? source.BakeUvs[original] : default;
                var key = TangentVertexKey.Create(vertex, bake);
                if (!remap.TryGetValue(key, out var index)) {
                    RequireTangentSourceBudget(checked(vertices.Count + 1 + unused), corners, source.Sections.Length,
                        source.HasBakeUvs, maximumBytes);
                    index = checked((uint)vertices.Count);
                    remap.Add(key, index); vertices.Add(vertex); bakeUvs?.Add(bake);
                }
                indices[corner] = index;
            }
            if (input.ReadByte() != -1) throw new InvalidDataException("Trailing reference tangent result data.");
            for (var original = 0; original < used.Length; original++) {
                cancellationToken.ThrowIfCancellationRequested();
                if (used[original]) continue;
                // Do not fold an orphan into a referenced record or another orphan.
                vertices.Add(source.Vertices[original]);
                bakeUvs?.Add(source.BakeUvs[original]);
            }
            RequireTangentSourceBudget(vertices.Count, corners, source.Sections.Length, source.HasBakeUvs, maximumBytes);
            cancellationToken.ThrowIfCancellationRequested();
            // Create snapshots, validates and hashes synchronously. As with the
            // existing source decoder, cancellation is observed around this phase.
            var result = MeshSource.Create(new(vertices.ToArray(), indices, source.Bounds), source.Sections,
                bakeUvs is null ? default : bakeUvs.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) {
            var cancelled = new OperationCanceledException(error.Message, error, cancellationToken);
            failure = cancelled;
            throw cancelled;
        }
        catch (Exception error) { failure = error; throw; }
        finally {
            var cleanup = new List<Exception>();
            var stopped = !started;
            try { pipes.Cancel(); } catch (Exception error) { cleanup.Add(error); }
            if (started) {
                try {
                    if (!process!.HasExited) {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
                    }
                    process!.WaitForExit();
                    stopped = true;
                }
                catch (Exception error) { cleanup.Add(error); }
                try { process!.StandardOutput.Dispose(); } catch (Exception error) { cleanup.Add(error); }
                try { process!.StandardError.Dispose(); } catch (Exception error) { cleanup.Add(error); }
            }
            // Every task is observed after pipes close and the owned child exits.
            // An OS failure to stop it preserves its working files instead.
            if (stopped) {
                foreach (var task in tasks) {
                    try { task.GetAwaiter().GetResult(); }
                    catch { /* Preserve the initiating tool/cancellation failure. */ }
                }
            }
            else {
                foreach (var task in tasks)
                    _ = task.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                cleanup.Add(new IOException("Tangent child exit could not be confirmed; its working directory was preserved."));
            }
            try { process?.Dispose(); } catch (Exception error) { cleanup.Add(error); }
            if (stopped) {
                try {
                    RequireTangentDirectory(directory);
                    if (inputCreated) DeleteTangentFile(inputPath);
                    if (Path.Exists(outputPath)) DeleteTangentFile(outputPath);
                    Directory.Delete(directory, recursive: false);
                }
                catch (Exception error) { cleanup.Add(error); }
            }
            if (cleanup.Count != 0) {
                if (failure is not null)
                    throw new AggregateException("Tangent generation failed and cleanup was incomplete.", [failure, .. cleanup]);
                throw new AggregateException("Tangent generation cleanup was incomplete.", cleanup);
            }
        }
    }

    private static void RequireTangentSourceBudget(int vertices, int indices, int sections, bool bake, ulong maximumBytes)
    {
        var encoded = checked(77ul + (ulong)vertices * (bake ? 56ul : 48ul) + (ulong)indices * 4 + (ulong)sections * 12);
        var owned = checked((ulong)vertices * ((ulong)MeshVertex.Stride + (bake ? (ulong)Unsafe.SizeOf<float2>() : 0))
            + (ulong)indices * 4 + (ulong)sections * 12);
        if (encoded > maximumBytes || owned > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Canonical tangent source exceeds its encoded/owned byte limit.");
    }

    private static void RequireTangentDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Tangent working directory must not be a directory link.");
    }

    private static void DeleteTangentFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Unexpected tangent working entry was preserved.");
        File.Delete(path);
    }

    private static async Task DrainTangentPipeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var total = 0;
        while (true) {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            total = checked(total + read);
            if (total > 8192) throw new InvalidDataException("Reference tangent tool exceeded its diagnostic output limit.");
        }
    }

    private static async Task AwaitTangentProcessAsync(IReadOnlyList<Task> tasks)
    {
        var pending = tasks.ToList();
        while (pending.Count != 0) {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            pending.Remove(completed);
        }
    }

    // Only meaningful serialized float32 words participate; ABI padding does not.
    // A value-based float key would silently merge distinct +0/-0 source words.
    private readonly record struct TangentVertexKey(int Px, int Py, int Pz, int Nx, int Ny, int Nz,
        int U, int V, int Tx, int Ty, int Tz, int Tw, int BakeU, int BakeV)
    {
        internal static TangentVertexKey Create(MeshVertex vertex, float2 bake)
            => new(Bits(vertex.Position.x), Bits(vertex.Position.y), Bits(vertex.Position.z),
                Bits(vertex.Normal.x), Bits(vertex.Normal.y), Bits(vertex.Normal.z), Bits(vertex.UV.x), Bits(vertex.UV.y),
                Bits(vertex.Tangent.x), Bits(vertex.Tangent.y), Bits(vertex.Tangent.z), Bits(vertex.Tangent.w), Bits(bake.x), Bits(bake.y));
        private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    }
}
#endif
