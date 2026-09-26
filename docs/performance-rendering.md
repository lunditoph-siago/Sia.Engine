# Rendering budgets and verification

The example defaults to the original Bistro asset, standard PBR, Low quality and
automatic LOD. All quality profiles preserve the complete root cut and use real
geometry for shadows. Low reduces shadow resolution and disables screen-space
effects. This increment does not establish browser 720p60 or native 4K144.

## Scope

The retained changes add output/internal resolution separation, sampled full-frame
GPU timing and bounded native offscreen completion. Native visibility uses implicit
depth; the browser retains its capability-dependent depth workaround. Rendering
uses the existing clustered PBR, LOD selection and instance extraction paths.

PR #163 cleanup removes flat shading, sparse root sampling, CPU root-work caching,
box shadows, unclustered fused lighting and the experimental parallel LOD selector.
Their switches and duplicate paths are removed together. The selector's fixed
per-root limits and zero maximum-error statistic were not compatible with the
existing diagnostic contract. Mainline streaming decode optimizations are preserved.
No runtime dependency is added. `SampleGpuTiming` is the remaining new library
control; fixed-geometry timing support is already part of the merged mainline.

## Example controls

Run the native example with arguments; browser queries use the same names without
`--`. A plain native build needs `--scene PATH`; publish includes bundled assets.

| Control | Behavior |
| --- | --- |
| `--quality low\|medium\|high` | Standard render profile; default Low |
| `--width N --height N` | Output dimensions, 64..8192 |
| `--render-scale F` | Internal scale, 0.0625..1; nearest-neighbor final scaling |
| `--gpu-timing true` | Optional timestamp queries for monolithic PBR scenes |
| `--target-fps N` | GPU feedback budget, 1..1000; requires timestamp queries |
| `--offscreen true --benchmark-frames N` | Native only; exact-size PBR output, three bounded completion readbacks |
| `--present fifo\|immediate` | Native only; immediate falls back when unsupported |

The resolution controller uses a GPU budget of `800 / targetFps` milliseconds,
sampled every 16 frames. Two over-budget samples reduce scale; 32 samples below
65% of budget slowly restore it. It rejects stale dimensions and invalid feedback.
Output dimensions remain fixed. CPU cadence never drives the GPU controller.

Benchmark JSON reports actual output/internal dimensions, adapter, quality,
per-frame scale, CPU stages and sampled GPU stages. `OffscreenReadback3` measures
completion throughput with readback overhead, not physical presentation. Pending
GPU sample counts are reported; final pending samples may miss the emitted report.
CPU/RAF cadence and summed GPU stages are not interchangeable with full-frame GPU
time. PBR begin/end markers cover the composed PBR graph.

## Checks

From a workspace root with its documented environment setup completed:

```powershell
./.dotnet/dotnet.exe build Sia.Engine/Sia.Engine.Example/Sia.Engine.Example.csproj -c Release
./.dotnet/dotnet.exe build Sia.Engine/Sia.Rendering.Benchmarks/Sia.Rendering.Benchmarks.csproj -c Release
./.dotnet/dotnet.exe run --no-build --project Sia.Engine/Sia.Rendering.Benchmarks/Sia.Rendering.Benchmarks.csproj -c Release -- --verify-gpu-scene
./.dotnet/dotnet.exe run --no-build --project Sia.Engine/Sia.Rendering.Benchmarks/Sia.Rendering.Benchmarks.csproj -c Release -- --verify-visibility-cache
./.dotnet/dotnet.exe run --no-build --project Sia.Engine/Sia.Rendering.Benchmarks/Sia.Rendering.Benchmarks.csproj -c Release -- --verify-pbr-frame
./.dotnet/dotnet.exe run --no-build --project Sia.Engine/Sia.Rendering.Benchmarks/Sia.Rendering.Benchmarks.csproj -c Release -- --verify-resolution-budget
```

These cover writable-ref instance updates, removal/reuse/recovery, complete root
coverage at exact capacity, timed/untimed pixel equality, camera/cache invalidation,
live resizing, textured PBR light response, real shadow rendering/reuse and frame
timestamp readback. They do not establish Bistro image quality or target throughput.

`--cook-performance-fixture OUTPUT.siapbr` generates a 256-sphere fixture without
external assets and refuses to overwrite output. Native/browser execution and
matched performance measurements remain separate from builds.
