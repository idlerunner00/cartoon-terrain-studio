#[compute]
// Compiled with exactly the same contour include as the spatial fallback.
#version 450
#define INK_MSAA
layout(local_size_x=8, local_size_y=8, local_size_z=1) in;
layout(set=0, binding=0) uniform sampler2DMS depth_texture;
layout(set=0, binding=1) uniform sampler2DMS normal_roughness;
layout(r8, set=0, binding=2) uniform writeonly image2D coverage_image;
layout(push_constant, std430) uniform Parameters {
    vec4 camera; // footprint at z=1, depth precision, reserved, orthographic
    vec4 output_info; // width, height, scale, sample count
    vec4 depth_projection; // inverse GPU projection: zz, wz, zw, ww
} params;
#define px_scale params.output_info.z
const float width = 2.15;
int ink_sample;
float ink_depth(ivec2 pixel, ivec2 size, mat4 unused_projection) {
    float raw = texelFetch(depth_texture, clamp(pixel, ivec2(0), size-1), ink_sample).r;
    if (raw <= 0.0) return 1e8;
    // RenderSceneData already supplies the corrected reverse-Z GPU projection.
    // CPU Projection.GetZNear/GetZFar assume a different clip convention.
    vec4 z = params.depth_projection;
    return -(raw*z.x+z.y) / max(raw*z.z+z.w, .000001);
}
vec4 ink_normal(ivec2 pixel, ivec2 size) {
    vec4 packed = texelFetch(normal_roughness, clamp(pixel, ivec2(0), size-1), ink_sample);
    // Forward+'s raw normal buffer encodes roughness and the dynamic flag in A.
    float rough = packed.a > .5 ? 1.0-packed.a : packed.a;
    return vec4(normalize(packed.xyz*2.0-1.0)*.5+.5, rough*(255.0/127.0));
}
#include "world_ink_sampling.gdshaderinc"
void main() {
    ivec2 pixel=ivec2(gl_GlobalInvocationID.xy), size=ivec2(params.output_info.xy);
    if(any(greaterThanEqual(pixel,size))) return;
    float coverage=0.0;
    for(ink_sample=0;ink_sample<int(params.output_info.w);ink_sample++) {
        float depth=ink_depth(pixel,size,mat4(1.0));
        if(depth>=1e8) continue;
        float footprint=params.camera.x * (params.camera.w>.5 ? 1.0 : depth);
        float depth_tolerance=max(params.camera.y,depth*.0000005);
        coverage+=ink_contour(pixel,size,depth,footprint,depth_tolerance,mat4(1.0));
    }
    imageStore(coverage_image,pixel,vec4(coverage/params.output_info.w));
}
