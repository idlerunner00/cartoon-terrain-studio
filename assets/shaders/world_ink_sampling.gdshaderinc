// One shared contour filter. Geometry is sampled on exact texel centres;
// fractional line widths are coverage, never fractional depth sample offsets.
#ifndef INK_MSAA
uniform float px_scale = 1.0;
uniform bool ink_msaa = false;
uniform sampler2D ink_coverage : repeat_disable, filter_nearest;

vec4 ink_normal(ivec2 pixel, ivec2 size) {
#ifdef INK_DEPTH_ONLY
    return vec4(.5, .5, 1.0, 0.0);
#else
    return texelFetch(normal_roughness, clamp(pixel, ivec2(0), size - ivec2(1)), 0);
#endif
}

float ink_depth(ivec2 pixel, ivec2 size, mat4 inverse_projection) {
    pixel = clamp(pixel, ivec2(0), size - ivec2(1));
    float raw = texelFetch(depth_texture, pixel, 0).r;
    if (raw <= 0.0) return 1e8;
    vec2 uv = (vec2(pixel) + .5) / vec2(size);
    vec4 p = inverse_projection * vec4(uv * 2.0 - 1.0, raw, 1.0);
    return -p.z / max(p.w, .000001);
}

#endif

float ink_break(float center, float a, float b) {
    return max(0.0, max(a, b) - center - max(0.0, center - min(a, b)) * 1.8);
}

float ink_opacity(float roughness) {
    return mix(1.0, .42, smoothstep(.955, .985, roughness))
        * (1.0 - smoothstep(.985, .998, roughness));
}

float ink_bend(ivec2 pixel, ivec2 size, vec3 normal, float center, float neighbour) {
    vec3 n = ink_normal(pixel, size).xyz * 2.0 - 1.0;
    n *= inversesqrt(max(dot(n, n), .000001));
    // A foreground feature owns its crease; don't stamp it onto the background.
    return (1.0 - dot(normal, n)) * step(center - .0005, neighbour);
}

float ink_ring(ivec2 pixel, ivec2 size, int radius, float depth, vec3 normal,
        vec2 threshold, float crease_weight, mat4 inverse_projection) {
    ivec2 x = ivec2(radius, 0), y = ivec2(0, radius);
    float right = ink_depth(pixel + x, size, inverse_projection);
    float left = ink_depth(pixel - x, size, inverse_projection);
    float down = ink_depth(pixel + y, size, inverse_projection);
    float up = ink_depth(pixel - y, size, inverse_projection);
    float edge = smoothstep(threshold.x, threshold.y,
        max(ink_break(depth, right, left), ink_break(depth, down, up)));
    if (crease_weight > .001 && edge < .95) {
        float bend = max(max(ink_bend(pixel + x, size, normal, depth, right),
                             ink_bend(pixel - x, size, normal, depth, left)),
                         max(ink_bend(pixel + y, size, normal, depth, down),
                             ink_bend(pixel - y, size, normal, depth, up)));
        edge = max(edge, smoothstep(.20, .42, bend) * .80 * crease_weight);
    }
    return edge;
}

// Identical contour rules for native depth and each individual MSAA sample.
float ink_contour(ivec2 pixel, ivec2 size, float depth, float footprint, float depth_tolerance, mat4 inverse_projection) {
    float line = clamp(width / (215.0 * footprint), 1.0, width) * px_scale;
    int inner = int(max(1.0, floor(line)));
    float outer_weight = max(0.0, line - float(inner));
    float coverage = min(1.0, line);

    vec4 packed_normal = ink_normal(pixel, size);
    float roughness = packed_normal.a;
    if (roughness >= .998) return 0.0;
    float foliage = smoothstep(.955, .985, roughness);
    vec3 normal = packed_normal.xyz * 2.0 - 1.0;
    normal *= inversesqrt(max(dot(normal, normal), .000001));
    float crease = (1.0 - smoothstep(.008, .028, footprint)) * (1.0 - foliage);
#ifdef INK_DEPTH_ONLY
    crease = 0.0;
#endif
    // Filter detail by its projected footprint and account for depth precision.
    // A centimetre threshold across a whole distant valley draws quantization.
    vec2 threshold = vec2(max(.008, footprint * .25), max(.024, footprint * .65)) + depth_tolerance;
    float edge = ink_ring(pixel, size, inner, depth, normal, threshold, crease, inverse_projection);
    if (outer_weight > .001)
        edge = mix(edge, max(edge, ink_ring(pixel, size, inner + 1, depth, normal, threshold, crease, inverse_projection)), outer_weight);
    edge *= ink_opacity(roughness);
    return edge * coverage;
}

#ifndef INK_MSAA
void vertex() { POSITION = vec4(VERTEX.xy, 1.0, 1.0); }

void fragment() {
    ivec2 size = textureSize(depth_texture, 0);
    ivec2 pixel = clamp(ivec2(SCREEN_UV * vec2(size)), ivec2(0), size - ivec2(1));
    float raw = texelFetch(depth_texture, pixel, 0).r;
    if (raw <= 0.0) discard;
    vec2 uv = (vec2(pixel) + .5) / vec2(size);
    vec4 p = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, raw, 1.0);
    vec3 view_point = p.xyz / max(p.w, .000001);
    float depth = -view_point.z;
    bool orthographic = PROJECTION_MATRIX[3][3] > .5;
    vec3 world_point = (INV_VIEW_MATRIX * vec4(view_point, 1.0)).xyz;
    float visibility = 1.0;
    if (distance_fog.y > distance_fog.x)
        visibility = 1.0 - smoothstep(distance_fog.x, distance_fog.y,
            length(world_point.xz - INV_VIEW_MATRIX[3].xz));
    if (water_cutoff) visibility *= smoothstep(-.03, .10, world_point.y);
    if (visibility <= 0.0) discard;

    // Metres per OUTPUT pixel, independent of render scale and camera distance
    // in orthographic views. The authored line is 1 cm, capped at close-up weight.
    float footprint = 2.0 * (orthographic ? 1.0 : depth) * px_scale
        / (abs(PROJECTION_MATRIX[1][1]) * float(size.y));
    float depth_tolerance = max(abs(INV_PROJECTION_MATRIX[2][2]) * .00000025, depth * .0000005);
    float edge = ink_msaa ? texelFetch(ink_coverage, pixel, 0).r
        : ink_contour(pixel, size, depth, footprint, depth_tolerance, INV_PROJECTION_MATRIX);
    if (edge <= 0.0) discard;
    ALBEDO = COMIC_INK;
    // Preserve the reference's softer foliage ink: subpixel blades must not
    // turn into a black mesh. All surfaces still use this one contour filter.
    ALPHA = edge * .985 * visibility;
}

#endif
