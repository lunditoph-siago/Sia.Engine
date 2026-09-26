# Rendering budgets and verification

The example defaults to the original Bistro asset, PBR shading, Low quality and
automatic LOD. Low quality deliberately sacrifices geometry coverage: it keeps
one root patch per interval of 16, with a fixed phase per asset. Missing walls
and large gaps are expected. Use Medium/High or `--lod finest` when full geometry
coverage matters. These changes do not establish browser 720p60 or native 4K144.

## Retained changes

- PBR without screen-space reflection/indirect passes fuses material resolution
  and lighting, avoiding three full-size surface textures. Texture maps, direct
  lights, IBL and transparency remain active.
- Low shadows use conservative 12-triangle instance boxes and a scene-bound sun
  projection. Camera movement can reuse shadow depth; caster changes invalidate
  it. Box shadows lose silhouette fidelity and can over-occlude interiors.
- Bounded GPU LOD divides spare refinement budgets among root workgroups. Failed
  refinement retains its parent. Sparse strides below 16 select and emit on GPU;
  strides of 16 or more reuse a CPU-built work list until instances change.
- Instance membership events skip redundant queries, while retained-value
  comparison preserves updates made through both `Set` and writable `Get` refs.
- Standalone `FlatShading` resolves constant material/instance colors directly
  into final output; it omits textures, lights and transparency. Select it before
  preparation and do not compose it with `PbrRenderFeature`.
- Fixed and dynamic geometry can use GPU timestamps. Sparse query resolution
  reads only stages written in the current frame; cached/skipped stages are zero.
  Fixed timing uses the same indexed draw as uninstrumented rendering.

No runtime dependencies are added. Public additions are the optional fixed/stream
timing arguments, `FlatShading`, `SampleGpuTiming`, `RootCutStride` (1..64, sparse
values require zero refinement budgets), and the two shadow approximation flags.
Existing default library settings retain full root coverage.

## Example controls

Run the native example with arguments; browser queries use the same names without
`--`. A plain native build needs `--scene PATH`; publish includes bundled assets.

| Control | Behavior |
| --- | --- |
| `--quality low\|medium\|high` | Low is explicitly lossy; default Low |
| `--shading pbr\|flat` | PBR by default; flat is standalone diagnostic output |
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
./.dotnet/dotnet.exe run --no-build --project Sia.Engine/Sia.Rendering.Benchmarks/Sia.Rendering.Benchmarks.csproj -c Release -- --verify-pbr-performance
./.dotnet/dotnet.exe run --no-build --project Sia.Engine/Sia.Rendering.Benchmarks/Sia.Rendering.Benchmarks.csproj -c Release -- --verify-resolution-budget
```

These cover instance updates/removal/reuse/recovery, exact sparse work capacities,
timed/untimed pixel equality, camera/cache invalidation, live resizing, fused PBR
reference tolerance and shadow invalidation. The PBR reference fixture uses rough
materials and no shadows for image equality; separate shadow checks assert cache
behavior. It is not a full Bistro lighting/occlusion quality comparison.

`--cook-performance-fixture OUTPUT.siapbr` produces a generated 256-sphere fixture
without external assets and refuses to overwrite output. Native/window/browser
runtime checks and matched performance measurements remain separate from builds.

## Cleanup validation, 2026-09-26

Release native Example/Benchmarks builds and browser publish passed. Mesh and
Rendering.Debug tests passed (12 each). All four regressions above passed on
NVIDIA GTX 1650 Max-Q/Vulkan. Restoring the writable-ref regression first exposed
a missed instance upload; the retained-value comparison fixes it.

The generated fixture completed 120 measured frames at native 3840x2160 with
bounded offscreen completion and GPU timing. Browser Intel/WebGPU completed 120
frames at 1280x720 with seven GPU samples. An intentionally excessive 1000-FPS
budget caused four internal resizes down to 80x45 while output stayed 1280x720;
that fresh-page run had no console errors. Navigation between earlier test pages
logged fetch errors, so fresh-page runs were checked separately. These are
functional smoke checks, not Bistro performance or visual-quality certification.
