// Adapted from Sebastien Hillaire's EGSR 2020 sky atmosphere reference implementation.
// Copyright (c) 2020 Epic Games, Inc.
// Source: https://github.com/sebh/UnrealEngineSkyAtmosphere
// MIT License: https://opensource.org/license/mit

#define_import_path pbr::atmosphere

const ATM_PI: f32 = 3.14159265359;

struct Atmosphere {
    radii: vec4<f32>,
    rayleigh: vec4<f32>,
    mie: vec4<f32>,
    mie_absorption: vec4<f32>,
    ozone: vec4<f32>,
    ground: vec4<f32>,
    sun: vec4<f32>,
    irradiance: vec4<f32>,
    camera_planet: vec4<f32>,
    camera_world: vec4<f32>,
    inverse_view_projection: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> atmosphere: Atmosphere;
@group(0) @binding(1) var transmittance_lut: texture_2d<f32>;
@group(0) @binding(2) var multiple_scattering_lut: texture_2d<f32>;
@group(0) @binding(3) var sky_view_lut: texture_2d<f32>;
@group(0) @binding(4) var atmosphere_sampler: sampler;

fn sub_uv(uv: vec2<f32>, size: vec2<f32>) -> vec2<f32> {
    return (clamp(uv, vec2<f32>(0.0), vec2<f32>(1.0)) * (size - 1.0) + 0.5) / size;
}

fn sphere_roots(p: vec3<f32>, d: vec3<f32>, radius: f32) -> vec2<f32> {
    let b = dot(p, d);
    let c = (length(p) - radius) * (length(p) + radius);
    let discriminant = b * b - c;
    if (discriminant < 0.0) { return vec2<f32>(-1.0); }
    let root = sqrt(discriminant);
    return vec2<f32>(-b - root, -b + root);
}

fn ground_distance(p: vec3<f32>, d: vec3<f32>) -> f32 {
    let hit = sphere_roots(p, d, atmosphere.radii.x);
    return select(-1.0, hit.x, hit.x >= 0.0);
}

struct Medium {
    rayleigh: vec3<f32>,
    mie: vec3<f32>,
    extinction: vec3<f32>,
};

fn sample_medium(p: vec3<f32>) -> Medium {
    let h = max(length(p) - atmosphere.radii.x, 0.0);
    let r = atmosphere.rayleigh.rgb * exp(-h / atmosphere.rayleigh.w);
    let m = atmosphere.mie.rgb * exp(-h / atmosphere.mie.w);
    let ozone = max(0.0, 1.0 - abs(h - atmosphere.ozone.w) / atmosphere.ground.w);
    return Medium(r, m, r + m + atmosphere.mie_absorption.rgb * exp(-h / atmosphere.mie.w) + atmosphere.ozone.rgb * ozone);
}

fn segment_integral(extinction: vec3<f32>, distance: f32) -> vec3<f32> {
    let x = extinction * distance;
    let exact = (1.0 - exp(-x)) / max(extinction, vec3<f32>(1e-20));
    let series = distance * (1.0 - x * 0.5 + x * x / 6.0);
    return select(exact, series, x < vec3<f32>(1e-3));
}

fn transmittance_uv(radius: f32, mu: f32) -> vec2<f32> {
    let bottom = atmosphere.radii.x;
    let top = atmosphere.radii.y;
    let h = sqrt((top - bottom) * (top + bottom));
    let rho = sqrt(max((radius - bottom) * (radius + bottom), 0.0));
    let delta = radius * radius * mu * mu + (top - radius) * (top + radius);
    let distance = max(-radius * mu + sqrt(max(delta, 0.0)), 0.0);
    let low = top - radius;
    return sub_uv(vec2<f32>((distance - low) / max(rho + h - low, 1e-6), rho / h), vec2<f32>(textureDimensions(transmittance_lut)));
}

fn transmittance_to_sun(p: vec3<f32>, sun: vec3<f32>) -> vec3<f32> {
    if (ground_distance(p + normalize(p) * 0.001, sun) >= 0.0) { return vec3<f32>(0.0); }
    let radius = clamp(length(p), atmosphere.radii.x, atmosphere.radii.y);
    return textureSampleLevel(transmittance_lut, atmosphere_sampler,
        transmittance_uv(radius, dot(normalize(p), sun)), 0.0).rgb;
}

fn multiple_scattering(p: vec3<f32>, sun: vec3<f32>) -> vec3<f32> {
    let uv = vec2<f32>(dot(normalize(p), sun) * 0.5 + 0.5,
        (length(p) - atmosphere.radii.x) / (atmosphere.radii.y - atmosphere.radii.x));
    return textureSampleLevel(multiple_scattering_lut, atmosphere_sampler,
        sub_uv(uv, vec2<f32>(textureDimensions(multiple_scattering_lut))), 0.0).rgb;
}

struct AtmosphereIntegral {
    radiance: vec3<f32>,
    transmittance: vec3<f32>,
    feedback: vec3<f32>,
};

fn integrate_atmosphere(origin: vec3<f32>, direction: vec3<f32>, sun: vec3<f32>,
    maximum_distance: f32, samples: u32, isotropic: bool, include_ground: bool) -> AtmosphereIntegral {
    var result = AtmosphereIntegral(vec3<f32>(0.0), vec3<f32>(1.0), vec3<f32>(0.0));
    let top_hit = sphere_roots(origin, direction, atmosphere.radii.y);
    if (top_hit.y <= 0.0) { return result; }
    let start = max(top_hit.x, 0.0);
    let ground_hit = ground_distance(origin, direction);
    var finish = min(top_hit.y, maximum_distance);
    if (ground_hit >= 0.0) { finish = min(finish, ground_hit); }
    if (finish <= start) { return result; }
    let cosine = clamp(dot(direction, sun), -1.0, 1.0);
    let g = atmosphere.mie_absorption.w;
    let phase_r = 3.0 * (1.0 + cosine * cosine) / (16.0 * ATM_PI);
    let phase_m = 3.0 * (1.0 - g * g) * (1.0 + cosine * cosine)
        / (8.0 * ATM_PI * (2.0 + g * g) * pow(max(1.0 + g * g - 2.0 * g * cosine, 1e-6), 1.5));
    for (var i = 0u; i < samples; i += 1u) {
        let a = f32(i) / f32(samples);
        let b = f32(i + 1u) / f32(samples);
        let t0 = start + (finish - start) * a * a;
        let t1 = start + (finish - start) * b * b;
        let dt = t1 - t0;
        let p = origin + direction * (t0 + dt * 0.5);
        let medium = sample_medium(p);
        let scattering = medium.rayleigh + medium.mie;
        var source = scattering / (4.0 * ATM_PI);
        if (!isotropic) { source = medium.rayleigh * phase_r + medium.mie * phase_m; }
        source *= transmittance_to_sun(p, sun);
        if (!isotropic) { source += multiple_scattering(p, sun) * scattering; }
        let weight = result.transmittance * segment_integral(medium.extinction, dt);
        result.radiance += weight * source;
        result.feedback += weight * scattering;
        result.transmittance *= exp(-medium.extinction * dt);
    }
    if (include_ground && ground_hit >= 0.0 && ground_hit <= maximum_distance && ground_hit <= top_hit.y) {
        let p = origin + direction * ground_hit;
        let ground_light = transmittance_to_sun(p, sun) * max(dot(normalize(p), sun), 0.0);
        result.radiance += result.transmittance * atmosphere.ground.rgb * ground_light / ATM_PI;
    }
    return result;
}

fn sky_angles(uv: vec2<f32>, radius: f32) -> vec2<f32> {
    let beta = asin(clamp(atmosphere.radii.x / radius, 0.0, 1.0));
    let horizon = ATM_PI - beta;
    var zenith: f32;
    if (uv.y < 0.5) { zenith = horizon * (1.0 - pow(1.0 - uv.y * 2.0, 2.0)); }
    else { zenith = horizon + beta * pow(uv.y * 2.0 - 1.0, 2.0); }
    return vec2<f32>(cos(zenith), 1.0 - 2.0 * uv.x * uv.x);
}

fn sky_radiance(direction: vec3<f32>) -> vec3<f32> {
    let origin = atmosphere.camera_planet.xyz;
    let radius = length(origin);
    let up = normalize(origin);
    let view_mu = clamp(dot(direction, up), -1.0, 1.0);
    let horizontal_view = direction - up * view_mu;
    let horizontal_sun = atmosphere.sun.xyz - up * dot(atmosphere.sun.xyz, up);
    let azimuth_cos = clamp(dot(horizontal_view, horizontal_sun)
        / max(length(horizontal_view) * length(horizontal_sun), 1e-8), -1.0, 1.0);
    let beta = asin(clamp(atmosphere.radii.x / radius, 0.0, 1.0));
    let horizon = ATM_PI - beta;
    let zenith = acos(view_mu);
    var v: f32;
    if (zenith < horizon) { v = 0.5 * (1.0 - sqrt(max(1.0 - zenith / horizon, 0.0))); }
    else { v = 0.5 + 0.5 * sqrt(clamp((zenith - horizon) / beta, 0.0, 1.0)); }
    let uv = vec2<f32>(sqrt(0.5 - 0.5 * azimuth_cos), v);
    return textureSampleLevel(sky_view_lut, atmosphere_sampler,
        sub_uv(uv, vec2<f32>(textureDimensions(sky_view_lut))), 0.0).rgb;
}

fn atmosphere_view_direction(uv: vec2<f32>) -> vec3<f32> {
    let ndc = uv * vec2<f32>(2.0, -2.0) + vec2<f32>(-1.0, 1.0);
    let h = atmosphere.inverse_view_projection * vec4<f32>(ndc, 0.5, 1.0);
    return normalize(h.xyz / h.w - atmosphere.camera_world.xyz);
}
