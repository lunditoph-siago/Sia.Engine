# Scene reflections and glass

`PbrRenderFeatureOptions.ScreenSpaceReflections` enables current-frame reflections
on opaque Visibility surfaces in shaded mode (default `true`). It replaces the
environment specular contribution where an opaque scene hit is available,
preserving direct lighting and falling back to the existing IBL on misses.
The forward opaque path does not use this pass.

Optical glass is explicit and independent of alpha coverage:

```csharp
var glass = new PbrMaterialAsset(
    PbrMaterial.Default with { BaseColor = new(1, 1, 1), Metallic = 0, Roughness = .05f },
    DoubleSided: true, AlphaBlend: true, Opacity: 1,
    Transmission: 1, Thickness: .01f);
```

`Transmission` is a finite fraction in [0, 1]; `Thickness` is a finite slab
thickness in world meters in [0, 10], independent of instance scale. Positive
transmission requires the transparent pass (`AlphaBlend: true`). `Opacity` and
base-color texture alpha still control surface coverage. Base-color RGB tints
transmitted light. Metals suppress dielectric transmission. IOR is fixed at 1.5.

`PbrTransparentScene` supplies Fresnel reflection and refraction through a locally
parallel slab on both rendering paths. Zero thickness gives undistorted thin-sheet
transmission. Reflection uses the scene on hits and environment IBL on misses;
refraction falls back to the current background pixel. The opaque reflection
option does not disable explicitly authored optical glass.

The screen trace uses at most 48 steps over 24 meters and six hit refinements.
Reflections fade out between roughness .15 and .35; this is intended for clear
glass and polished surfaces. Rough transmission blur, arbitrary volumes, absorption,
variable IOR, off-screen geometry, hidden layers and multiple transparent bounces
are not implemented. Constant slab thickness does not describe the side faces of
a solid block correctly. Separate overlapping panes still inherit object sorting
limits. These effects do not solve indirect occlusion or local diffuse GI.

Each opaque reflection pass copies one RGBA16F scene image; an optical scene adds
another copy after opaque reflections and before transparent rendering. This
avoids reading an HDR attachment while writing it. The legacy alpha-only path
does not allocate the optical snapshot.

## Asset compatibility

SIAPBR version 3 appends transmission and thickness floats after opacity in each
material record (78 fixed bytes including texture indices; v2 was 70 and v1 was 64).
The decoder accepts v1/v2 and supplies zero optical parameters. Existing callers
also retain zero defaults. The encoder emits v3; older runtimes cannot read it.
Deploy a compatible reader before replacing downloaded assets with v3 files.
No asset name convention implicitly enables refraction.

## Validation

Run `dotnet test Sia.Rendering.Pbr.Tests/Sia.Rendering.Pbr.Tests.csproj -c Release`
from the engine root using its configured SDK. The tests cover independent legacy
wire records, coverage-preserving optical round trips and invalid parameters.

The workspace blender-sync skill owns file-based cooking and matched Blender/Sia
capture tools. Its clear slab, emissive checker wall and polished floor fixture
uses shared geometry, camera, light and linear captures. At 640 × 480, Cycles
128 samples versus Sia 4 × 4 supersampling on Vulkan/GTX 1650 Max-Q, relative RGB
RMSE changed from 24.69% (alpha approximation) to 15.45% (optical glass), then
12.38% (plus opaque reflections). Coverage IoU was .9942. The full comparison
still fails its 10% RMSE / 95% pixel-tolerance gate; do not treat this as Cycles parity.

Existing v2 Bistro with opaque reflections disabled rendered byte-identically to
the prior linear capture. With reflections enabled, entrance/cafe comparisons
remain at 68.71% / 32.88% relative RMSE: indirect lighting is still the dominant
gap tracked by issue #119. The published alpha-based Bistro materials do not opt
into physical refraction. No published asset migration is included in this change.
