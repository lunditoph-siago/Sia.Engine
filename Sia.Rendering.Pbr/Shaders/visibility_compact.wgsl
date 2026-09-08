struct Patch { minimum_error: vec4<f32>, maximum: vec4<f32>, children: vec4<u32>, geometry: vec4<u32> }
struct PatchState { error: u32, node_id: u32, offset: u32, visibility: u32, instance: u32 }
struct Parameters { counts: vec4<u32>, range: vec4<u32> }
@group(0) @binding(0) var<uniform> parameters: Parameters;
@group(0) @binding(1) var<storage, read> patches: array<Patch>;
@group(0) @binding(2) var<storage, read_write> states: array<PatchState>;
@group(0) @binding(3) var<storage, read_write> scratch: array<u32>;
@group(0) @binding(4) var<storage, read_write> status: array<atomic<u32>>;
var<workgroup> partial: array<vec4<u32>, 256>;

fn prefix(value: vec4<u32>, lane: u32) -> vec4<u32> {
    partial[lane] = value;
    workgroupBarrier();
    for (var stride = 1u; stride < 256u; stride *= 2u) {
        var left = vec4<u32>(0u);
        if (lane >= stride) { left = partial[lane - stride]; }
        workgroupBarrier();
        partial[lane] = vec4<u32>(partial[lane].xyz + left.xyz, max(partial[lane].w, left.w));
        workgroupBarrier();
    }
    return partial[lane];
}

@compute @workgroup_size(256)
fn local_prefix(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let block = group.y * parameters.counts.z + group.x;
    if (block >= max(1u, (parameters.counts.x + 255u) / 256u)) { return; }
    let index = block * 256u + lane;
    var value = vec4<u32>(0u);
    if (index < atomicLoad(&status[16])) {
        let state = states[index];
        if (state.node_id != 0u) {
            let triangles = patches[state.node_id - 1u].geometry.z;
            if (parameters.counts.w == 0u) {
                value = vec4<u32>(select(0u, triangles, state.visibility == 0u), u32(state.visibility == 1u), u32(state.visibility == 2u), state.error);
            } else {
                value = vec4<u32>(select(0u, triangles, state.visibility == 4u), u32(state.visibility == 4u), 0u, 0u);
            }
        }
    }
    let total = prefix(value, lane);
    if (value.x != 0u) { states[index].offset = total.x - value.x; }
    if (lane == 255u) {
        scratch[block] = total.x;
        if (parameters.counts.w == 0u) {
            atomicAdd(&status[13], total.y);
            atomicAdd(&status[14], total.z);
            atomicMax(&status[7], total.w);
        } else { atomicAdd(&status[15], total.y); }
    }
}

@compute @workgroup_size(256)
fn scan(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let block = group.y * parameters.counts.z + group.x;
    if (block >= (parameters.range.y + 255u) / 256u) { return; }
    let index = block * 256u + lane;
    var value = 0u;
    let count = level_count();
    if (index < count) { value = scratch[parameters.range.x + index]; }
    let total = prefix(vec4<u32>(value, 0u, 0u, 0u), lane).x;
    if (index < count) { scratch[parameters.range.x + index] = total - value; }
    if (lane == 255u) { scratch[parameters.range.z + block] = total; }
}

fn level_count() -> u32 {
    var count = atomicLoad(&status[16]);
    var capacity = parameters.counts.x;
    while (capacity > parameters.range.y) {
        capacity = (capacity + 255u) / 256u;
        count = (count + 255u) / 256u;
    }
    return max(1u, count);
}

@compute @workgroup_size(256)
fn add_prefix(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = (group.y * parameters.counts.z + group.x) * 256u + lane;
    if (index < level_count()) {
        scratch[parameters.range.x + index] += scratch[parameters.range.z + index / 256u];
    }
}

@compute @workgroup_size(256)
fn finish(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let block = group.y * parameters.counts.z + group.x;
    let index = block * 256u + lane;
    var start = 0u;
    if (parameters.counts.w != 0u) { start = atomicLoad(&status[0]) / 3u; }
    if (index < atomicLoad(&status[16])) {
        let state = states[index];
        if (state.node_id != 0u && state.visibility == select(0u, 4u, parameters.counts.w != 0u)) {
            var offset = start;
            if (parameters.counts.x > 256u) { offset += scratch[block]; }
            states[index].offset += offset;
        }
    }
    if (index == 0u) {
        let triangles = scratch[parameters.range.w];
        if (parameters.counts.w == 0u) {
            atomicStore(&status[0], triangles * 3u);
            atomicStore(&status[8], 0u);
            atomicStore(&status[10], triangles * 3u);
        } else { atomicStore(&status[8], triangles * 3u); }
    }
}
