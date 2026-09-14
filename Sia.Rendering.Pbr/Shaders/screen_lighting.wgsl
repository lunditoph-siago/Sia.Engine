#import pbr::screen_trace::{trace_scene, trace_scene_bounded, scene_color, scene_position}
#import pbr::scene_lighting::{scene_lighting}
@group(0) @binding(3) var base_roughness: texture_2d<f32>;
@group(0) @binding(4) var normal_metallic: texture_2d<f32>;
@group(0) @binding(5) var emissive_occlusion: texture_2d<f32>;

@vertex fn vertex(@builtin(vertex_index) index:u32) -> @builtin(position) vec4<f32> {
    let xy=vec2<f32>(f32((index<<1u)&2u),f32(index&2u));
    return vec4<f32>(xy*2.0-1.0,0.0,1.0);
}
fn gather_pixel(pixel: vec2<i32>) -> vec2<i32> {
    return min(pixel*8+vec2<i32>(4),vec2<i32>(textureDimensions(opaque_depth))-1);
}

#ifdef SCREEN_GATHER
struct GatherOutput { @location(0) radiance:vec4<f32>, @location(1) surface:vec4<f32> }
// One current-frame sample per 8x8 pixels. RGB is incoming hit radiance and A
// is the fraction of environment replaced by those hits, not final material color.
@fragment fn fragment(@builtin(position) pixel:vec4<f32>) -> GatherOutput {
    let xy=gather_pixel(vec2<i32>(pixel.xy));
    let depth=textureLoad(opaque_depth,xy,0).r;
    if (depth>=1.0) { return GatherOutput(vec4<f32>(0.0),vec4<f32>(0.0,0.0,0.0,1.0)); }
    let nm=textureLoad(normal_metallic,xy,0);
    if (nm.w>=0.99) { return GatherOutput(vec4<f32>(0.0),vec4<f32>(0.0,0.0,0.0,1.0)); }
    let normal=normalize(nm.xyz);
    let uv=(vec2<f32>(xy)+0.5)/vec2<f32>(textureDimensions(opaque_depth));
    let position=scene_position(uv,depth);
    let axis=select(vec3<f32>(0.0,0.0,1.0),vec3<f32>(0.0,1.0,0.0),abs(normal.z)>0.99);
    let tangent=normalize(cross(axis,normal));
    let bitangent=cross(normal,tangent);
    var result=vec4<f32>(0.0);
    for (var i=0u;i<4u;i++) {
        let r2=(f32(i)+0.5)/4.0;
        let angle=f32(i)*2.39996323;
        let direction=tangent*(sqrt(r2)*cos(angle))+bitangent*(sqrt(r2)*sin(angle))+normal*sqrt(1.0-r2);
        let hit=trace_scene_bounded(position+normal*0.025,direction,12u,3.0);
        if (hit.z<=0.0) { continue; }
        let hitPixel=scene_pixel(hit.xy);
        let hitNormal=textureLoad(normal_metallic,hitPixel,0).xyz;
        if (dot(hitNormal,-direction)<=0.05) { continue; }
        let hitPosition=scene_position(hit.xy,textureLoad(opaque_depth,hitPixel,0).r);
        let weight=hit.z*(1.0-smoothstep(1.5,3.0,length(hitPosition-position)))*0.25;
        result+=vec4<f32>(scene_color(hit.xy),1.0)*weight;
    }
    return GatherOutput(result,vec4<f32>(nm.xyz,depth));
}
#else
#ifdef SCREEN_INDIRECT
@group(0) @binding(6) var gathered_light: texture_2d<f32>;
@group(0) @binding(7) var gathered_surface: texture_2d<f32>;
// Static neighbors keep the hot full-resolution path free of dynamically indexed arrays.
struct GatherQuad {
    first:vec2<i32>, fraction:vec2<f32>,
    a:vec4<f32>, b:vec4<f32>, c:vec4<f32>, d:vec4<f32>,
}
fn gather_quad(pixel:vec2<f32>) -> GatherQuad {
    let size=vec2<i32>(textureDimensions(gathered_light));
    let grid=(pixel-4.5)/8.0;
    let first=vec2<i32>(floor(grid));
    return GatherQuad(first,fract(grid),
        textureLoad(gathered_light,clamp(first,vec2<i32>(0),size-1),0),
        textureLoad(gathered_light,clamp(first+vec2<i32>(1,0),vec2<i32>(0),size-1),0),
        textureLoad(gathered_light,clamp(first+vec2<i32>(0,1),vec2<i32>(0),size-1),0),
        textureLoad(gathered_light,clamp(first+vec2<i32>(1,1),vec2<i32>(0),size-1),0));
}
fn gather_weight(sample:vec2<i32>,normal:vec3<f32>,plane_clip:vec4<f32>,inverse_w:vec4<f32>) -> f32 {
    let size=vec2<i32>(textureDimensions(gathered_light));
    let tile=clamp(sample,vec2<i32>(0),size-1);
    let surface=textureLoad(gathered_surface,tile,0);
    let depth=surface.w;
    if (depth>=1.0 || dot(surface.xyz,normal)<0.85) { return 0.0; }
    let xy=gather_pixel(tile);
    let uv=(vec2<f32>(xy)+0.5)/vec2<f32>(textureDimensions(opaque_depth));
    let clip=vec4<f32>(uv*vec2<f32>(2.0,-2.0)+vec2<f32>(-1.0,1.0),depth,1.0);
    let separation=abs(dot(plane_clip,clip)/dot(inverse_w,clip));
    return 1.0-smoothstep(0.025,0.15,separation);
}
fn indirect_sample(q:GatherQuad,position:vec3<f32>,normal:vec3<f32>) -> vec4<f32> {
    // Transform the receiver plane once. Homogeneous W preserves meter rejection.
    let plane_clip=transpose(camera.inverse_view_proj)*vec4<f32>(normal,-dot(normal,position));
    let inverse_w=vec4<f32>(camera.inverse_view_proj[0].w,camera.inverse_view_proj[1].w,
        camera.inverse_view_proj[2].w,camera.inverse_view_proj[3].w);
    let f=q.fraction;
    let weights=vec4<f32>(
        gather_weight(q.first,normal,plane_clip,inverse_w),
        gather_weight(q.first+vec2<i32>(1,0),normal,plane_clip,inverse_w),
        gather_weight(q.first+vec2<i32>(0,1),normal,plane_clip,inverse_w),
        gather_weight(q.first+vec2<i32>(1,1),normal,plane_clip,inverse_w))
        *vec4<f32>((1.0-f.x)*(1.0-f.y),f.x*(1.0-f.y),(1.0-f.x)*f.y,f.x*f.y);
    // No accepted surface means retaining the existing environment light.
    return (q.a*weights.x+q.b*weights.y+q.c*weights.z+q.d*weights.w)
        /max(dot(weights,vec4<f32>(1.0)),0.0001);
}
#endif
@fragment fn fragment(@builtin(position) pixel:vec4<f32>) -> @location(0) vec4<f32> {
    let xy=vec2<i32>(pixel.xy); let original=textureLoad(opaque_color,xy,0);
    let depth=textureLoad(opaque_depth,xy,0).r;
    if (depth>=1.0) { return original; }
    let base=textureLoad(base_roughness,xy,0);
    var contributes=false;
#ifdef SCREEN_REFLECTIONS
    contributes=base.w<0.35;
#endif
#ifdef SCREEN_INDIRECT
    let quad=gather_quad(pixel.xy);
    let has_indirect=any(quad.a+quad.b+quad.c+quad.d!=vec4<f32>(0.0));
    contributes=contributes || has_indirect;
#endif
    if (!contributes) { return original; }
    let uv=pixel.xy/vec2<f32>(textureDimensions(opaque_depth));
    let position=scene_position(uv,depth);
    let nm=textureLoad(normal_metallic,xy,0);
    let normal=normalize(nm.xyz);let view=normalize(camera.world_position.xyz-position);
    let roughness=max(base.w,0.045);
    let f0=mix(vec3<f32>(0.04),base.rgb,nm.w);
    let nDotV=max(dot(normal,view),0.0001);
    let ao=textureLoad(emissive_occlusion,xy,0).w;
    var result=original.rgb;
#ifdef SCREEN_INDIRECT
    if (has_indirect && nm.w<0.99) {
        let gathered=indirect_sample(quad,position,normal);
        let kd=(vec3<f32>(1.0)-fresnel_schlick_roughness(nDotV,f0,roughness))*(1.0-nm.w);
        let environment=evaluate_sh_irradiance(ibl_sh.coefficients,normal)/PBR_PI;
        // Replace occluded diffuse environment energy, preserving direct light,
        // emission and authored material AO. No feedback into this frame's source.
        result+=kd*base.rgb*ao*(gathered.rgb-environment*gathered.a);
    }
#endif
#ifdef SCREEN_REFLECTIONS
    if (base.w<0.35) {
        let direction=reflect(-view,normal);
        let hit=trace_scene(position+normal*0.015,direction);
        let weight=hit.z*(1.0-smoothstep(0.15,0.35,base.w));
        if (weight>0.0) {
            let lut=sample_brdf_lut(ibl_brdf_lut,ibl_brdf_lut_sampler,nDotV,roughness);
            let brdf=f0*lut.x+lut.y;
            let environment=sample_prefiltered_specular(ibl_prefiltered,ibl_prefiltered_sampler,direction,roughness,IBL_PREFILTERED_MIP_COUNT)*brdf;
            result+=weight*(scene_color(hit.xy)*brdf-environment*ao);
        }
    }
#endif
    return vec4<f32>(max(result,vec3<f32>(0.0)),original.a);
}
#endif
