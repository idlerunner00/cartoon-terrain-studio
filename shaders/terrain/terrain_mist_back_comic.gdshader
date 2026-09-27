// Generated from the original's captured three.js program, then maintained by hand in this project.
// Source: program-008/009 (MeshBasicMaterial chasm mist), comic look

shader_type spatial;
uniform bool fluitown_mirrored = false;
#define GL_DFDY(x) (fluitown_mirrored ? dFdy(x) : -dFdy(x))
#define COMIC_DEFAULT_ROLE 4
#include "res://assets/shaders/comic_style.gdshaderinc"
render_mode unshaded, shadows_disabled, ambient_light_disabled, blend_mix, cull_front, depth_draw_never;

// Yawed-world matrix of the original scene root (three view space = yawed world, see RENDER_ARCHITECTURE).
uniform mat4 u_three_view;
// three uploads isOrthographic = camera.isOrthographicCamera, false for the terrain's plain Camera.
const bool isOrthographic = false;
const vec3 cameraPosition = vec3(0.0);
uniform float uMmoratMistTime;
uniform vec3 three_diffuse;
uniform float three_opacity;
uniform float uMmoratMistNight;
uniform vec3 uMmoratHaze;
uniform vec4 uMmoratHazeCfg;
uniform vec4 uMmoratHazeBody;



varying vec3 vMmoratWorld;
varying float vMmoratMistPhase;
varying float vMmoratMistLayer;
varying float vMmoratMistSeed;
varying vec4 vColor;
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
float mmoratMistHash(vec2 p) {
  return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}
vec2 mmoratMistGradient(vec2 cell) {
  float h = mmoratMistHash(cell);
  float sx = mix(-1.0, 1.0, step(0.5, fract(h * 11.137 + 0.173)));
  float sy = mix(-1.0, 1.0, step(0.5, fract(h * 7.137 + 0.371)));
  vec2 diagonal = vec2(sx, sy) * 0.70710678;
  vec2 axial = mix(vec2(sx, 0.0), vec2(0.0, sy), step(0.5, fract(h * 5.231 + 0.619)));
  return mix(diagonal, axial, step(0.5, h));
}
float mmoratMistNoise(vec2 p) {
  vec2 cell = floor(p);
  vec2 local = fract(p);
  vec2 eased = local * local * local * (local * (local * 6.0 - 15.0) + 10.0);
  float a = dot(mmoratMistGradient(cell), local);
  float b = dot(mmoratMistGradient(cell + vec2(1.0, 0.0)), local - vec2(1.0, 0.0));
  float c = dot(mmoratMistGradient(cell + vec2(0.0, 1.0)), local - vec2(0.0, 1.0));
  float d = dot(mmoratMistGradient(cell + vec2(1.0, 1.0)), local - vec2(1.0, 1.0));
  return clamp(0.5 + mix(mix(a, b, eased.x), mix(c, d, eased.x), eased.y) * 0.72, 0.0, 1.0);
}
vec2 mmoratMistCurl(vec2 p, float t, float seed) {
  float diagonal = p.x * 0.013 + p.y * 0.017 + t * 0.21 + seed * 1.73;
  float vertical = p.y * 0.021 - t * 0.17 + seed * 2.31;
  float lateral = p.x * 0.019 + t * 0.13 - seed * 1.19;
  return vec2(
    cos(vertical) + cos(diagonal) * 0.78,
    -cos(lateral) - cos(diagonal) * 0.6
  ) * 0.5;
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
	vec4 aMist = CUSTOM1;
	mat4 modelMatrix = u_three_view * MODEL_MATRIX;
	mat4 modelViewMatrix = u_three_view * MODEL_MATRIX;

	vColor = vec4( 1.0 );
	vColor *= color;
vec3 transformed = vec3( position );
float mmoratMistLayer = floor(aMist.z);
float mmoratMistRise = fract(aMist.z);
float mmoratMistSpeed = mmoratMistLayer < 0.5 ? 0.075 : (mmoratMistLayer < 1.5 ? 0.13 : 0.19);
float mmoratMistT = uMmoratMistTime * mmoratMistSpeed + aMist.x * 6.2831853;
vec2 mmoratMistCurrent = mmoratMistLayer < 0.5
  ? vec2(0.72, -0.26)
  : (mmoratMistLayer < 1.5 ? vec2(-0.42, 0.76) : vec2(0.34, 0.91));
float mmoratMistLocalPhase = position.y * (0.011 + mmoratMistLayer * 0.004)
  + position.x * 0.003 - position.z * 0.002;
float mmoratMistSway = sin(mmoratMistT + aMist.w * 2.1 + mmoratMistLocalPhase) * 0.72
  + sin(mmoratMistT * 0.47 + aMist.w * 4.7 - mmoratMistLocalPhase * 0.63) * 0.28;
float mmoratMistCross = cos(mmoratMistT * 0.73 + aMist.w * 1.7 + mmoratMistLocalPhase * 0.81) * 0.68
  + sin(mmoratMistT * 0.39 + aMist.w * 3.1 - mmoratMistLocalPhase * 0.47) * 0.32;
transformed.x += (mmoratMistCurrent.x * mmoratMistSway - mmoratMistCurrent.y * mmoratMistCross * 0.42)
  * aMist.y * (0.72 + mmoratMistLayer * 0.16);
transformed.z += (mmoratMistCurrent.y * mmoratMistSway + mmoratMistCurrent.x * mmoratMistCross * 0.42)
  * aMist.y * (0.48 + mmoratMistLayer * 0.13);
transformed.y += (
  sin(mmoratMistT * 0.61 + aMist.w * 2.7) * 0.72
  + sin(mmoratMistT * 1.13 + aMist.w * 5.3) * 0.28
) * mmoratMistRise;
vMmoratWorld = transformed;
vMmoratMistPhase = mmoratMistT;
vMmoratMistLayer = mmoratMistLayer;
vMmoratMistSeed = aMist.w;
vec4 mvPosition = vec4( transformed, 1.0 );
mvPosition = modelViewMatrix * mvPosition;

	vec4 worldPosition = vec4( transformed, 1.0 );
	worldPosition = modelMatrix * worldPosition;

	VERTEX = transformed;
	COMIC_VERTEX
	POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));

}

void fragment() {

	vec4 pc_fragColor = vec4(0.0);

	vec4 diffuseColor = vec4( three_diffuse, three_opacity );
	diffuseColor *= vColor;
float mmoratMistLayer01 = clamp(vMmoratMistLayer * 0.5, 0.0, 1.0);
vec2 mmoratMistCurrent = vMmoratMistLayer < 0.5
  ? vec2(0.72, -0.26)
  : (vMmoratMistLayer < 1.5 ? vec2(-0.42, 0.76) : vec2(0.34, 0.91));
float mmoratMistRate = vMmoratMistLayer < 0.5
  ? 1.25
  : (vMmoratMistLayer < 1.5 ? 2.3 : 3.85);
vec2 mmoratMistUv = vec2(
  vMmoratWorld.x + vMmoratWorld.z * 0.37,
  vMmoratWorld.y * 0.58 + vMmoratWorld.z
);
mmoratMistUv = vMmoratMistLayer < 0.5
  ? vec2(mmoratMistUv.x * 0.7, mmoratMistUv.y * 1.05)
  : (vMmoratMistLayer < 1.5
      ? mat2(vec2(0.927, -0.375), vec2(0.375, 0.927)) * vec2(mmoratMistUv.x * 0.9, mmoratMistUv.y * 0.95)
      : vec2(mmoratMistUv.x * 1.05, mmoratMistUv.y * 0.75));
vec2 mmoratMistSeedAxis = normalize(vec2(
  fract(vMmoratMistSeed * 17.31 + 0.19) * 2.0 - 1.0,
  fract(vMmoratMistSeed * 29.17 + 0.73) * 2.0 - 1.0
) + vec2(0.001, 0.0));
vec2 mmoratMistSeedRotated = mat2(vec2(mmoratMistSeedAxis.x, -mmoratMistSeedAxis.y), vec2(mmoratMistSeedAxis.y, mmoratMistSeedAxis.x)) * mmoratMistUv;
float mmoratMistSeedTurn = vMmoratMistLayer < 0.5
  ? 0.16
  : (vMmoratMistLayer < 1.5 ? 0.28 : 0.12);
mmoratMistUv = mix(mmoratMistUv, mmoratMistSeedRotated, mmoratMistSeedTurn);
vec2 mmoratMistTravel = mmoratMistCurrent * uMmoratMistTime * mmoratMistRate;
vec2 mmoratMistBacktrace = mmoratMistUv - mmoratMistTravel;
vec2 mmoratMistCurlA = mmoratMistCurl(
  mmoratMistBacktrace,
  uMmoratMistTime * mix(0.34, 0.68, mmoratMistLayer01),
  vMmoratMistSeed
);
mmoratMistBacktrace -= mmoratMistCurlA * mix(18.0, 8.5, mmoratMistLayer01);
vec2 mmoratMistCurlB = mmoratMistCurl(
  mmoratMistBacktrace + vec2(31.0, -19.0),
  -uMmoratMistTime * mix(0.21, 0.47, mmoratMistLayer01),
  vMmoratMistSeed + 7.13
);
mmoratMistBacktrace -= mmoratMistCurlB * mix(7.5, 3.5, mmoratMistLayer01);
float mmoratMistWarpScale = mix(154.0, 88.0, mmoratMistLayer01);
float mmoratMistWarpA = mmoratMistNoise(
  mmoratMistBacktrace / mmoratMistWarpScale + vMmoratMistSeed * 3.7
);
float mmoratMistWarpB = mmoratMistNoise(
  mmoratMistBacktrace.yx / (mmoratMistWarpScale * 0.83) - vMmoratMistSeed * 2.1 + 17.3
);
vec2 mmoratMistWarp = vec2(mmoratMistWarpA, mmoratMistWarpB) - 0.5;
vec2 mmoratMistWarpedUv = mmoratMistBacktrace + mmoratMistWarp * mix(42.0, 19.0, mmoratMistLayer01);
float mmoratMistWideScale = mix(106.0, 54.0, mmoratMistLayer01);
float mmoratMistMiddleScale = mix(43.0, 23.0, mmoratMistLayer01);
float mmoratMistFineScale = mix(18.0, 9.5, mmoratMistLayer01);
float mmoratMistWide = mmoratMistNoise(
  mmoratMistWarpedUv / mmoratMistWideScale
);
float mmoratMistMiddle = mmoratMistNoise(
  mat2(vec2(0.839, -0.545), vec2(0.545, 0.839)) * mmoratMistWarpedUv / mmoratMistMiddleScale
    + vec2(uMmoratMistTime * -0.013, uMmoratMistTime * 0.009) + 7.1
);
float mmoratMistFine = mmoratMistNoise(
  mat2(vec2(-0.391, -0.921), vec2(0.921, -0.391)) * mmoratMistWarpedUv / mmoratMistFineScale
    + vec2(uMmoratMistTime * 0.027, uMmoratMistTime * -0.019) + 29.4
);
float mmoratMistPixelWorld = max(length(dFdx(mmoratMistUv)), length(GL_DFDY(mmoratMistUv)));
float mmoratMistFineVisibility = 1.0 - smoothstep(
  mmoratMistFineScale * 0.18,
  mmoratMistFineScale * 0.92,
  mmoratMistPixelWorld
);
float mmoratMistFineWeight = 0.18 * mmoratMistFineVisibility;
float mmoratMistFbm = (
  mmoratMistWide * 0.49 + mmoratMistMiddle * 0.33 + mmoratMistFine * mmoratMistFineWeight
) / (0.82 + mmoratMistFineWeight);
float mmoratMistFilament = 1.0 - abs(mmoratMistMiddle * 2.0 - 1.0);
float mmoratMistFarDensity = smoothstep(0.24, 0.72, mmoratMistWide * 0.64 + mmoratMistMiddle * 0.36);
float mmoratMistMiddleDensity = smoothstep(0.25, 0.72, mmoratMistFbm);
float mmoratMistNearDensity = smoothstep(
  0.28,
  0.74,
  mmoratMistFbm * 0.8 + mmoratMistFilament * 0.08 + mmoratMistFine * 0.12 * mmoratMistFineVisibility
);
float mmoratMistCloud = vMmoratMistLayer < 0.5
  ? mmoratMistFarDensity
  : (vMmoratMistLayer < 1.5 ? mmoratMistMiddleDensity : mmoratMistNearDensity);
float mmoratMistDissipation = 0.9 + 0.1 * sin(
  uMmoratMistTime * mix(0.16, 0.31, mmoratMistLayer01)
    + vMmoratMistSeed * 5.7 + mmoratMistWarpB * 3.14159
);
float mmoratMistSoftBody = smoothstep(0.12, 0.72, mmoratMistCloud);
float mmoratMistDenseCore = smoothstep(0.42, 0.86, mmoratMistCloud);
float mmoratVapour = mmoratMistSoftBody * (0.32 + mmoratMistDenseCore * 0.68)
  * mmoratMistDissipation;
float mmoratLayerOpacity = vMmoratMistLayer < 0.5
  ? 0.58
  : (vMmoratMistLayer < 1.5 ? 0.82 : 0.88);
diffuseColor.a *= mmoratVapour * mmoratLayerOpacity * mix(1.0, 0.86, uMmoratMistNight);
vec2 mmoratMistDensityGradient = vec2(dFdx(mmoratMistCloud), GL_DFDY(mmoratMistCloud));
float mmoratMistShoulderLight = clamp(
  dot(mmoratMistDensityGradient, normalize(vec2(-0.62, -0.78))) * 2.2,
  -0.07,
  0.11
);
float mmoratMistCoreOcclusion = smoothstep(0.58, 0.92, mmoratMistCloud);
diffuseColor.rgb *= 0.91 + mmoratMistCloud * 0.13 + mmoratMistShoulderLight
  - mmoratMistCoreOcclusion * 0.055;
diffuseColor.rgb = mix(
  diffuseColor.rgb,
  mix(diffuseColor.rgb, uMmoratHaze, 0.42),
  clamp(uMmoratMistNight, 0.0, 1.0) * 0.18
);
if (diffuseColor.a < 0.002) discard;
float specularStrength;
	specularStrength = 1.0;
	ReflectedLight reflectedLight = ReflectedLight( vec3( 0.0 ), vec3( 0.0 ), vec3( 0.0 ), vec3( 0.0 ) );
		reflectedLight.indirectDiffuse += vec3( 1.0 );
	reflectedLight.indirectDiffuse *= diffuseColor.rgb;
	vec3 outgoingLight = reflectedLight.indirectDiffuse;
pc_fragColor = vec4( outgoingLight, diffuseColor.a );
pc_fragColor.rgb = mmoratAirMix(
  pc_fragColor.rgb,
  uMmoratHaze,
  mmoratHazeF()
);
pc_fragColor = linearToOutputTexel( pc_fragColor );

	// godot-flui's comic pipeline: the unlit colour through the central comic ink of this surface role.
	vec3 fluitown_comic_form = normalize(cross(dFdx(comic_position), dFdy(comic_position)));
	if (dot(fluitown_comic_form, (INV_VIEW_MATRIX * vec4(VIEW, 0.0)).xyz) < 0.0) fluitown_comic_form = -fluitown_comic_form;
	pc_fragColor.rgb = comic_surface(pc_fragColor.rgb, comic_position, fluitown_comic_form, NORMAL, VIEW, comic_role);

	ALBEDO = pc_fragColor.rgb;
	ALPHA = pc_fragColor.a;
}
