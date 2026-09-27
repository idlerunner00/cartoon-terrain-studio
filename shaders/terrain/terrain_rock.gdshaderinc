// One rock material across crowns, exposed faces and their foot. Compile units: 25/metre.
// Only pigment and a shallow height field; comic_style owns every lighting and ink decision.
// STUDIO (Cartoon Terrain Studio): the material's key constants as live uniforms; defaults are the original values.
/// (large variation, mineral grain, bed contrast, joint depth)
uniform vec4 studio_rock_detail = vec4(0.16, 0.13, 0.34, 0.22);
/// size multipliers (large field, mineral grain, bed thickness, growth patches)
uniform vec4 studio_rock_scale = vec4(1.0, 1.0, 1.0, 1.0);
/// (crest growth coverage shift −0.3..0.3, cliff reflectance, unused, unused)
uniform vec4 studio_rock_growth = vec4(0.0, 1.38, 0.0, 0.0);
vec3 fluitownRockMaterial(vec3 base, vec3 position, vec4 ground, float kind, out float height) {
    vec2 p = position.xz;
    bool cap = kind < 1.5;
    // Loose fragments use the cap's mineral grain, with ground.y = -1. They inherit the wall's reflectance,
    // but are not grassy mountain crowns. Keep this semantic in the existing material and opaque pass.
    bool fragment = cap && ground.y < -0.5;
    float footprint = max(length(dFdx(position)), length(dFdy(position))) * fluitown_lod_scale;
    float detail = 1.0 - smoothstep(1.2, 4.0, footprint);
    float land = mmoratMaterialGradientNoise(p / (190.0 * studio_rock_scale.x) + vec2(2.9, -5.4));
    // The same vegetation field reaches over a crown. No continuous green stripe around every wall.
    float growth = mmoratMaterialGradientNoise(p / (73.0 * studio_rock_scale.w) + vec2(8.1, 3.7)) * 0.72
        + mmoratMaterialGradientNoise(p / (24.0 * studio_rock_scale.w) - vec2(2.1, 9.8)) * 0.28;
    float growth_aa = max(fwidth(growth) * fluitown_lod_scale, 0.015);
    float mantle = fluitown_mountain ? smoothstep(0.50 - studio_rock_growth.x - growth_aa, 0.61 - studio_rock_growth.x + growth_aa, growth) : 0.0;
    if (fragment) mantle *= 0.08;
    vec2 wall_uv = vec2(dot(p, vec2(0.79, 0.61)), position.y);
    vec2 material_uv = cap ? p : wall_uv;
    float mineral = mmoratMaterialGradientNoise(material_uv / (vec2(34.0, 19.0) * studio_rock_scale.y) + land * 2.0);
    // Walls' ported pigment was authored for a luminous fill. Re-author its reflectance for the common light.
    vec3 pigment = base * (cap && !fragment ? 1.0 : studio_rock_growth.y) * (0.98 + (land - 0.5) * studio_rock_detail.x + (mineral - 0.5) * studio_rock_detail.y);
    height = 0.5 + (mineral - 0.5) * 0.28 * detail;

    if (!cap && fluitown_mountain) {
        // Metre-scale beds of unequal thickness, with sparse exposed joints rather than ladder rungs.
        float warp = mmoratMaterialGradientNoise(vec2(position.y / 110.0, dot(p, vec2(0.0023, 0.0017))));
        float bed = (position.y + dot(p, vec2(0.13, 0.08)) + (land - 0.5) * 30.0) / (52.0 * studio_rock_scale.z) + warp * 0.72;
        float bed_id = floor(bed);
        float bed_t = fract(bed);
        vec3 bed_seed = fluitownUnpack3(fluitownHash3(ivec3(int(bed_id), 71, 29)));
        float aa = min(max(fwidth(bed) * fluitown_lod_scale, 0.003), 0.25);
        float resolved = 1.0 - smoothstep(0.12, 0.4, aa);
        float edge = min(bed_t, 1.0 - bed_t);
        float joint = (1.0 - smoothstep(0.015, 0.035 + aa, edge)) * resolved;
        float exposed = 0.25 + 0.75 * smoothstep(0.38, 0.66, mineral);
        // Fade each bed's tint at its boundaries, so there is no discontinuity under minification.
        float bed_body = smoothstep(0.0, 0.1 + aa, edge) * resolved;
        pigment *= 1.0 + (bed_seed.x - 0.5) * studio_rock_detail.z * bed_body - joint * exposed * studio_rock_detail.w;
        pigment *= mix(vec3(1.0), mix(vec3(0.97, 1.0, 1.03), vec3(1.035, 1.0, 0.96), bed_seed.y), bed_body);
        height -= joint * exposed * 0.07 * detail;
        float foot = 1.0 - smoothstep(2.0, 38.0, max(ground.z, 0.0));
        float damp = foot * smoothstep(0.33, 0.66, growth);
        pigment *= 1.0 - foot * 0.08;
        vec3 moss = fluitownRegionSplat(fluitown_moss.rgb, 4, p);
        float value = dot(pigment, vec3(0.2126, 0.7152, 0.0722));
        pigment = mix(pigment, moss * (0.65 + value * 1.3), damp * fluitown_moss.a * 0.45);
        float drop = max(ground.y, 0.0);
        float reach = 3.0 + mantle * 13.0;
        mantle *= 1.0 - smoothstep(reach * 0.35, reach, drop);
    }
    vec3 crown = fluitownRegionSplat(fluitown_cap_grass.rgb, 5, p);
    pigment = mix(pigment, crown * (0.94 + (mineral - 0.5) * 0.12), mantle * fluitown_cap_grass.a * 0.64);
    pigment *= mix(vec3(1.0), vec3(0.9, 0.925, 0.95), clamp(uMmoratWeatherSurface.x, 0.0, 1.0));
    if (cap) {
        float snow = smoothstep(0.76 - uMmoratWeatherSnow * 0.7, 0.86 - uMmoratWeatherSnow * 0.54, growth)
            * clamp(uMmoratWeatherSnow * 1.15, 0.0, 0.94);
        pigment = mix(pigment, mix(vec3(0.9, 0.925, 0.94), uMmoratHaze, 0.08), snow);
    }
    // The drifting cloud pigment has the same world-space phase on soil and exposed rock.
    if (uMmoratWeatherSurface.y > 0.0015) {
        vec2 drift = uMmoratTerrainWindDirection * uMmoratTerrainTime * (6.0 + uMmoratWeatherSurface.w * 15.0);
        float cloud = mmoratMaterialGradientNoise((p - drift) / 560.0 + vec2(3.1));
        pigment *= 1.0 - smoothstep(0.44, 0.8, cloud) * uMmoratWeatherSurface.y * 0.22;
    }
    return max(pigment, vec3(0.0));
}
