#define_import_path pbr/stream_traversal

#import rendering/Geometry/projected_error
#import pbr/instance_types
#import pbr/stream_selection_types
#import pbr/stream_node_types
#import pbr/stream_work_types

@group(0) @binding(0) var<uniform> selection: StreamSelection;
@group(0) @binding(1) var<storage, read> nodes: array<Node>;
@group(0) @binding(2) var<storage, read> parts: array<vec4<u32>>;
@group(0) @binding(3) var<storage, read> residency: array<vec4<u32>>;
@group(0) @binding(4) var<storage, read_write> work: array<vec4<u32>>;
// 0..7: draw arguments; 8..11: traversal counters; 12: selected nodes;
// 13..14: frontier counts; 15: actual triangles; 16..18: dispatch.
@group(0) @binding(5) var<storage, read_write> args: array<atomic<u32>>;
@group(0) @binding(6) var<storage, read_write> feedback: array<atomic<u32>>;
@group(0) @binding(7) var<storage, read> instances: array<Instance>;

@compute @workgroup_size(64)
fn reset_feedback(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x < selection.table.z * 2u) { atomicStore(&feedback[id.x], 0u); }
}

@compute @workgroup_size(1)
fn reset_draws() {
    for (var i = 0u; i < 20u; i++) { atomicStore(&args[i], 0u); }
    atomicStore(&args[0], STREAM_WORK_BLOCK_TRIANGLES * 3u);
    atomicStore(&args[4], STREAM_WORK_BLOCK_TRIANGLES * 3u);
    atomicStore(&args[6], selection.output.x * STREAM_WORK_BLOCK_TRIANGLES * 3u);
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

fn emit(node: u32, n: Node, instance: u32, side: u32) {
    var count = 0u;
    var triangles = 0u;
    for (var p = 0u; p < n.links.w; p++) {
        let size = parts[n.links.z + p].z;
        triangles += size;
        count += (size + STREAM_WORK_BLOCK_TRIANGLES - 1u) / STREAM_WORK_BLOCK_TRIANGLES;
    }
    let first = atomicAdd(&args[side * 4u + 1u], count);
    let capacity = select(selection.output.x, selection.output.y, side != 0u);
    if (first > capacity || count > capacity - first) { atomicAdd(&args[10], 1u); return; }
    let base = select(0u, selection.output.x, side != 0u);
    let selected = atomicAdd(&args[12], 1u);
    let node_capacity = frontier_capacity();
    if (selected >= node_capacity) { atomicAdd(&args[10], 1u); return; }
    let record = selection.output.w + selected;
    work[record] = vec4<u32>(node, instance, base + first, 0u);
    atomicAdd(&args[15], triangles);
}

@compute @workgroup_size(1)
fn prepare_expansion() {
    let capacity = frontier_capacity();
    let count = min(atomicLoad(&args[12]), capacity);
    atomicStore(&args[16], min(count, 65535u));
    atomicStore(&args[17], (count + 65534u) / 65535u);
    atomicStore(&args[18], 1u);
}

fn frontier_capacity() -> u32 {
    return (arrayLength(&work) - selection.output.w) / 3u;
}

fn frontier_base(odd: bool) -> u32 {
    return selection.output.w + frontier_capacity() * select(1u, 2u, odd);
}

@compute @workgroup_size(64)
fn seed_roots(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x == 0u) { atomicStore(&args[13], selection.table.y); }
    if (id.x >= selection.table.y) { return; }
    if (id.x >= frontier_capacity()) { atomicAdd(&args[10], 1u); return; }
    let root = parts[selection.table.x + id.x];
    let at = frontier_base(false) + id.x;
    work[at] = root;
}

fn prepare_level(odd: bool) {
    let count = min(atomicLoad(&args[select(13u, 14u, odd)]), frontier_capacity());
    atomicStore(&args[select(14u, 13u, odd)], 0u);
    let groups = (count + 63u) / 64u;
    atomicStore(&args[16], min(groups, 65535u));
    atomicStore(&args[17], (groups + 65534u) / 65535u);
    atomicStore(&args[18], 1u);
}

@compute @workgroup_size(1)
fn prepare_even() { prepare_level(false); }
@compute @workgroup_size(1)
fn prepare_odd() { prepare_level(true); }

fn visit(index: u32, odd: bool) {
    let count = min(atomicLoad(&args[select(13u, 14u, odd)]), frontier_capacity());
    if (index >= count) { return; }
    let at = frontier_base(odd) + index;
    let entry = work[at];
    let side = entry.z;
    let current = entry.x;
    let instance = instances[entry.y];
    let n = nodes[current];
    let local_center = (n.lo.xyz + n.hi.xyz) * 0.5;
    let local_extent = (n.hi.xyz - n.lo.xyz) * 0.5;
    let center = (instance.transform * vec4<f32>(local_center, 1.0)).xyz;
    let extent = abs(instance.transform[0].xyz) * local_extent.x
        + abs(instance.transform[1].xyz) * local_extent.y
        + abs(instance.transform[2].xyz) * local_extent.z;
    let spatial_error = min(n.lo.w * bitcast<f32>(instance.material.y), 1.0e30);
    var error = -1.0;
    if (selection.forward_pixels.w > 0.0) {
        if (bounds_visible(center - extent, center + extent, selection.vp)) {
            error = sphere_pixel_error(center, n.hi.w * bitcast<f32>(instance.material.y), spatial_error,
                selection.eye_near, selection.forward_pixels);
        }
    } else {
        error = bounds_pixel_error(center - extent, center + extent, spatial_error, selection.vp, selection.screen.xy);
    }
    if (error >= 0.0) {
        touch(n);
        if (n.links.y > 0u && (selection.screen.z == 0.0 || error > selection.screen.z)) {
            if (children_ready(n, error)) {
                let visited = atomicAdd(&args[8], n.links.y);
                if (visited <= selection.table.w && n.links.y <= selection.table.w - visited) {
                    let first = atomicAdd(&args[select(14u, 13u, odd)], n.links.y);
                    let capacity = frontier_capacity();
                    if (first <= capacity && n.links.y <= capacity - first) {
                        let next = frontier_base(!odd) + first;
                        for (var c = 0u; c < n.links.y; c++) {
                            work[next + c] = vec4<u32>(n.links.x + c, entry.y, side, 0u);
                        }
                        return;
                    }
                    atomicAdd(&args[10], 1u);
                }
                atomicAdd(&args[9], 1u);
            }
            else { atomicAdd(&args[11], 1u); }
        }
        emit(current, n, entry.y, side);
    }
}

@compute @workgroup_size(64)
fn traverse_even(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    visit((group.y * 65535u + group.x) * 64u + lane, false);
}
@compute @workgroup_size(64)
fn traverse_odd(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    visit((group.y * 65535u + group.x) * 64u + lane, true);
}
