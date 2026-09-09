#define_import_path pbr::parallel_lod

struct ParallelParameters { range: vec4<u32>, counts: vec4<u32> }
struct ParallelCost { cost: vec4<u32>, counts: vec4<u32> }
@group(1) @binding(0) var<uniform> parallel_parameters: ParallelParameters;
@group(1) @binding(1) var<storage, read_write> parallel_maximum: atomic<u32>;
var<workgroup> parallel_costs: array<vec4<u32>, 128>;
var<workgroup> parallel_counts: array<vec4<u32>, 128>;
var<workgroup> parallel_control: vec4<u32>;

fn parallel_read(index: u32) -> ParallelCost {
    let at = index * 8u;
    return ParallelCost(vec4<u32>(heap[at], heap[at + 1u], heap[at + 2u], heap[at + 3u]),
        vec4<u32>(heap[at + 4u], heap[at + 5u], heap[at + 6u], heap[at + 7u]));
}

fn parallel_write(index: u32, value: ParallelCost) {
    let at = index * 8u;
    for (var i = 0u; i < 4u; i++) { heap[at + i] = value.cost[i]; heap[at + i + 4u] = value.counts[i]; }
}

fn parallel_sum(a: ParallelCost, b: ParallelCost) -> ParallelCost {
    let sum = a.cost + b.cost;
    return ParallelCost(select(sum, vec4<u32>(0xffffffffu), sum < a.cost),
        vec4<u32>(a.counts.xyz + b.counts.xyz, max(a.counts.w, b.counts.w)));
}

fn parallel_prefix(value: ParallelCost, lane: u32) -> ParallelCost {
    parallel_costs[lane] = value.cost;
    parallel_counts[lane] = value.counts;
    workgroupBarrier();
    for (var stride = 1u; stride < 128u; stride *= 2u) {
        var left = ParallelCost();
        if (lane >= stride) { left = ParallelCost(parallel_costs[lane - stride], parallel_counts[lane - stride]); }
        workgroupBarrier();
        let total = parallel_sum(ParallelCost(parallel_costs[lane], parallel_counts[lane]), left);
        parallel_costs[lane] = total.cost;
        parallel_counts[lane] = total.counts;
        workgroupBarrier();
    }
    return ParallelCost(parallel_costs[lane], parallel_counts[lane]);
}

fn parallel_global_prefix(index: u32) -> ParallelCost {
    var value = parallel_read(4u + index);
    if (parallel_parameters.counts.x > 128u) {
        value = parallel_sum(value, parallel_read(4u + parallel_parameters.counts.x + index / 128u));
    }
    return value;
}

fn parallel_fits(value: ParallelCost) -> bool {
    let control = parallel_read(0u);
    let consumed = parallel_read(1u);
    return all(value.cost <= vec4<u32>(parameters.budget.xyz, parameters.traversal.y) - control.counts)
        && value.counts.x <= parameters.traversal.x - consumed.cost.x;
}

fn parallel_narrow(value: ParallelCost) -> bool {
    return !parallel_fits(value) && (value.counts.w & 0x7ff00000u) > parallel_read(2u).cost.x;
}

fn parallel_status() {
    let control = parallel_read(0u);
    let consumed = parallel_read(1u);
    status.draw = vec4<u32>(consumed.counts.z * 3u, 1u, 0u, 0u);
    status.post_draw = vec4<u32>(0u, 1u, 0u, 0u);
    status.selection = vec4<u32>(consumed.counts.xy, consumed.cost.z | (consumed.cost.w << 1u), 0u);
    status.culling = vec4<u32>(consumed.counts.z, 0u, 0u, 0u);
    status.traversal = vec4<u32>(control.cost.z, consumed.cost.xy, 0u);
    dispatch_size(0u, (control.cost.z + 63u) / 64u);
    dispatch_size(3u, control.cost.z);
    var count = control.cost.z;
    for (var level = 0u; level < 4u; level++) {
        count = (count + 255u) / 256u;
        dispatch_size(6u + level * 3u, count);
    }
}

@compute @workgroup_size(1)
fn parallel_init() {
    atomicStore(&parallel_maximum, 0u);
    let totals = vec3<u32>(parameters.counts.y, parameters.traversal.zw);
    let unreachable = u32(any(totals > parameters.budget.xyz));
    parallel_write(0u, ParallelCost(vec4<u32>(0u, parameters.counts.y, parameters.counts.y,
        u32(parameters.counts.y == 0u || unreachable != 0u)), vec4<u32>(totals, 0u)));
    parallel_write(1u, ParallelCost(vec4<u32>(0u, 0u, unreachable, unreachable), vec4<u32>(totals, 0u)));
    parallel_write(2u, ParallelCost(vec4<u32>(0x7f800000u, 0u, 0u, 0u), vec4<u32>(0u)));
    parallel_status();
}

@compute @workgroup_size(128)
fn parallel_local(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let block = group.y * parameters.counts.w + group.x;
    if (lane == 0u) { parallel_control = parallel_read(0u).cost; }
    let control = workgroupUniformLoad(&parallel_control);
    if (control.w != 0u || block * 128u >= control.y) { return; }
    let index = block * 128u + lane;
    var value = ParallelCost();
    if (index < control.y && states[control.x + index].node_id != 0u) {
        let state = states[control.x + index];
        let node = patches[state.node_id - 1u];
        if (state.error > parameters.budget.w && node.children.y > 0u) {
            value.counts.w = state.error;
            if (state.error >= parallel_read(2u).cost.x) {
                let delta = node.children.zw - node.geometry.xz;
                value = ParallelCost(vec4<u32>(node.children.y - 1u,
                    select(vec2<u32>(0u), delta, node.children.zw >= node.geometry.xz), node.children.y), vec4<u32>(1u, delta, state.error));
            }
        }
    }
    let total = parallel_prefix(value, lane);
    if (index < control.y) { parallel_write(4u + index, total); }
    if (lane == 127u) { parallel_write(4u + parallel_parameters.counts.x + block, total); }
}

fn parallel_level_count() -> u32 {
    var count = parallel_read(0u).cost.y;
    for (var i = 0u; i < parallel_parameters.range.w; i++) { count = (count + 127u) / 128u; }
    return count;
}

@compute @workgroup_size(128)
fn parallel_scan(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let block = group.y * parameters.counts.w + group.x;
    if (lane == 0u) { parallel_control = parallel_read(0u).cost; }
    let control = workgroupUniformLoad(&parallel_control);
    var count = control.y;
    for (var i = 0u; i < parallel_parameters.range.w; i++) { count = (count + 127u) / 128u; }
    if (control.w != 0u || block * 128u >= count) { return; }
    let index = block * 128u + lane;
    var value = ParallelCost();
    if (index < count) { value = parallel_read(parallel_parameters.range.x + index); }
    let total = parallel_prefix(value, lane);
    var exclusive = ParallelCost();
    if (lane > 0u) { exclusive = ParallelCost(parallel_costs[lane - 1u], parallel_counts[lane - 1u]); }
    if (index < count) { parallel_write(parallel_parameters.range.x + index, exclusive); }
    if (lane == 127u) { parallel_write(parallel_parameters.range.z + block, total); }
}

@compute @workgroup_size(128)
fn parallel_add(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = (group.y * parameters.counts.w + group.x) * 128u + lane;
    if (parallel_read(0u).cost.w != 0u || index >= parallel_level_count()) { return; }
    parallel_write(parallel_parameters.range.x + index, parallel_sum(parallel_read(parallel_parameters.range.x + index),
        parallel_read(parallel_parameters.range.z + index / 128u)));
}

@compute @workgroup_size(128)
fn parallel_refine(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let index = (group.y * parameters.counts.w + group.x) * 128u + lane;
    let control = parallel_read(0u).cost;
    if (control.w != 0u || index >= control.y) { return; }
    let prefix = parallel_global_prefix(index);
    let state = states[control.x + index];
    if (state.node_id == 0u) { return; }
    let node = patches[state.node_id - 1u];
    if (state.error <= parameters.budget.w || node.children.y == 0u) { return; }
    if (state.error < parallel_read(2u).cost.x || !parallel_fits(prefix)
        || parallel_narrow(parallel_global_prefix(control.y - 1u))) {
        parallel_next_error(state.error);
        return;
    }
    let offset = control.z + prefix.cost.w - node.children.y;
    states[control.x + index].node_id = 0u;
    var maximum = 0u;
    for (var child = 0u; child < node.children.y; child++) {
        let projected = project_node(node.children.x + child, state.instance);
        states[offset + child] = projected;
        if (patches[node.children.x + child].children.y != 0u) { maximum = max(maximum, projected.error); }
    }
    parallel_next_error(maximum);
}

fn parallel_next_error(error: u32) {
    if (error > atomicLoad(&parallel_maximum)) { atomicMax(&parallel_maximum, error); }
}

@compute @workgroup_size(1)
fn parallel_advance() {
    var control = parallel_read(0u);
    if (control.cost.w != 0u) { return; }
    var consumed = parallel_read(1u);
    let total = parallel_global_prefix(control.cost.y - 1u);
    if (parallel_narrow(total)) {
        var priority = parallel_read(2u);
        priority.cost.x = total.counts.w & 0x7ff00000u;
        priority.cost.y++;
        atomicStore(&parallel_maximum, 0u);
        parallel_write(2u, priority);
        if (priority.cost.y == parallel_parameters.counts.y) {
            consumed.cost.z = 1u;
            parallel_write(1u, consumed);
            parallel_status();
        }
        return;
    }
    var low = 0u;
    var high = control.cost.y;
    while (low < high) {
        let middle = low + (high - low) / 2u;
        if (parallel_fits(parallel_global_prefix(middle))) { low = middle + 1u; }
        else { high = middle; }
    }
    var accepted = ParallelCost();
    if (low != 0u) { accepted = parallel_global_prefix(low - 1u); }
    consumed.cost.x += min(total.counts.x, parameters.traversal.x - consumed.cost.x);
    consumed.cost.y += accepted.counts.x;
    consumed.cost.z |= u32(low != control.cost.y);
    consumed.counts = vec4<u32>(consumed.counts.xyz + vec3<u32>(accepted.cost.x, accepted.counts.yz), consumed.counts.w);
    control.counts += accepted.cost;
    let visited = control.cost.z + accepted.cost.w;
    var priority = parallel_read(2u);
    let maximum = atomicLoad(&parallel_maximum);
    let finished = maximum <= parameters.budget.w
        || (total.counts.x != 0u && accepted.cost.w == 0u);
    control.cost = vec4<u32>(0u, visited, visited, u32(finished));
    priority.cost.x = max(parameters.budget.w, maximum & 0x7f800000u);
    atomicStore(&parallel_maximum, 0u);
    priority.cost.y++;
    if (priority.cost.y == parallel_parameters.counts.y && !finished) { consumed.cost.z = 1u; }
    parallel_write(0u, control);
    parallel_write(1u, consumed);
    parallel_write(2u, priority);
    parallel_status();
}
