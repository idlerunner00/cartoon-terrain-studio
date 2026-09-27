// Ground pigment and shallow relief. Lighting and ink stay in comic_style.gdshaderinc.
// Compile coordinates (25 units/metre); the baked cover/wear remain authoritative for plants and paths.
// STUDIO (Cartoon Terrain Studio): the material's key constants as live uniforms; defaults are the original values.
/// (dry-soil mix, turf mix, wear darkening, broad contrast)
uniform vec4 studio_ground_mix = vec4(0.16, 0.5, 0.075, 0.22);
/// (brush strokes, fine grain, stones, meadow fibre)
uniform vec4 studio_ground_detail = vec4(0.07, 0.065, 0.7, 0.11);
/// size multipliers (broad field, brush strokes, fine grain, stones & chips)
uniform vec4 studio_ground_scale = vec4(1.0, 1.0, 1.0, 1.0);
vec3 fluitownGroundMaterial(vec3 base, vec3 position, vec4 ground, out float height) {
    vec2 p = position.xz;
    float footprint = max(length(dFdx(p)), length(dFdy(p))) * fluitown_lod_scale;
    // Metre-scale strokes are still resolved from above. The former grain-sized cutoff erased
    // all of them at ordinary gameplay zoom and left enormous featureless sand-coloured patches.
    float meso_resolve = 1.0 - smoothstep(4.0, 16.0, footprint);
    float fine_resolve = 1.0 - smoothstep(0.45, 1.5, footprint);
    float broad = mmoratMaterialGradientNoise(p / (260.0 * studio_ground_scale.x) + vec2(7.2, -3.1));
    vec2 warp = vec2(broad - 0.5, 0.5 - broad);
    vec2 brush_uv = vec2(dot(p, vec2(0.8, 0.6)), dot(p, vec2(-0.6, 0.8)));
    float meso = mmoratMaterialGradientNoise(brush_uv / (vec2(34.0, 17.0) * studio_ground_scale.y) + warp * 0.8);
    float grain = mmoratMaterialGradientNoise(p / (2.8 * studio_ground_scale.z) + vec2(13.7, 4.1));
    float cover = clamp(ground.x, 0.0, 1.0);
    // Perturb only the transition: fully bare paths and fully grown meadow retain their habitat.
    cover = clamp(cover + (meso - 0.5) * min(cover, 1.0 - cover) * 0.45, 0.0, 1.0);
    float wear = clamp(ground.y, 0.0, 1.0);
    vec3 lush = fluitownRegionSplat(uMmoratFloorSplatLush, 0, p);
    vec3 turf = fluitownRegionSplat(uMmoratFloorTurf, 3, p);
    vec3 dry = fluitownRegionSplat(uMmoratFloorSplatDry, 1, p);
    vec3 pigment = mix(base, dry, (1.0 - cover) * studio_ground_mix.x);
    pigment = mix(pigment, lush, cover * uMmoratTerrainMaterialB.x);
    pigment = mix(pigment, turf, cover * studio_ground_mix.y);
    // A shoulder is a continuous material transition, never a threshold of a cell's wear value.
    pigment *= 1.0 - wear * studio_ground_mix.z;
    // Broad brush shapes read as compacted soil rather than a blurred noise texture.
    // Their edges soften with footprint, including a grazing view of the ground.
    float brush_aa = max(fwidth(meso) * fluitown_lod_scale, 0.035);
    float brush = smoothstep(0.48 - brush_aa, 0.55 + brush_aa, meso) - 0.5;
    float brush_group = smoothstep(0.32, 0.70, broad);
    pigment *= 1.0 + (broad - 0.5) * studio_ground_mix.w + brush * studio_ground_detail.x * meso_resolve * brush_group
        + (grain - 0.5) * studio_ground_detail.y * fine_resolve;
    // Small dry shelves, cool soil and warmer worn patches break up an open clearing at every zoom.
    // They share the existing field, so this adds no extra noise lookup or draw call.
    float exposed = (1.0 - cover) * (1.0 - wear * 0.65);
    pigment *= mix(vec3(0.965, 0.982, 1.01), vec3(1.025, 1.007, 0.973),
        smoothstep(0.30, 0.70, meso * 0.35 + broad * 0.65)) * exposed + vec3(1.0 - exposed);
    pigment *= mix(uMmoratDriftA, uMmoratDriftB, broad);
    height = 0.5 + (meso - 0.5) * 0.38 * meso_resolve
        + (grain - 0.5) * 0.16 * fine_resolve;

    if (fluitown_ground_paint) {
        // Sparse embedded stone groups, large enough to read from the overhead camera.
        // Cell interiors contain the complete marks; footprint filtering prevents distant sparkles.
        vec2 stones = mat2(vec2(0.8, -0.6), vec2(0.6, 0.8)) * p / (42.0 * studio_ground_scale.w);
        vec3 stone_seed = fluitownUnpack3(fluitownHash3(ivec3(ivec2(floor(stones)), 193)));
        vec2 stone_local = (fract(stones) - 0.3 - stone_seed.xy * 0.4) * vec2(1.0, 1.4);
        float stone_edge = max(abs(stone_local.x) * 0.82 + abs(stone_local.y) * 0.45, abs(stone_local.y));
        float stone_radius = 0.075 + stone_seed.y * 0.09;
        float stone_aa = min(max(footprint / 42.0, 0.002), 0.06);
        float stone_resolve = 1.0 - smoothstep(2.2, 6.0, footprint);
        float stone_group = smoothstep(0.40, 0.64, meso) * smoothstep(0.50, 0.72, stone_seed.z) * exposed * stone_resolve;
        float stone = (1.0 - smoothstep(stone_radius - stone_aa, stone_radius + stone_aa, stone_edge)) * stone_group;
        float rim = (1.0 - smoothstep(stone_radius + stone_aa, stone_radius + 2.5 * stone_aa, stone_edge)) * stone_group - stone;
        pigment = mix(pigment * (1.0 - rim * 0.12), pigment * 1.13 + vec3(0.025, 0.023, 0.018), stone * studio_ground_detail.z);
        // Flattened, irregular mineral chips, with a resolved bevel instead of painted circular dots.
        // Centres stay well inside cells, including the support of the AA filter, so no grid edges appear.
        vec2 q = mat2(vec2(0.8, -0.6), vec2(0.6, 0.8)) * p / (12.0 * studio_ground_scale.w);
        vec3 seed = fluitownUnpack3(fluitownHash3(ivec3(ivec2(floor(q)), 77)));
        vec2 local = (fract(q) - vec2(0.3) - seed.xy * 0.4) * vec2(1.0, 1.45);
        float radius = 0.08 + seed.z * 0.085;
        float edge = max(abs(local.x) * 0.86 + abs(local.y) * 0.5, abs(local.y));
        float aa = min(max(footprint / 12.0, 0.001), 0.08);
        float chip = (1.0 - smoothstep(radius - aa, radius + aa, edge))
            * smoothstep(0.66, 0.88, seed.z) * (1.0 - cover) * fine_resolve;
        pigment *= 1.0 + chip * (0.055 + seed.x * 0.07);
        // Rounded embedded chips contribute to the shared relief normal, rather than flat pale polka dots.
        float bevel = 1.0 - smoothstep(radius * 0.1, radius + aa, edge);
        height += bevel * smoothstep(0.66, 0.88, seed.z) * (1.0 - cover) * fine_resolve * 0.16;
        // Low-contrast directional fibres keep the meadow readable after its geometric tufts resolve away.
        float fibre = mmoratMaterialGradientNoise(vec2(p.x * 0.09 + p.y * 0.045, p.y * 0.26 - p.x * 0.13));
        pigment *= 1.0 + (fibre - 0.5) * cover * studio_ground_detail.w * fine_resolve;
    }
    // Wetness and snow use the same low-frequency material field in both projections.
    float wet = clamp(uMmoratWeatherSurface.x, 0.0, 1.0);
    pigment *= mix(vec3(1.0), vec3(0.88, 0.91, 0.94), wet);
    float snow = smoothstep(0.76 - uMmoratWeatherSnow * 0.7,
        0.86 - uMmoratWeatherSnow * 0.54, broad * 0.7 + meso * 0.3)
        * clamp(uMmoratWeatherSnow * 1.15, 0.0, 0.94);
    pigment = mix(pigment, mix(vec3(0.9, 0.925, 0.94), uMmoratHaze, 0.08), snow);
    if (uMmoratWeatherSurface.y > 0.0015) {
        vec2 drift = uMmoratTerrainWindDirection * uMmoratTerrainTime * (6.0 + uMmoratWeatherSurface.w * 15.0);
        float cloud = mmoratMaterialGradientNoise((p - drift) / 560.0 + vec2(3.1));
        pigment *= 1.0 - smoothstep(0.44, 0.8, cloud) * uMmoratWeatherSurface.y * 0.22;
    }
    return max(pigment, vec3(0.0));
}
