@group(0) @binding(0) var<uniform> dimension: vec4<u32>;
@group(0) @binding(1) var<storage, read> work: array<vec2<u32>>;
@group(0) @binding(2) var<storage, read> triangles: array<vec2<u32>>;
@group(0) @binding(3) var<storage, read> status: array<u32>;
@group(0) @binding(4) var<storage, read_write> indices: array<u32>;
@group(1) @binding(0) var<storage, read_write> arguments: array<u32>;
var<workgroup> references: array<vec2<u32>, 64>;
var<workgroup> starts: array<u32, 64>;

@compute @workgroup_size(1)
fn prepare() {
    let groups = (status[0] / 3u + 63u) / 64u;
    arguments[0] = min(groups, dimension.x);
    arguments[1] = (groups + dimension.x - 1u) / dimension.x;
    arguments[2] = 1u;
    arguments[3] = status[0];
    arguments[4] = 1u;
    arguments[5] = 0u;
    arguments[6] = 0u;
    arguments[7] = 0u;
}

@compute @workgroup_size(64)
fn emit(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let base = (group.y * dimension.x + group.x) * 64u;
    let index = base + lane;
    var triangle = vec2<u32>(0u);
    var reference = vec2<u32>(0u);
    if (index < status[0] / 3u) {
        reference = work[index];
        triangle = triangles[reference.x];
    }
    references[lane] = vec2<u32>(triangle.x, reference.y);
    workgroupBarrier();
    var start = lane;
    if (lane > 0u && all(references[lane - 1u] == references[lane])) { start = 0u; }
    starts[lane] = start;
    workgroupBarrier();
    for (var stride = 1u; stride < 64u; stride *= 2u) {
        var previous = 0u;
        if (lane >= stride) { previous = starts[lane - stride]; }
        workgroupBarrier();
        starts[lane] = max(starts[lane], previous);
        workgroupBarrier();
    }
    if (index >= status[0] / 3u) { return; }
    let vertex = (base + starts[lane]) * 256u;
    for (var corner = 0u; corner < 3u; corner++) {
        indices[index * 3u + corner] = vertex + ((triangle.y >> (corner * 8u)) & 255u);
    }
}
