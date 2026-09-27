#ifndef FLUI_COMIC_PALETTE
#define FLUI_COMIC_PALETTE
// The common ink values are linear, shared by world, hull, surface and UI passes.
const vec3 COMIC_INK = vec3(0.0015, 0.002, 0.003);
const vec3 COMIC_DISTANT_INK = vec3(0.035, 0.052, 0.065);
const vec3 COMIC_PAPER = vec3(0.91, 0.86, 0.72);
// Sky adapters share the same restrained, printed color steps.
vec3 comic_background(vec3 pigment) { return floor(max(pigment, COMIC_INK) * 32.0 + 0.5) / 32.0; }
// Godot uniform defaults require a literal after preprocessing.
#define COMIC_OUTLINE_WIDTH 2.15
#endif
