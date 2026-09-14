#define_import_path pbr::screen_trace

struct GlassCamera { view_proj: mat4x4<f32>, world_position: vec4<f32>, inverse_view_proj: mat4x4<f32> }
@group(0) @binding(0) var<uniform> camera: GlassCamera;
@group(0) @binding(1) var opaque_color: texture_2d<f32>;
// Match the existing Visibility depth path: unfilterable float sampling avoids
// compatibility-mode fragment textureLoad restrictions on texture_depth_2d.
@group(0) @binding(2) var opaque_depth: texture_2d<f32>;

fn project_clip(clip: vec4<f32>) -> vec3<f32> {
    if (clip.w <= 0.0) { return vec3<f32>(-1.0); }
    let ndc = clip.xyz / clip.w;
    return vec3<f32>(ndc.xy * vec2<f32>(0.5, -0.5) + 0.5, ndc.z);
}
fn scene_position(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    let world = camera.inverse_view_proj * vec4<f32>(uv * vec2<f32>(2.0, -2.0) + vec2<f32>(-1.0, 1.0), depth, 1.0);
    return world.xyz / world.w;
}
fn scene_pixel(uv: vec2<f32>) -> vec2<i32> {
    let size = vec2<i32>(textureDimensions(opaque_depth));
    return clamp(vec2<i32>(uv * vec2<f32>(size)), vec2<i32>(0), size - 1);
}
fn scene_color(uv: vec2<f32>) -> vec3<f32> {
    let size = vec2<i32>(textureDimensions(opaque_color));
    let p = uv * vec2<f32>(size) - 0.5;
    let lo = vec2<i32>(floor(p)); let f = fract(p);
    let a = textureLoad(opaque_color, clamp(lo, vec2<i32>(0), size-1), 0).rgb;
    let b = textureLoad(opaque_color, clamp(lo+vec2<i32>(1,0), vec2<i32>(0), size-1), 0).rgb;
    let c = textureLoad(opaque_color, clamp(lo+vec2<i32>(0,1), vec2<i32>(0), size-1), 0).rgb;
    let d = textureLoad(opaque_color, clamp(lo+vec2<i32>(1,1), vec2<i32>(0), size-1), 0).rgb;
    return mix(mix(a,b,f.x),mix(c,d,f.x),f.y);
}
// Bounded current-frame trace against the first opaque layer. Misses preserve
// environment reflection/undistorted transmission instead of sampling screen edges.
fn trace_scene_bounded(origin: vec3<f32>, direction: vec3<f32>, steps: u32, distance: f32) -> vec3<f32> {
    // Project the ray once. Perspective division still happens at every sample;
    // interpolating divided screen coordinates would change the ray trajectory.
    let start = camera.view_proj * vec4<f32>(origin, 1.0);
    let delta = camera.view_proj * vec4<f32>(direction, 0.0);
    var previous = 0.0;
    for (var i=1u; i<=steps; i++) {
        let fraction = f32(i) / f32(steps);
        let t = 0.02 + distance * fraction * fraction;
        let projected = project_clip(start + delta*t);
        if (any(projected.xy < vec2<f32>(0.0)) || any(projected.xy > vec2<f32>(1.0)) || projected.z < 0.0 || projected.z > 1.0) { break; }
        let depth = textureLoad(opaque_depth, scene_pixel(projected.xy), 0).r;
        if (depth < 1.0 && projected.z >= depth) {
            var low=previous; var high=t;
            for (var j=0u; j<6u; j++) {
                let middle=(low+high)*0.5;
                let q=project_clip(start+delta*middle);
                if (q.z >= textureLoad(opaque_depth,scene_pixel(q.xy),0).r) { high=middle; } else { low=middle; }
            }
            let q=project_clip(start+delta*high);
            let z=textureLoad(opaque_depth,scene_pixel(q.xy),0).r;
            let world=camera.inverse_view_proj*vec4<f32>(q.xy*vec2<f32>(2.0,-2.0)+vec2<f32>(-1.0,1.0),z,1.0);
            if (length(world.xyz/world.w-(origin+direction*high)) < 0.06+high*0.015) {
                let edge=min(min(q.x,q.y),min(1.0-q.x,1.0-q.y));
                return vec3<f32>(q.xy,smoothstep(0.0,0.04,edge));
            }
            return vec3<f32>(0.0);
        }
        previous=t;
    }
    return vec3<f32>(0.0);
}
fn trace_scene(origin: vec3<f32>, direction: vec3<f32>) -> vec3<f32> {
    return trace_scene_bounded(origin, direction, 48u, 24.0);
}
