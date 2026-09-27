// Generated from the original's captured three.js program, then maintained by hand in this project.
// Source: program-005 (MeshBasicMaterial terrain backdrop), comic look

shader_type spatial;
#define COMIC_DEFAULT_ROLE 4
#include "res://assets/shaders/comic_style.gdshaderinc"
render_mode unshaded, shadows_disabled, ambient_light_disabled, cull_disabled, depth_draw_never;

// Yawed-world matrix of the original scene root (three view space = yawed world, see RENDER_ARCHITECTURE).
uniform mat4 u_three_view;
// three uploads isOrthographic = camera.isOrthographicCamera, false for the terrain's plain Camera.
const bool isOrthographic = false;
const vec3 cameraPosition = vec3(0.0);
uniform vec3 three_diffuse;
uniform float three_opacity;
uniform vec3 uMmoratHaze;
uniform vec4 uMmoratHazeCfg;
uniform vec4 uMmoratHazeBody;
uniform vec4 uMmoratView;
uniform float uMmoratVoidTime;
uniform float uMmoratNight;
uniform float uMmoratAetherSky;
uniform vec3 uMmoratAetherSkyColor;

varying float fluitown_backdrop_depth;

varying vec3 vMmoratWorld;
varying vec3 vNormal;

struct ReflectedLight {
	vec3 directDiffuse;
	vec3 directSpecular;
	vec3 indirectDiffuse;
	vec3 indirectSpecular;
};
vec4 LinearTransferOETF( in vec4 value ) {
	return value;
}
vec4 linearToOutputTexel( vec4 value ) {
	return LinearTransferOETF( vec4( value.rgb * mat3(vec3(1.0000, -0.0000, -0.0000), vec3(-0.0000, 1.0000, 0.0000), vec3(0.0000, 0.0000, 1.0000)), value.a ) );
}
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
	vec3 normal = NORMAL;
	vec2 uv = UV;
	mat4 modelMatrix = u_three_view * MODEL_MATRIX;
	mat4 modelViewMatrix = u_three_view * MODEL_MATRIX;

vec3 transformed = vec3( position );
vMmoratWorld = (modelMatrix * vec4(transformed, 1.0)).xyz;
vec4 mvPosition = vec4( transformed, 1.0 );
mvPosition = modelViewMatrix * mvPosition;

	vec4 worldPosition = vec4( transformed, 1.0 );
	worldPosition = modelMatrix * worldPosition;

	VERTEX = transformed;
	COMIC_VERTEX
	vec4 fluitown_clip = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	fluitown_backdrop_depth = fluitown_clip.z / fluitown_clip.w;
	POSITION = vec4(fluitown_clip.x, fluitown_clip.y, 1e-6 * fluitown_clip.w, fluitown_clip.w);

}

void fragment() {

	vec4 pc_fragColor = vec4(0.0);

	vec4 diffuseColor = vec4( three_diffuse, three_opacity );
{
  vec2 slowDrift = vec2(uMmoratVoidTime * 0.82, -uMmoratVoidTime * 0.37);
  float macro = mmoratNoise((vMmoratWorld.xz + slowDrift) / 338.0);
  float middle = mmoratNoise((vMmoratWorld.xz - slowDrift * 1.63 + 41.0) / 126.0);
  float breakField = mmoratNoise((vMmoratWorld.xz + slowDrift * 2.11 - 73.0) / 54.0);
  float riftWide = 1.0 - abs(mmoratNoise((vMmoratWorld.xz + slowDrift * 0.29 + 83.0) / 191.0) * 2.0 - 1.0);
  float riftCross = 1.0 - abs(mmoratNoise((vMmoratWorld.zx - slowDrift * 0.51 - 29.0) / 103.0) * 2.0 - 1.0);
  float cavernPocket = smoothstep(0.14, 0.86, macro * 0.61 + middle * 0.39);
  float faultGlow = pow(clamp(riftWide * riftCross, 0.0, 1.0), 1.45);
  float suspendedHaze = smoothstep(0.5, 0.86, breakField * 0.58 + riftWide * 0.42);
  float depthField = clamp(
    cavernPocket * 0.37 + middle * 0.2 + breakField * 0.12
      + faultGlow * 0.18 + suspendedHaze * 0.13,
    0.0,
    1.0
  );
  float continuumScreenY = vMmoratWorld.z * 0.66601577
    - vMmoratWorld.y * 0.74593766;
  vec2 plumeUv = mat2(vec2(0.884, -0.467), vec2(0.467, 0.884)) * vMmoratWorld.xz;
  float plumeBody = mmoratNoise(
    vec2(plumeUv.x / 76.0 + uMmoratVoidTime * 0.004, plumeUv.y / 286.0 - uMmoratVoidTime * 0.002)
  );
  float plumeBreak = mmoratNoise(
    vec2(plumeUv.x / 31.0 - uMmoratVoidTime * 0.009, plumeUv.y / 118.0 + uMmoratVoidTime * 0.005) + 19.0
  );
  float depthPlume = smoothstep(0.54, 0.86, plumeBody * 0.78 + plumeBreak * 0.22)
    * smoothstep(0.2, 0.72, middle);
  vec2 farFogFlow = vec2(uMmoratVoidTime * 1.45, -uMmoratVoidTime * 0.62);
  float farFogBody = mmoratNoise((vMmoratWorld.xz + farFogFlow + 113.0) / 274.0);
  float farFogBreak = mmoratNoise((vMmoratWorld.zx - farFogFlow * 0.47 - 61.0) / 137.0);
  float farFog = smoothstep(0.48, 0.84, farFogBody * 0.72 + farFogBreak * 0.28);
  vec2 middleFogFlow = vec2(-uMmoratVoidTime * 2.65, uMmoratVoidTime * 1.72);
  vec2 middleFogUv = vMmoratWorld.xz + middleFogFlow;
  vec2 middleFogWarp = vec2(
    mmoratNoise((middleFogUv + 37.0) / 168.0),
    mmoratNoise((middleFogUv.yx - 89.0) / 151.0)
  ) - 0.5;
  float middleFogBody = mmoratNoise((middleFogUv + middleFogWarp * 46.0) / 91.0);
  float middleFogBreak = mmoratNoise((middleFogUv.yx - middleFogWarp * 29.0 + 19.0) / 47.0);
  float middleFog = smoothstep(0.5, 0.82, middleFogBody * 0.68 + middleFogBreak * 0.32)
    * mix(0.72, 1.0, farFog);
  vec2 nearFogFlow = vec2(uMmoratVoidTime * 4.3, uMmoratVoidTime * 2.35);
  vec2 nearFogUv = mat2(vec2(0.819, -0.574), vec2(0.574, 0.819)) * (vMmoratWorld.xz + nearFogFlow);
  float nearFogBody = mmoratNoise(nearFogUv / 52.0 + middleFogWarp * 0.61);
  float nearFogFilament = 1.0 - abs(mmoratNoise(nearFogUv.yx / 31.0 - 23.0) * 2.0 - 1.0);
  float nearFog = smoothstep(0.58, 0.88, nearFogBody * 0.7 + nearFogFilament * 0.3)
    * smoothstep(0.2, 0.82, middleFog);
  float continuumValue = mix(
    0.085,
    0.180,
    pow(smoothstep(0.12, 0.88, depthField), 1.32)
  );
  continuumValue += depthPlume * 0.016;
  continuumValue += farFog * 0.007;
  continuumValue += middleFog * 0.013;
  continuumValue += nearFog * 0.007;
  continuumValue += pow(faultGlow, 2.1) * 0.003;
  float voidNight = clamp(uMmoratNight, 0.0, 1.0);
  continuumValue += voidNight * (
    0.003 + farFog * 0.002 + middleFog * 0.004 + nearFog * 0.002
  );
  float continuumStrata = 0.5 + 0.5 * cos(continuumScreenY / 82.0 + macro * 2.1 + middle * 0.7);
  continuumValue *= mix(0.8, 1.2, smoothstep(0.18, 0.82, continuumStrata));
  float continuumFineStructure = (middle - 0.5) * 0.2
    + (breakField - 0.5) * 0.11 + (nearFogFilament - 0.5) * 0.05;
  continuumValue *= clamp(1.0 + continuumFineStructure, 0.84, 1.18);
  vec3 voidHue = mix(
    max(diffuseColor.rgb, vec3(0.001)),
    uMmoratHaze,
    0.16 + suspendedHaze * 0.055 + depthPlume * 0.08
      + farFog * 0.025 + middleFog * 0.045 + nearFog * 0.06 + voidNight * 0.035
  );
  float voidHueLuminance = max(dot(voidHue, vec3(0.2126, 0.7152, 0.0722)), 0.001);
  diffuseColor.rgb = voidHue * (continuumValue / voidHueLuminance);
}
float specularStrength;
	specularStrength = 1.0;
	ReflectedLight reflectedLight = ReflectedLight( vec3( 0.0 ), vec3( 0.0 ), vec3( 0.0 ), vec3( 0.0 ) );
		reflectedLight.indirectDiffuse += vec3( 1.0 );
	reflectedLight.indirectDiffuse *= diffuseColor.rgb;
	vec3 outgoingLight = reflectedLight.indirectDiffuse;
diffuseColor.a = 1.0;
pc_fragColor = vec4( outgoingLight, diffuseColor.a );
pc_fragColor.rgb = mmoratAirMix(
  pc_fragColor.rgb,
  uMmoratHaze,
  mmoratHazeF() * 0.035
);
float mmoratBackdropPostWide = mmoratNoise(
  (vMmoratWorld.xz + vec2(uMmoratVoidTime * 1.1, -uMmoratVoidTime * 0.47) + 91.0) / 284.0
);
float mmoratBackdropPostBreak = mmoratNoise(
  (vMmoratWorld.xz - vec2(uMmoratVoidTime * 0.63, uMmoratVoidTime * 0.29) - 47.0) / 83.0
);
pc_fragColor.rgb *= mix(
  0.9,
  1.12,
  mmoratBackdropPostWide * 0.68 + mmoratBackdropPostBreak * 0.32
);
float mmoratAetherVertical = clamp(
  0.5 + (vMmoratWorld.z - uMmoratView.y) * uMmoratView.w * 0.34,
  0.0,
  1.0
);
float mmoratAetherAir = mix(
  0.94,
  1.055,
  mmoratBackdropPostWide * 0.72 + mmoratBackdropPostBreak * 0.28
);
vec3 mmoratAetherSkyColor = uMmoratAetherSkyColor
  * mix(0.82, 1.16, mmoratAetherVertical)
  * mmoratAetherAir;
pc_fragColor.rgb = mix(pc_fragColor.rgb, mmoratAetherSkyColor, uMmoratAetherSky);
pc_fragColor = linearToOutputTexel( pc_fragColor );

	// godot-flui's comic pipeline: the unlit colour through the central comic ink of this surface role.
	vec3 fluitown_comic_form = normalize(cross(dFdx(comic_position), dFdy(comic_position)));
	if (dot(fluitown_comic_form, (INV_VIEW_MATRIX * vec4(VIEW, 0.0)).xyz) < 0.0) fluitown_comic_form = -fluitown_comic_form;
	pc_fragColor.rgb = comic_surface(pc_fragColor.rgb, comic_position, fluitown_comic_form, NORMAL, VIEW, comic_role);
	if (fluitown_backdrop_depth < 0.0 || fluitown_backdrop_depth > 1.0) discard;
	ALBEDO = pc_fragColor.rgb;
}
