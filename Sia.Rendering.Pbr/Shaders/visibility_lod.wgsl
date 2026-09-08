#import pbr::occlusion

struct Camera {
    view_projection: mat4x4<f32>, eye: vec4<f32>, size_counts: vec4<u32>,
    light_direction: vec4<f32>, light_radiance: vec4<f32>,
}
struct Instance {
    transform: mat4x4<f32>, normal_transform: mat4x4<f32>,
    color: vec4<f32>, material: vec4<f32>, emissive: vec4<f32>, roots: vec4<u32>,
}
struct Patch { minimum_error: vec4<f32>, maximum: vec4<f32>, children: vec4<u32>, geometry: vec4<u32> }
struct PatchState { error: u32, node_id: u32, offset: u32, visibility: u32, instance: u32 }
struct Parameters { counts: vec4<u32>, budget: vec4<u32>, traversal: vec4<u32> }
struct Status { draw: vec4<u32>, selection: vec4<u32>, post_draw: vec4<u32>, culling: vec4<u32>, traversal: vec4<u32> }
@group(0) @binding(0) var<uniform> camera: Camera;
@group(0) @binding(1) var<uniform> parameters: Parameters;
@group(0) @binding(2) var<storage, read> patches: array<Patch>;
@group(0) @binding(3) var<storage, read> instances: array<Instance>;
@group(0) @binding(4) var<storage, read_write> states: array<PatchState>;
@group(0) @binding(5) var<storage, read_write> heap: array<u32>;
@group(0) @binding(6) var<storage, read_write> status: Status;
@group(0) @binding(7) var<storage, read_write> work: array<vec2<u32>>;
@group(1) @binding(2) var<storage, read_write> dispatch: array<u32>;
var<private> heap_size: u32;
var<private> heap_peak: u32;
var<workgroup> frontier: vec4<u32>;
var<workgroup> heap_cache: array<vec4<u32>, 256>;

fn finite(value: vec4<f32>) -> bool {
    return all((bitcast<vec4<u32>>(value) & vec4<u32>(0x7f800000u)) != vec4<u32>(0x7f800000u));
}

fn row_length(value: vec3<f32>) -> f32 {
    let scale = max(max(abs(value.x), abs(value.y)), abs(value.z));
    if (scale == 0.0) { return 0.0; }
    return scale * length(value / scale);
}

fn projected_error(node: Patch, source_matrix: mat4x4<f32>) -> u32 {
    let error = node.minimum_error.w;
    if (error == 0.0) { return 0u; }
    var scale = 0.0;
    for (var column = 0u; column < 4u; column++) {
        if (!finite(source_matrix[column])) { return 0x7f800000u; }
        if (column < 3u) {
            let values = abs(source_matrix[column]);
            scale = max(scale, max(max(values.x, values.y), max(values.z, values.w)));
        }
    }
    if (scale == 0.0) { scale = 1.0; }
    let matrix = mat4x4<f32>(source_matrix[0] / scale, source_matrix[1] / scale,
        source_matrix[2] / scale, source_matrix[3] / scale);
    var min_w = 3.402823466e+38;
    var min_z = min_w;
    var max_xy = vec2<f32>(0.0);
    for (var corner = 0u; corner < 8u; corner++) {
        let point = select(node.minimum_error.xyz, node.maximum.xyz,
            (vec3<u32>(corner) & vec3<u32>(1u, 2u, 4u)) != vec3<u32>(0u));
        let clip = matrix * vec4<f32>(point, 1.0);
        if (!finite(clip) || clip.w <= 0.0) { return 0x7f800000u; }
        min_w = min(min_w, clip.w);
        min_z = min(min_z, clip.z);
        max_xy = max(max_xy, abs(clip.xy / clip.w));
    }
    let rows = vec4<f32>(row_length(vec3<f32>(matrix[0].x, matrix[1].x, matrix[2].x)),
        row_length(vec3<f32>(matrix[0].y, matrix[1].y, matrix[2].y)),
        row_length(vec3<f32>(matrix[0].z, matrix[1].z, matrix[2].z)),
        row_length(vec3<f32>(matrix[0].w, matrix[1].w, matrix[2].w)));
    let displacement = error * rows;
    if (!finite(displacement) || min_w <= displacement.w || min_z <= displacement.z) { return 0x7f800000u; }
    let pixels = vec2<f32>(camera.size_counts.xy) * (displacement.xy + max_xy * displacement.w);
    let result = 0.5 * max(pixels.x, pixels.y) / (min_w - displacement.w);
    if (!finite(vec4<f32>(result))) { return 0x7f800000u; }
    return bitcast<u32>(max(0.0, result));
}

@compute @workgroup_size(64)
fn project(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let instance = group.y * parameters.counts.w + group.x;
    if (instance >= parameters.counts.z) { return; }
    let roots = instances[instance].roots;
    for (var root = lane; root < roots.y; root += 64u) {
        states[roots.z + root] = project_node(roots.x + root, instance);
    }
}

fn project_node(node_id: u32, instance: u32) -> PatchState {
    let node = patches[node_id];
    let matrix = camera.view_projection * instances[instance].transform;
    if (outside_frustum(node, matrix)) { return PatchState(0u, node_id + 1u, 0u, 1u, instance); }
    return PatchState(projected_error(node, matrix), node_id + 1u, 0u, 0u, instance);
}

fn heap_item(index: u32) -> vec4<u32> {
    let state = states[index];
    return vec4<u32>(index, state.error, state.instance, state.node_id);
}

fn read_heap(position: u32) -> vec4<u32> {
    if (position < 256u) { return heap_cache[position]; }
    return heap_item(heap[position]);
}

fn write_heap(position: u32, item: vec4<u32>) {
    if (position < 256u) { heap_cache[position] = item; }
    else { heap[position] = item.x; }
}

fn precedes(a: vec4<u32>, b: vec4<u32>) -> bool {
    let earlier = a.z < b.z || (a.z == b.z && a.w < b.w);
    return a.y > b.y || (a.y == b.y && earlier);
}

fn push(index: u32) {
    let item = heap_item(index);
    if (item.y <= parameters.budget.w || patches[item.w - 1u].children.y == 0u) { return; }
    var position = heap_size;
    heap_size++;
    heap_peak = max(heap_peak, heap_size);
    while (position > 0u) {
        let parent = (position - 1u) / 2u;
        let parent_item = read_heap(parent);
        if (!precedes(item, parent_item)) { break; }
        write_heap(position, parent_item);
        position = parent;
    }
    write_heap(position, item);
}

fn pop() -> u32 {
    let result = read_heap(0u).x;
    heap_size--;
    if (heap_size == 0u) { return result; }
    let last = read_heap(heap_size);
    var position = 0u;
    loop {
        var child = position * 2u + 1u;
        if (child >= heap_size) { break; }
        var child_item = read_heap(child);
        if (child + 1u < heap_size) {
            let right = read_heap(child + 1u);
            if (precedes(right, child_item)) { child++; child_item = right; }
        }
        if (!precedes(child_item, last)) { break; }
        write_heap(position, child_item);
        position = child;
    }
    write_heap(position, last);
    return result;
}

@compute @workgroup_size(64)
fn select_cut(@builtin(local_invocation_index) lane: u32) {
    var candidates = 0u;
    var refinements = 0u;
    var refined_nodes = 0u;
    var totals = vec3<u32>(0u);
    var unreachable = false;
    var limited = false;
    if (lane == 0u) {
        heap_size = 0u;
        heap_peak = 0u;
        for (var instance = 0u; instance < parameters.counts.z; instance++) {
            let roots = instances[instance].roots;
            for (var root = 0u; root < roots.y; root++) {
                let index = roots.z + root;
                let node = patches[roots.x + root];
                totals += vec3<u32>(1u, node.geometry.x, node.geometry.z);
                push(index);
            }
        }
        unreachable = any(totals > parameters.budget.xyz);
        limited = unreachable;
    }
    loop {
        if (lane == 0u) {
            frontier = vec4<u32>(0u);
            while (!unreachable && heap_size > 0u) {
                if (candidates == parameters.traversal.x) { limited = true; break; }
                let index = pop();
                candidates++;
                let source = states[index].node_id - 1u;
                let node = patches[source];
                let next = totals - vec3<u32>(1u, node.geometry.x, node.geometry.z)
                    + vec3<u32>(node.children.y, node.children.z, node.children.w);
                if (any(next > parameters.budget.xyz) || node.children.y > parameters.traversal.y - refined_nodes) {
                    limited = true;
                    continue;
                }
                totals = next;
                refinements++;
                frontier = vec4<u32>(index, node.children.x,
                    node.children.y, parameters.counts.y + refined_nodes);
                refined_nodes += node.children.y;
                break;
            }
        }
        let next = workgroupUniformLoad(&frontier);
        if (next.z == 0u) { break; }
        for (var child = lane; child < next.z; child += 64u) {
            states[next.w + child] = project_node(next.y + child, states[next.x].instance);
        }
        storageBarrier();
        if (lane == 0u) {
            states[next.x].node_id = 0u;
            for (var child = 0u; child < next.z; child++) { push(next.w + child); }
        }
    }
    if (lane == 0u) {
        status.draw = vec4<u32>(totals.z * 3u, 1u, 0u, 0u);
        status.post_draw = vec4<u32>(0u, 1u, 0u, 0u);
        status.selection = vec4<u32>(totals.xy, u32(limited) | (u32(unreachable) << 1u), 0u);
        status.culling = vec4<u32>(totals.z, 0u, 0u, 0u);
        status.traversal = vec4<u32>(parameters.counts.y + refined_nodes, candidates, refinements, heap_peak);
        dispatch_size(0u, (status.traversal.x + 63u) / 64u);
        dispatch_size(3u, status.traversal.x);
        var count = status.traversal.x;
        for (var level = 0u; level < 4u; level++) {
            count = (count + 255u) / 256u;
            dispatch_size(6u + level * 3u, count);
        }
    }
}

fn dispatch_size(offset: u32, size: u32) {
    let count = max(1u, size);
    dispatch[offset] = min(count, parameters.counts.w);
    dispatch[offset + 1u] = (count + parameters.counts.w - 1u) / parameters.counts.w;
    dispatch[offset + 2u] = 1u;
}

@compute @workgroup_size(64)
fn emit_work(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = group.y * parameters.counts.w + group.x;
    if (index >= status.traversal.x) { return; }
    if (states[index].node_id == 0u || states[index].visibility != 0u) { return; }
    let source = states[index].node_id - 1u;
    let node = patches[source];
    for (var triangle = lane; triangle < node.geometry.z; triangle += 64u) {
        work[states[index].offset + triangle] = vec2<u32>(node.geometry.y + triangle, states[index].instance);
    }
}

fn outside_frustum(node: Patch, matrix: mat4x4<f32>) -> bool {
    var outside_xy = vec4<bool>(true);
    var outside_z = vec2<bool>(true);
    for (var corner = 0u; corner < 8u; corner++) {
        let point = select(node.minimum_error.xyz, node.maximum.xyz,
            (vec3<u32>(corner) & vec3<u32>(1u, 2u, 4u)) != vec3<u32>(0u));
        var clip = matrix * vec4<f32>(point, 1.0);
        if (!finite(clip)) { return false; }
        let magnitude = max(max(abs(clip.x), abs(clip.y)), max(abs(clip.z), abs(clip.w)));
        if (magnitude < 1.17549435e-38 || magnitude > 8.50705917e37) { return false; }
        clip /= magnitude;
        outside_xy &= vec4<f32>(clip.x + clip.w, clip.w - clip.x, clip.y + clip.w, clip.w - clip.y) < vec4<f32>(-1e-5);
        outside_z &= vec2<f32>(clip.z, clip.w - clip.z) < vec2<f32>(-1e-5);
    }
    return any(outside_xy) || any(outside_z);
}

@compute @workgroup_size(64)
fn cull_main(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = (group.y * parameters.counts.w + group.x) * 64u + lane;
    if (index >= status.traversal.x) { return; }
    if (states[index].node_id == 0u || states[index].visibility == 1u) { return; }
    let source = states[index].node_id - 1u;
    let node = patches[source];
    let transform = instances[states[index].instance].transform;
    if (hierarchy.size.w != 0u) {
        if (occluded(node.minimum_error.xyz, node.maximum.xyz, hierarchy.previous_projection * transform)) { states[index].visibility = 2u; }
    }
}

@compute @workgroup_size(64)
fn cull_post(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = (group.y * parameters.counts.w + group.x) * 64u + lane;
    if (index >= status.traversal.x) { return; }
    if (states[index].node_id == 0u || states[index].visibility != 2u) { return; }
    let source = states[index].node_id - 1u;
    let matrix = camera.view_projection * instances[states[index].instance].transform;
    states[index].visibility = select(4u, 3u, occluded(patches[source].minimum_error.xyz, patches[source].maximum.xyz, matrix));
}

@compute @workgroup_size(64)
fn emit_post(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = group.y * parameters.counts.w + group.x;
    if (index >= status.traversal.x) { return; }
    if (states[index].node_id == 0u || states[index].visibility != 4u) { return; }
    let source = states[index].node_id - 1u;
    let node = patches[source];
    for (var triangle = lane; triangle < node.geometry.z; triangle += 64u) {
        work[states[index].offset + triangle] = vec2<u32>(node.geometry.y + triangle, states[index].instance);
    }
}
