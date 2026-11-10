#define_import_path pbr/shadow_coverage_gate

@group(0) @binding(0) var<storage,read> selection_args: array<u32>;
@group(0) @binding(1) var<storage,read_write> coverage_args: array<vec4<u32>>;
@group(0) @binding(2) var<storage,read_write> coverage_range: vec4<u32>;

// The exact-cut kernel may discover a depth rebuild even when the CPU pose is
// unchanged. Gate alpha on that same result, skipping topology/vertex fetch and
// rasterization on reuse. Cached direct draws still invoke clipped vertices.
// Replay all alpha blockers after any GPU-driven opaque depth clear.
@compute @workgroup_size(64)
fn gate_shadow_coverage(@builtin(global_invocation_id) id: vec3<u32>) {
    let rebuild = select(0u, 1u, selection_args[21] != 0u);
    if (id.x == 0u) { coverage_range.z = rebuild; }
    if (id.x >= coverage_range.y) { return; }
    coverage_args[coverage_range.x + id.x].y = rebuild;
}
