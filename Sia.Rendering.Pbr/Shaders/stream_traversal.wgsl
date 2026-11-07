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
@group(0) @binding(5) var<storage, read_write> args: array<atomic<u32>>;
@group(0) @binding(6) var<storage, read_write> feedback: array<atomic<u32>>;
@group(0) @binding(7) var<storage, read> instances: array<Instance>;
@group(0) @binding(8) var<storage, read_write> shadow_cuts: array<atomic<u32>>;

@compute @workgroup_size(64)
fn reset_feedback(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x < selection.table.z + arrayLength(&nodes)) { atomicStore(&feedback[id.x], 0u); }
}

@compute @workgroup_size(64)
fn reset_draws(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = (group.y * 65535u + group.x) * 64u + lane;
    if (index == 0u) {
        for (var i = 0u; i < 20u; i++) { atomicStore(&args[i], 0u); }
        atomicStore(&args[0], STREAM_WORK_BLOCK_TRIANGLES * 3u);
        atomicStore(&args[4], STREAM_WORK_BLOCK_TRIANGLES * 3u);
        atomicStore(&args[6], selection.output.x * STREAM_WORK_BLOCK_TRIANGLES * 3u);
        if (selection.screen.w >= 2.0) { atomicStore(&args[19], 1u); }
    }
    if (selection.screen.w > 0.0) {
        let words = arrayLength(&shadow_cuts) / 8u;
        if (index < words) { atomicStore(&shadow_cuts[7u * words + index], 0u); }
    }
}

fn touch(n: Node) {
    for (var i = 0u; i < n.links.w; i++) {
        let part = parts[n.links.z + i];
        if (residency[part.x].z == 0u) { atomicOr(&feedback[part.x], 1u); }
    }
}

fn node_visible(n: Node, instance: Instance) -> bool {
    if ((instance.material.x & 0x80000000u) != 0u) { return false; }
    let local_center = (n.lo.xyz + n.hi.xyz) * 0.5;
    let local_extent = (n.hi.xyz - n.lo.xyz) * 0.5;
    let center = (instance.transform * vec4<f32>(local_center, 1.0)).xyz;
    let extent = abs(instance.transform[0].xyz) * local_extent.x
        + abs(instance.transform[1].xyz) * local_extent.y + abs(instance.transform[2].xyz) * local_extent.z;
    return bounds_visible(center - extent, center + extent, selection.vp);
}

fn children_ready(n: Node, instance: Instance, priority: f32) -> bool {
    var ready = true;
    var importance = priority;
    if (selection.screen.z > 0.0) { importance /= selection.screen.z; }
    if (!(importance > 0.0)) { importance = 1.0e-20; }
    importance = clamp(importance, 1.0e-20, 1.0e30);
    for (var c = 0u; c < n.links.y; c++) {
        let child_id = n.links.x + c;
        let child = nodes[child_id];
        if (!node_visible(child, instance)) { continue; }
        atomicMax(&feedback[selection.table.z + child_id], bitcast<u32>(importance));
        for (var p = 0u; p < child.links.w; p++) {
            let part = parts[child.links.z + p];
            let page = residency[part.x];
            if (page.w == 0u) {
                ready = false;
            }
            else if (page.z == 0u) { atomicOr(&feedback[part.x], 1u); }
        }
    }
    return ready;
}

fn emit(node: u32, n: Node, instance: u32, side: u32, shadow_base: u32) {
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
    // Source-instance domains were appended in source row order. Within each
    // domain the ordinal is the immutable source-local node index.
    let ordinal = shadow_base + (node - n.owner.y);
    work[record] = vec4<u32>(node, instance, base + first, ordinal);
    atomicAdd(&args[15], triangles);
    if (selection.screen.w > 0.0) {
        // Each actual instance owns a disjoint source-tree domain. No hash or
        // traversal ordering participates in depth-cache identity.
        let words = arrayLength(&shadow_cuts) / 8u;
        if (ordinal / 32u >= words) { atomicAdd(&args[10], 1u); return; }
        atomicOr(&shadow_cuts[7u * words + ordinal / 32u], 1u << (ordinal % 32u));
    }
}

@compute @workgroup_size(64)
fn compare_shadow_cut(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = (group.y * 65535u + group.x) * 64u + lane;
    let words = arrayLength(&shadow_cuts) / 8u;
    if (index >= words) { return; }
    let current = atomicLoad(&shadow_cuts[7u * words + index]);
    let previous_at = selection.output.z * words + index;
    if (current != atomicLoad(&shadow_cuts[previous_at])) { atomicOr(&args[19], 1u); }
    atomicStore(&shadow_cuts[previous_at], current);
}

@compute @workgroup_size(1)
fn prepare_expansion() {
    let capacity = frontier_capacity();
    var count = min(atomicLoad(&args[12]), capacity);
    if (selection.screen.w > 0.0) {
        let changed = atomicLoad(&args[19]) != 0u || atomicLoad(&args[10]) != 0u;
        atomicStore(&args[20], 3u);
        atomicStore(&args[21], select(0u, 1u, changed));
        atomicStore(&args[22], 0u);
        atomicStore(&args[23], 0u);
        if (!changed) {
            atomicStore(&args[1], 0u);
            atomicStore(&args[5], 0u);
            count = 0u;
        }
    }
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

// Traversal frontiers are dead once the selected cut is complete. Reuse one
// for immutable counts and canonical keys before assigning dense work bases.
// No physical page address participates in the equal-depth draw-order policy.
@compute @workgroup_size(64)
fn prepare_stable_work(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    if (lane != 0u) { return; }
    let selected = group.y * 65535u + group.x;
    if (selected >= min(atomicLoad(&args[12]), frontier_capacity())) { return; }
    let entry = work[selection.output.w + selected];
    let n = nodes[entry.x];
    var records = 0u;
    for (var p = 0u; p < n.links.w; p++) {
        records += (parts[n.links.z + p].z + STREAM_WORK_BLOCK_TRIANGLES - 1u) / STREAM_WORK_BLOCK_TRIANGLES;
    }
    let side = select(0u, 1u, entry.z >= selection.output.x);
    work[frontier_base(false) + selected] = vec4<u32>(records, side, entry.y, entry.x);
}

var<workgroup> stable_work_sums: array<vec2<u32>, 64>;

@compute @workgroup_size(64)
fn prefix_stable_work(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let selected = group.y * 65535u + group.x;
    let count = min(atomicLoad(&args[12]), frontier_capacity());
    let valid = selected < count;
    var own = vec4<u32>(0u);
    if (valid) { own = work[frontier_base(false) + selected]; }
    var sum = 0u; var invalid = 0u;
    for (var other = lane; other < count; other += 64u) {
        let entry = work[frontier_base(false) + other];
        if (valid && entry.y == own.y) {
            if (entry.z < own.z || (entry.z == own.z && entry.w < own.w)) { sum += entry.x; }
            if (other != selected && entry.z == own.z && entry.w == own.w) { invalid = 1u; }
        }
    }
    stable_work_sums[lane] = vec2<u32>(sum, invalid);
    workgroupBarrier();
    for (var step = 32u; step != 0u; step /= 2u) {
        if (lane < step) {
            let right = stable_work_sums[lane + step];
            stable_work_sums[lane] = vec2<u32>(stable_work_sums[lane].x + right.x, stable_work_sums[lane].y | right.y);
        }
        workgroupBarrier();
    }
    if (lane == 0u && valid) {
        let first = stable_work_sums[0].x;
        let capacity = select(selection.output.x, selection.output.y, own.y != 0u);
        if (stable_work_sums[0].y != 0u || first > capacity || own.x > capacity - first) {
            atomicAdd(&args[10], 1u); return;
        }
        let at = selection.output.w + selected;
        let entry = work[at];
        work[at] = vec4<u32>(entry.xy, select(0u, selection.output.x, own.y != 0u) + first, entry.w);
    }
}

fn stable_scan_domain() -> u32 {
    return (arrayLength(&shadow_cuts) / 8u) * 32u;
}

// The main-view CPU eligibility check bounds D + ceil(D/64) by both dead
// frontiers. Nothing in persistent shadow-cut planes0..6 is modified here.
@compute @workgroup_size(64)
fn clear_stable_scan(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let domain = stable_scan_domain();
    let records = domain + (domain + 63u) / 64u;
    let block = group.y * 65535u + group.x;
    if (block >= (records + 63u) / 64u) { return; }
    let index = block * 64u + lane;
    if (index < records) {
        work[frontier_base(false) + index] = vec4<u32>(0u);
    }
    let words = arrayLength(&shadow_cuts) / 8u;
    if (index < words) { atomicStore(&shadow_cuts[7u * words + index], 0u); }
}

@compute @workgroup_size(64)
fn count_stable_scan(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    if (lane != 0u) { return; }
    let selected = group.y * 65535u + group.x;
    if (selected >= min(atomicLoad(&args[12]), frontier_capacity())) { return; }
    let entry = work[selection.output.w + selected];
    if (entry.w >= stable_scan_domain()) { atomicAdd(&args[10], 1u); return; }
    let words = arrayLength(&shadow_cuts) / 8u;
    let bit = 1u << (entry.w % 32u);
    let previous = atomicOr(&shadow_cuts[7u * words + entry.w / 32u], bit);
    if ((previous & bit) != 0u) { atomicAdd(&args[10], 1u); return; }
    let n = nodes[entry.x];
    var records = 0u;
    for (var p = 0u; p < n.links.w; p++) {
        records += (parts[n.links.z + p].z + STREAM_WORK_BLOCK_TRIANGLES - 1u) / STREAM_WORK_BLOCK_TRIANGLES;
    }
    let side = entry.z >= selection.output.x;
    work[frontier_base(false) + entry.w] = vec4<u32>(select(records, 0u, side), select(0u, records, side), 0u, 0u);
}

@compute @workgroup_size(64)
fn scan_stable_blocks(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let block = group.y * 65535u + group.x;
    let domain = stable_scan_domain();
    // Extra workgroups in a rectangular2D dispatch still cross all barriers,
    // but do not multiply their padded block index into a u32 address.
    var ordinal = domain;
    if (block < (domain + 63u) / 64u) { ordinal = block * 64u + lane; }
    var own = vec2<u32>(0u);
    if (ordinal < domain) { own = work[frontier_base(false) + ordinal].xy; }
    stable_work_sums[lane] = own;
    workgroupBarrier();
    for (var step = 1u; step < 64u; step *= 2u) {
        var previous = vec2<u32>(0u);
        if (lane >= step) { previous = stable_work_sums[lane - step]; }
        // Snapshot every predecessor before any lane updates this scan step.
        workgroupBarrier();
        stable_work_sums[lane] += previous;
        workgroupBarrier();
    }
    if (ordinal < domain) {
        work[frontier_base(false) + ordinal] = vec4<u32>(stable_work_sums[lane] - own, own);
    }
    if (lane == 63u && block < (domain + 63u) / 64u) {
        work[frontier_base(false) + domain + block] = vec4<u32>(stable_work_sums[lane], 0u, 0u);
    }
}

@compute @workgroup_size(64)
fn prefix_stable_blocks(@builtin(local_invocation_index) lane: u32) {
    let domain = stable_scan_domain();
    let blocks = (domain + 63u) / 64u;
    let chunk = (blocks + 63u) / 64u;
    let first = min(lane * chunk, blocks);
    let end = min(first + chunk, blocks);
    var sum = vec2<u32>(0u);
    // Lanes own disjoint contiguous chunks. The only global writes here are
    // local exclusive prefixes; the block totals.xy remain immutable.
    for (var block = first; block < end; block++) {
        let at = frontier_base(false) + domain + block;
        let total = work[at].xy;
        work[at] = vec4<u32>(total, sum);
        sum += total;
    }
    stable_work_sums[lane] = sum;
    workgroupBarrier();
    for (var step = 1u; step < 64u; step *= 2u) {
        var previous = vec2<u32>(0u);
        if (lane >= step) { previous = stable_work_sums[lane - step]; }
        workgroupBarrier();
        stable_work_sums[lane] += previous;
        workgroupBarrier();
    }
    let base = stable_work_sums[lane] - sum;
    for (var block = first; block < end; block++) {
        let at = frontier_base(false) + domain + block;
        let entry = work[at];
        work[at] = vec4<u32>(entry.xy, entry.zw + base);
    }
}

@compute @workgroup_size(1)
fn assign_stable_scan(@builtin(workgroup_id) group: vec3<u32>) {
    let selected = group.y * 65535u + group.x;
    if (selected >= min(atomicLoad(&args[12]), frontier_capacity()) || atomicLoad(&args[10]) != 0u) { return; }
    let entry = work[selection.output.w + selected];
    if (entry.w >= stable_scan_domain()) { return; }
    let local = work[frontier_base(false) + entry.w];
    let block = work[frontier_base(false) + stable_scan_domain() + entry.w / 64u].zw;
    let side = entry.z >= selection.output.x;
    let first = select(block.x + local.x, block.y + local.y, side);
    let records = select(local.z, local.w, side);
    let capacity = select(selection.output.x, selection.output.y, side);
    if (first > capacity || records > capacity - first) { atomicAdd(&args[10], 1u); return; }
    work[selection.output.w + selected] = vec4<u32>(entry.xy, select(0u, selection.output.x, side) + first, entry.w);
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
    if ((instance.material.x & 0x80000000u) != 0u) { return; }
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
            if (children_ready(n, instance, error)) {
                let visited = atomicAdd(&args[8], n.links.y);
                if (visited <= selection.table.w && n.links.y <= selection.table.w - visited) {
                    let first = atomicAdd(&args[select(14u, 13u, odd)], n.links.y);
                    let capacity = frontier_capacity();
                    if (first <= capacity && n.links.y <= capacity - first) {
                        let next = frontier_base(!odd) + first;
                        for (var c = 0u; c < n.links.y; c++) {
                            work[next + c] = vec4<u32>(n.links.x + c, entry.y, side, entry.w);
                        }
                        return;
                    }
                    atomicAdd(&args[10], 1u);
                }
                atomicAdd(&args[9], 1u);
            }
            else { atomicAdd(&args[11], 1u); }
        }
        emit(current, n, entry.y, side, entry.w);
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

@vertex
fn clear_shadow_vertex(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let corner = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    return vec4<f32>(corner * 2.0 - 1.0, 1.0, 1.0);
}

@fragment
fn clear_shadow_depth() -> @builtin(frag_depth) f32 { return 1.0; }
