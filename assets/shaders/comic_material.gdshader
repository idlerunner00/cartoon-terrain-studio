shader_type spatial;
render_mode
#ifdef COMIC_UNLIT
unshaded,
#endif
#ifdef COMIC_DOUBLE_SIDED
cull_disabled,
#elif defined(COMIC_CULL_FRONT)
cull_front,
#else
cull_back,
#endif
#ifdef COMIC_DEPTH_PREPASS
depth_prepass_alpha,
#endif
#ifdef COMIC_NO_DEPTH_TEST
depth_test_disabled,
#endif
#ifdef COMIC_ADDITIVE
blend_add,
#endif
diffuse_burley;
#include "res://assets/shaders/comic_style.gdshaderinc"

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic;
uniform sampler2D roughness_texture : hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D metallic_texture : hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D ao_texture : hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D emission_texture : source_color, hint_default_white, filter_linear_mipmap_anisotropic;
uniform vec3 emission_color : source_color = vec3(0.0);
uniform float emission_energy = 1.0;
uniform float roughness = 0.85;
uniform float metallic = 0.0;
uniform float specular = 0.12;
uniform bool vertex_color_enabled = false;
uniform bool vertex_color_srgb = false;
uniform bool normal_enabled = false;
uniform float normal_scale = 1.0;
uniform bool ao_enabled = false;
uniform float ao_light_affect = 0.0;
uniform bool emission_enabled = false;
uniform bool emission_add_texture = false;
uniform float alpha_scissor = 0.5;
uniform vec4 roughness_channel = vec4(1.0,0.0,0.0,0.0);
uniform vec4 metallic_channel = vec4(1.0,0.0,0.0,0.0);
uniform vec4 ao_channel = vec4(1.0,0.0,0.0,0.0);
uniform vec3 uv_scale = vec3(1.0);
uniform vec3 uv_offset = vec3(0.0);

void vertex() {
    COMIC_VERTEX
}
void fragment() {
    vec2 uv = UV * uv_scale.xy + uv_offset.xy;
    vec4 pigment = albedo_color * texture(albedo_texture, uv);
    if (vertex_color_enabled) {
        vec3 vertex_pigment = vertex_color_srgb ? mix(COLOR.rgb / 12.92, pow((COLOR.rgb + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), COLOR.rgb)) : COLOR.rgb;
        pigment *= vec4(vertex_pigment, COLOR.a);
    }
    ALBEDO = pigment.rgb;
    ROUGHNESS = clamp(roughness * dot(texture(roughness_texture, uv), roughness_channel), 0.05, 1.0);
    METALLIC = metallic * dot(texture(metallic_texture, uv), metallic_channel);
    SPECULAR = specular;
    if (normal_enabled) { NORMAL_MAP = texture(normal_texture, uv).rgb; NORMAL_MAP_DEPTH = normal_scale; }
    if (ao_enabled) { AO = dot(texture(ao_texture, uv), ao_channel); AO_LIGHT_AFFECT = ao_light_affect; }
    if (emission_enabled) {
        vec3 emission_map = texture(emission_texture, uv).rgb;
        EMISSION = (emission_add_texture ? emission_color + emission_map : emission_color * emission_map) * emission_energy;
    }
#ifdef COMIC_ALPHA_SCISSOR
    if (pigment.a < alpha_scissor) discard;
#elif defined(COMIC_ALPHA_HASH)
    if (pigment.a < fract(52.9829189 * fract(dot(floor(FRAGCOORD.xy), vec2(0.06711056,0.00583715))))) discard;
#elif defined(COMIC_ALPHA)
    ALPHA = pigment.a;
#endif
#ifdef COMIC_DOUBLE_SIDED
    if (!FRONT_FACING) NORMAL = -NORMAL;
#endif
    COMIC_FRAGMENT
}
