// Fluitown comic look: shared terms of the terrain's vegetation (hand-written, not generated).
// The terrain's distance haze (terrain_surface_comic.gdshader: mmoratHazeF / mmoratAirMix, applied as a wash with the
// haze colour as emission) and its wind, so plants fade and sway with the ground they stand on. The uniforms are the
// terrain surface material's values, copied every frame by TerrainVegetationLayer.
#ifndef FLUITOWN_VEGETATION_AIR
#define FLUITOWN_VEGETATION_AIR

uniform vec3 uMmoratHaze;
uniform vec4 uMmoratHazeCfg;
uniform vec4 uMmoratHazeBody;
uniform vec4 uMmoratTerrainWind;
uniform vec2 uMmoratTerrainWindDirection = vec2(1.0, 0.0);
uniform float uMmoratTerrainTime;
/// Godot world metres → the terrain's compile space (world px): compile = (world - offset) * scale.
uniform vec3 fluitown_compile_offset;
uniform float fluitown_compile_scale = 25.0;
/// 1: the vegetation takes the terrain's aerial haze.
uniform float fluitown_haze_enabled = 1.0;

float vegetation_hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.zyx + 31.32);
	return fract((p3.x + p3.y) * p3.z);
}

float vegetation_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = vegetation_hash12(i);
	float b = vegetation_hash12(i + vec2(1.0, 0.0));
	float c = vegetation_hash12(i + vec2(0.0, 1.0));
	float d = vegetation_hash12(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float vegetation_haze_f(vec3 air) {
	float eye_depth = air.z * 0.66601577 + air.y * 0.59465694;
	float u = max((uMmoratHazeCfg.x - eye_depth) * uMmoratHazeCfg.y, 0.0);
	float banded = pow(min(u, 1.0), 0.8500) * uMmoratHazeCfg.z;
	float far = min(uMmoratHazeCfg.w, uMmoratHazeCfg.z * 1.2500);
	float haze = mix(banded, far - (far - banded) * exp(-0.8000 * max(u - 1.0, 0.0)), step(1.0, u));
	float pool = 1.0 - smoothstep(-27.0, 24.0, air.y);
	float broad = vegetation_noise(air.xz / 274.0 + 9.2);
	float detail = vegetation_noise(mat2(vec2(0.839, -0.545), vec2(0.545, 0.839)) * air.xz / 118.0 - 27.6);
	float body = broad * 0.64 + detail * 0.36 - 0.5;
	float mod_ = clamp(1.0 + uMmoratHazeBody.x * pool + uMmoratHazeBody.y * body * 2.0, 0.7800, 1.2500);
	return clamp(haze * mod_, 0.0, uMmoratHazeCfg.w);
}

vec3 vegetation_air_mix(vec3 colour, vec3 sky, float f) {
	vec3 g = sqrt(max(colour, vec3(0.0)));
	vec3 a = sqrt(max(sky, vec3(0.0)));
	float lg = dot(g, vec3(0.2126, 0.7152, 0.0722));
	float la = dot(a, vec3(0.2126, 0.7152, 0.0722));
	float ink = 1.0 - smoothstep(0.0, uMmoratHazeBody.w, lg);
	float k = clamp(f * (1.0 + uMmoratHazeBody.z * ink), 0.0, 1.0);
	float kc = clamp(k * 1.3500, 0.0, 1.0);
	vec3 mixed = max(vec3(mix(lg, la, k)) + mix(g - lg, a - la, kc), vec3(0.0));
	return mixed * mixed;
}

/// The wash share of the terrain's haze for a pigment (linear, three convention) at a world position (metres).
float vegetation_haze(vec3 pigment, vec3 world) {
	if (fluitown_haze_enabled < 0.5 || uMmoratHazeCfg.y <= 0.0) return 0.0;
	vec3 air = (world - fluitown_compile_offset) * fluitown_compile_scale;
	float f = vegetation_haze_f(air);
	vec3 mixed = vegetation_air_mix(pigment, uMmoratHaze, f);
	float range = dot(uMmoratHaze - pigment, vec3(0.2126, 0.7152, 0.0722));
	return abs(range) > 0.001 ? clamp(dot(mixed - pigment, vec3(0.2126, 0.7152, 0.0722)) / range, 0.0, 1.0) : clamp(f, 0.0, 1.0);
}

vec3 vegetation_srgb_to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

vec3 vegetation_unpack_hex(float packed_hex) {
	float n = floor(packed_hex + 0.5);
	float r = floor(n / 65536.0);
	float g = floor((n - r * 65536.0) / 256.0);
	float b = n - r * 65536.0 - g * 256.0;
	return vec3(r, g, b) / 255.0;
}

#endif
