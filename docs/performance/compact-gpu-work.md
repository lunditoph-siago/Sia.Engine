# Compact GPU geometry work measurements

Recorded on 2026-10-02 and 2026-10-03. These results describe the internal
16-triangle work layout change, not an overall engine performance guarantee.
The baseline already includes parallel hierarchy traversal and node expansion
(baseline PBR source 1fb3cbe, identical to master f51f6ef before this patch).
Camera, assets, quality and streaming budgets remain unchanged in production.

## Layout and validation

The old work buffer stored one 8-byte triangle/instance pair per triangle. Each
new 16-byte record addresses up to 16 consecutive triangles within a page part,
preserving arena and instance identity and recording the valid count. Capacity
uses the maximum of the parent's rounded part count and child-cut record count;
it includes aliased ranges and all legal cuts, not just leaves. Padded slots
produce degenerate triangles. True triangle feedback excludes padding.

The two committed CPU regression cases cover larger parent record cuts, aliased
short ranges, partial blocks and independent roots. Local submitted GPU checks
covered twelve exact triangle/instance multiset cases, missing-child and budget
fallback, re-entry, zero dispatch, mixed sidedness and more than 65,535 selected
nodes. Visibility ID/depth and shadow-depth readbacks confirmed unused slots do
not write pixels. The task-local GPU harness and captures are retained in the
originating workspace; they are not packaged by this repository.

Four RenderDoc 1.46 captures compared shaded Bunny near/far images at 1280 x 720;
both baseline/B16 RGB thumbnail pairs were pixel-identical. Browser non-AOT
publication ran on Intel WebGPU Core and Compatibility: shaded/triangle toggles,
near/far camera changes and scene shutdown passed without warn/error logs.
Desktop Compatibility does not qualify mobile drivers. Native/browser AOT,
mobile, moving Bistro, Medium/High and fully resident finest Bistro were not
measured in this pass.

## Native measurements

NVIDIA GeForce GTX 1650 with Max-Q Design, driver 617.14, Vulkan; Release,
immediate presentation, fixed scale one and static camera. Each profile uses
three alternating/rotated-order runs, 120 warmup frames and 960 measured frames.
Each run summarizes its final 30 asynchronous GPU samples; tables report the
median of three run summaries (also for P95), not a pooled global P95.
`pbr-frame` GPU timestamps exclude pre-graph queue uploads, presentation and
query readback. They do not measure CPU cadence or total FPS.

| Profile | Baseline median / P95 ms | B16 median / P95 ms |
| --- | --- | --- |
| Bunny near 720p | 1.063 / 1.260 | 1.055 / 1.285 |
| Bunny far 720p | 2.900 / 2.921 | 2.800 / 2.848 |
| Bunny far 1080p | 4.128 / 4.180 | 4.043 / 4.092 |
| Bistro default Low 720p | 9.407 / 11.531 | 10.337 / 11.767 |
| Bistro default Low 1080p | 13.933 / 14.276 | 14.065 / 14.777 |
| Bistro fixed-root 720p (2026-10-03) | 8.574 / 8.753 | 8.293 / 8.403 |

Bunny comparisons retain identical selected counts: 190,475 / 2,468,575 /
3,320,759 respectively, with zero deferred refinements/failed readbacks in the
measurement windows. Near run variation is large; no near speedup is established.
Far improvements are small (3.48% and 2.07%), not a general 10% speedup claim.

Default Bistro uses Low Visibility, procedural environment, environment-only
diffuse GI, nine material batches, 256 shadow tiles and 0.5 main/shadow error
targets. Internal resolutions are 1280 x 720 and 1920 x 1080; the window manager
clamps 1080p presentation to 1920 x 1055. All eighteen Bistro runs exit normally
with zero failed GPU readbacks.

Default 720p run medians are baseline 9.233 / 11.625 / 9.407 ms and B16 11.461 /
10.337 / 9.029 ms. Per-round differences change sign. Baseline selected counts
range 1,208,573..2,190,521 in the measurement windows; B16 ranges
2,141,788..2,142,700. Default 1080p medians are baseline 13.933 / 14.112 / 12.675
and B16 14.471 / 14.065 / 12.409 ms. Both versions continue uploading detail and
defer thousands of refinements. These are observed workflow timings with
different cuts, not isolated same-quality layout speedups. The observed default
720p median and 1080p P95 regressions are retained; default GPU benefit is not
established. Read-only partial GPU telemetry does not prove a thermal cause.

The fixed-root fixture overrides only main/shadow error targets to float.MaxValue
in a task-local Example build. All six runs select 2,139,728 triangles with zero
deferred refinements. Medians are baseline 8.550 / 8.574 / 8.620 and B16 8.293 /
8.263 / 8.318 ms, a repeatable 3.27% median improvement for this fixture. It is
not a default-quality or fully resident finest throughput result. Fixture changes
are absent from production source and this PR.

## Allocation results

| Per-view allocation | Baseline bytes | B16 bytes | Saved MiB |
| --- | --- | --- | --- |
| Bunny 720p | 102,745,520 | 38,030,888 | 61.72 |
| Bunny 1080p | 121,249,520 | 56,534,888 | 61.72 |
| Bistro 720p | 57,613,848 | 32,869,528 | 23.60 |
| Bistro 1080p | 76,693,848 | 51,949,528 | 23.60 |

Bistro shared scene allocation stays 319,898,868 bytes. Selection/view counters
overlap; their savings must not be added. CPU objects, driver storage and in-flight
resources are additional memory. The dependable gain is reduced view allocation.

## Reproduction

Use the repository's .NET 11 SDK and run from the Engine checkout:

```powershell
dotnet build Sia.Engine.Example/Sia.Engine.Example.csproj -c Release -p:PublishAot=false -m:1
dotnet test Sia.Rendering.Debug.Tests/Sia.Rendering.Debug.Tests.csproj -c Release -m:1
dotnet test Sia.Mesh.Tests/Sia.Mesh.Tests.csproj -c Release -m:1
dotnet Sia.Engine.Example/bin/Release/net11.0/Sia.Engine.Example.dll --pipeline pbr --scene PATH/Bistro.siastream --quality low --width 1280 --height 720 --render-scale 1 --present immediate --gpu-timing true --benchmark-frames 960
```

For 1080p, change width/height to 1920/1080. Freeze Release outputs before each
A/B series and change only the PBR DLL; hash the scene and chunks. The Bistro
stream metadata SHA256 is
`01863a9040acf30515f4c34dd897b4d3680615145383eabb01c2cf92b1cfcd5e`, with
16,928 sibling chunk files. This input is the Quadric stream, not the older
Blender-study stream. Hardware, identical command-line options and live cut/
residency statistics are necessary context for any repeated timing comparison.
