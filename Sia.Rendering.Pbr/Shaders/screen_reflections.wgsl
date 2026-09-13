#import pbr::screen_trace::{trace_scene, scene_color}
#import pbr::scene_lighting::{scene_lighting}
@group(0) @binding(3) var base_roughness: texture_2d<f32>;
@group(0) @binding(4) var normal_metallic: texture_2d<f32>;
@group(0) @binding(5) var emissive_occlusion: texture_2d<f32>;

@vertex fn vertex(@builtin(vertex_index) index:u32) -> @builtin(position) vec4<f32> {
    let xy=vec2<f32>(f32((index<<1u)&2u),f32(index&2u));
    return vec4<f32>(xy*2.0-1.0,0.0,1.0);
}
@fragment fn fragment(@builtin(position) pixel:vec4<f32>) -> @location(0) vec4<f32> {
    let xy=vec2<i32>(pixel.xy); let original=textureLoad(opaque_color,xy,0);
    let depth=textureLoad(opaque_depth,xy,0).r;
    let base=textureLoad(base_roughness,xy,0);
    if (depth>=1.0 || base.w>=0.35) { return original; }
    let uv=pixel.xy/vec2<f32>(textureDimensions(opaque_depth));
    let world=camera.inverse_view_proj*vec4<f32>(uv*vec2<f32>(2.0,-2.0)+vec2<f32>(-1.0,1.0),depth,1.0);
    let position=world.xyz/world.w;
    let nm=textureLoad(normal_metallic,xy,0);
    let normal=normalize(nm.xyz);let view=normalize(camera.world_position.xyz-position);
    let hit=trace_scene(position+normal*0.015,reflect(-view,normal));
    let weight=hit.z*(1.0-smoothstep(0.15,0.35,base.w));
    if (weight<=0.0) { return original; }
    let roughness=max(base.w,0.045);
    let f0=mix(vec3<f32>(0.04),base.rgb,nm.w);
    let lut=sample_brdf_lut(ibl_brdf_lut,ibl_brdf_lut_sampler,max(dot(normal,view),0.0001),roughness);
    let brdf=f0*lut.x+lut.y;
    let environment=sample_prefiltered_specular(ibl_prefiltered,ibl_prefiltered_sampler,reflect(-view,normal),roughness,IBL_PREFILTERED_MIP_COUNT)*brdf;
    let ao=textureLoad(emissive_occlusion,xy,0).w;
    return vec4<f32>(max(original.rgb+weight*(scene_color(hit.xy)*brdf-environment*ao),vec3<f32>(0.0)),original.a);
}
