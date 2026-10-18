# Signed dynamic diffuse transport

The asset entry remains Sia.Asset. Scene composition borrows independently acquired
Mesh BVHs, materials and textures; it performs no cooking or hidden acquisition.
Rendering owns mutable instance state, uploads, dispatch and deterministic disposal.

When dynamic GI and surface lightmaps are both selected, the renderer builds the
reference instance domain from the lightmap set's stable receiver IDs. Selection
shares local BVHs, bindings and material values, orders instances by Guid, and
checks exact static source/material/transform identity and every receiver slot.
Stale reference inputs fail before GPU allocation. The frozen set retains the
reference sky, directional lighting, samples, maximum distance and ray bias.

One local geometry packet/buffer serves both domains. Live and reference have
independent instance acceleration and material tables. GPU integration traces
identical directions and subtracts live-minus-reference incident radiance before
SH reduction. It uses the reference sample/ray domain and the bake's one-bounce
mean-material Lambertian model and L1 basis. The same dispatch also stores live
absolute L1 coefficients for unbaked surfaces. Both fields occupy two sets of seven
bands in one RGBA16Float 3D texture; no extra shading texture binding is required.

Shading evaluates the signed bake and correction before final clipping. Missing,
out-of-volume or invalid correction samples contribute zero. Sparse valid corners
keep their interpolation weight instead of being amplified by renormalization.
Absolute live samples retain validity normalization and the environment provider
outside their valid domain. Direct/specular lighting and emission remain separate.
Pairs with excessive one-sided backface coverage or nonfinite/unrepresentable
coefficients are invalidated. This is a coarse spatial approximation, not exact
per-texel dynamic transport or a multibounce solution.

`UpdateInstanceTransforms` and `UpdateInstanceVisibility` stage absolute updates
by authored instance ID. All expanded opaque/transparent sections change together;
the live CPU/GPU BVH sees the same inclusion state. Disabled instances retain
conservative bounds/topology and immutable geometry. The renderer rejects runtime
changes to frozen static bake receivers. Draw/shadow selection and stale traversal
feedback are invalidated before the next frame. No geometry upload or recook occurs.

`UpdateTransportMaterials` consumes explicit acquired material values by asset ID
for live mean diffuse/emissive transport. It does not replace draw materials or the
frozen bake. Complete batches are validated before staging. Changed instance and
surface rows are uploaded in contiguous ranges; unchanged rows are retained.
SceneTraceData replacement methods share immutable geometry and authored identity.

Changes to live instances/materials or lighting clear stale field validity before
the next dispatch. Reset scratch is preallocated, reset is coalesced once between
successful integration recordings, and the bounded round-robin cursor continues.
A moving scene can have sparse current coverage until its update budget catches
up. No stale field is reused as if it represented the new scene. Temporal reuse
and multibounce reconstruction are separate capabilities.

`DiffuseProbeGpu.RequiredBytes` counts local geometry once, both instance/material
domains, uniforms, coefficient buffer and both texture fields. The constructor
checks the total before GPU allocation. The texture needs twice the ordinary
probe depth; reset scratch is at most4096 probes *14 bands *8 bytes =458752 bytes
and is host memory outside that GPU budget. Static/reference packets and caller
asset lifetimes remain explicit. Runtime correction cannot be persisted through
the absolute probe bake method. Render graph declarations include both domains.

CPU/asset regression and actual shader Naga validation passed, as did native and
browser managed builds. GPU execution, pixels, convergence, queue behavior, FPS
and actual driver memory remain unverified while graphics acceptance is paused.
