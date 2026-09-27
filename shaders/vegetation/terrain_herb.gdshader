// Fluitown comic look: wildflowers and wall plants of the terrain (hand-written). Smooth, two-sided low-poly herbs (HerbMesher): the terrain's wind, the Flui and its companion brushing
// through them, a collapse before the draw distance, and godot-flui's comic surface with the foliage role — petals
// glow a little against the sun.
//
// CUSTOM0 = (wind weight, phase — negative: not brushed aside —, substance: 1 green, 2 petal, 3 flower centre, 4 wood,
// 5 thin stalk, 6 shell leaf of a crown; height above the plant's foot).
// UV2 = the plant's foot (world x, z). COLOR = sRGB pigment.
shader_type spatial;
#define COMIC_DEFAULT_ROLE 2
#include "res://assets/shaders/comic_style.gdshaderinc"
#include "res://shaders/studio/studio_grade.gdshaderinc"
// Terrain pixels (stencil 2) for the terrain occlusion effect only; world ink is shared.
stencil_mode write, compare_always, 2;
render_mode cull_disabled, diffuse_burley, specular_disabled;
#include "res://shaders/vegetation/vegetation_air.gdshaderinc"

uniform vec3 explorer = vec3(0.0, -10000.0, 0.0);
uniform vec3 companion = vec3(0.0, -10000.0, 0.0);
/// Flui perspective: herbs shrink into their foot before this distance (m); the bird's-eye view sets it far away.
uniform float draw_distance = 1.0e7;
/// The leaves of a crown's shell shrink away sooner: beyond this the crown alone reads (and costs less).
uniform float shell_distance = 1.0e7;

varying vec3 herb_world;
varying float herb_substance;

vec2 brush(vec3 foot, vec3 body, float lift, inout float bow) {
	vec3 near = foot - body;
	float contact = (1.0 - smoothstep(0.18, 0.75, length(near.xz))) * (1.0 - smoothstep(0.6, 2.0, abs(near.y)));
	bow = max(bow, contact);
	return normalize(near.xz + vec2(0.001)) * contact * 0.32 * min(1.0, lift * 2.5);
}

void vertex() {
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float weight = CUSTOM0.x;
	float phase = CUSTOM0.y;
	herb_substance = CUSTOM0.z;
	float lift = CUSTOM0.w;
	vec3 foot = vec3(UV2.x, world.y - lift, UV2.y);
	float range = herb_substance > 5.5 ? shell_distance : draw_distance;
	float reach = 1.0 - smoothstep(range - 6.0, range, distance(foot, CAMERA_POSITION_WORLD));
	if (reach > 0.0) {
		// The terrain's wind (terrain_plant.gdshader): carrier, secondary and travelling gust fronts, in compile space.
		vec3 p = (foot - fluitown_compile_offset) * fluitown_compile_scale;
		float tempo = max(0.05, uMmoratTerrainWind.z);
		float t = uMmoratTerrainTime;
		float carrier = sin(t * tempo * 1.36 + p.x * 0.0085 + p.z * 0.0052 + phase * 6.2831853);
		float secondary = sin(t * tempo * 2.17 - p.x * 0.0031 + p.z * 0.0074 + phase * 3.1);
		vec2 direction = uMmoratTerrainWindDirection;
		float along = dot(p.xz, direction);
		float across = p.z * direction.x - p.x * direction.y;
		float front_phase = along * 0.0108 + sin(across * 0.0037 + t * tempo * 0.23) * 0.82 - t * tempo * 1.04;
		float wave = 0.5 + 0.5 * sin(front_phase);
		float beat = 0.5 + 0.5 * sin(front_phase * 0.371 + 1.7);
		float front = wave * wave * (0.3 + 0.7 * beat);
		float gust = 0.58 + max(0.0, uMmoratTerrainWind.w) * (0.18 + front * 0.82);
		float strength = clamp(uMmoratTerrainWind.y, 0.0, 2.0);
		float sway = weight * strength * (carrier * 0.72 + secondary * 0.28) * gust;
		// Petals flutter on the swaying head.
		float flutter = herb_substance > 1.5 && herb_substance < 2.5
			? sin(t * 6.1 + dot(world, vec3(9.1, 7.3, 8.7)) + phase * 9.0) * weight * 0.08 : 0.0;
		world.xz += direction * sway + vec2(flutter, -flutter * 0.7);
		world.y -= abs(sway) * 0.15;
		// The Flui and its companion brush the plants aside as they walk through (not the plants on walls: their
		// phase is negative).
		if (CUSTOM0.y >= 0.0) {
			float bow = 0.0;
			vec2 push = brush(foot, explorer, lift, bow) + brush(foot, companion, lift, bow);
			world.xz += push;
			world.y -= bow * lift * 0.28;
		}
	}
	world = mix(foot, world, reach);
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
	herb_world = world;

	COMIC_VERTEX
}

void fragment() {
	// Vertex colours are sRGB pigments of the terrain's world (three.js hex colours).
	vec3 pigment = studio_grade(vegetation_srgb_to_linear(COLOR.rgb), 3); // STUDIO: live colour grade
	bool petal = herb_substance > 1.5 && herb_substance < 2.5;
	// The terrain's pigment convention: three pigments enter the
	// comic ramp divided by π.
	vec3 base = pigment * 0.3183098861837907;
	ALBEDO = base;
	// Stalks, petals and shell leaves share the world's full silhouette ink.
	// Roughness only distinguishes timber's interior creases from smooth foliage.
	ROUGHNESS = herb_substance > 3.5 && herb_substance < 4.5 ? 0.9 : 0.98;
	// Thin petals and leaves let the sun through (the central light()'s foliage back-scatter adds to it).
	BACKLIGHT = base * (petal ? 0.42 : herb_substance < 1.5 || herb_substance > 5.5 ? 0.22 : 0.08);

	COMIC_FRAGMENT

	float haze = vegetation_haze(pigment, herb_world);
	ALBEDO *= 1.0 - haze;
	BACKLIGHT *= 1.0 - haze;
	EMISSION = uMmoratHaze * haze;
}
