#ifndef FLUI_COMIC_STYLE
#define FLUI_COMIC_STYLE
#include "res://assets/shaders/comic_palette.gdshaderinc"

// This is the ONLY surface ink and lighting implementation. Material programs
// supply pigment, geometry, textures and a surface role, never their own light().
#ifndef COMIC_DEFAULT_ROLE
#define COMIC_DEFAULT_ROLE 0
#endif
uniform int comic_role = COMIC_DEFAULT_ROLE; // 0 solid, 1 Flui skin, 2 foliage, 3 water, 4 luminous FX
uniform float comic_highlight = 0.0;
varying vec3 comic_position;
#define COMIC_VERTEX comic_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
#define COMIC_FRAGMENT ALBEDO = comic_surface(ALBEDO, comic_position, normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz), NORMAL, VIEW, comic_role);

float comic_band(float threshold, float value) {
    float aa = max(fwidth(value) * 0.65, 0.018);
    return smoothstep(threshold - aa, threshold + aa, value);
}
float comic_shade(float facing) {
    return 0.035 + 0.22 * comic_band(0.12, facing)
        + 0.38 * comic_band(0.40, facing) + 0.365 * comic_band(0.72, facing);
}
#include "res://assets/shaders/comic_sampling.gdshaderinc"
float comic_hatch(vec3 point, float shadow) {
    float first = comic_line(dot(point, vec3(1.0, 1.35, 0.72)));
    float crossing = comic_line(dot(point, vec3(-0.8, 1.25, 1.0)) + 0.31);
    return max(first * smoothstep(0.25, 0.65, shadow), crossing * smoothstep(0.68, 0.94, shadow));
}
// Keep the argument distinct from the voxel instance uniform `pigment`:
// Godot 4.5 substitutes that uniform even inside a same-named function argument.
vec3 comic_surface(vec3 comic_base_pigment, vec3 point, vec3 form_normal, vec3 normal, vec3 view, int role) {
    bool skin = role == 1;
    float shadow = 1.0 - smoothstep(skin ? -0.25 : -0.15, skin ? 0.55 : 0.62,
        dot(form_normal, normalize(vec3(-0.45, 0.78, 0.43))));
    float scale = skin ? 85.0 : 5.0;
    float hatch_weight = role == 2 ? 0.0 : role >= 3 ? 0.08 : skin ? 0.20 : 0.30;
    float hatch = comic_hatch(point * scale, shadow) * hatch_weight;
    float underside = (1.0 - smoothstep(-0.70, -0.08, form_normal.y))
        * (skin ? 0.0 : role == 2 ? 0.20 : role >= 3 ? 0.12 : 0.60);
    float rim = skin ? (1.0 - smoothstep(0.035, 0.14, abs(dot(normalize(normal), normalize(view))))) * 0.88 : 0.0;
    return mix(comic_base_pigment, COMIC_INK, max(max(hatch, underside), rim));
}
void light() {
    float facing = max(dot(NORMAL, LIGHT), 0.0);
    // Godot applies ALBEDO after accumulation. Every material uses this one ramp.
    DIFFUSE_LIGHT += LIGHT_COLOR * ATTENUATION * comic_shade(facing) / 3.14159265;
    if (comic_role == 2) DIFFUSE_LIGHT += LIGHT_COLOR * ATTENUATION
        * pow(max(dot(-LIGHT, VIEW), 0.0), 3.0) * 0.10 / 3.14159265;
    if (comic_highlight > 0.0) {
        vec3 halfway = normalize(LIGHT + VIEW);
        SPECULAR_LIGHT += LIGHT_COLOR * ATTENUATION
            * pow(max(dot(NORMAL, halfway), 0.0), 48.0) * comic_highlight;
    }
}
#endif
