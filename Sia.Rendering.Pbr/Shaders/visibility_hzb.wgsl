struct Reduction { source: vec4<u32>, destination: vec4<u32> }
@group(0) @binding(0) var depth: texture_2d<f32>;
@group(0) @binding(1) var<storage, read_write> hzb: array<f32>;
@group(0) @binding(2) var<uniform> reduction: Reduction;
@group(0) @binding(3) var<uniform> reduction_upper: Reduction;

// Binding 1 is a separate four-byte scratch buffer for this entry point, not the HZB.
@compute @workgroup_size(1)
fn read_after_post() {
    hzb[0] = textureLoad(depth, vec2<i32>(textureDimensions(depth) / 2u), 0).r;
}

@compute @workgroup_size(8, 8)
fn seed(@builtin(global_invocation_id) thread: vec3<u32>) {
    if (any(thread.xy >= reduction.destination.xy)) { return; }
    let start = thread.xy * reduction.source.w;
    let end = min(start + vec2<u32>(reduction.source.w), reduction.source.xy);
    var maximum = 0.0;
    for (var y = start.y; y < end.y; y++) {
        for (var x = start.x; x < end.x; x++) {
            maximum = max(maximum, textureLoad(depth, vec2<i32>(i32(x), i32(y)), 0).r);
        }
    }
    hzb[reduction.destination.z + thread.y * reduction.destination.x + thread.x] = maximum;
}

fn reduce_box(coord: vec2<u32>) -> f32 {
    let start = coord * 2u;
    let end = min(start + vec2<u32>(2u), reduction.source.xy);
    var maximum = 0.0;
    for (var y = start.y; y < end.y; y++) {
        for (var x = start.x; x < end.x; x++) {
            maximum = max(maximum, hzb[reduction.source.z + y * reduction.source.x + x]);
        }
    }
    return maximum;
}

@compute @workgroup_size(8, 8)
fn reduce(@builtin(global_invocation_id) thread: vec3<u32>) {
    if (any(thread.xy >= reduction.destination.xy)) { return; }
    hzb[reduction.destination.z + thread.y * reduction.destination.x + thread.x] = reduce_box(thread.xy);
}

@compute @workgroup_size(8, 8)
fn reduce2(@builtin(global_invocation_id) thread: vec3<u32>) {
    if (any(thread.xy >= reduction_upper.destination.xy)) { return; }
    var lower_values = array<f32, 4>(0.0, 0.0, 0.0, 0.0);
    for (var i = 0u; i < 4u; i++) {
        let lower_coord = thread.xy * 2u + vec2<u32>(i & 1u, i >> 1u);
        if (any(lower_coord >= reduction.destination.xy)) { continue; }
        let maximum = reduce_box(lower_coord);
        hzb[reduction.destination.z + lower_coord.y * reduction.destination.x + lower_coord.x] = maximum;
        lower_values[i] = maximum;
    }
    let upper_maximum = max(max(lower_values[0], lower_values[1]), max(lower_values[2], lower_values[3]));
    hzb[reduction_upper.destination.z + thread.y * reduction_upper.destination.x + thread.x] = upper_maximum;
}
