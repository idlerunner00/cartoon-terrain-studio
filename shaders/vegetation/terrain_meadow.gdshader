// Fluitown comic look: the terrain's meadow grass (hand-written).
// godot-flui's meadow_grass.gdshader — its wind, footprints, the explorer's contact bend and the heavy walker, the
// distance fade and the cupped normal — with the pigment of the ground each tuft grows out of (per instance: the
// terrain's turf mat lifted towards the world's lush pole) and the terrain's distance haze.
//
// INSTANCE_CUSTOM = (mid pigment, tip pigment, root pigment — 24-bit sRGB hex values — , variation·255 + phase).
shader_type spatial;
#define COMIC_DEFAULT_ROLE 2
#include "res://assets/shaders/comic_style.gdshaderinc"
#include "res://shaders/studio/studio_grade.gdshaderinc"
// Terrain pixels (stencil 2) for the terrain occlusion effect only; world ink is shared.
stencil_mode write, compare_always, 2;
render_mode cull_disabled, specular_disabled;
#include "res://shaders/vegetation/vegetation_air.gdshaderinc"
uniform vec4 footsteps[16];
uniform vec4 footstep_bounds = vec4(0.0);
uniform vec4 walker = vec4(0.0, -10000.0, 0.0, 0.0);
uniform vec3 explorer = vec3(0.0, -10000.0, 0.0);
uniform vec3 companion = vec3(0.0, -10000.0, 0.0);
uniform float draw_distance = 46.0;
/// Bird's-eye view: how much of its height a blade stands up (TerrainVegetationLayer.ApplyMeadowScreenFade). Far out a
/// 0.42 m tuft falls below one pixel of the internal buffer and the upscaler smears the sub-pixel carpet into a coarse
/// ripple over the whole ground; the blades sink into the ground before that. 1 = godot-flui's standing meadow.
uniform float blade_fade = 1.0;
// Screen-space consolidation of blades, controlled by TerrainVisualQuality.MeadowClumps.
uniform bool meadow_clumps = true;
varying float blade_height;
varying float variation;
varying vec3 meadow_mid;
varying vec3 meadow_tip;
varying vec3 meadow_root;
varying vec3 meadow_world;
varying float blade_detail;

vec2 contact_bend(vec3 root, vec3 body, inout float flatten) {
	vec3 near = root - body;
	float contact = (1.0 - smoothstep(0.2, 1.1, length(near.xz))) * (1.0 - smoothstep(0.6, 2.0, abs(near.y)));
	flatten = max(flatten, contact * .86);
	return normalize(near.xz + vec2(.001)) * contact * .7;
}

void vertex() {
	vec3 root = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
	blade_height = UV.y;
	variation = floor(INSTANCE_CUSTOM.w) / 255.0;
	float phase = fract(INSTANCE_CUSTOM.w);
	meadow_mid = vegetation_srgb_to_linear(vegetation_unpack_hex(INSTANCE_CUSTOM.x));
	meadow_tip = vegetation_srgb_to_linear(vegetation_unpack_hex(INSTANCE_CUSTOM.y));
	meadow_root = vegetation_srgb_to_linear(vegetation_unpack_hex(INSTANCE_CUSTOM.z));
	// Resolve individual blades, not the much taller tuft. The same projected
	// footprint works in orthographic and perspective cameras, including FSR.
	float view_depth = max(-(VIEW_MATRIX * vec4(root, 1.0)).z, 0.01);
	float pixels_per_metre = abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y * 0.5
		/ (PROJECTION_MATRIX[3][3] > 0.5 ? 1.0 : view_depth);
	float blade_pixels = pixels_per_metre * 0.045 * length(MODEL_MATRIX[0].xyz);
	blade_detail = smoothstep(0.65, 2.0, blade_pixels);
	// Retain a stable silhouette with fewer wider blades. No alpha, discard, dither or extra draw.
	float width_gain = meadow_clumps ? clamp(1.15 / max(blade_pixels, 0.01), 1.0, 2.8) : 1.0;
	vec3 blade_right = vec3(NORMAL.z, 0.0, -NORMAL.x);
    float half_width = UV2.x > 0.0001 ? UV2.x : 0.045 * (1.0 - UV.y);
    VERTEX += blade_right * (UV.x * 2.0 - 1.0) * half_width * (width_gain - 1.0);
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float tuft_pixels = pixels_per_metre * 0.42 * length(MODEL_MATRIX[1].xyz);
	float resolved = meadow_clumps ? smoothstep(0.7, 2.2, tuft_pixels) : blade_detail;
	float reach = blade_fade * resolved * (1.0 - smoothstep(draw_distance - 12.0, draw_distance, distance(root, CAMERA_POSITION_WORLD)));
	vec2 bend = vec2(0.0);
	float flatten = 0.0;
	if (reach > 0.0) {
		float wind = sin(dot(root.xz, vec2(0.31, 0.18)) - TIME * 1.7 + phase * 6.28) * 0.13;
		wind += sin(dot(root.xz, vec2(-0.12, 0.27)) - TIME * 0.9) * 0.19;
		bend = vec2(0.82, 0.39) * wind * mix(0.22, 1.0, blade_detail);
		// Footstep/contact interaction cannot affect remote tufts. Skip sixteen tests per vertex there.
		bool interactive = all(greaterThanEqual(root.xz, footstep_bounds.xy)) && all(lessThanEqual(root.xz, footstep_bounds.zw));
		if (interactive) for (int i = 0; i < 16; i++) {
			vec3 offset = root - footsteps[i].xyz;
			// Time decay is uniform across every blade and is prepared once on the CPU.
			if (dot(offset.xz, offset.xz) < 2.25 && abs(offset.y) < 2.0 && footsteps[i].w > 0.0) {
				float force = (1.0 - smoothstep(0.15, 1.5, length(offset.xz))) * footsteps[i].w * (1.0 - smoothstep(0.7, 2.0, abs(offset.y)));
				bend += normalize(offset.xz + vec2(0.001)) * force * 0.46;
				flatten = max(flatten, force * .82);
			}
		}
		bend += contact_bend(root, explorer, flatten);
		bend += contact_bend(root, companion, flatten);
		vec3 tread = root - walker.xyz;
		float heavy = 0.0;
		if (walker.w > .1) heavy = (1.0 - smoothstep(walker.w * .45, walker.w, length(tread.xz))) * (1.0 - smoothstep(1.0, 4.5, abs(tread.y)));
		bend += normalize(tread.xz + vec2(.001)) * heavy * .8;
		flatten = max(flatten, heavy * .95);
	}
	world.xz += bend * UV.y * UV.y * reach;
	world.y = mix(root.y - .035, world.y, reach * (1.0 - flatten * UV.y));
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
	meadow_world = world;
	// A softly cupped blade stays readable from both sides under the same toon lighting as Flui.
	// Unresolved blades share the meadow's up normal. Independent toon-band crossings on tiny
	// blades were a second source of dark stipple, even after their silhouettes had been filtered.
	float cup = mix(0.035, 0.30, blade_detail);
	NORMAL = normalize(vec3(NORMAL.x * cup, 1.0, NORMAL.z * cup));

	COMIC_VERTEX
}

void fragment() {
	// godot-flui: root to tip, a share of the tufts with pale tips.
	vec3 tip = mix(meadow_mid, meadow_tip, 0.45 + 0.55 * smoothstep(.65, 1.0, variation));
	vec3 pigment = mix(mix(meadow_mid, meadow_root, 0.6), tip, smoothstep(0.0, 1.0, blade_height));
	// Distant grass merges into its meadow pigment; dark roots and animated
	// subpixel tips must not become a high-contrast stipple after upscaling.
	pigment = mix(meadow_mid, pigment, blade_detail);
	// STUDIO: live colour grade — grass follows the floor it grows from.
	pigment = studio_grade(pigment, 0);
	// The terrain's pigment convention: three pigments enter the comic ramp divided by π.
	ALBEDO = pigment * 0.3183098861837907;
	BACKLIGHT = ALBEDO * .24;
	// The shared filter's coverage range: unresolved blades merge into the
	// meadow colour instead of gaining a full pixel of dark outline each.
	ROUGHNESS = mix(1.0, 0.98, blade_detail);
	// A blade is a card lit from both sides with its cupped normal: undo Godot's back-face flip (cull_disabled), which
	// turned every blade seen from behind into a black sliver under the comic ramp.
	if (!FRONT_FACING) NORMAL = -NORMAL;

	COMIC_FRAGMENT


	float haze = vegetation_haze(pigment, meadow_world);
	ALBEDO *= 1.0 - haze;
	BACKLIGHT *= 1.0 - haze;
	EMISSION = uMmoratHaze * haze;
}
