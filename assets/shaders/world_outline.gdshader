shader_type spatial;
#include "res://assets/shaders/comic_palette.gdshaderinc"
render_mode unshaded, fog_disabled, cull_disabled, depth_draw_never, depth_test_disabled;
uniform sampler2D depth_texture : hint_depth_texture, repeat_disable, filter_nearest;
// INK_DEPTH_ONLY: renderers without the normal-roughness buffer (Mobile, Compatibility) draw the contours from
// depth alone, without creases and without the per-material ink strength.
#ifndef INK_DEPTH_ONLY
uniform sampler2D normal_roughness : hint_normal_roughness_texture, repeat_disable, filter_nearest;
#endif
uniform float width=2.15;
uniform vec2 distance_fog=vec2(0.0);
// Optional compatibility with the former flat sea. Voxel water has no global datum.
uniform bool water_cutoff = false;
#include "res://assets/shaders/world_ink_sampling.gdshaderinc"
