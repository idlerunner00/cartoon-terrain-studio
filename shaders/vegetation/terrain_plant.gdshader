// Fluitown comic look: trees and bushes of the terrain (hand-written). godot-flui's low-poly landscape material for timber and foliage (low_poly_landscape.gdshader: leaf,
// blossom and bark pigment, the comic ramp with role 2 on foliage, back light, the foliage roughness the world ink
// reads) with the terrain's wind and distance haze. Geometry: PlantMesher (flat-shaded facets, world metres).
shader_type spatial;
#define COMIC_DEFAULT_ROLE 0
#include "res://assets/shaders/comic_style.gdshaderinc"
#include "res://shaders/studio/studio_grade.gdshaderinc"
// Terrain pixels (stencil 2) for the terrain occlusion effect only; world ink is shared.
stencil_mode write, compare_always, 2;
render_mode cull_back, diffuse_burley, specular_disabled;
#include "res://assets/shaders/world_palette.gdshaderinc"
#include "res://shaders/vegetation/vegetation_air.gdshaderinc"

varying vec3 plant_world;
varying vec3 plant_normal;
varying float plant_substance;

void vertex() {
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float weight = CUSTOM0.x;
	float phase = CUSTOM0.y;
	plant_substance = CUSTOM0.z;
	if (weight > 0.0) {
		// The terrain's wind (terrain_surface_comic.gdshader): carrier, secondary and travelling gust fronts, in its
		// compile space so plants move with the grass and flowers of the ground.
		vec3 p = (world - fluitown_compile_offset) * fluitown_compile_scale;
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
		// Leaves flutter a little on top of the swaying limbs.
		float flutter = plant_substance > 6.5 ? sin(t * 5.3 + dot(world, vec3(3.1, 2.3, 2.7)) + phase * 9.0) * weight * 0.12 : 0.0;
		world.xz += direction * sway + vec2(flutter, -flutter * 0.6);
		world.y -= abs(sway) * 0.12;
	}
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
	plant_world = world;
	plant_normal = normalize(MODEL_NORMAL_MATRIX * NORMAL);

	COMIC_VERTEX
}

void fragment() {
	bool foliage = plant_substance > 6.5 && plant_substance < 7.5;
	// Vertex colours are sRGB pigments of the terrain's world (three.js hex colours).
	vec3 pigment = vegetation_srgb_to_linear(COLOR.rgb);
	vec3 n = normalize(plant_normal);
	// godot-flui's world pigments: blossom cells on reddish foliage, leaf cells with a vein on green, bark fissures.
	if (foliage) pigment = pigment.r > pigment.g * 1.08 ? blossom_pigment(pigment, plant_world, n) : leaf_pigment(pigment, plant_world, n);
	else pigment = bark_pigment(pigment, plant_world);
	// The terrain's pigment convention: three pigments enter the
	// comic ramp divided by π, so plants keep the brightness of the ground they grow on under the same sun.
	// STUDIO: live colour grade (foliage, or bark with the rock walls' grade).
	pigment = studio_grade(pigment, foliage ? 3 : 2);
	vec3 base = pigment * 0.3183098861837907;
	// Shared surface style: foliage role on leaves, solid role on timber.
	ALBEDO = comic_surface(base, comic_position, n, NORMAL, VIEW, foliage ? 2 : 0);
	ROUGHNESS = foliage ? 0.98 : 0.90;
	BACKLIGHT = foliage ? base * 0.18 : vec3(0.0);
	float haze = vegetation_haze(pigment, plant_world);
	ALBEDO *= 1.0 - haze;
	BACKLIGHT *= 1.0 - haze;
	EMISSION = uMmoratHaze * haze;
}
