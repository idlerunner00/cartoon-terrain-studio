// Fluitown comic look: water lilies, reeds and water grass (hand-written). One material for every mesh (InstancedPlants.cs); the part is COLOR.a:
//   0 lily pad, 0.25 lily flower, 0.5 reed clump, 0.75 water grass tuft, 0.9 hanging vine or roots.
// Pads and flowers drift on a slow loop around their anchor, turn a little, bob with the water and slide aside around
// the Flui and its companion; reeds and grass bend with the terrain's wind (its carrier, secondary and gust fronts, like
// the trees) and part around the walkers like the meadow. Pigment: the terrain's convention (three pigment / π) through
// the central comic surface, mixed back like the rest of the terrain, and the terrain's distance haze.
//
// INSTANCE_CUSTOM = (pigment A, pigment B, pigment C — 24-bit sRGB hex values — , variation·255 + phase). A negative
// pigment C (-hex - 1) collapses the optional part: a pad's flower, a reed clump's cattails; on a hanging plant it
// shows the roots (COLOR.b = 1) instead of the leafy vine.
//   pad: A leaf, B leaf shade, C blossom          reed: A stem, B root, C cattail head
//   flower: A leaf, B leaf shade, C blossom       grass: A blade, B root, C tip
// COLOR.r: pad — vein/rim shade; flower — 0 petal, 1 heart; reed — 0 blade, 1 cattail head, 0.5 stalk.
// COLOR.g: stem id (0..1) of a reed or blade. UV.y: height along a blade (0 root, 1 tip); pad: UV = (angle/τ, radius).
shader_type spatial;
#define COMIC_DEFAULT_ROLE 2
#include "res://assets/shaders/comic_style.gdshaderinc"
#include "res://shaders/studio/studio_grade.gdshaderinc"
// Terrain pixels (stencil 2) for the terrain occlusion effect only; world ink is shared.
stencil_mode write, compare_always, 2;
render_mode cull_disabled, specular_disabled;
#include "res://shaders/vegetation/vegetation_air.gdshaderinc"

uniform vec3 explorer = vec3(0.0, -10000.0, 0.0);
uniform vec3 companion = vec3(0.0, -10000.0, 0.0);

varying float part;
varying float plant_height;
varying vec2 plant_uv;
varying vec4 plant_tag;
varying vec3 pigment_a;
varying vec3 pigment_b;
varying vec3 pigment_c;
varying float variation;
varying vec3 plant_world;

vec2 rotate2(vec2 v, float a) {
	float c = cos(a), s = sin(a);
	return vec2(c * v.x - s * v.y, s * v.x + c * v.y);
}

/// A floating leaf pushed aside by a body wading through: away from it, fading from 0.55 m to 1.25 m off its centre.
vec2 wade(vec2 at, vec3 anchor, vec3 body, inout float sink) {
	vec2 away = at - body.xz;
	float d = length(away);
	float near = (1.0 - smoothstep(0.35, 1.25, d)) * (1.0 - smoothstep(0.8, 1.8, abs(anchor.y - body.y)));
	sink = max(sink, near);
	return away / max(d, 0.001) * near * 0.42;
}

/// A stem bent aside by a body brushing through it (the meadow's contact bend, wider for reeds).
vec2 brush(vec3 root, vec3 body, inout float flatten) {
	vec3 near = root - body;
	float contact = (1.0 - smoothstep(0.25, 1.2, length(near.xz))) * (1.0 - smoothstep(0.8, 2.0, abs(near.y)));
	flatten = max(flatten, contact * 0.5);
	return normalize(near.xz + vec2(0.001)) * contact * 0.8;
}

void vertex() {
	part = COLOR.a;
	plant_tag = COLOR;
	plant_uv = UV;
	plant_height = UV.y;
	// The 256 bit of the variation code picks the second part of a shared mesh (water grass on the reed mesh).
	float code = floor(INSTANCE_CUSTOM.w);
	bool second = code >= 256.0;
	variation = mod(code, 256.0) / 255.0;
	float phase = fract(INSTANCE_CUSTOM.w);
	pigment_a = vegetation_srgb_to_linear(vegetation_unpack_hex(INSTANCE_CUSTOM.x));
	pigment_b = vegetation_srgb_to_linear(vegetation_unpack_hex(INSTANCE_CUSTOM.y));
	bool alternate = INSTANCE_CUSTOM.z < 0.0;
	pigment_c = vegetation_srgb_to_linear(vegetation_unpack_hex(alternate ? -INSTANCE_CUSTOM.z - 1.0 : INSTANCE_CUSTOM.z));
	// The part this plant does not have collapses to its anchor (zero-area triangles).
	bool hidden = part > 0.85 ? (COLOR.b > 0.5) != alternate
		: part > 0.6 ? !second
		: part > 0.4 ? second || (alternate && COLOR.r > 0.25)
		: part > 0.1 && alternate;
	vec3 anchor = MODEL_MATRIX[3].xyz;
	float t = uMmoratTerrainTime;
	vec3 world;
	if (part < 0.4) {
		// Lily pad or flower: a slow Lissajous wander of ~0.1 m around the anchor (the stem below lets it roam that far),
		// a turn of up to ±20°, a bob of a centimetre and a tilt that rolls across the pad with the ripples.
		float tau = 6.2831853;
		float wa = 0.061 + variation * 0.037;
		float wb = 0.047 + fract(phase * 7.3) * 0.029;
		vec2 drift = vec2(sin(t * wa + phase * tau), sin(t * wb + phase * 11.0 + 1.3)) * (0.07 + variation * 0.06);
		// The wind leans the whole colony a little downwind.
		drift += uMmoratTerrainWindDirection * (0.03 + 0.03 * sin(t * 0.13 + phase * 5.0)) * clamp(uMmoratTerrainWind.y, 0.0, 2.0);
		float turn = sin(t * (0.05 + variation * 0.03) + phase * 17.0) * 0.35;
		float sink = 0.0;
		vec2 centre = anchor.xz + drift;
		drift += wade(centre, anchor, explorer, sink);
		drift += wade(centre, anchor, companion, sink);
		vec3 local = VERTEX;
		local.xz = rotate2(local.xz, turn);
		NORMAL.xz = rotate2(NORMAL.xz, turn);
		world = (MODEL_MATRIX * vec4(local, 1.0)).xyz;
		vec2 rel = world.xz - anchor.xz;
		// Ripples run across the colony (a function of the place, not of the pad), so neighbours rise and fall together.
		float ripple = sin(t * 1.35 + dot(anchor.xz, vec2(0.83, 0.47)) * 2.0) * 0.7 + sin(t * 0.83 - dot(anchor.xz, vec2(-0.41, 0.9)) * 1.6) * 0.3;
		float roll = sin(t * 0.9 + phase * 4.0 + dot(rel, vec2(3.1, 2.3)));
		world.xz += drift;
		world.y += ripple * 0.005 + roll * dot(rel, rel) * 0.03 - sink * 0.012;
	} else if (part > 0.85) {
		// Hanging vine: it swings along the wall (local X) with the square of its depth below the crest, the leaves
		// flutter a little off the face.
		float d = UV.y;
		float strength = clamp(uMmoratTerrainWind.y, 0.0, 2.0);
		float swing = sin(t * (0.7 + variation * 0.3) + phase * 6.2831853 - d * 2.4) * (0.022 + 0.018 * strength);
		vec3 local = VERTEX;
		local.x += swing * d * d;
		local.z += COLOR.r * sin(t * 3.1 + COLOR.g * 40.0) * 0.004 * strength;
		world = (MODEL_MATRIX * vec4(local, 1.0)).xyz;
	} else {
		// Reeds and grass: the terrain's wind (terrain_plant.gdshader) grows with the square of the height along the
		// blade; each stem flutters on its own phase; walkers brush them aside.
		world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		float h = UV.y;
		vec3 p = (anchor - fluitown_compile_offset) * fluitown_compile_scale;
		float tempo = max(0.05, uMmoratTerrainWind.z);
		float carrier = sin(t * tempo * 1.36 + p.x * 0.0085 + p.z * 0.0052 + phase * 6.2831853);
		float secondary = sin(t * tempo * 2.17 - p.x * 0.0031 + p.z * 0.0074 + phase * 3.1);
		vec2 direction = uMmoratTerrainWindDirection;
		float along = dot(p.xz, direction);
		float across = p.z * direction.x - p.x * direction.y;
		float front_phase = along * 0.0108 + sin(across * 0.0037 + t * tempo * 0.23) * 0.82 - t * tempo * 1.04;
		float wave = 0.5 + 0.5 * sin(front_phase);
		float gust = 0.58 + max(0.0, uMmoratTerrainWind.w) * (0.18 + wave * wave * 0.82);
		float strength = clamp(uMmoratTerrainWind.y, 0.0, 2.0);
		float reach = part > 0.6 ? 0.1 : 0.2;
		float sway = strength * (carrier * 0.72 + secondary * 0.28) * gust * reach;
		float flutter = sin(t * (2.1 + COLOR.g * 1.3) + COLOR.g * 23.0 + phase * 9.0) * 0.035;
		vec2 bend = direction * (sway + 0.04 * strength) + vec2(flutter, -flutter * 0.7);
		float flatten = 0.0;
		bend += brush(anchor, explorer, flatten);
		bend += brush(anchor, companion, flatten);
		float height = max(0.1, world.y - anchor.y + 0.001) / max(UV.y, 0.05);
		vec2 offset = bend * h * h * min(height, 1.3);
		world.xz += offset;
		// Keep the blade's length: the tip drops as it bends.
		world.y -= dot(offset, offset) * 0.45 * h + flatten * h * 0.2 * height;
	}
	if (hidden) world = anchor;
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
	plant_world = world;

	COMIC_VERTEX
}

void fragment() {
	vec3 pigment;
	// Blades and reeds keep the same shared silhouette ink in both cameras.
	float roughness = 0.98;
	float backlight = 0.18;
	if (part < 0.1) {
		// Lily pad: radial veins from the heart, a lighter rim band and a shaded fold at the notch.
		float angle = plant_uv.x * 6.2831853;
		float radius = plant_uv.y;
		float veins = 1.0 - smoothstep(0.0, 0.09, abs(fract(plant_uv.x * 17.0) - 0.5) * 2.0 * max(radius, 0.08));
		veins *= smoothstep(0.12, 0.3, radius) * (1.0 - smoothstep(0.82, 0.95, radius));
		float mottle = vegetation_noise(plant_world.xz * 9.0 + variation * 40.0);
		pigment = mix(pigment_a, pigment_b, 0.18 + plant_tag.r * 0.55 + (mottle - 0.5) * 0.25);
		pigment = mix(pigment, pigment_a * 1.18, veins * 0.45);
		pigment = mix(pigment, mix(pigment_a, vec3(0.9, 0.86, 0.55), 0.18), smoothstep(0.9, 0.99, radius) * 0.6);
		roughness = 0.93;
		backlight = 0.1;
	} else if (part < 0.4) {
		// Flower: petals from a deeper base to a pale tip; a golden heart.
		vec3 petal = mix(pigment_c * vec3(0.86, 0.8, 0.84), mix(pigment_c, vec3(1.0), 0.28), smoothstep(0.1, 0.95, plant_uv.y));
		pigment = mix(petal, vec3(0.93, 0.66, 0.16), step(0.5, plant_tag.r));
		roughness = 0.975;
		backlight = 0.3;
	} else if (part < 0.6) {
		// Reed: root shade under the water line to stem to straw tips; cattail heads in their own brown.
		vec3 stalk = mix(pigment_b, pigment_a, smoothstep(0.0, 0.45, plant_height));
		stalk = mix(stalk, mix(pigment_a, vec3(0.78, 0.71, 0.42), 0.55), smoothstep(0.7, 1.0, plant_height) * (0.4 + variation * 0.6));
		pigment = plant_tag.r > 0.75 ? pigment_c * (0.85 + 0.3 * vegetation_noise(plant_world.xz * 40.0 + plant_world.y * 30.0)) : stalk;
		if (plant_tag.r > 0.75) roughness = 0.94;
	} else if (part > 0.85) {
		// Vine: stem (B), leaves (A) with a lighter young tip and a vein, a few tiny blossoms (C).
		float young = smoothstep(0.55, 1.0, plant_height);
		vec3 leaf = mix(pigment_a, mix(pigment_a, vec3(0.72, 0.8, 0.42), 0.35), young) * (0.88 + 0.24 * plant_tag.g);
		pigment = plant_tag.r > 0.75 ? leaf : plant_tag.r > 0.25 ? mix(pigment_c, vec3(1.0), 0.2) : pigment_b;
		roughness = 0.975;
		backlight = 0.12;
	} else {
		// Water grass: root to tip.
		pigment = mix(pigment_b, mix(pigment_a, pigment_c, 0.4 + 0.6 * variation), smoothstep(0.0, 1.0, plant_height));
	}
	// The terrain's pigment convention: three pigments enter the comic ramp divided by π.
	pigment = studio_grade(pigment, 3); // STUDIO: live colour grade
	ALBEDO = pigment * 0.3183098861837907;
	BACKLIGHT = ALBEDO * backlight;
	ROUGHNESS = roughness;
	// Cards lit from both sides: undo Godot's back-face flip (cull_disabled), like the meadow.
	if (!FRONT_FACING) NORMAL = -NORMAL;

	COMIC_FRAGMENT

	float haze = vegetation_haze(pigment, plant_world);
	ALBEDO *= 1.0 - haze;
	BACKLIGHT *= 1.0 - haze;
	EMISSION = uMmoratHaze * haze;
}
