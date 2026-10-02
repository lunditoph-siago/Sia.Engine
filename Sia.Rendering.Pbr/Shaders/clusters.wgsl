#define_import_path pbr/clusters

#import pbr/bindings

@compute @workgroup_size(64)
fn cull(@builtin(global_invocation_id) id: vec3<u32>) {
    let count = frame.grid.x * frame.grid.y * frame.grid.z;
    if (id.x >= count) {
        return;
    }
    let tx = id.x % frame.grid.x;
    let ty = (id.x / frame.grid.x) % frame.grid.y;
    let z = id.x / (frame.grid.x * frame.grid.y);
    let lo = vec2<f32>(f32(tx) / f32(frame.grid.x) * 2.0 - 1.0, 1.0 - f32(ty + 1u) / f32(frame.grid.y) * 2.0);
    let hi = vec2<f32>(f32(tx + 1u) / f32(frame.grid.x) * 2.0 - 1.0, 1.0 - f32(ty) / f32(frame.grid.y) * 2.0);
    let near = -exp((f32(z) + frame.depth.y) / frame.depth.x);
    let far = -exp((f32(z + 1u) + frame.depth.y) / frame.depth.x);
    let r0 = frame.inv_proj * vec4<f32>(lo, 0.0, 1.0);
    let r1 = frame.inv_proj * vec4<f32>(hi, 0.0, 1.0);
    let p0 = r0.xyz * (near / r0.z);
    let p1 = r1.xyz * (near / r1.z);
    let p2 = r0.xyz * (far / r0.z);
    let p3 = r1.xyz * (far / r1.z);
    let minimum = min(min(p0, p1), min(p2, p3));
    let maximum = max(max(p0, p1), max(p2, p3));
    let start = id.x * (frame.grid.w + 1u);
    var matches = 0u;
    for (var i = 0u; i < frame.counts.x; i++) {
        let light = scene_data[i * 4u];
        let position = (frame.view * vec4<f32>(light.xyz, 1.0)).xyz;
        let delta = position - clamp(position, minimum, maximum);
        if (dot(delta, delta) > light.w * light.w) {
            continue;
        }
        if (matches >= frame.grid.w) {
            clusters[start] = 0xffffffffu;
            return;
        }
        clusters[start + 1u + matches] = i;
        matches++;
    }
    clusters[start] = matches;
}
