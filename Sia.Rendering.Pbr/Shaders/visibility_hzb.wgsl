struct Reduction { source: vec4<u32>, destination: vec4<u32> }
@group(0) @binding(0) var depth: texture_depth_2d;
@group(0) @binding(1) var<storage, read_write> hzb: array<f32>;
@group(0) @binding(2) var<uniform> reduction: Reduction;

@compute @workgroup_size(8, 8)
fn seed(@builtin(global_invocation_id) thread: vec3<u32>) {
    if (any(thread.xy >= reduction.destination.xy)) { return; }
    let start = thread.xy * reduction.source.w;
    let end = min(start + vec2<u32>(reduction.source.w), reduction.source.xy);
    var maximum = 0.0;
    for (var y = start.y; y < end.y; y++) {
        for (var x = start.x; x < end.x; x++) {
            maximum = max(maximum, textureLoad(depth, vec2<i32>(i32(x), i32(y)), 0));
        }
    }
    hzb[reduction.destination.z + thread.y * reduction.destination.x + thread.x] = maximum;
}

@compute @workgroup_size(8, 8)
fn reduce(@builtin(global_invocation_id) thread: vec3<u32>) {
    if (any(thread.xy >= reduction.destination.xy)) { return; }
    let start = thread.xy * 2u;
    let end = min(start + vec2<u32>(2u), reduction.source.xy);
    var maximum = 0.0;
    for (var y = start.y; y < end.y; y++) {
        for (var x = start.x; x < end.x; x++) {
            maximum = max(maximum, hzb[reduction.source.z + y * reduction.source.x + x]);
        }
    }
    hzb[reduction.destination.z + thread.y * reduction.destination.x + thread.x] = maximum;
}
