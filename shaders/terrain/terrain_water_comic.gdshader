// Generated from the original's captured three.js program, then maintained by hand in this project.
// Source: program-007 (MeshStandardMaterial terrain water), comic look

shader_type spatial;
stencil_mode write, compare_always, 2;
uniform bool fluitown_mirrored = false;
#define GL_DFDY(x) (fluitown_mirrored ? dFdy(x) : -dFdy(x))
#define COMIC_DEFAULT_ROLE 3
#include "res://assets/shaders/comic_style.gdshaderinc"
#include "res://shaders/studio/studio_grade.gdshaderinc"
/// STUDIO: live colour grade of the water pigment (hue turn, saturation, value, depth contrast).
uniform vec4 studio_grade_water = vec4(0.0, 1.0, 1.0, 1.0);
render_mode cull_disabled, depth_draw_opaque, diffuse_burley, specular_schlick_ggx;

// Yawed-world matrix of the original scene root (three view space = yawed world, see RENDER_ARCHITECTURE).
uniform mat4 u_three_view;
// three uploads isOrthographic = camera.isOrthographicCamera, false for the terrain's plain Camera.
const bool isOrthographic = false;
const vec3 cameraPosition = vec3(0.0);
uniform vec4 uMmoratTerrainContour;
uniform vec3 three_diffuse;
uniform vec3 three_emissive;
uniform float three_roughness;
uniform float three_metalness;
uniform float three_opacity;
uniform float uMmoratTime;
uniform float uMmoratNight;
uniform vec3 uMmoratFoam;
uniform vec3 uMmoratShallow;
uniform vec3 uMmoratDeep;
uniform vec3 uMmoratWaterLightDirection;
uniform vec4 uMmoratHazeCfg;
uniform vec3 uMmoratHaze;
uniform vec4 uMmoratHazeBody;
uniform vec4 uMmoratWaterStyle;
uniform float uMmoratWaterSky;
uniform float uMmoratViewZoom;

// Sun shadow: directionalShadowMatrix maps yawed-world positions to [0,1] uv + normalised light depth.
uniform mat4 directionalShadowMatrix;
uniform float u_shadow_intensity = 1.0;
uniform float u_shadow_bias = -0.0003;
uniform float u_shadow_normal_bias = 1.8;
uniform float u_shadow_radius = 1.0;
uniform vec2 u_shadow_map_size = vec2(1024.0);
// Packed light depth (r = floor(d*1024)/1024, g = fract(d*1024)) rendered by the Godot shadow pass.
uniform sampler2D u_shadow_depth : filter_nearest, repeat_disable;

// Visual enhancements beyond the original (TerrainVisualQuality). The defaults reproduce the original exactly:
// products with 1.0 are exact and the 5-tap shadow branch is the verbatim one.
// Internal render pixels per output pixel (supersampling): pixel-footprint decisions stay those of the original.
uniform float fluitown_lod_scale = 1.0;
// Sun shadow PCF: 0 = three's 5-tap Vogel disk; more = the same rotated Vogel disk with this many taps.
uniform int fluitown_shadow_taps = 0;



varying vec3 vViewPosition;
varying vec3 vMmoratWorld;
varying vec4 vMmoratWater;
varying vec2 vMmoratWaterFold;
varying vec3 vMmoratWaterReflection;
varying vec4 vColor;
varying vec3 vNormal;
varying vec4 vDirectionalShadowCoord;

const float fluitown_WATERFALL_FLOW_TIME_SCALE = 0.34;
struct ReflectedLight {
	vec3 directDiffuse;
	vec3 directSpecular;
	vec3 indirectDiffuse;
	vec3 indirectSpecular;
};
struct PhysicalMaterial {
	vec3 diffuseColor;
	vec3 diffuseContribution;
	vec3 specularColor;
	vec3 specularColorBlended;
	float roughness;
	float metalness;
	float specularF90;
	float dispersion;
};
vec3 transformNormalByInverseViewMatrix( in vec3 normal, in mat4 viewMatrix ) {
	return normalize( ( vec4( normal, 0.0 ) * viewMatrix ).xyz );
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
vec4 mmoratSurfaceWaterField(vec2 p, float t, float fallFlowAlignment) {
  float pixelWorld = max(length(dFdx(p)), length(GL_DFDY(p)));
  float bodyVisibility = 1.0 - smoothstep(5.0, 18.0, pixelWorld);
  float fieldT = t * (1.0 - clamp(fallFlowAlignment, 0.0, 1.0));
  vec2 warpUvA = p / 238.0 + vec2(fieldT * 0.014, -fieldT * 0.009);
  vec2 warpUvB = mat2(vec2(0.682, -0.731), vec2(0.731, 0.682)) * p / 211.0
    + vec2(-fieldT * 0.011, fieldT * 0.016) + 19.7;
  float warpA = mmoratNoise(warpUvA);
  float warpB = mmoratNoise(warpUvB);
  vec2 warped = p + (vec2(warpA, warpB) - 0.5) * 32.0;
  warped = mix(p, warped, bodyVisibility);
  float broad = mmoratNoise(
    warped / 108.0 + vec2(fieldT * 0.022, -fieldT * 0.013) + 7.3
  );
  float middle = mmoratNoise(
    mat2(vec2(0.819, -0.574), vec2(0.574, 0.819)) * warped / 43.0
      + vec2(-fieldT * 0.037, fieldT * 0.024) + 31.1
  );
  broad = mix(0.5, broad, bodyVisibility);
  middle = mix(0.5, middle, bodyVisibility);
  float fineVisibility = 1.0 - smoothstep(
    2.80,
    11.76,
    pixelWorld
  );
  float ridgeVisibility = 1.0 - smoothstep(3.0, 11.5, pixelWorld);
  float fine = 0.5;
  if (fineVisibility > 0.0) {
    fine = mmoratNoise(
      mat2(vec2(-0.438, -0.899), vec2(0.899, -0.438)) * warped / 22.0
        + vec2(fieldT * 0.061, fieldT * 0.043) + 53.9
    );
    fine = mix(0.5, fine, fineVisibility);
  }
  float rippleA = 0.5 + 0.5 * sin(
    dot(warped, vec2(0.115, 0.039)) + fieldT * 0.74
      + (middle - 0.5) * 5.4 + (fine - 0.5) * 1.7
  );
  float rippleB = 0.5 + 0.5 * sin(
    dot(warped, vec2(-0.059, 0.145)) - fieldT * 0.57
      + (broad - 0.5) * 5.0 + (middle - 0.5) * 1.6
  );
  rippleA = mix(0.5, rippleA, ridgeVisibility);
  rippleB = mix(0.5, rippleB, ridgeVisibility);
  float height = smoothstep(
    0.08,
    0.92,
    0.5 + (rippleA - 0.5) * 0.28 + (rippleB - 0.5) * 0.2
      + (broad - 0.5) * 0.13 + (middle - 0.5) * 0.1 + (fine - 0.5) * 0.14
  );
  return vec4(
    height,
    broad * 0.56 + middle * 0.44,
    rippleA,
    rippleB
  );
}
vec3 mmoratPerturbWaterNormal(vec3 surfacePosition, vec3 surfaceNormal, float height, float strength) {
  vec3 sigmaX = dFdx(surfacePosition);
  vec3 sigmaY = GL_DFDY(surfacePosition);
  vec3 r1 = cross(sigmaY, surfaceNormal);
  vec3 r2 = cross(surfaceNormal, sigmaX);
  float determinant = dot(sigmaX, r1);
  vec2 gradient = vec2(dFdx(height), GL_DFDY(height));
  vec3 mappedGradient = sign(determinant) * (gradient.x * r1 + gradient.y * r2);
  return normalize(max(abs(determinant), 0.00001) * surfaceNormal - mappedGradient * strength);
}


void vertex() {
	vec3 position = VERTEX;
	vec3 normal = NORMAL;
	vec3 color = CUSTOM0.rgb;
	vec4 aWater = CUSTOM1;
	vec2 aWaterFold = UV;
	vec3 aWaterReflection = CUSTOM2.rgb;
	mat4 modelMatrix = u_three_view * MODEL_MATRIX;
	mat4 viewMatrix = mat4(1.0);
	mat4 modelViewMatrix = u_three_view * MODEL_MATRIX;
	mat3 normalMatrix = transpose(inverse(mat3(u_three_view * MODEL_MATRIX)));

	vColor = vec4( 1.0 );
	vColor.rgb *= color;
vec3 objectNormal = vec3( normal );
vec3 transformedNormal = objectNormal;
transformedNormal = normalMatrix * transformedNormal;
	vNormal = normalize( transformedNormal );
vec3 transformed = vec3( position );
vec2 mmoratWaterContourOffset = vec2(0.0);
if (uMmoratTerrainContour.x > 0.001) {
  mmoratWaterContourOffset = mmoratTerrainContourWarp(position.xz);
  transformed.xz += mmoratWaterContourOffset;
}
vMmoratWorld = vec3(transformed);
vMmoratWater = aWater;
vMmoratWaterFold = aWaterFold + mmoratWaterContourOffset;
vMmoratWaterReflection = aWaterReflection;
vec4 mvPosition = vec4( transformed, 1.0 );
mvPosition = modelViewMatrix * mvPosition;

	vViewPosition = - mvPosition.xyz;
	vec4 worldPosition = vec4( transformed, 1.0 );
	worldPosition = modelMatrix * worldPosition;
		vec3 shadowWorldNormal = transformNormalByInverseViewMatrix( transformedNormal, viewMatrix );
	vec4 shadowWorldPosition;
			shadowWorldPosition = worldPosition + vec4( shadowWorldNormal * u_shadow_normal_bias, 0 );
			vDirectionalShadowCoord = directionalShadowMatrix * shadowWorldPosition;

	VERTEX = transformed;
	COMIC_VERTEX
	POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));

}

void fragment() {

	vec4 pc_fragColor = vec4(0.0);
	float mmoratSharedWaterWave = 0.0;
	float mmoratSharedWaterDepth = 0.0;
	float mmoratSharedWaterShore = 0.0;
	float mmoratSharedWaterFalling = 0.0;
	float mmoratSharedWaterCrest = 0.0;
	float mmoratSharedWaterCross = 0.0;
	float mmoratSharedWaterFoam = 0.0;
	float mmoratSharedWaterFallTurn = 0.0;
	float mmoratSharedWaterInteriorMotion = 0.0;
	float mmoratSharedWaterStableShore = 0.0;
	float mmoratSharedWaterGroundBlend = 0.0;
	float mmoratSharedWaterAnimatedMarkVisibility = 0.0;
	vec3 mmoratWaterVertexPigment = vec3(0.0);
	vec3 clearcoatSpecularDirect = vec3( 0.0 );
	vec3 clearcoatSpecularIndirect = vec3( 0.0 );
	vec3 sheenSpecularDirect = vec3( 0.0 );
	vec3 sheenSpecularIndirect = vec3(0.0 );

	vec4 diffuseColor = vec4( three_diffuse, three_opacity );
	ReflectedLight reflectedLight = ReflectedLight( vec3( 0.0 ), vec3( 0.0 ), vec3( 0.0 ), vec3( 0.0 ) );
	vec3 totalEmissiveRadiance = three_emissive;
	diffuseColor *= vColor;
{
  mmoratWaterVertexPigment = diffuseColor.rgb;
  float fallingPath = step(vMmoratWater.y, -0.001);
  float t = uMmoratTime * uMmoratWaterStyle.x * 1.12;
  float fallT = t * fluitown_WATERFALL_FLOW_TIME_SCALE;
  vec2 surfaceUv = vMmoratWaterFold;
  float sheetAxis = step(0.0, vMmoratWater.z);
  float sheetAcross = mix(vMmoratWorld.z, vMmoratWorld.x, sheetAxis);
  float fallDistance = max(vMmoratWater.w - vMmoratWorld.y, 0.0);
  float chasmPath = step(1.001, -vMmoratWater.y);
  float rapidTurn = smoothstep(0.0, 0.34, clamp(vMmoratWater.x, 0.0, 1.0));
  float fallFieldTurn = fallingPath * rapidTurn;
  mmoratSharedWaterFallTurn = fallFieldTurn;
  float falling = fallFieldTurn;
  float fallAdvection = fallT * 96.0;
  vec2 foldDelta = surfaceUv - vMmoratWorld.xz;
  float fallArc = length(foldDelta);
  vec2 foldDirection = foldDelta / max(fallArc, 0.0001);
  const float fallPatternStretch = 0.34;
  vec2 fallCrestUv = surfaceUv - foldDirection * fallArc;
  vec2 fallSurfaceUv = fallCrestUv
    + foldDirection * (fallArc * fallPatternStretch - fallAdvection);
  vec2 fallUv = vec2(sheetAcross, fallArc * fallPatternStretch - fallAdvection);
  vec2 continuousWaterUv = mix(surfaceUv, fallSurfaceUv, fallFieldTurn);
  float animatedMarkVisibility = smoothstep(
    0.38,
    0.72,
    uMmoratViewZoom
  );
  mmoratSharedWaterAnimatedMarkVisibility = animatedMarkVisibility;
  float fallFlowAlignment = smoothstep(0.08, 0.52, falling);
  vec4 waterLayers = mmoratSurfaceWaterField(continuousWaterUv, t, fallFlowAlignment);
  mmoratSharedWaterWave = waterLayers.x;
  float w = mmoratSharedWaterWave;
  float fallSkyContinuity = 1.0 - falling * 0.22;
  float skyExposure = (1.0 - falling) + falling * fallSkyContinuity;
  float ambientSkyExposure = mix(1.0, fallSkyContinuity, falling);
  float waterNight = clamp(uMmoratNight, 0.0, 1.0);
  float surfaceFallingFlow = clamp(waterLayers.z * 0.64 + waterLayers.w * 0.36, 0.0, 1.0);
  float curtainDetail = 0.5;
  float curtainFilament = 0.5;
  if (fallingPath > 0.5) {
    curtainDetail = mmoratNoise(
      vec2(fallUv.x / 15.5, fallUv.y / 152.0)
    );
    curtainFilament = mmoratNoise(
      vec2(fallUv.x / 6.2 + (curtainDetail - 0.5) * 0.72, fallUv.y / 224.0) + 23.1
    );
  }
  float curtainFlow = clamp(curtainDetail * 0.46 + curtainFilament * 0.54, 0.0, 1.0);
  float fallPatternTurn = smoothstep(0.1, 0.88, falling);
  float fallingFlow = mix(surfaceFallingFlow, curtainFlow, fallPatternTurn);
  float groundBlend = clamp(-vMmoratWater.x, 0.0, 1.0);
  float shore = clamp(vMmoratWater.x, 0.0, 1.0);
  float encodedDepth = clamp(abs(vMmoratWater.y), 0.0, 1.0);
  float topologyDepth = mix(
    clamp(vMmoratWaterReflection.x, 0.0, 1.0),
    encodedDepth,
    falling
  );
  float worldDepthSignal = mmoratNoise(
    mat2(vec2(0.788, -0.616), vec2(0.616, 0.788)) * surfaceUv
      / 214.0 + 41.7
  );
  float worldDepth = mix(
    0.240,
    0.840,
    smoothstep(0.12, 0.88, worldDepthSignal)
  );
  float packedBank = floor(vMmoratWater.w + 0.5);
  float bankMask = mod(packedBank, 16.0);
  float bankN = mod(bankMask, 2.0);
  float bankE = mod(floor(bankMask / 2.0), 2.0);
  float bankS = mod(floor(bankMask / 4.0), 2.0);
  float bankW = mod(floor(bankMask / 8.0), 2.0);
  float radiusNw = mod(floor(packedBank / 16.0), 16.0) / 32.0;
  float radiusNe = mod(floor(packedBank / 256.0), 16.0) / 32.0;
  float radiusSe = mod(floor(packedBank / 4096.0), 16.0) / 32.0;
  float radiusSw = mod(floor(packedBank / 65536.0), 16.0) / 32.0;
  vec2 cellUv = clamp(vMmoratWaterReflection.yz, 0.0, 1.0);
  float tileSize = mix(max(abs(vMmoratWater.z), 1.0), 62.5, fallingPath);
  float bankDistance = tileSize * 4.0;
  bankDistance = mix(bankDistance, min(bankDistance, cellUv.y * tileSize), bankN);
  bankDistance = mix(bankDistance, min(bankDistance, (1.0 - cellUv.x) * tileSize), bankE);
  bankDistance = mix(bankDistance, min(bankDistance, (1.0 - cellUv.y) * tileSize), bankS);
  bankDistance = mix(bankDistance, min(bankDistance, cellUv.x * tileSize), bankW);
  float roundedNw = max(radiusNw - length(cellUv - vec2(radiusNw)), 0.0) * tileSize;
  float roundedNe = max(radiusNe - length(cellUv - vec2(1.0 - radiusNe, radiusNe)), 0.0) * tileSize;
  float roundedSe = max(radiusSe - length(cellUv - vec2(1.0 - radiusSe)), 0.0) * tileSize;
  float roundedSw = max(radiusSw - length(cellUv - vec2(radiusSw, 1.0 - radiusSw)), 0.0) * tileSize;
  float roundedGateNw = step(0.001, radiusNw) * step(cellUv.x, radiusNw) * step(cellUv.y, radiusNw);
  float roundedGateNe = step(0.001, radiusNe) * step(1.0 - cellUv.x, radiusNe) * step(cellUv.y, radiusNe);
  float roundedGateSe = step(0.001, radiusSe) * step(1.0 - cellUv.x, radiusSe) * step(1.0 - cellUv.y, radiusSe);
  float roundedGateSw = step(0.001, radiusSw) * step(cellUv.x, radiusSw) * step(1.0 - cellUv.y, radiusSw);
  bankDistance = mix(bankDistance, min(bankDistance, roundedNw), roundedGateNw);
  bankDistance = mix(bankDistance, min(bankDistance, roundedNe), roundedGateNe);
  bankDistance = mix(bankDistance, min(bankDistance, roundedSe), roundedGateSe);
  bankDistance = mix(bankDistance, min(bankDistance, roundedSw), roundedGateSw);
  float shoreMeanderField = clamp(
    (mmoratNoise(
      vMmoratWorld.xz / 34.0 + 19.7
    ) - 0.5) * 2.10 + 0.5,
    0.0,
    1.0
  );
  float staticShoreMeander = smoothstep(0.0, 1.0, shoreMeanderField)
    * 13.00
    * (1.0 - fallingPath);
  float organicBankDistance = max(0.0, bankDistance - staticShoreMeander);
  float horizontalContinuity = 1.0 - falling;
  float stableShore = (1.0 - fallingPath) * (1.0 - smoothstep(
    0.20,
    24.50,
    organicBankDistance
  ));
  float interiorMotion = mix(smoothstep(
    5.20,
    26.00,
    organicBankDistance
  ), 1.0, fallingPath);
  mmoratSharedWaterStableShore = stableShore;
  mmoratSharedWaterInteriorMotion = interiorMotion;
  float worldDepthBlend = 0.820
    * (1.0 - fallingPath) * (1.0 - stableShore) * (1.0 - shore * 0.72);
  float depth = mix(topologyDepth, worldDepth, worldDepthBlend);
  float deepAbsorption = smoothstep(0.16, 0.94, depth)
    * (1.0 - shore * 0.58) * (1.0 - falling);
  diffuseColor.rgb = mix(diffuseColor.rgb, uMmoratDeep, deepAbsorption * 0.52);
  diffuseColor.rgb *= mix(1.045, 0.91, depth);
  diffuseColor.rgb = mix(
    diffuseColor.rgb,
    uMmoratShallow,
    shore * 0.3 * (1.0 - falling)
  );
  float waterTrough = 1.0 - smoothstep(0.19, 0.48, w);
  float waterRidge = smoothstep(0.58, 0.86, w);
  diffuseColor.rgb *= clamp(
    0.975 + (waterLayers.y - 0.5) * 0.075 + (w - 0.5) * 0.025
      - waterTrough * 0.035
      + waterRidge * 0.042,
    0.86,
    1.11
  );
  vec2 viewAcross = normalize(vec2(-0.42, 0.91));
  vec2 underUv = surfaceUv + viewAcross * (5.20 * depth);
  float subsurface = 0.0;
  if (horizontalContinuity > 0.001) {
    float underBody = mmoratNoise(
      mat2(vec2(0.755, -0.656), vec2(0.656, 0.755)) * underUv / 88.0
        + vec2(-t * 0.017, t * 0.012)
        + (waterLayers.yz - 0.5) * 0.21
    );
    float underPocket = 1.0 - abs(underBody * 2.0 - 1.0);
    subsurface = smoothstep(0.36, 0.82, underBody * 0.7 + underPocket * 0.3)
      * horizontalContinuity;
  }
  diffuseColor.rgb *= 0.975 + subsurface * 0.05 * depth * (1.0 - shore * 0.55) * interiorMotion;
  diffuseColor.rgb = mix(
    diffuseColor.rgb,
    uMmoratShallow,
    falling * fallingFlow * 0.13
  );
  float fallBodyFold = falling * clamp(
    (waterLayers.z - waterLayers.w) * 0.035
      + (curtainDetail - 0.5) * 0.11
      + (curtainFilament - 0.5) * 0.15 * fallPatternTurn,
    -0.12,
    0.12
  );
  diffuseColor.rgb *= 1.0 + fallBodyFold;
  float fallAeration = falling * animatedMarkVisibility
    * mix(0.055, 0.23, smoothstep(0.5, 0.8, curtainFlow));
  diffuseColor.rgb = mix(
    diffuseColor.rgb,
    mix(uMmoratShallow, uMmoratFoam, 0.32),
    fallAeration
  );
  vec2 crownSlope = vec2(dFdx(w), GL_DFDY(w)) * fluitown_lod_scale;
  float crownTilt = clamp((crownSlope.x * 0.38 - crownSlope.y) * 30.0, -1.0, 1.0)
    * mix(0.18, 1.0, interiorMotion) * animatedMarkVisibility;
  float crownShare = depth * uMmoratWaterSky
    * (ambientSkyExposure + falling * 0.14)
    * (1.0 + crownTilt * 0.3)
    * (1.0 - waterNight * 0.42);
  vec3 crownSky = mix(
    uMmoratHaze,
    mix(uMmoratHaze, uMmoratFoam, 0.04),
    waterNight
  ) * (1.0 + crownTilt * 0.07);
  diffuseColor.rgb = mix(diffuseColor.rgb, crownSky, clamp(crownShare, 0.0, 1.0));
  float silhouetteReflection =
    clamp(vMmoratWaterReflection.x, 0.0, 1.0) * depth * (1.0 - falling);
  vec3 reflectedSilhouette = mix(diffuseColor.rgb * 0.58, uMmoratHaze * 0.34, 0.28);
  diffuseColor.rgb = mix(
    diffuseColor.rgb,
    reflectedSilhouette,
    silhouetteReflection * (0.2 + (1.0 - shore) * 0.16)
  );
  vec3 mmoratWaterBodyPigment = diffuseColor.rgb;
  float mmoratWaterBodyLuma = dot(mmoratWaterBodyPigment, vec3(0.2126, 0.7152, 0.0722));
  float crest = smoothstep(0.72, 0.94, w);
  float shoreLace = waterLayers.z * 0.55 + waterLayers.w * 0.25 + w * 0.2;
  float shoreFoam = shore * interiorMotion * smoothstep(0.57, 0.84, shoreLace);
  float fallingFoam = falling * (
    0.035
      + fallingFlow * 0.085
      + smoothstep(0.62, 0.86, curtainFlow) * 0.11
      + shore * 0.055
  );
  float foamAmount = clamp(
    (
      stableShore * 0.010
        + (crest * interiorMotion * (0.025 + 0.1 * shore) + shoreFoam * 0.3)
          * animatedMarkVisibility
    ) * uMmoratWaterStyle.y + fallingFoam * animatedMarkVisibility,
    0.0,
    0.46
  );
  foamAmount *= mix(1.0, 0.64, waterNight);
  diffuseColor.rgb = mix(diffuseColor.rgb, uMmoratFoam, foamAmount);
  float glintRibbon = crest * crest * smoothstep(0.5, 0.84, waterLayers.z)
    * mix(0.08, 1.0, interiorMotion)
    * (skyExposure + falling * 0.18)
    * mix(1.0, 0.12, waterNight);
  diffuseColor.rgb += uMmoratFoam
    * (glintRibbon * animatedMarkVisibility * 0.13 * uMmoratWaterStyle.z
      * (0.45 + 0.55 * (1.0 - depth)));
  float ca = waterLayers.z;
  float cb = waterLayers.w;
  float surfaceCausticAgreement = smoothstep(0.84, 0.985, 1.0 - abs(ca - cb));
  float fallCausticAgreement = smoothstep(0.66, 0.94, 1.0 - abs(ca - cb));
  float fallFilamentAgreement = smoothstep(0.5, 0.8, curtainFlow);
  float fallPatternAgreement = mix(fallCausticAgreement, fallFilamentAgreement, fallPatternTurn);
  float causticAgreement = mix(surfaceCausticAgreement, fallPatternAgreement, falling);
  float surfaceCausticDepth = smoothstep(0.1, 0.8, 1.0 - depth);
  float causticDepth = mix(surfaceCausticDepth, 0.86, falling);
  float causticCarrier = mix(horizontalContinuity, 0.88, falling);
  float caustic = causticAgreement * causticDepth * interiorMotion * causticCarrier;
  diffuseColor.rgb += uMmoratFoam
    * (caustic * animatedMarkVisibility * uMmoratWaterStyle.w * 0.68
      * skyExposure * (1.0 - waterNight * 0.96));
  float fallRidgeA = smoothstep(0.74, 0.94, ca);
  float fallRidgeB = smoothstep(0.74, 0.94, cb);
  float fallCarriedNet = mix(max(fallRidgeA, fallRidgeB), fallFilamentAgreement, fallPatternTurn);
  float fallCartoonNet = falling * fallCarriedNet * animatedMarkVisibility
    * skyExposure * (1.0 - waterNight * 0.9);
  diffuseColor.rgb = mix(diffuseColor.rgb, uMmoratFoam, fallCartoonNet * 0.42);
  float mmoratWaterWhiteBand = max(stableShore, shore * 0.55);
  float mmoratWaterWhiteBudget = mix(1.0, 3.0, mmoratWaterWhiteBand)
    * max(mmoratWaterBodyLuma, 0.0901)
    + falling * 0.22;
  float mmoratWaterWhiteLift =
    dot(diffuseColor.rgb, vec3(0.2126, 0.7152, 0.0722)) - mmoratWaterBodyLuma;
  diffuseColor.rgb = mix(
    mmoratWaterBodyPigment,
    diffuseColor.rgb,
    mmoratWaterWhiteLift > 1e-5
      ? min(1.0, mmoratWaterWhiteBudget / mmoratWaterWhiteLift)
      : 1.0
  );
  mmoratSharedWaterDepth = depth;
  mmoratSharedWaterShore = shore;
  mmoratSharedWaterFalling = falling;
  mmoratSharedWaterCrest = crest;
  mmoratSharedWaterCross = mix(waterLayers.z, fallingFlow, falling);
  mmoratSharedWaterFoam = foamAmount;
  mmoratSharedWaterGroundBlend = groundBlend;
  {
    // Fluitown comic look: clear shallow water, the wet ground at the waterline, a lapping foam line.
    float fluitownBank = smoothstep(0.0, 1.0, groundBlend);
    vec3 fluitownShallow = mix(diffuseColor.rgb, uMmoratShallow * 1.06, 0.6);
    diffuseColor.rgb = mix(diffuseColor.rgb, fluitownShallow, fluitownBank * 0.75);
    diffuseColor.rgb = mix(diffuseColor.rgb, mmoratWaterVertexPigment * 0.8, smoothstep(0.9, 1.0, groundBlend) * 0.8);
    float fluitownSwash = mmoratNoise(vMmoratWorld.xz / 46.0 + 3.7);
    float fluitownLap = 0.72 + 0.07 * sin(t * 0.85 + fluitownSwash * 6.2831853);
    float fluitownLine = 1.0 - smoothstep(0.015, 0.075, abs(groundBlend - fluitownLap));
    float fluitownLace = smoothstep(0.3, 0.62, waterLayers.z * 0.55 + w * 0.45);
    float fluitownFoam = fluitownLine * mix(0.55, 1.0, fluitownLace) * mix(0.6, 1.0, animatedMarkVisibility)
      * (1.0 - falling) * mix(1.0, 0.55, waterNight);
    diffuseColor.rgb = mix(diffuseColor.rgb, uMmoratFoam, fluitownFoam * 0.72);
  }
  float chasmEntry = chasmPath;
  float cloudEntry = falling * chasmEntry * smoothstep(0.42, 1.0, shore);
  if (cloudEntry > 0.0) {
    vec3 entryTint = mix(uMmoratDeep, uMmoratFoam, 0.58 + curtainFlow * 0.2);
    float entryAbsorption = mix(0.48, 0.7, curtainDetail) * cloudEntry;
    diffuseColor.rgb = mix(diffuseColor.rgb, entryTint, entryAbsorption);
    diffuseColor.rgb *= mix(1.0, 0.92, cloudEntry);
  }
}
float roughnessFactor = three_roughness;
float mmoratWaterSkyExposure = (1.0 - mmoratSharedWaterFalling)
  * step(0.001, vMmoratWater.z);
roughnessFactor = clamp(
  roughnessFactor
    + clamp(uMmoratNight, 0.0, 1.0)
      * mmoratWaterSkyExposure
      * (0.075 + (1.0 - mmoratSharedWaterCrest) * 0.035)
      * clamp(uMmoratWaterStyle.z, 0.45, 1.7),
  0.34,
  0.72
);
float metalnessFactor = three_metalness;
float faceDirection = (FRONT_FACING != fluitown_mirrored) ? 1.0 : - 1.0;
	vec3 normal = normalize( vNormal );
vec3 nonPerturbedNormal = normal;
{
  vec3 mmoratWaterGeometryNormal = normalize(normal);
  float mmoratWaterNormalTurn = smoothstep(0.02, 0.72, mmoratSharedWaterFallTurn);
  vec3 mmoratWaterBaseNormal = mmoratWaterGeometryNormal;
  vec3 mmoratWaterPerturbedNormal = mmoratPerturbWaterNormal(
    -vViewPosition,
    mmoratWaterBaseNormal,
    mmoratSharedWaterWave,
    1.800 * clamp(uMmoratWaterStyle.x, 0.55, 1.4)
      * mix(0.0, 1.0, mmoratSharedWaterInteriorMotion)
      * mmoratSharedWaterAnimatedMarkVisibility
      * (1.0 - clamp(-vMmoratWater.x, 0.0, 1.0))
  );
  float mmoratWaterfallSheetNormal = step(vMmoratWater.y, -0.001);
  vec3 mmoratWaterfallTurnedNormal = normalize(mix(
    vec3(0.0, 1.0, 0.0),
    mmoratWaterPerturbedNormal,
    mmoratWaterNormalTurn
  ));
  normal = normalize(mix(
    mmoratWaterPerturbedNormal,
    mmoratWaterfallTurnedNormal,
    mmoratWaterfallSheetNormal
  ));
}
PhysicalMaterial material;
material.diffuseColor = diffuseColor.rgb;
material.diffuseContribution = diffuseColor.rgb * ( 1.0 - metalnessFactor );
material.metalness = metalnessFactor;
vec3 dxy = max( abs( dFdx( nonPerturbedNormal ) ), abs( GL_DFDY( nonPerturbedNormal ) ) );
float geometryRoughness = max( max( dxy.x, dxy.y ), dxy.z ) * fluitown_lod_scale;
material.roughness = max( roughnessFactor, 0.0525 );material.roughness += geometryRoughness;
material.roughness = min( material.roughness, 1.0 );
	material.specularColor = vec3( 0.04 );
	material.specularColorBlended = mix( material.specularColor, diffuseColor.rgb, metalnessFactor );
	material.specularF90 = 1.0;
vec3 geometryPosition = - vViewPosition;
vec3 geometryNormal = normal;
vec3 geometryViewDir = ( isOrthographic ) ? vec3( 0, 0, 1 ) : normalize( vViewPosition );
vec3 geometryClearcoatNormal = vec3( 0.0 );


// ── godot-flui's comic pipeline (assets/shaders/comic_style.gdshaderinc), water role ──
mat3 fluitownThreeToWorld = transpose(mat3(u_three_view));
vec3 fluitownFace = cross(dFdx(VERTEX), dFdy(VERTEX));
fluitownFace *= inversesqrt(max(dot(fluitownFace, fluitownFace), 1e-30));
float fluitownSide = dot(fluitownFace, VIEW) * dot(fluitownFace, (VIEW_MATRIX * vec4(fluitownThreeToWorld * nonPerturbedNormal, 0.0)).xyz) < 0.0 ? -1.0 : 1.0;
vec3 fluitownMirror = fluitownSide < 0.0 ? fluitownFace : vec3(0.0);
vec3 fluitownMirrorWorld = (INV_VIEW_MATRIX * vec4(fluitownMirror, 0.0)).xyz;
vec3 fluitownForm = normalize(fluitownThreeToWorld * nonPerturbedNormal);
fluitownForm = normalize(fluitownForm - 2.0 * dot(fluitownForm, fluitownMirrorWorld) * fluitownMirrorWorld);
NORMAL = normalize((VIEW_MATRIX * vec4(normalize(fluitownThreeToWorld * normal), 0.0)).xyz);
NORMAL = normalize(NORMAL - 2.0 * dot(NORMAL, fluitownMirror) * fluitownMirror);
vec3 fluitownPigment = max(diffuseColor.rgb, vec3(0.0));
if (vMmoratWater.y < -0.001) {
  // A falling sheet keeps the original's value ceiling over its vertex pigment (foam and aeration stay water).
  float fluitownFallPigment = max(dot(mmoratWaterVertexPigment, vec3(0.2126, 0.7152, 0.0722)), 0.001);
  float fluitownFallValue = max(dot(fluitownPigment, vec3(0.2126, 0.7152, 0.0722)), 0.001);
  float fluitownFallEntry = mmoratSharedWaterFalling * step(1.001, -vMmoratWater.y) * smoothstep(0.46, 0.96, mmoratSharedWaterShore);
  float fluitownFallBound = 0.97 + (mmoratSharedWaterWave - 0.5) * 0.06 * mmoratSharedWaterAnimatedMarkVisibility;
  float fluitownFallCeiling = fluitownFallPigment * fluitownFallBound * mix(
    1.42 + mmoratSharedWaterFoam * 0.52,
    mix(0.72, 0.42, fluitownFallEntry),
    clamp(uMmoratNight, 0.0, 1.0)
  );
  fluitownPigment *= mix(1.0, min(1.0, fluitownFallCeiling / fluitownFallValue), smoothstep(0.42, 0.94, mmoratSharedWaterFallTurn));
}
// STUDIO: water grade, then the terrain's global grade.
fluitownPigment = studio_grade_one(studio_grade_one(fluitownPigment, vec4(studio_grade_water.xyz, 0.0)), studio_grade_global);
vec3 fluitownComicPigment = fluitownPigment * 0.3183098861837907;
ALBEDO = comic_surface(fluitownComicPigment, comic_position, fluitownForm, NORMAL, VIEW, comic_role);
ROUGHNESS = material.roughness;
SPECULAR = 0.5;
vec3 fluitownAir = mmoratAirMix(fluitownPigment, uMmoratHaze, mmoratHazeF());
float fluitownAirRange = dot(uMmoratHaze - fluitownPigment, vec3(0.2126, 0.7152, 0.0722));
float fluitownHaze = abs(fluitownAirRange) > 0.001
  ? clamp(dot(fluitownAir - fluitownPigment, vec3(0.2126, 0.7152, 0.0722)) / fluitownAirRange, 0.0, 1.0)
  : clamp(mmoratHazeF(), 0.0, 1.0);
ALBEDO *= 1.0 - fluitownHaze;
EMISSION = uMmoratHaze * fluitownHaze;



}
