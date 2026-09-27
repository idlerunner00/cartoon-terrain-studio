// Sun shadow depth pass for the terrain's shadow casters (the actor-wall lane).
//
// three.js renders these meshes with a double-sided MeshDepthMaterial into an orthographic depth texture and
// compares with `sampler2DShadow`. For an orthographic light the stored value is linear:
// depth01 = (-viewZ - near) / (far - near). Godot cannot hand a custom camera's depth buffer to another
// material, so this pass writes the same depth01 into an HDR colour target, split into a hi/lo pair so it keeps
// ~21 bits of precision in RGBA16F: r = floor(d·1024)/1024, g = fract(d·1024). The terrain shaders unpack it
// in `fluitown_unpack_depth` and emulate the hardware comparison.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always, shadows_disabled, ambient_light_disabled, fog_disabled;

uniform float u_near = 1.0;
uniform float u_far = 9000.0;

void fragment() {
	float d = clamp((-VERTEX.z - u_near) / (u_far - u_near), 0.0, 1.0);
	float scaled = d * 1024.0;
	float hi = floor(scaled) / 1024.0;
	float lo = scaled - floor(scaled);
	ALBEDO = vec3(hi, lo, 0.0);
}
