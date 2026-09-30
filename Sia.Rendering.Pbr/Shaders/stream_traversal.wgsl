#define_import_path pbr/stream_traversal
#import rendering/Geometry/projected_error

struct Node {
    lo: vec4<f32>, hi: vec4<f32>,
    links: vec4<u32>, // first child, child count, first part, part count
    owner: vec4<u32>  // parent, sidedness, unused, unused
}
struct Selection {
    vp: mat4x4<f32>,
    table: vec4<u32>, // root entries base, root count, page count, refinement limit
    screen: vec4<f32>, // width, height, error, unused
    output: vec4<u32> // single capacity, double capacity, shadow layer, unused
}
@group(0) @binding(0) var<uniform> selection: Selection;
@group(0) @binding(1) var<storage, read> nodes: array<Node>;
@group(0) @binding(2) var<storage, read> parts: array<vec4<u32>>;
@group(0) @binding(3) var<storage, read> residency: array<vec4<u32>>;
@group(0) @binding(4) var<storage, read_write> work: array<u32>;
@group(0) @binding(5) var<storage, read_write> args: array<atomic<u32>>;
@group(0) @binding(6) var<storage, read_write> feedback: array<atomic<u32>>;

@compute @workgroup_size(64)
fn reset_feedback(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x < selection.table.z * 2u) { atomicStore(&feedback[id.x], 0u); }
}
@compute @workgroup_size(1)
fn reset_draws() {
    for (var i = 0u; i < 12u; i++) { atomicStore(&args[i], 0u); }
    atomicStore(&args[1], 1u);
    atomicStore(&args[5], 1u);
    atomicStore(&args[6], selection.output.x * 3u);
}
fn touch(n: Node) {
    for (var i = 0u; i < n.links.w; i++) {
        let part = parts[n.links.z + i];
        if (residency[part.x].z == 0u) { atomicOr(&feedback[part.x * 2u + 1u], 1u); }
    }
}
fn children_ready(n: Node, priority: f32) -> bool {
    var ready = true;
    for (var c = 0u; c < n.links.y; c++) {
        let child = nodes[n.links.x + c];
        for (var p = 0u; p < child.links.w; p++) {
            let part = parts[child.links.z + p];
            let page = residency[part.x];
            if (page.w == 0u) {
                atomicMax(&feedback[part.x * 2u], bitcast<u32>(max(priority, 1.0e-20)));
                ready = false;
            }
            else if (page.z == 0u) { atomicOr(&feedback[part.x * 2u + 1u], 1u); }
        }
    }
    return ready;
}
fn emit(n: Node) {
    var count = 0u;
    for (var p = 0u; p < n.links.w; p++) { count += parts[n.links.z + p].z; }
    let side = n.owner.y;
    let first = atomicAdd(&args[side * 4u], count * 3u) / 3u;
    let capacity = select(selection.output.x, selection.output.y, side != 0u);
    if (first > capacity || count > capacity - first) { atomicAdd(&args[10], 1u); return; }
    let base = select(0u, selection.output.x, side != 0u);
    var at = base + first;
    for (var p = 0u; p < n.links.w; p++) {
        let part = parts[n.links.z + p];
        let page = residency[part.x];
        for (var t = 0u; t < part.z; t++) { work[at + t] = page.x + part.y + t; }
        at += part.z;
    }
}
@compute @workgroup_size(64)
fn traverse(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= selection.table.y) { return; }
    let root = parts[selection.table.x + id.x].x;
    var current = root;
    loop {
        let n = nodes[current];
        let error = bounds_pixel_error(n.lo.xyz, n.hi.xyz, n.lo.w, selection.vp, selection.screen.xy);
        if (error >= 0.0) {
            touch(n);
            if (n.links.y > 0u && (selection.screen.z == 0.0 || error > selection.screen.z)) {
                if (children_ready(n, error)) {
                    let visited = atomicAdd(&args[8], n.links.y);
                    if (visited <= selection.table.w && n.links.y <= selection.table.w - visited) {
                        current = n.links.x;
                        continue;
                    }
                    atomicAdd(&args[9], 1u);
                }
                else { atomicAdd(&args[11], 1u); }
            }
            emit(n);
        }
        // Stack-free depth-first walk. Parent links are validated at asset load.
        var done = false;
        loop {
            if (current == root) { done = true; break; }
            let parent = nodes[current].owner.x;
            let end = nodes[parent].links.x + nodes[parent].links.y;
            if (current + 1u < end) { current += 1u; break; }
            current = parent;
        }
        if (done) { break; }
    }
}
