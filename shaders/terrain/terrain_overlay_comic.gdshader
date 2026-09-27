// Generated from the original's captured three.js program, then maintained by hand in this project.
// Source: program-010 (MeshBasicMaterial terrain ink/decal overlay), comic look

shader_type spatial;
uniform bool fluitown_mirrored = false;
#define COMIC_DEFAULT_ROLE 4
#include "res://assets/shaders/comic_style.gdshaderinc"
render_mode unshaded, shadows_disabled, ambient_light_disabled, blend_mix, cull_back, depth_draw_never;

// Yawed-world matrix of the original scene root (three view space = yawed world, see RENDER_ARCHITECTURE).
uniform mat4 u_three_view;
// three uploads isOrthographic = camera.isOrthographicCamera, false for the terrain's plain Camera.
const bool isOrthographic = false;
const vec3 cameraPosition = vec3(0.0);
uniform vec4 uMmoratTerrainContour;
uniform vec3 three_diffuse;
uniform float three_opacity;
uniform int uTerrainInteriorCutawayCount;
uniform vec4 uTerrainInteriorCutawayBounds[8];
uniform vec4 uTerrainInteriorCutawayState[8];
uniform vec3 uMmoratHaze;
uniform vec4 uMmoratHazeCfg;
uniform vec4 uMmoratHazeBody;
uniform float uMmoratNight;



varying vec3 vTerrainInteriorCutWorld;
varying vec3 vTerrainInteriorCutNormal;
varying vec3 vMmoratWorld;
varying vec4 vColor;
varying vec3 vNormal;

struct ReflectedLight {
	vec3 directDiffuse;
	vec3 directSpecular;
	vec3 indirectDiffuse;
	vec3 indirectSpecular;
};
float mmoratHash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.zyx + 31.32);
  return fract((p3.x + p3.y) * p3.z);
}
float mmoratNoise(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  float a = mmoratHash12(i);
  float b = mmoratHash12(i + vec2(1.0, 0.0));
  float c = mmoratHash12(i + vec2(0.0, 1.0));
  float d = mmoratHash12(i + vec2(1.0, 1.0));
  return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
vec2 mmoratTerrainContourWarp(vec2 worldPosition) {
  float scalePx = max(
    96.0,
    uMmoratTerrainContour.y
  );
  vec2 uv = worldPosition / scalePx;
  float warpX = mmoratNoise(uv + vec2(71.3, -29.7)) - 0.5;
  float warpZ = mmoratNoise(mat2(vec2(0.819, -0.574), vec2(0.574, 0.819)) * uv - 43.1) - 0.5;
  return vec2(warpX, warpZ) * (uMmoratTerrainContour.x * 2.0);
}
vec4 LinearTransferOETF( in vec4 value ) {
	return value;
}
vec4 linearToOutputTexel( vec4 value ) {
	return LinearTransferOETF( vec4( value.rgb * mat3(vec3(1.0000, -0.0000, -0.0000), vec3(-0.0000, 1.0000, 0.0000), vec3(0.0000, 0.0000, 1.0000)), value.a ) );
}
float mmoratHazeF() {
  vec3 mmoratAir = vMmoratWorld;
  float mmoratEyeDepth = mmoratAir.z * 0.66601577 + mmoratAir.y * 0.59465694;
  float u = max((uMmoratHazeCfg.x - mmoratEyeDepth) * uMmoratHazeCfg.y, 0.0);
  float banded = pow(min(u, 1.0), 0.8500) * uMmoratHazeCfg.z;
  float far = min(uMmoratHazeCfg.w, uMmoratHazeCfg.z * 1.2500);
  float haze = mix(
    banded,
    far - (far - banded) * exp(-0.8000 * max(u - 1.0, 0.0)),
    step(1.0, u)
  );
  float hazePool = 1.0 - smoothstep(-27.0, 24.0, mmoratAir.y);
  float hazeBodyBroad = mmoratNoise(mmoratAir.xz / 274.0 + 9.2);
  float hazeBodyDetail = mmoratNoise(
    mat2(vec2(0.839, -0.545), vec2(0.545, 0.839)) * mmoratAir.xz / 118.0 - 27.6
  );
  float hazeBody = hazeBodyBroad * 0.64 + hazeBodyDetail * 0.36 - 0.5;
  float hazeMod = clamp(
    1.0 + uMmoratHazeBody.x * hazePool + uMmoratHazeBody.y * hazeBody * 2.0,
    0.7800,
    1.2500
  );
  return clamp(haze * hazeMod, 0.0, uMmoratHazeCfg.w);
}
vec3 mmoratAirMix(vec3 mmoratColor, vec3 mmoratSky, float mmoratF) {
  vec3 g = sqrt(max(mmoratColor, vec3(0.0)));
  vec3 a = sqrt(max(mmoratSky, vec3(0.0)));
  float mmoratLg = dot(g, vec3(0.2126, 0.7152, 0.0722));
  float mmoratLa = dot(a, vec3(0.2126, 0.7152, 0.0722));
  float mmoratInk = 1.0 - smoothstep(0.0, uMmoratHazeBody.w, mmoratLg);
  float k = clamp(mmoratF * (1.0 + uMmoratHazeBody.z * mmoratInk), 0.0, 1.0);
  float kc = clamp(k * 1.3500, 0.0, 1.0);
  vec3 mmoratMixed = max(
    vec3(mix(mmoratLg, mmoratLa, k)) + mix(g - mmoratLg, a - mmoratLa, kc),
    vec3(0.0)
  );
  return mmoratMixed * mmoratMixed;
}


void vertex() {
	vec3 position = VERTEX;
	vec4 color = CUSTOM0;
	mat4 modelMatrix = u_three_view * MODEL_MATRIX;
	mat4 modelViewMatrix = u_three_view * MODEL_MATRIX;

	vColor = vec4( 1.0 );
	vColor *= color;
vec3 transformed = vec3( position );
if (uMmoratTerrainContour.x > 0.001) {
  transformed.xz += mmoratTerrainContourWarp(position.xz);
}
vMmoratWorld = vec3(transformed);
vTerrainInteriorCutWorld = vec3(transformed);
vTerrainInteriorCutNormal = vec3(0.0, 1.0, 0.0);
vec4 mvPosition = vec4( transformed, 1.0 );
mvPosition = modelViewMatrix * mvPosition;

	vec4 worldPosition = vec4( transformed, 1.0 );
	worldPosition = modelMatrix * worldPosition;

	VERTEX = transformed;
	COMIC_VERTEX
	POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));

}

void fragment() {
	// three's gl_FragCoord has its origin bottom-left, Godot's FRAGCOORD top-left (unless mirrored).
	vec4 fluitown_FragCoord = fluitown_mirrored ? FRAGCOORD : vec4(FRAGCOORD.x, VIEWPORT_SIZE.y - FRAGCOORD.y, FRAGCOORD.z, FRAGCOORD.w);
	vec4 pc_fragColor = vec4(0.0);

	vec4 diffuseColor = vec4( three_diffuse, three_opacity );
for (int terrainCutIndex = 0; terrainCutIndex < 8; terrainCutIndex++) {
  if (terrainCutIndex >= uTerrainInteriorCutawayCount) break;
  vec4 terrainCutBounds = uTerrainInteriorCutawayBounds[terrainCutIndex];
  vec4 terrainCutState = uTerrainInteriorCutawayState[terrainCutIndex];
  float terrainCutInside =
    step(terrainCutBounds.x - 1.5, vTerrainInteriorCutWorld.x)
    * step(vTerrainInteriorCutWorld.x, terrainCutBounds.z + 1.5)
    * step(terrainCutBounds.y - 1.5, vTerrainInteriorCutWorld.z)
    * step(vTerrainInteriorCutWorld.z, terrainCutBounds.w + 1.5);
  float terrainCutDither = fract(
    sin(dot(fluitown_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453
  );
  if (
    terrainCutInside * step(terrainCutState.x, vTerrainInteriorCutWorld.y) > 0.5
    && terrainCutDither < terrainCutState.y
  ) discard;
}
	diffuseColor *= vColor;
float specularStrength;
	specularStrength = 1.0;
	ReflectedLight reflectedLight = ReflectedLight( vec3( 0.0 ), vec3( 0.0 ), vec3( 0.0 ), vec3( 0.0 ) );
		reflectedLight.indirectDiffuse += vec3( 1.0 );
	reflectedLight.indirectDiffuse *= diffuseColor.rgb;
	vec3 outgoingLight = reflectedLight.indirectDiffuse;
pc_fragColor = vec4( outgoingLight, diffuseColor.a );
float mmoratAccentPeak = max(pc_fragColor.r, max(pc_fragColor.g, pc_fragColor.b));
float mmoratAccentTrough = min(pc_fragColor.r, min(pc_fragColor.g, pc_fragColor.b));
float mmoratAccentMask = smoothstep(0.08, 0.32, mmoratAccentPeak - mmoratAccentTrough)
  * smoothstep(0.08, 0.35, mmoratAccentPeak);
float mmoratAccentLuminance = dot(pc_fragColor.rgb, vec3(0.2126, 0.7152, 0.0722));
pc_fragColor.rgb = max(
  vec3(mmoratAccentLuminance)
    + (pc_fragColor.rgb - vec3(mmoratAccentLuminance))
      * (1.0 + uMmoratNight * mmoratAccentMask * 0.16),
  vec3(0.0)
) * (1.0 + uMmoratNight * mmoratAccentMask * 0.055);
pc_fragColor.rgb = mmoratAirMix(pc_fragColor.rgb, uMmoratHaze, mmoratHazeF());
pc_fragColor = linearToOutputTexel( pc_fragColor );

	// godot-flui's comic pipeline: the unlit colour through the central comic ink of this surface role.
	vec3 fluitown_comic_form = normalize(cross(dFdx(comic_position), dFdy(comic_position)));
	if (dot(fluitown_comic_form, (INV_VIEW_MATRIX * vec4(VIEW, 0.0)).xyz) < 0.0) fluitown_comic_form = -fluitown_comic_form;
	pc_fragColor.rgb = comic_surface(pc_fragColor.rgb, comic_position, fluitown_comic_form, NORMAL, VIEW, comic_role);
	float fluitown_depth_slope = max(abs(dFdx(FRAGCOORD.z)), abs(dFdy(FRAGCOORD.z)));
	DEPTH = FRAGCOORD.z + fluitown_depth_slope * 1.0 + 2.0 * 5.9604644775390625e-8;
	ALBEDO = pc_fragColor.rgb;
	ALPHA = pc_fragColor.a;
}
