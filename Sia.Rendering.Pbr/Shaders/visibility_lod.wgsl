struct Camera {
    view_projection: mat4x4<f32>, eye: vec4<f32>, size_counts: vec4<u32>,
    light_direction: vec4<f32>, light_radiance: vec4<f32>,
}
struct Instance {
    transform: mat4x4<f32>, normal_transform: mat4x4<f32>,
    color: vec4<f32>, material: vec4<f32>, emissive: vec4<f32>,
}
struct Patch { minimum_error: vec4<f32>, maximum: vec4<f32>, children: vec4<u32>, geometry: vec4<u32> }
struct Parameters { counts: vec4<u32>, budget: vec4<u32> }
struct Status { draw: vec4<u32>, selection: vec4<u32>, post_draw: vec4<u32>, culling: vec4<u32>, traversal: vec4<u32> }
struct Hierarchy { previous_projection: mat4x4<f32>, size: vec4<u32>, levels: array<vec4<u32>, 32> }
@group(0) @binding(0) var<uniform> camera: Camera;
@group(0) @binding(1) var<uniform> parameters: Parameters;
@group(0) @binding(2) var<storage, read> patches: array<Patch>;
@group(0) @binding(3) var<storage, read> instances: array<Instance>;
@group(0) @binding(4) var<storage, read_write> states: array<vec4<u32>>;
@group(0) @binding(5) var<storage, read_write> heap: array<u32>;
@group(0) @binding(6) var<storage, read_write> status: Status;
@group(0) @binding(7) var<storage, read_write> work: array<vec4<u32>>;
@group(1) @binding(0) var<uniform> hierarchy: Hierarchy;
@group(1) @binding(1) var<storage, read> hzb: array<f32>;
var<private> heap_size: u32;
var<private> heap_peak: u32;

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
    let index = (group.y * parameters.counts.w + group.x) * 64u + lane;
    if (index >= parameters.counts.x * parameters.counts.z) { return; }
    let matrix = camera.view_projection * instances[index / parameters.counts.x].transform;
    states[index] = vec4<u32>(projected_error(patches[index % parameters.counts.x], matrix), 0u, 0u, 0u);
}

fn precedes(a: u32, b: u32) -> bool {
    return states[a].x > states[b].x || (states[a].x == states[b].x && a < b);
}

fn push(index: u32) {
    if (patches[index % parameters.counts.x].children.y == 0u || states[index].x <= parameters.budget.w) { return; }
    var position = heap_size;
    heap_size++;
    heap_peak = max(heap_peak, heap_size);
    while (position > 0u) {
        let parent = (position - 1u) / 2u;
        if (!precedes(index, heap[parent])) { break; }
        heap[position] = heap[parent];
        position = parent;
    }
    heap[position] = index;
}

fn pop() -> u32 {
    let result = heap[0];
    heap_size--;
    if (heap_size == 0u) { return result; }
    let last = heap[heap_size];
    var position = 0u;
    loop {
        var child = position * 2u + 1u;
        if (child >= heap_size) { break; }
        if (child + 1u < heap_size) {
            if (precedes(heap[child + 1u], heap[child])) { child++; }
        }
        if (!precedes(heap[child], last)) { break; }
        heap[position] = heap[child];
        position = child;
    }
    heap[position] = last;
    return result;
}

@compute @workgroup_size(1)
fn select_cut() {
    heap_size = 0u;
    heap_peak = 0u;
    var candidates = 0u;
    var refinements = 0u;
    var totals = vec3<u32>(0u);
    for (var instance = 0u; instance < parameters.counts.z; instance++) {
        for (var root = 0u; root < parameters.counts.y; root++) {
            let index = instance * parameters.counts.x + root;
            states[index].y = 1u;
            totals += vec3<u32>(1u, patches[root].geometry.x, patches[root].geometry.z);
            push(index);
        }
    }
    let unreachable = any(totals > parameters.budget.xyz);
    var limited = unreachable;
    while (!unreachable && heap_size > 0u) {
        let index = pop();
        candidates++;
        let node = patches[index % parameters.counts.x];
        let next = totals - vec3<u32>(1u, node.geometry.x, node.geometry.z)
            + vec3<u32>(node.children.y, node.children.z, node.children.w);
        if (any(next > parameters.budget.xyz)) { limited = true; continue; }
        totals = next;
        refinements++;
        states[index].y = 0u;
        let base = (index / parameters.counts.x) * parameters.counts.x;
        for (var child = node.children.x; child < node.children.x + node.children.y; child++) {
            states[base + child].y = 1u;
            push(base + child);
        }
    }
    var offset = 0u;
    var maximum_error = 0u;
    for (var index = 0u; index < parameters.counts.x * parameters.counts.z; index++) {
        if (states[index].y == 0u) { continue; }
        states[index].z = offset;
        offset += patches[index % parameters.counts.x].geometry.z;
        maximum_error = max(maximum_error, states[index].x);
    }
    status.draw = vec4<u32>(offset * 3u, 1u, 0u, 0u);
    status.selection = vec4<u32>(totals.xy, u32(limited) | (u32(unreachable) << 1u), maximum_error);
    status.traversal = vec4<u32>(parameters.counts.x * parameters.counts.z, candidates, refinements, heap_peak);
}

@compute @workgroup_size(64)
fn emit_work(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = group.y * parameters.counts.w + group.x;
    if (index >= parameters.counts.x * parameters.counts.z) { return; }
    if (states[index].y == 0u || states[index].w != 0u) { return; }
    let node = patches[index % parameters.counts.x];
    for (var triangle = lane; triangle < node.geometry.z; triangle += 64u) {
        work[states[index].z + triangle] = vec4<u32>(node.geometry.y + triangle, index / parameters.counts.x, 0u, 0u);
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
        if (magnitude == 0.0) { return false; }
        clip /= magnitude;
        outside_xy &= vec4<f32>(clip.x + clip.w, clip.w - clip.x, clip.y + clip.w, clip.w - clip.y) < vec4<f32>(-1e-5);
        outside_z &= vec2<f32>(clip.z, clip.w - clip.z) < vec2<f32>(-1e-5);
    }
    return any(outside_xy) || any(outside_z);
}

fn occluded(node: Patch, matrix: mat4x4<f32>) -> bool {
    var low = vec3<f32>(3.402823466e+38);
    var high = -low;
    for (var corner = 0u; corner < 8u; corner++) {
        let point = select(node.minimum_error.xyz, node.maximum.xyz,
            (vec3<u32>(corner) & vec3<u32>(1u, 2u, 4u)) != vec3<u32>(0u));
        let clip = matrix * vec4<f32>(point, 1.0);
        if (!finite(clip) || clip.w <= 0.0 || clip.z <= 0.0) { return false; }
        let ndc = clip.xyz / clip.w;
        if (!finite(vec4<f32>(ndc, 1.0))) { return false; }
        low = min(low, ndc);
        high = max(high, ndc);
    }
    let size = vec2<f32>(hierarchy.size.xy);
    let minimum = floor((vec2<f32>(low.x, -high.y) * 0.5 + 0.5) * size) - 2.0;
    let maximum = ceil((vec2<f32>(high.x, -low.y) * 0.5 + 0.5) * size) + 2.0;
    if (any(minimum < vec2<f32>(0.0)) || any(maximum >= size)) { return false; }
    var level = 0u;
    var factor = hierarchy.size.z;
    var first = vec2<u32>(minimum) / factor;
    var last = vec2<u32>(maximum) / factor;
    while (any(last - first > vec2<u32>(1u)) && level < 31u) {
        if (all(hierarchy.levels[level].xy == vec2<u32>(1u))) { break; }
        level++;
        factor *= 2u;
        first = vec2<u32>(minimum) / factor;
        last = vec2<u32>(maximum) / factor;
    }
    let mip = hierarchy.levels[level];
    var farthest = 0.0;
    for (var y = first.y; y <= last.y; y++) {
        for (var x = first.x; x <= last.x; x++) {
            farthest = max(farthest, hzb[mip.z + y * mip.x + x]);
        }
    }
    return low.z > farthest + 1e-5;
}

@compute @workgroup_size(64)
fn cull_main(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = (group.y * parameters.counts.w + group.x) * 64u + lane;
    if (index >= parameters.counts.x * parameters.counts.z) { return; }
    if (states[index].y == 0u) { return; }
    let node = patches[index % parameters.counts.x];
    let transform = instances[index / parameters.counts.x].transform;
    if (outside_frustum(node, camera.view_projection * transform)) { states[index].w = 1u; return; }
    if (hierarchy.size.w != 0u) {
        if (occluded(node, hierarchy.previous_projection * transform)) { states[index].w = 2u; }
    }
}

@compute @workgroup_size(1)
fn compact_main() {
    status.culling = vec4<u32>(status.draw.x / 3u, 0u, 0u, 0u);
    var offset = 0u;
    for (var index = 0u; index < parameters.counts.x * parameters.counts.z; index++) {
        if (states[index].y == 0u) { continue; }
        if (states[index].w == 1u) { status.culling.y++; continue; }
        if (states[index].w == 2u) { status.culling.z++; continue; }
        states[index].z = offset;
        offset += patches[index % parameters.counts.x].geometry.z;
    }
    status.draw = vec4<u32>(offset * 3u, 1u, 0u, 0u);
    status.post_draw = vec4<u32>(0u, 1u, offset * 3u, 0u);
}

@compute @workgroup_size(64)
fn cull_post(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = (group.y * parameters.counts.w + group.x) * 64u + lane;
    if (index >= parameters.counts.x * parameters.counts.z) { return; }
    if (states[index].y == 0u || states[index].w != 2u) { return; }
    let matrix = camera.view_projection * instances[index / parameters.counts.x].transform;
    states[index].w = select(4u, 3u, occluded(patches[index % parameters.counts.x], matrix));
}

@compute @workgroup_size(1)
fn compact_post() {
    let start = status.draw.x / 3u;
    var offset = start;
    for (var index = 0u; index < parameters.counts.x * parameters.counts.z; index++) {
        if (states[index].y == 0u || states[index].w != 4u) { continue; }
        states[index].z = offset;
        offset += patches[index % parameters.counts.x].geometry.z;
        status.culling.w++;
    }
    status.post_draw = vec4<u32>((offset - start) * 3u, 1u, start * 3u, 0u);
}

@compute @workgroup_size(64)
fn emit_post(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = group.y * parameters.counts.w + group.x;
    if (index >= parameters.counts.x * parameters.counts.z) { return; }
    if (states[index].y == 0u || states[index].w != 4u) { return; }
    let node = patches[index % parameters.counts.x];
    for (var triangle = lane; triangle < node.geometry.z; triangle += 64u) {
        work[states[index].z + triangle] = vec4<u32>(node.geometry.y + triangle, index / parameters.counts.x, 0u, 0u);
    }
}
