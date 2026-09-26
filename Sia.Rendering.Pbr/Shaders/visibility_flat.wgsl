#import pbr::visibility
#import pbr::materials

@group(1) @binding(0) var visibility: texture_2d<u32>;
@group(1) @binding(2) var<storage, read> materials: array<VisibilityMaterial>;
@group(1) @binding(3) var<uniform> output_settings: vec4<f32>;

fn flat_color(pixel: vec2<i32>) -> vec3<f32> {
    let id = textureLoad(visibility, pixel, 0).x;
    var color = vec3<f32>(0.015, 0.02, 0.03);
    if (id > 0u && id <= visibility_camera.size_counts.z) {
        let work = visibility_triangle_work(id - 1u);
        let instance = visibility_instances[work.y];
        color = instance.color.rgb * materials[u32(instance.material.z)].color_metallic.rgb;
    }
    return color;
}

struct FlatVertex { @builtin(position) position:vec4<f32>, @location(0) uv:vec2<f32> }
@vertex fn vertex(@builtin(vertex_index) index:u32)->FlatVertex {
    let uv=vec2<f32>(f32((index<<1u)&2u),f32(index&2u));
    return FlatVertex(vec4<f32>(uv*vec2<f32>(2.0,-2.0)+vec2<f32>(-1.0,1.0),0.0,1.0),uv);
}
@fragment fn fragment(input:FlatVertex)->@location(0) vec4<f32> {
    let size=textureDimensions(visibility);
    let pixel=min(vec2<u32>(input.uv*vec2<f32>(size)),size-1u);
    let exposed=max(flat_color(vec2<i32>(pixel))*output_settings.x,vec3<f32>(0.0));
    var color=exposed/(1.0+exposed);
    if(output_settings.y>0.5) {
        color=select(1.055*pow(color,vec3<f32>(1.0/2.4))-0.055,color*12.92,color<=vec3<f32>(0.0031308));
    }
    return vec4<f32>(color,1.0);
}
