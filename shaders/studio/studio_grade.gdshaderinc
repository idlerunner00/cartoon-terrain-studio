// Cartoon Terrain Studio: live colour grade of the terrain's pigment per material family (hand-written).
// Applied to the final pigment right before the comic tail (comic_surface), so every ported material pattern, splat,
// drift and region paint is kept and only re-coloured: hue turn, saturation, value and warmth — no re-bake needed.
// Each vec4 is (hue turn in turns −0.5..0.5, saturation ×, value ×, warmth −1..1). Identity = (0, 1, 1, 0).
#ifndef STUDIO_GRADE
#define STUDIO_GRADE

uniform vec4 studio_grade_global = vec4(0.0, 1.0, 1.0, 0.0);
uniform vec4 studio_grade_floor = vec4(0.0, 1.0, 1.0, 0.0);
uniform vec4 studio_grade_cap = vec4(0.0, 1.0, 1.0, 0.0);
uniform vec4 studio_grade_wall = vec4(0.0, 1.0, 1.0, 0.0);
uniform vec4 studio_grade_foliage = vec4(0.0, 1.0, 1.0, 0.0);
/// (contrast around mid value, unused, unused, unused).
uniform vec4 studio_grade_tone = vec4(1.0, 0.0, 0.0, 0.0);

vec3 studio_grade_one(vec3 c, vec4 g) {
	if (g.x == 0.0 && g.y == 1.0 && g.z == 1.0 && g.w == 0.0) return c;
	// Luma-preserving hue turn in YIQ.
	const mat3 to_yiq = mat3(vec3(0.299, 0.596, 0.211), vec3(0.587, -0.274, -0.523), vec3(0.114, -0.322, 0.312));
	const mat3 to_rgb = mat3(vec3(1.0, 1.0, 1.0), vec3(0.956, -0.272, -1.106), vec3(0.621, -0.647, 1.703));
	vec3 yiq = to_yiq * max(c, vec3(0.0));
	float angle = g.x * 6.2831853;
	float cs = cos(angle), sn = sin(angle);
	yiq.yz = vec2(yiq.y * cs - yiq.z * sn, yiq.y * sn + yiq.z * cs) * g.y;
	vec3 rgb = max(to_rgb * yiq, vec3(0.0)) * g.z;
	float luma = dot(rgb, vec3(0.2126, 0.7152, 0.0722));
	rgb += vec3(0.07, 0.015, -0.07) * g.w * luma * 1.4;
	return max(rgb, vec3(0.0));
}

/// family: 0 floor, 1 rock cap, 2 wall / riser / chasm, 3 foliage.
vec3 studio_grade(vec3 pigment, int family) {
	vec4 g = family == 0 ? studio_grade_floor : family == 1 ? studio_grade_cap : family == 2 ? studio_grade_wall : studio_grade_foliage;
	vec3 c = studio_grade_one(studio_grade_one(pigment, g), studio_grade_global);
	if (studio_grade_tone.x != 1.0) {
		// Contrast in perceptual (square-root) space around a mid value.
		vec3 p = sqrt(max(c, vec3(0.0)));
		p = max((p - 0.45) * studio_grade_tone.x + 0.45, vec3(0.0));
		c = p * p;
	}
	return c;
}

#endif
