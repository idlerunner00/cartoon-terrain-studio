// Hand-written: the comic look's output pass. The scene Environment already tone-maps (Filmic, glow, adjustments, like
// godot-flui's world); the HDR 2D scene target stays linear, so this pass only encodes sRGB and dithers.

shader_type canvas_item;
render_mode unshaded, blend_disabled;

// The scene target is stored bottom-up when the terrain camera is mirrored.
uniform bool fluitown_mirrored = false;
// Output dither amplitude in 8-bit levels (TerrainVisualQuality.Dither).
uniform float fluitown_dither = 0.0;
uniform sampler2D tDiffuse : filter_nearest, repeat_disable;

float mmoratPaperHash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

// three's linearToOutputTexel (sRGBTransferOETF).
vec3 fluitown_srgb(vec3 value) {
	return mix(pow(value, vec3(0.41666)) * 1.055 - vec3(0.055), value * 12.92, vec3(lessThanEqual(value, vec3(0.0031308))));
}

void fragment() {
	// three's vUv has its origin bottom-left.
	vec2 vUv = vec2(UV.x, 1.0 - UV.y);
	vec4 source = texture(tDiffuse, fluitown_mirrored ? vUv : vec2(vUv.x, 1.0 - vUv.y));
	vec3 color = fluitown_srgb(clamp(source.rgb, 0.0, 1.0));
	if (fluitown_dither > 0.0) {
		float fluitownDither = mmoratPaperHash(FRAGCOORD.xy + vec2(0.37, 0.61)) + mmoratPaperHash(FRAGCOORD.xy + vec2(71.3, 19.7)) - 1.0;
		color += fluitownDither * fluitown_dither / 255.0;
	}
	COLOR = vec4(color, source.a);
}
