#define_import_path pbr/Providers/environment

#import pbr/bindings
#import pbr/pbr_lighting
#import pbr/Common/surface
#if REFLECTION_CAPTURES
#import pbr/Providers/reflection_capture
#endif
#if STATIC_LIGHTMAP
#import pbr/Providers/lightmap
#endif
#if SCENE_GI
#import rendering/Lighting/probe_volume
#endif

#if SCENE_GI
fn scene_probe_irradiance(position: vec3<f32>, normal: vec3<f32>, correction: bool) -> vec3<f32> {
    return rendering_scene_probe_irradiance(probe_volume, environment_sampler, probe_header, sh, position, normal, correction);
}
#endif

fn get_environment_specular(position: vec3<f32>, direction: vec3<f32>, roughness: f32) -> vec3<f32> {
    let sky = sample_prefiltered_specular(environment, environment_sampler, direction, roughness, f32(textureNumLevels(environment)));
#if REFLECTION_CAPTURES
    return capture_specular(position, direction, roughness, sky);
#else
    return sky;
#endif
}

fn get_indirect_lighting(surface: Surface, view: vec3<f32>) -> vec3<f32> {
    let position = surface.position; let normal = surface.normal;
    let base = surface.base; let metal = surface.metal; let rough = surface.rough;
    var irradiance = vec3<f32>(0);
    // Pure reflection and fully metallic surfaces have no diffuse contribution.
    if (metal < 1.0 && any(base != vec3<f32>(0))) {
#if STATIC_LIGHTMAP
        let baked = lightmap_irradiance(surface);
        if (baked.w > 0.0) {
            irradiance = baked.xyz;
#if SCENE_GI
            if (probe_header[2].z > 0.5) { irradiance += scene_probe_irradiance(position, normal, true); }
#endif
        }
        else {
#endif
#if SCENE_GI
        irradiance = scene_probe_irradiance(position, normal, false);
#else
        irradiance = evaluate_sh_irradiance(sh, normal);
#endif
#if STATIC_LIGHTMAP
        }
#endif
    }
    return indirect_lighting(normal, view, base, metal, rough, max(irradiance, vec3<f32>(0.0)),
        get_environment_specular(position, reflect(-view, normal), rough), brdf_lut, brdf_sampler);
}
