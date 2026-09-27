// Generated from the original's captured three.js program, then maintained by hand in this project.
// Source: program-006 (MeshStandardMaterial terrain surface), comic look

shader_type spatial;
stencil_mode write, compare_always, 2;
uniform bool fluitown_mirrored = false;
#define GL_DFDY(x) (fluitown_mirrored ? dFdy(x) : -dFdy(x))
#define COMIC_DEFAULT_ROLE 0
#include "res://assets/shaders/comic_style.gdshaderinc"
#include "res://shaders/studio/studio_grade.gdshaderinc"
uniform bool fluitown_debug_backfaces = false;
uniform bool fluitown_organic = false;
// Fluitown mountain form (world look from Flui height; TerrainOrganicForm.MountainForm): strata, moss, crest greenery.
uniform bool fluitown_mountain = false;
// Moss at the foot of walls and grass on rock (linear pigment; a = amount).
uniform vec4 fluitown_moss = vec4(0.15, 0.19, 0.07, 0.7);
uniform vec4 fluitown_cap_grass = vec4(0.21, 0.26, 0.085, 0.8);
// Fluitown regions (world look; WorldRegions): weights of four regions around the eye (rgba) over a window of the world
// (xy = window origin in compile px, z = 1 / window size), their pigments per role ([role * 4 + slot]: lush, dry,
// mineral, turf, moss, cap grass) and which slots keep the ported pigment (the start's own region).
uniform bool fluitown_regions = false;
uniform sampler2D fluitown_region_weights : filter_linear, repeat_disable;
uniform vec4 fluitown_region_frame = vec4(0.0, 0.0, 1.0, 0.0);
uniform vec3 fluitown_region_palette[24];
uniform vec4 fluitown_region_native = vec4(1.0, 0.0, 0.0, 0.0);
// Which of the meadow roles (lush, dry, mineral, turf) those slots keep (the Flui look paints dry and mineral).
uniform vec4 fluitown_region_keep = vec4(1.0);
// Fluitown ground paint (world look from Flui height): brush strokes and pebbles on bare ground near the eye.
uniform bool fluitown_ground_paint = false;
// Continuous soil/meadow layers, with shared habitat and bounded, footprint-filtered relief.
uniform bool fluitown_ground_layers = true;
// Coherent crown/face rock material; independent of the diagnostic legacy mountain style.
uniform bool fluitown_rock_layers = true;
render_mode cull_disabled, depth_draw_opaque, depth_test_default, diffuse_burley, specular_disabled;

// Yawed-world matrix of the original scene root (three view space = yawed world, see RENDER_ARCHITECTURE).
uniform mat4 u_three_view;
const bool isOrthographic = false;
uniform vec3 three_diffuse;
uniform vec3 three_emissive;
uniform float three_roughness;
uniform float three_metalness;
uniform float three_opacity;
uniform sampler2D uMmoratActorShadowMap : filter_nearest, repeat_disable;
uniform mat4 uMmoratActorShadowMatrix;
uniform vec4 uMmoratActorShadow;
uniform int uTerrainInteriorCutawayCount;
uniform vec4 uTerrainInteriorCutawayBounds[8];
uniform vec4 uTerrainInteriorCutawayState[8];
uniform float uMmoratTerrainTime;
uniform float uMmoratNight;
uniform float uMmoratChasmGlow;
uniform vec3 uMmoratChasmGlowColor;
uniform vec4 uMmoratTerrainWind;
uniform vec2 uMmoratTerrainWindDirection;
uniform float uMmoratViewZoom;
uniform vec3 uMmoratHaze;
uniform vec4 uMmoratHazeCfg;
uniform vec4 uMmoratHazeBody;
uniform vec4 uMmoratStyle;
uniform vec3 uMmoratDriftA;
uniform vec3 uMmoratDriftB;
uniform vec3 uMmoratWaterBasin;
uniform vec3 uMmoratFloorSplatLush;
uniform vec3 uMmoratFloorSplatDry;
uniform vec3 uMmoratFloorSplatMineral;
uniform vec3 uMmoratFloorTurf;
uniform vec4 uMmoratTerrainMaterialA;
uniform vec2 uMmoratTerrainMaterialRotation;
uniform vec3 uMmoratTerrainMaterialFrequency;
uniform vec4 uMmoratTerrainMaterialB;
uniform vec4 uMmoratTerrainMaterialFinish;
uniform vec4 uMmoratWeatherSurface;
uniform float uMmoratWeatherSnow;
uniform vec4 uMmoratInkA;
uniform vec4 uMmoratInkB;
uniform vec4 uMmoratInkStructure;
uniform vec3 uMmoratInkTint;
uniform vec3 uMmoratInkLine;
uniform vec3 uMmoratPaperSheet;


// three.js light uniforms, in three view space (yawed world), exactly as the original uploads them.
uniform vec3 three_ambientLightColor;
uniform vec3 u_dir_light_direction[2];
uniform vec3 u_dir_light_color[2];
uniform vec3 u_point_light_position[3];
uniform vec3 u_point_light_color[3];
uniform float u_point_light_distance[3];
uniform float u_point_light_decay[3];
uniform vec3 u_hemi_direction;
uniform vec3 u_hemi_sky;
uniform vec3 u_hemi_ground;
uniform bool receiveShadow = true;
uniform sampler2D dfgLUT : filter_linear, repeat_disable;


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
varying vec3 vNormal;
varying vec4 vColor;
varying vec3 vMmoratWorld;
varying vec2 vMmoratSurf;
varying float vMmoratSideProjection;
varying float vMmoratEmissive;
varying vec4 vMmoratGround;


struct IncidentLight {
	vec3 color;
	vec3 direction;
	bool visible;
};

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

float pow2( in float x ) { return x*x; }

float pow4( in float x ) { float x2 = x*x; return x2*x2; }

vec3 BRDF_Lambert( in vec3 diffuseColor ) {
	return 0.3183098861837907 * diffuseColor;
}

vec3 F_Schlick( in vec3 f0, in float f90, in float dotVH ) {
	float fresnel = exp2( ( - 5.55473 * dotVH - 6.98316 ) * dotVH );
	return f0 * ( 1.0 - fresnel ) + ( f90 * fresnel );
}

vec3 getAmbientLightIrradiance( in vec3 ambientLightColor ) {
	vec3 irradiance = ambientLightColor;
	return irradiance;
}

float getDistanceAttenuation( in float lightDistance, in float cutoffDistance, in float decayExponent ) {
	float distanceFalloff = 1.0 / max( pow( lightDistance, decayExponent ), 0.01 );
	if ( cutoffDistance > 0.0 ) {
		distanceFalloff *= pow2( clamp( 1.0 - pow4( lightDistance / cutoffDistance ), 0.0, 1.0 ) );
	}
	return distanceFalloff;
}

float V_GGX_SmithCorrelated( in float alpha, in float dotNL, in float dotNV ) {
	float a2 = pow2( alpha );
	float gv = dotNL * sqrt( a2 + ( 1.0 - a2 ) * pow2( dotNV ) );
	float gl = dotNV * sqrt( a2 + ( 1.0 - a2 ) * pow2( dotNL ) );
	return 0.5 / max( gv + gl, 1e-6 );
}

float D_GGX( in float alpha, in float dotNH ) {
	float a2 = pow2( alpha );
	float denom = pow2( dotNH ) * ( a2 - 1.0 ) + 1.0;
	return 0.3183098861837907 * a2 / pow2( denom );
}

vec3 BRDF_GGX( in vec3 lightDir, in vec3 viewDir, in vec3 normal, in PhysicalMaterial material ) {
	vec3 f0 = material.specularColorBlended;
	float f90 = material.specularF90;
	float roughness = material.roughness;
	float alpha = pow2( roughness );
	vec3 halfDir = normalize( lightDir + viewDir );
	float dotNL = clamp( dot( normal, lightDir ), 0.0, 1.0 );
	float dotNV = clamp( dot( normal, viewDir ), 0.0, 1.0 );
	float dotNH = clamp( dot( normal, halfDir ), 0.0, 1.0 );
	float dotVH = clamp( dot( viewDir, halfDir ), 0.0, 1.0 );
	vec3 F = F_Schlick( f0, f90, dotVH );
		float V = V_GGX_SmithCorrelated( alpha, dotNL, dotNV );
		float D = D_GGX( alpha, dotNH );
	return F * ( V * D );
}

void computeMultiscattering( in vec3 normal, in vec3 viewDir, in vec3 specularColor, in float specularF90, in float roughness, inout vec3 singleScatter, inout vec3 multiScatter ) {
	float dotNV = clamp( dot( normal, viewDir ), 0.0, 1.0 );
	vec2 fab = texture( dfgLUT, vec2( roughness, dotNV ) ).rg;
		vec3 Fr = specularColor;
	vec3 FssEss = Fr * fab.x + specularF90 * fab.y;
	float Ess = fab.x + fab.y;
	float Ems = 1.0 - Ess;
	vec3 Favg = Fr + ( 1.0 - Fr ) * 0.047619;	vec3 Fms = FssEss * Favg / ( 1.0 - Ems * Favg );
	singleScatter += FssEss;
	multiScatter += Fms * Ems;
}

vec3 BRDF_GGX_Multiscatter( in vec3 lightDir, in vec3 viewDir, in vec3 normal, in PhysicalMaterial material ) {
	vec3 singleScatter = BRDF_GGX( lightDir, viewDir, normal, material );
	float dotNL = clamp( dot( normal, lightDir ), 0.0, 1.0 );
	float dotNV = clamp( dot( normal, viewDir ), 0.0, 1.0 );
	vec2 dfgV = texture( dfgLUT, vec2( material.roughness, dotNV ) ).rg;
	vec2 dfgL = texture( dfgLUT, vec2( material.roughness, dotNL ) ).rg;
	vec3 FssEss_V = material.specularColorBlended * dfgV.x + material.specularF90 * dfgV.y;
	vec3 FssEss_L = material.specularColorBlended * dfgL.x + material.specularF90 * dfgL.y;
	float Ess_V = dfgV.x + dfgV.y;
	float Ess_L = dfgL.x + dfgL.y;
	float Ems_V = 1.0 - Ess_V;
	float Ems_L = 1.0 - Ess_L;
	vec3 Favg = material.specularColorBlended + ( 1.0 - material.specularColorBlended ) * 0.047619;
	vec3 Fms = FssEss_V * FssEss_L * Favg / ( 1.0 - Ems_V * Ems_L * Favg + 1e-6 );
	float compensationFactor = Ems_V * Ems_L;
	vec3 multiScatter = Fms * compensationFactor;
	return singleScatter + multiScatter;
}

void RE_Direct_Physical( in IncidentLight directLight, in vec3 geometryPosition, in vec3 geometryNormal, in vec3 geometryViewDir, in vec3 geometryClearcoatNormal, in PhysicalMaterial material, inout ReflectedLight reflectedLight ) {
	float dotNL = clamp( dot( geometryNormal, directLight.direction ), 0.0, 1.0 );
	vec3 irradiance = dotNL * directLight.color;
	reflectedLight.directSpecular += irradiance * BRDF_GGX_Multiscatter( directLight.direction, geometryViewDir, geometryNormal, material );
	reflectedLight.directDiffuse += irradiance * BRDF_Lambert( material.diffuseContribution );
}

void RE_IndirectDiffuse_Physical( in vec3 irradiance, in vec3 geometryPosition, in vec3 geometryNormal, in vec3 geometryViewDir, in vec3 geometryClearcoatNormal, in PhysicalMaterial material, inout ReflectedLight reflectedLight ) {
	vec3 diffuse = irradiance * BRDF_Lambert( material.diffuseContribution );
	reflectedLight.indirectDiffuse += diffuse;
}

void RE_IndirectSpecular_Physical( in vec3 radiance, in vec3 irradiance, in vec3 clearcoatRadiance, in vec3 geometryPosition, in vec3 geometryNormal, in vec3 geometryViewDir, in vec3 geometryClearcoatNormal, in PhysicalMaterial material, inout ReflectedLight reflectedLight) {
	vec3 singleScatteringDielectric = vec3( 0.0 );
	vec3 multiScatteringDielectric = vec3( 0.0 );
	vec3 singleScatteringMetallic = vec3( 0.0 );
	vec3 multiScatteringMetallic = vec3( 0.0 );
		computeMultiscattering( geometryNormal, geometryViewDir, material.specularColor, material.specularF90, material.roughness, singleScatteringDielectric, multiScatteringDielectric );
		computeMultiscattering( geometryNormal, geometryViewDir, material.diffuseColor, material.specularF90, material.roughness, singleScatteringMetallic, multiScatteringMetallic );
	vec3 singleScattering = mix( singleScatteringDielectric, singleScatteringMetallic, material.metalness );
	vec3 multiScattering = mix( multiScatteringDielectric, multiScatteringMetallic, material.metalness );
	vec3 totalScatteringDielectric = singleScatteringDielectric + multiScatteringDielectric;
	vec3 diffuse = material.diffuseContribution * ( 1.0 - totalScatteringDielectric );
	vec3 cosineWeightedIrradiance = irradiance * 0.3183098861837907;
	vec3 indirectSpecular = radiance * singleScattering;
	indirectSpecular += multiScattering * cosineWeightedIrradiance;
	vec3 indirectDiffuse = diffuse * cosineWeightedIrradiance;
	reflectedLight.indirectSpecular += indirectSpecular;
	reflectedLight.indirectDiffuse += indirectDiffuse;
}

void getDirectionalLightInfo(vec3 direction, vec3 color, out IncidentLight light) {
	light.color = color;
	light.direction = direction;
	light.visible = true;
}

void getPointLightInfo(vec3 position, vec3 color, float cutoffDistance, float decay, vec3 geometryPosition, out IncidentLight light) {
	vec3 lVector = position - geometryPosition;
	light.direction = normalize(lVector);
	float lightDistance = length(lVector);
	light.color = color;
	light.color *= getDistanceAttenuation(lightDistance, cutoffDistance, decay);
	light.visible = (light.color != vec3(0.0));
}

vec3 getHemisphereLightIrradiance(vec3 direction, vec3 skyColor, vec3 groundColor, vec3 normal) {
	float dotNL = dot(normal, direction);
	float hemiDiffuseWeight = 0.5 * dotNL + 0.5;
	vec3 irradiance = mix(groundColor, skyColor, hemiDiffuseWeight);
	return irradiance;
}


float fluitown_unpack_depth(vec2 packed) {
	return packed.x + packed.y / 1024.0;
}

// Emulates `texture(sampler2DShadow, vec3(uv, ref))` with LINEAR filtering: the four texels around uv are
// compared individually and the 0/1 results are bilinearly weighted, exactly like hardware PCF. Texture v runs
// downwards in Godot while three's shadow uv runs upwards, hence the flip.
float fluitown_shadow_compare(vec2 uv, float ref) {
	vec2 size = u_shadow_map_size;
	vec2 texel = vec2(uv.x, 1.0 - uv.y) * size - 0.5;
	vec2 base = floor(texel);
	vec2 f = texel - base;
	ivec2 i0 = ivec2(base);
	ivec2 limit = ivec2(size) - 1;
	float s00 = ref <= fluitown_unpack_depth(texelFetch(u_shadow_depth, clamp(i0, ivec2(0), limit), 0).rg) ? 1.0 : 0.0;
	float s10 = ref <= fluitown_unpack_depth(texelFetch(u_shadow_depth, clamp(i0 + ivec2(1, 0), ivec2(0), limit), 0).rg) ? 1.0 : 0.0;
	float s01 = ref <= fluitown_unpack_depth(texelFetch(u_shadow_depth, clamp(i0 + ivec2(0, 1), ivec2(0), limit), 0).rg) ? 1.0 : 0.0;
	float s11 = ref <= fluitown_unpack_depth(texelFetch(u_shadow_depth, clamp(i0 + ivec2(1, 1), ivec2(0), limit), 0).rg) ? 1.0 : 0.0;
	return mix(mix(s00, s10, f.x), mix(s01, s11, f.x), f.y);
}

float interleavedGradientNoise(vec2 position) {
	return fract(52.9829189 * fract(dot(position, vec2(0.06711056, 0.00583715))));
}

vec2 vogelDiskSample(int sampleIndex, int samplesCount, float phi) {
	const float goldenAngle = 2.399963229728653;
	float r = sqrt((float(sampleIndex) + 0.5) / float(samplesCount));
	float theta = float(sampleIndex) * goldenAngle + phi;
	return vec2(cos(theta), sin(theta)) * r;
}

// three.js r185 getShadow (PCFShadowMap): 5 Vogel taps rotated by interleaved gradient noise.
float getShadow(vec2 fragCoord, vec2 shadowMapSize, float shadowIntensity, float shadowBias, float shadowRadius, vec4 shadowCoord) {
	float shadow = 1.0;
	shadowCoord.xyz /= shadowCoord.w;
	shadowCoord.z += shadowBias;
	bool inFrustum = shadowCoord.x >= 0.0 && shadowCoord.x <= 1.0 && shadowCoord.y >= 0.0 && shadowCoord.y <= 1.0;
	bool frustumTest = inFrustum && shadowCoord.z <= 1.0;
	if (frustumTest) {
		vec2 texelSize = vec2(1.0) / shadowMapSize;
		float radius = shadowRadius * texelSize.x;
		float phi = interleavedGradientNoise(fragCoord) * 6.283185307179586;
		if (fluitown_shadow_taps > 5) {
			// Enhancement: the same rotated Vogel disk with more taps (smoother penumbrae on the finer map).
			float sum = 0.0;
			for (int i = 0; i < fluitown_shadow_taps; i++) {
				sum += fluitown_shadow_compare(shadowCoord.xy + vogelDiskSample(i, fluitown_shadow_taps, phi) * radius, shadowCoord.z);
			}
			return mix(1.0, sum / float(fluitown_shadow_taps), shadowIntensity);
		}
		shadow = (
			fluitown_shadow_compare(shadowCoord.xy + vogelDiskSample(0, 5, phi) * radius, shadowCoord.z) +
			fluitown_shadow_compare(shadowCoord.xy + vogelDiskSample(1, 5, phi) * radius, shadowCoord.z) +
			fluitown_shadow_compare(shadowCoord.xy + vogelDiskSample(2, 5, phi) * radius, shadowCoord.z) +
			fluitown_shadow_compare(shadowCoord.xy + vogelDiskSample(3, 5, phi) * radius, shadowCoord.z) +
			fluitown_shadow_compare(shadowCoord.xy + vogelDiskSample(4, 5, phi) * radius, shadowCoord.z)
		) * 0.2;
	}
	return mix(1.0, shadow, shadowIntensity);
}


// Packed-depth comparison for the (optional) actor shadow map; same packing as the sun shadow.
float fluitown_actor_shadow_compare(vec3 coord) {
	vec2 size = vec2(textureSize(uMmoratActorShadowMap, 0));
	vec2 texel = vec2(coord.x, 1.0 - coord.y) * size - 0.5;
	vec2 base = floor(texel);
	vec2 f = texel - base;
	ivec2 i0 = ivec2(base);
	ivec2 limit = ivec2(size) - 1;
	float s00 = coord.z <= fluitown_unpack_depth(texelFetch(uMmoratActorShadowMap, clamp(i0, ivec2(0), limit), 0).rg) ? 1.0 : 0.0;
	float s10 = coord.z <= fluitown_unpack_depth(texelFetch(uMmoratActorShadowMap, clamp(i0 + ivec2(1, 0), ivec2(0), limit), 0).rg) ? 1.0 : 0.0;
	float s01 = coord.z <= fluitown_unpack_depth(texelFetch(uMmoratActorShadowMap, clamp(i0 + ivec2(0, 1), ivec2(0), limit), 0).rg) ? 1.0 : 0.0;
	float s11 = coord.z <= fluitown_unpack_depth(texelFetch(uMmoratActorShadowMap, clamp(i0 + ivec2(1, 1), ivec2(0), limit), 0).rg) ? 1.0 : 0.0;
	return mix(mix(s00, s10, f.x), mix(s01, s11, f.x), f.y);
}

float mmoratActorShadow(vec3 worldPos, vec2 fragCoord) {
  if (uMmoratActorShadow.x <= 0.0) return 1.0;
  vec4 coord = uMmoratActorShadowMatrix * vec4(worldPos, 1.0);
  coord.xyz /= coord.w;
  if (coord.x < 0.0 || coord.x > 1.0 || coord.y < 0.0 || coord.y > 1.0 || coord.z > 1.0) return 1.0;
  coord.z += uMmoratActorShadow.y;
  float radius = uMmoratActorShadow.z * uMmoratActorShadow.w;
  float phi = fract(52.9829189 * fract(dot(fragCoord, vec2(0.06711056, 0.00583715)))) * 6.28318531;
  vec2 turn = vec2(cos(phi), sin(phi));
  mat2 rotate = mat2(vec2(turn.x, turn.y), vec2(-turn.y, turn.x));
  float shade = 0.25 * (
    fluitown_actor_shadow_compare(vec3(coord.xy + rotate * vec2(0.35355339, 0.00000000) * radius, coord.z)) +
    fluitown_actor_shadow_compare(vec3(coord.xy + rotate * vec2(-0.45154438, 0.41365164) * radius, coord.z)) +
    fluitown_actor_shadow_compare(vec3(coord.xy + rotate * vec2(0.06911610, -0.78754236) * radius, coord.z)) +
    fluitown_actor_shadow_compare(vec3(coord.xy + rotate * vec2(0.56914244, 0.74234553) * radius, coord.z))
  );
  return mix(1.0, shade, uMmoratActorShadow.x);
}
vec2 mmoratInkSheet(vec3 worldPos) {
  vec2 turned = vec2(
    uMmoratPaperSheet.x * worldPos.x + uMmoratPaperSheet.y * worldPos.z,
    uMmoratPaperSheet.x * worldPos.z - uMmoratPaperSheet.y * worldPos.x
  );
  return vec2(
    turned.x,
    turned.y * 0.66601577 - worldPos.y * 0.74593766
  );
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

vec2 mmoratMaterialHash22(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.xx + p3.yz) * p3.zy);
}

vec2 mmoratMaterialGradient(vec2 lattice) {
  vec2 gradient = mmoratMaterialHash22(lattice) * 2.0 - 1.0;
  return gradient * inversesqrt(max(dot(gradient, gradient), 0.0001));
}

uniform sampler2D fluitown_material_noise : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool fluitown_fast_noise = true;
float mmoratMaterialGradientNoise(vec2 p) {
  if (fluitown_fast_noise) return texture(fluitown_material_noise, p * (1.0 / 64.0)).r;
  vec2 lattice = floor(p);
  vec2 local = fract(p);
  vec2 fade = local * local * local * (local * (local * 6.0 - 15.0) + 10.0);
  float nw = dot(mmoratMaterialGradient(lattice), local);
  float ne = dot(mmoratMaterialGradient(lattice + vec2(1.0, 0.0)), local - vec2(1.0, 0.0));
  float sw = dot(mmoratMaterialGradient(lattice + vec2(0.0, 1.0)), local - vec2(0.0, 1.0));
  float se = dot(mmoratMaterialGradient(lattice + vec2(1.0, 1.0)), local - vec2(1.0, 1.0));
  return clamp(0.5 + mix(mix(nw, ne, fade.x), mix(sw, se, fade.x), fade.y) * 0.82, 0.0, 1.0);
}

float mmoratReliefHeightFromMaterial(float broad, float mid, float fine) {
  return broad * 0.620
    + mid * 0.270
    + fine * 0.110;
}

vec3 mmoratPerturbTerrainNormal(vec3 surfacePosition, vec3 surfaceNormal, float height, float strength) {
  vec3 sigmaX = dFdx(surfacePosition);
  vec3 sigmaY = GL_DFDY(surfacePosition);
  vec3 r1 = cross(sigmaY, surfaceNormal);
  vec3 r2 = cross(surfaceNormal, sigmaX);
  float determinant = dot(sigmaX, r1);
  vec2 gradient = vec2(dFdx(height), GL_DFDY(height));
  vec3 mappedGradient = sign(determinant) * (gradient.x * r1 + gradient.y * r2);
  return normalize(max(abs(determinant), 0.00001) * surfaceNormal - mappedGradient * strength);
}

float mmoratReliefTone(float height, float fade) {
  float cavity = (1.0 - smoothstep(0.18, 0.48, height)) * fade;
  float ridge = smoothstep(0.58, 0.84, height) * fade;
  float directionalSlope = clamp((dFdx(height) - GL_DFDY(height)) * fluitown_lod_scale * 6.5, -0.05, 0.05) * fade * fade;
  return (height - 0.5) * 0.055 * fade
    - cavity * 0.125
    + ridge * 0.082
    + directionalSlope * 0.070 * 12.0;
}
float mmoratHazeF(vec3 vMmoratWorld_p) {
  vec3 mmoratAir = vMmoratWorld_p;
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
float mmoratMaterialContrast(float a, float peak) {
  return sign(a) * pow(min(abs(a) * 2.0, 1.0), 0.62) * peak;
}

// Fluitown organic terrain: rock facets. A facet is a cell of the intersection of two rotated box lattices of
// different pitch: irregular convex shards with straight borders in six directions, two integer hashes per fragment
// (a Voronoi search cost four times as much in the depth pre-pass, where the facet normals are needed too).
uint fluitownHash3(ivec3 c) {
	uint h = (uint(c.x) * 0x8da6b343u) ^ (uint(c.y) * 0xd8163841u) ^ (uint(c.z) * 0xcb1ab31fu);
	h ^= h >> 16u;
	h *= 0x7feb352du;
	h ^= h >> 15u;
	h *= 0x846ca68bu;
	return h ^ (h >> 16u);
}
vec3 fluitownUnpack3(uint h) {
	return vec3(uvec3(h, h >> 10u, h >> 20u) & uvec3(1023u)) * (1.0 / 1023.0);
}
const mat3 FLUITOWN_FACET_A = mat3(vec3(0.8165, 0.4082, -0.4082), vec3(-0.2113, 0.7887, 0.5774), vec3(0.5377, -0.4597, 0.7071));
const mat3 FLUITOWN_FACET_B = mat3(vec3(0.5, -0.7071, 0.5), vec3(0.8536, 0.3536, -0.3827), vec3(0.1464, 0.6124, 0.7765));
// The random vector of the facet containing p (p in facet units).
vec3 fluitownFacet(vec3 p) {
	uint a = fluitownHash3(ivec3(floor(FLUITOWN_FACET_A * p)));
	uint b = fluitownHash3(ivec3(floor(FLUITOWN_FACET_B * p * 1.43 + 0.37)) + ivec3(911, 353, 577));
	return fluitownUnpack3((a ^ (b * 0x9e3779b9u)) * 0x85ebca6bu);
}
// Component i of v. The Compatibility renderer on ANGLE (OpenGL ES on Direct3D) fails to compile shaders that index
// vectors with a variable ("redefinition of dyn_index_vec4_float4_int"), so the component is picked with a one-hot
// mask instead.
float fluitownComponent(vec4 v, int i) {
	return dot(v, vec4(equal(ivec4(i), ivec4(0, 1, 2, 3))));
}
// Fluitown regions: a pigment of role `which` at compile px `at`, mixed from the four regions around the eye; slots
// of the start's own region keep `native` (roles 0-3, the ported meadow).
vec3 fluitownRegionSplat(vec3 native, int which, vec2 at) {
	if (!fluitown_regions) return native;
	vec4 w = texture(fluitown_region_weights, (at - fluitown_region_frame.xy) * fluitown_region_frame.z);
	w /= max(w.x + w.y + w.z + w.w, 1e-4);
	vec3 c = vec3(0.0);
	for (int s = 0; s < 4; s++) {
		float keep = which < 4 ? fluitownComponent(fluitown_region_native, s) * fluitownComponent(fluitown_region_keep, which) : 0.0;
		c += fluitownComponent(w, s) * mix(fluitown_region_palette[which * 4 + s], native, keep);
	}
	return c;
}

#include "res://shaders/terrain/terrain_ground.gdshaderinc"
#include "res://shaders/terrain/terrain_rock.gdshaderinc"


void vertex() {

	vec3 position = VERTEX;
	vec3 normal = NORMAL;
	vec2 uv = vec2(0.0);
	vec3 color = CUSTOM0.rgb;
	vec2 aSurf = UV;
	float aEmissive = CUSTOM0.a;
	vec4 aGround = CUSTOM1;
	mat4 modelMatrix = u_three_view * MODEL_MATRIX;
	mat4 viewMatrix = mat4(1.0);
	mat4 modelViewMatrix = u_three_view * MODEL_MATRIX;
	mat3 normalMatrix = mat3(u_three_view * MODEL_MATRIX);


		vColor = vec4( 1.0 );
		vColor.rgb *= color;
	vec3 objectNormal = vec3( normal );
	vec3 transformedNormal = objectNormal;
	transformedNormal = normalMatrix * transformedNormal;
		vNormal = normalize( transformedNormal );
	vec3 transformed = vec3( position );
	vMmoratSurf = aSurf;
	vMmoratSideProjection = 1.0 - abs(normal.y);
	vMmoratEmissive = aEmissive;
	vMmoratGround = aGround;
	float mmoratWind = max(0.0, -aSurf.y - 0.001);
	float mmoratWindStrength = max(0.0, uMmoratTerrainWind.y);
	float mmoratWindProjectedPeak = mmoratWind * mmoratWindStrength * uMmoratViewZoom;
	if (mmoratWindProjectedPeak > 1.75) {
	  float mmoratWindMotionVisibility = smoothstep(
	    1.75,
	    5.50,
	    mmoratWindProjectedPeak
	  );
	  float mmoratWindFacetCssPx = max(0.0, -aGround.w) * uMmoratViewZoom;
	  mmoratWindMotionVisibility *= smoothstep(
	    0.85,
	    2.60,
	    mmoratWindFacetCssPx
	  );
	  float tempo = max(0.05, uMmoratTerrainWind.z);
	  float authored = position.x * 0.0085 + position.z * 0.0052;
	  float carrier = sin(uMmoratTerrainTime * tempo * 1.36 + authored);
	  float secondary = sin(uMmoratTerrainTime * tempo * 2.17 - position.x * 0.0031 + position.z * 0.0074);
	  vec2 direction = uMmoratTerrainWindDirection;
	  float alongWind = dot(position.xz, direction);
	  float acrossWind = position.z * direction.x - position.x * direction.y;
	  float frontPhase = alongWind * 0.0108
	    + sin(acrossWind * 0.0037 + uMmoratTerrainTime * tempo * 0.23) * 0.82
	    - uMmoratTerrainTime * tempo * 1.04;
	  float frontWave = 0.5 + 0.5 * sin(frontPhase);
	  float frontBeat = 0.5 + 0.5 * sin(frontPhase * 0.371 + 1.7);
	  float front = frontWave * frontWave * (0.3 + 0.7 * frontBeat);
	  float gust = 0.58 + max(0.0, uMmoratTerrainWind.w) * (0.18 + front * 0.82);
	  float sway = mmoratWind * mmoratWindStrength * (carrier * 0.72 + secondary * 0.28) * gust
	    * mmoratWindMotionVisibility;
	  transformed.x += direction.x * sway;
	  transformed.z += direction.y * sway * 0.42;
	  transformed.y += abs(sway) * 0.025;
	}
	vMmoratWorld = vec3(transformed);
	vec4 mvPosition = vec4( transformed, 1.0 );
	mvPosition = modelViewMatrix * mvPosition;

		vViewPosition = - mvPosition.xyz;
		vec4 worldPosition = vec4( transformed, 1.0 );
		worldPosition = modelMatrix * worldPosition;
			vec3 shadowWorldNormal = transformNormalByInverseViewMatrix( transformedNormal, viewMatrix );
		vec4 shadowWorldPosition;
				shadowWorldPosition = worldPosition + vec4( shadowWorldNormal * u_shadow_normal_bias, 0 );
			
	VERTEX = transformed;
	COMIC_VERTEX
	POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
}

void fragment() {

	// Module-scope mutables of the original shader (Godot has no mutable globals).
	float mmoratAlbedoLum = 1.0;
	float mmoratPaperShade = 1.0;
	float mmoratPaperTear = 0.0;
	float mmoratSharedReliefHeight = 0.0;
	float mmoratSharedDetailFade = 1.0;
	float mmoratSharedWallDetailFade = 1.0;
	float mmoratSharedChasmFloorVariation = 0.0;
	float mmoratSharedChasmWallVariation = 0.0;
	float mmoratCloudShade = 1.0;
	float mmoratCloudLift = 1.0;
	vec4 pc_fragColor = vec4(0.0);
	// three's gl_FragCoord has its origin bottom-left, Godot's FRAGCOORD top-left (unless mirrored).
	vec4 fluitown_FragCoord = fluitown_mirrored ? FRAGCOORD : vec4(FRAGCOORD.x, VIEWPORT_SIZE.y - FRAGCOORD.y, FRAGCOORD.z, FRAGCOORD.w);


		vec4 diffuseColor = vec4( three_diffuse, three_opacity );
		ReflectedLight reflectedLight = ReflectedLight( vec3( 0.0 ), vec3( 0.0 ), vec3( 0.0 ), vec3( 0.0 ) );
		vec3 totalEmissiveRadiance = three_emissive;
		diffuseColor *= vColor;
	if (fluitown_ground_layers && vMmoratSurf.x < 0.5 && vMmoratSurf.y >= 0.0) {
	  diffuseColor.rgb = fluitownGroundMaterial(diffuseColor.rgb, vMmoratWorld, vMmoratGround, mmoratSharedReliefHeight);
	} else if (fluitown_rock_layers && vMmoratSurf.x >= 0.5 && vMmoratSurf.x < 3.5 && vMmoratSurf.y >= 0.0) {
	  diffuseColor.rgb = fluitownRockMaterial(diffuseColor.rgb, vMmoratWorld, vMmoratGround, vMmoratSurf.x, mmoratSharedReliefHeight);
	} else {
	  float kind = vMmoratSurf.x;
	  mmoratSharedChasmFloorVariation = 1.0;
	  mmoratSharedChasmWallVariation = 1.0;
	  vec2 uv = vMmoratSideProjection > 0.5
	    ? vec2(vMmoratWorld.x + vMmoratWorld.z, vMmoratWorld.y)
	    : vMmoratWorld.xz;
	  float mmoratWorldFootprintX = fwidth(vMmoratWorld.x) * fluitown_lod_scale;
	  float mmoratWorldFootprintY = fwidth(vMmoratWorld.y) * fluitown_lod_scale;
	  float mmoratWorldFootprintZ = fwidth(vMmoratWorld.z) * fluitown_lod_scale;
	  float mmoratDetailFootprint = mmoratWorldFootprintX + mmoratWorldFootprintZ;
	  float mmoratDetailFade = 1.0 - smoothstep(6.0, 22.0, mmoratDetailFootprint);
	  float wallFootprint = mmoratWorldFootprintX + mmoratWorldFootprintY;
	  float wallDetailFade = 1.0 - smoothstep(6.0, 22.0, wallFootprint);
	  float mmoratSheetFootprint = max(mmoratDetailFootprint, wallFootprint) * uMmoratPaperSheet.z;
	float mmoratSheetScale = min(1.0, 1.88 / max(0.001, mmoratSheetFootprint));
	  mmoratSharedDetailFade = mmoratDetailFade;
	  mmoratSharedWallDetailFade = wallDetailFade;
	  float mmoratFamilyFade = (kind > 1.5 && kind < 3.5) || (kind > 5.5 && kind < 7.5)
	    ? wallDetailFade
	    : mmoratDetailFade;
	  float mmoratMaterialCos = uMmoratTerrainMaterialRotation.x;
	  float mmoratMaterialSin = uMmoratTerrainMaterialRotation.y;
	  mat2 mmoratMaterialRotation = mat2(vec2(mmoratMaterialCos, -mmoratMaterialSin), vec2(mmoratMaterialSin, mmoratMaterialCos));
	  vec2 materialUv = kind < 1.5 ? mmoratMaterialRotation * uv : uv;
	  vec2 macroUv = mat2(vec2(0.819, -0.574), vec2(0.574, 0.819))
	    * materialUv * uMmoratTerrainMaterialFrequency.x + vec2(3.1, -7.7);
	  vec2 midUv = mat2(vec2(0.438, 0.899), vec2(-0.899, 0.438))
	    * materialUv * uMmoratTerrainMaterialFrequency.y + vec2(13.7, -19.3);
	  vec2 fineUv = mat2(vec2(0.946, -0.324), vec2(0.324, 0.946))
	    * materialUv * uMmoratTerrainMaterialFrequency.z + vec2(-31.9, 5.3);
	  float mmoratLandscapeField = mmoratMaterialGradientNoise(
	    mat2(vec2(0.743, -0.669), vec2(0.669, 0.743))
	      * vMmoratWorld.xz / 360.0 + 17.31
	  );
	  float macroSample = mmoratMaterialGradientNoise(macroUv);
	  vec2 materialWarp = vec2(macroSample - 0.5, 0.5 - macroSample);
	  float midSample = mmoratMaterialGradientNoise(midUv + materialWarp * 0.58);
	  float fineSample = mmoratMaterialGradientNoise(
	    fineUv + vec2(midSample - 0.5, macroSample - 0.5) * 0.46
	  );
	  float mmoratQuietArea = smoothstep(
	    0.460,
	    0.720,
	    mmoratLandscapeField
	  );
	  float mmoratHorizontalSurface = 1.0 - step(1.5, kind);
	  float mmoratHorizontalCalm = mix(
	    1.0,
	    0.460,
	    mmoratQuietArea * mmoratHorizontalSurface
	  );
	  float mmoratLineCalm = mix(
	    1.0,
	    0.350,
	    mmoratQuietArea * mmoratHorizontalSurface
	  );
	  float mmoratReliefCalm = mix(
	    1.0,
	    0.600,
	    mmoratQuietArea * mmoratHorizontalSurface
	  );
	  float macro = macroSample - 0.5;
	  float mid = midSample - 0.5;
	  float fine = fineSample - 0.5;
	  float detail;
	  float gain = 1.0;
	  if (kind < 0.5) { detail = macro * 0.5 + mid * 0.36 + fine * 0.14; gain = uMmoratStyle.x; }
	  else if (kind < 1.5) { detail = macro * 0.28 + mid * 0.3 + fine * 0.42; gain = uMmoratStyle.y; }
	  else if (kind < 2.5) { detail = (mmoratNoise(vec2(uv.x / 90.0, uv.y / 7.0)) - 0.5) * 0.8 + fine * 0.2; gain = uMmoratStyle.z; }
	  else if (kind < 3.5) { detail = (mmoratNoise(vec2(uv.x / 70.0, uv.y / 9.0)) - 0.5) * 0.66 + fine * 0.34; gain = uMmoratStyle.z; }
	  else if (kind < 4.5) detail = (mmoratNoise(vec2(uv.x / 64.0, uv.y / 5.0)) - 0.5) * 0.85 + fine * 0.15;
	  else if (kind < 5.5) detail = mid * 0.5 + fine * 0.5;
	  else if (kind < 6.5) {
	    detail = (mmoratNoise(vec2(uv.x / 84.0, uv.y / 7.5)) - 0.5) * 0.74 + fine * 0.26;
	    gain = uMmoratStyle.z;
	  } else if (kind < 7.5) {
	    float chasmRockFamily = step(0.175, vMmoratSurf.y);
	    float chasmEarthDetail = (mmoratNoise(vec2(uv.x / 90.0, uv.y / 7.0)) - 0.5) * 0.8 + fine * 0.2;
	    float chasmRockDetail = (mmoratNoise(vec2(uv.x / 70.0, uv.y / 9.0)) - 0.5) * 0.66 + fine * 0.34;
	    detail = mix(chasmEarthDetail, chasmRockDetail, chasmRockFamily);
	    gain = uMmoratStyle.z;
	    mmoratSharedChasmWallVariation = clamp(
	      1.0
	        + mmoratMaterialContrast(
	            detail * gain,
	            0.340
	          ) * wallDetailFade,
	      0.7,
	      1.3
	    );
	    float chasmStepFloorMaterial = 1.0 - step(0.145, abs(vMmoratSurf.y));
	    float chasmStepFloorDetail = macro * 0.5 + mid * 0.36 + fine * 0.14;
	    mmoratSharedChasmFloorVariation = mix(
	      1.0,
	      clamp(1.0 + mmoratMaterialContrast(chasmStepFloorDetail, 0.36), 0.64, 1.36),
	      chasmStepFloorMaterial
	    );
	  } else if (kind < 8.5) {
	    detail = macro * 0.5 + mid * 0.36 + fine * 0.14;
	    gain = uMmoratStyle.x;
	    float mmoratChasmFloorBody = macro * 0.76 + mid * 0.24;
	    mmoratSharedChasmFloorVariation = clamp(
	      1.0
	        + mmoratMaterialContrast(
	            detail,
	            0.430
	          )
	        + mmoratMaterialContrast(
	            mmoratChasmFloorBody,
	            0.140
	          ),
	      0.56,
	      1.46
	    );
	  } else {
	    detail = 0.0;
	    gain = 0.0;
	  }
	  detail *= mmoratHorizontalCalm;
	  float patternStrength = vMmoratSurf.y < 0.0 ? 0.05 : vMmoratSurf.y;
	  if (kind > 6.5 && kind < 7.5) patternStrength = min(patternStrength, 0.17);
	  diffuseColor.rgb *= 1.0 + detail * patternStrength * gain * mmoratFamilyFade;
	  float mmoratBareGround = 0.0;
	  float mmoratDeepFloor = step(7.5, kind) * (1.0 - step(8.5, kind));
	  if ((kind < 0.5 || mmoratDeepFloor > 0.5) && vMmoratSurf.y > 0.09) {
	    float splatElevation = smoothstep(
	      28.0,
	      118.0,
	      vMmoratWorld.y
	    );
	    float zoneContrast = clamp(uMmoratTerrainMaterialB.w, 0.5, 1.4);
	    float ecologyField = clamp(0.5 + (macro * 0.72 + mid * 0.28) * zoneContrast, 0.0, 1.0);
	    float dryField = clamp(
	      0.5 + (-macro * 0.58 + fine * 0.22) * zoneContrast + splatElevation * 0.24,
	      0.0,
	      1.0
	    );
	    float mineralField = clamp(
	      0.5 + (mid * 0.62 - fine * 0.16) * zoneContrast + splatElevation * 0.32,
	      0.0,
	      1.0
	    );
	    float mmoratTurfCover = clamp(vMmoratGround.x, 0.0, 1.0);
	    float lushMask = max(
	      smoothstep(0.655, 0.705, ecologyField) * (1.0 - splatElevation * 0.68),
	      mmoratTurfCover
	    );
	    float dryMask = smoothstep(0.66, 0.79, dryField) * (1.0 - lushMask * 0.54);
	    float mineralMask = smoothstep(0.71, 0.84, mineralField) * (1.0 - lushMask * 0.76);
	    diffuseColor.rgb = mix(
	      diffuseColor.rgb,
	      fluitownRegionSplat(uMmoratFloorSplatLush, 0, vMmoratWorld.xz),
	      lushMask * uMmoratTerrainMaterialB.x
	    );
	    diffuseColor.rgb = mix(
	      diffuseColor.rgb,
	      fluitownRegionSplat(uMmoratFloorSplatDry, 1, vMmoratWorld.xz),
	      dryMask * uMmoratTerrainMaterialB.y
	    );
	    diffuseColor.rgb = mix(
	      diffuseColor.rgb,
	      fluitownRegionSplat(uMmoratFloorSplatMineral, 2, vMmoratWorld.xz),
	      mineralMask * uMmoratTerrainMaterialB.z
	    );
	    if (mmoratTurfCover > 0.001) {
	      diffuseColor.rgb = mix(
	        diffuseColor.rgb,
	        fluitownRegionSplat(uMmoratFloorTurf, 3, vMmoratWorld.xz),
	        mmoratTurfCover * 0.500
	      );
	      diffuseColor.rgb *= clamp(
	        1.0 + mmoratMaterialContrast(fine * 0.82 + mid * 0.18, 0.2)
	          * mmoratTurfCover * uMmoratStyle.x * mmoratDetailFade * mmoratHorizontalCalm,
	        0.82,
	        1.22
	      );
	    }
	    mmoratBareGround = clamp(dryMask + mineralMask * 0.8, 0.0, 1.0) * (1.0 - lushMask * 0.85);
	    float mmoratGrainReach = mix(
	      0.55,
	      1.0,
	      mmoratBareGround
	    ) * (1.0 - mmoratTurfCover * 0.72) * mmoratHorizontalCalm;
	    float aggregate = (fine * 0.74 + mid * 0.26) * mmoratGrainReach * uMmoratStyle.x;
	    diffuseColor.rgb *= clamp(
	      1.0 + mmoratMaterialContrast(aggregate, 0.22) * mmoratDetailFade,
	      0.74,
	      1.26
	    );
	    float mmoratWear = clamp(vMmoratGround.y, 0.0, 1.0);
	    if (mmoratWear > 0.02) {
	      vec2 mmoratRouteDir = vMmoratGround.zw;
	      float mmoratRouteCore = clamp(length(mmoratRouteDir), 0.0, 1.0);
	      mmoratRouteDir = mmoratRouteCore > 0.001
	        ? mmoratRouteDir / mmoratRouteCore
	        : vec2(1.0, 0.0);
	      float mmoratAcross = vMmoratWorld.z * mmoratRouteDir.x - vMmoratWorld.x * mmoratRouteDir.y;
	      float mmoratGroove = cos(
	        (mmoratAcross + (macro * 2.0) * 11.76)
	          * 0.224399
	      );
	      float mmoratRut = pow(max(0.0, mmoratGroove), 2.4) * mmoratRouteCore;
	      float mmoratGrit = (fine * 0.72 + mid * 0.28) * mmoratWear;
	      float mmoratSurfaceFade = mmoratDetailFade
	        * (1.0 - mmoratTurfCover * 0.85)
	        * mmoratHorizontalCalm;
	      diffuseColor.rgb *= clamp(
	        1.0
	          - mmoratRut * 0.170 * mmoratSurfaceFade
	          + mmoratMaterialContrast(mmoratGrit, 0.150) * mmoratSurfaceFade,
	        0.7,
	        1.3
	      );
	    }
	  }
	  float mmoratMaterialInk = 0.0;
	  float mmoratMaterialWash = 0.0;
	  if ((kind < 0.5 || mmoratDeepFloor > 0.5) && vMmoratSurf.y > 0.09) {
	    float floorVeinField = midSample + macro * 0.22;
	    float floorVeinAa = max(fwidth(floorVeinField) * 1.2, 0.006);
	    float floorVein = 1.0 - smoothstep(
	      0.018,
	      0.018 + floorVeinAa,
	      abs(floorVeinField - 0.48)
	    );
	    float floorStrokeBreak = smoothstep(0.36, 0.67, fineSample + macro * 0.12);
	    float floorCarrier = mix(0.28, 1.0, mmoratBareGround);
	    mmoratMaterialInk = floorVein * floorStrokeBreak * floorCarrier * uMmoratInkStructure.x;
	    mmoratMaterialWash = (macro * 0.42 + mid * 0.58)
	      * uMmoratInkStructure.w
	      * mix(0.55, 1.0, mmoratBareGround);
	  } else if (kind < 1.5) {
	    float capCarrier = smoothstep(0.142, 0.15, patternStrength);
	    vec2 capJointUv = materialUv * uMmoratTerrainMaterialFrequency.y;
	    float capJointA = abs(fract(capJointUv.x + macro * 0.78 + fine * 0.12) - 0.5);
	    float capJointB = abs(fract(capJointUv.y + mid * 0.74 - macro * 0.12) - 0.5);
	    float capPlateDistance = min(capJointA, capJointB);
	    float capPlateAa = max(fwidth(capPlateDistance) * 1.15, 0.008);
	    float capPlate = 1.0 - smoothstep(0.022, 0.022 + capPlateAa, capPlateDistance);
	    float capStrokeBreak = mix(0.34, 1.0, smoothstep(0.38, 0.7, fineSample + macro * 0.1));
	    mmoratMaterialInk = capPlate * capStrokeBreak * capCarrier * uMmoratInkStructure.y;
	    mmoratMaterialWash = (macro * 0.68 + mid * 0.32)
	      * capCarrier
	      * uMmoratInkStructure.w
	      * 1.18;
	  } else if ((kind < 3.5) || (kind > 5.5 && kind < 7.5)) {
	    float faceCarrier = smoothstep(0.145, 0.17, patternStrength);
	    float stratumCoord = vMmoratWorld.y / 18.0 + macro * 1.06 + mid * 0.34;
	    float stratumDistance = abs(fract(stratumCoord) - 0.5);
	    float stratumAa = max(fwidth(stratumCoord) * 1.16, 0.012);
	    float stratum = 1.0 - smoothstep(0.04, 0.04 + stratumAa, stratumDistance);
	    float stratumBreak = smoothstep(0.3, 0.66, fineSample + macro * 0.16);
	    float fractureField = midSample + macro * 0.2;
	    float fractureAa = max(fwidth(fractureField) * 1.25, 0.007);
	    float fracture = 1.0 - smoothstep(
	      0.016,
	      0.016 + fractureAa,
	      abs(fractureField - 0.46)
	    );
	    float fractureBreak = smoothstep(0.43, 0.72, fineSample - macro * 0.08);
	    float faceStructure = max(stratum * stratumBreak * 0.82, fracture * fractureBreak);
	    mmoratMaterialInk = faceStructure * faceCarrier * uMmoratInkStructure.z;
	    mmoratMaterialWash = (macro * 0.55 + mid * 0.45)
	      * faceCarrier
	      * uMmoratInkStructure.w
	      * 1.28;
	  }
	  mmoratMaterialInk *= mmoratLineCalm;
	  mmoratMaterialWash *= mmoratLineCalm;
	  diffuseColor.rgb *= clamp(1.0 + mmoratMaterialWash, 0.92, 1.08);
	  diffuseColor.rgb = mix(
	    diffuseColor.rgb,
	    uMmoratInkLine,
	    clamp(mmoratMaterialInk, 0.0, 0.34) * (kind < 0.5 ? 0.35 : 1.0)
	  );
	  if (mmoratDeepFloor > 0.5) {
	    mmoratSharedChasmFloorVariation *= clamp(
	      1.0 + mmoratMaterialWash * 0.12 - mmoratMaterialInk * 0.65,
	      0.78,
	      1.1
	    );
	  }
	  if (kind > 6.5 && kind < 7.5 && abs(vMmoratSurf.y) >= 0.145) {
	    mmoratSharedChasmWallVariation *= clamp(
	      1.0 + mmoratMaterialWash * 0.18 - mmoratMaterialInk * 0.72,
	      0.74,
	      1.14
	    );
	  }
	  float staticRelief = step(0.0, vMmoratSurf.y) * (1.0 - step(8.5, kind));
	  float reliefToneGain = kind < 0.5 ? 0.82 + mmoratBareGround * 0.5
	    : kind > 1.5 && kind < 4.5 ? 1.24
	    : kind < 1.5 ? 1.08
	    : kind < 3.5 ? 1.26
	    : kind < 4.5 ? 0.92
	    : kind < 5.5 ? 0.74
	    : kind < 6.5 ? 0.78
	    : kind < 7.5 ? 1.24
	    : kind < 8.5 ? 0.96
	    : 0.38;
	  if (staticRelief > 0.5) {
	    mmoratSharedReliefHeight = mmoratReliefHeightFromMaterial(
	      macroSample,
	      midSample,
	      fineSample
	    );
	    mmoratSharedReliefHeight = mix(
	      0.5,
	      mmoratSharedReliefHeight,
	      mmoratReliefCalm
	    );
	    diffuseColor.rgb *= clamp(
	      1.0 + mmoratReliefTone(mmoratSharedReliefHeight, mmoratFamilyFade) * reliefToneGain,
	      0.76,
	      1.18
	    );
	  }
	  if ((kind >= 1.5 && kind < 3.5) || (kind > 5.5 && kind < 7.5)) {
	    float wallAggregate = (fine * 0.6 + mid * 0.4) * uMmoratStyle.z;
	    diffuseColor.rgb *= clamp(
	      1.0 + mmoratMaterialContrast(wallAggregate, 0.26) * wallDetailFade,
	      0.7,
	      1.3
	    );
	    float streak = mmoratMaterialGradientNoise(vec2(uv.x * 0.075, uv.y * 0.011) + 17.3);
	    diffuseColor.rgb *= clamp(
	      1.0 + (streak - 0.5) * 0.34 * uMmoratStyle.z * wallDetailFade,
	      0.78,
	      1.22
	    );
	    float mmoratCrestDrop = vMmoratGround.y;
	    if (mmoratCrestDrop > 0.0) {
	      float mmoratContact = 1.0 - smoothstep(0.0, 5.0, mmoratCrestDrop);
	      diffuseColor.rgb *= 1.0 - mmoratContact * mmoratContact * 0.200;
	      float mmoratBreak = (1.0 - smoothstep(
	        5.0,
	        17.0,
	        mmoratCrestDrop
	      )) * smoothstep(0.0, 5.0, mmoratCrestDrop);
	      float mmoratBreakGrain = 0.45 + 0.55 * clamp(0.5 + fine * 1.5 + mid * 0.7, 0.0, 1.0);
	      diffuseColor.rgb *= 1.0
	        + mmoratBreak * mmoratBreakGrain * 0.130 * wallDetailFade;
	      float mmoratLip = 1.0 - smoothstep(0.0, 3.2, mmoratCrestDrop);
	      diffuseColor.rgb = mix(
	        diffuseColor.rgb,
	        uMmoratFloorSplatMineral,
	        clamp(mmoratLip * mmoratBreakGrain * 0.340, 0.0, 0.3)
	      );
	    }
	  if (kind > 4.5 && kind < 6.5) {
	    float faceTop = 1.0 - smoothstep(0.0, 26.0, uv.y);
	    float soaked = smoothstep(0.35, 1.0, faceTop);
	    diffuseColor.rgb *= mix(1.0, 0.82, soaked);
	    diffuseColor.rgb = mix(diffuseColor.rgb, uMmoratWaterBasin, soaked * 0.16);
	    float line = exp(-pow((faceTop - 0.78) * 9.0, 2.0));
	    float broken = 0.55 + 0.45 * clamp(0.5 + fine * 1.4 + mid * 0.6, 0.0, 1.0);
	    diffuseColor.rgb = mix(diffuseColor.rgb, uMmoratFloorSplatMineral, clamp(line * broken * 0.34, 0.0, 0.34));
	  }
	  }
	  if ((kind > 1.5 && kind < 3.5) || (kind > 6.5 && kind < 7.5)) {
	    vec3 geologicalDrift = mix(uMmoratDriftA, uMmoratDriftB, macroSample);
	    diffuseColor.rgb = mix(
	      diffuseColor.rgb,
	      diffuseColor.rgb * geologicalDrift,
	      0.055
	    );
	  }
	  if (kind < 1.5 || (kind > 3.5 && kind < 6.5) || mmoratDeepFloor > 0.5) {
	    diffuseColor.rgb *= mix(uMmoratDriftA, uMmoratDriftB, mmoratLandscapeField);
	  }
	  float mmoratFloorCapRole = 1.0 - step(0.5, kind);
	  float mmoratRockCapRole = step(0.5, kind) * (1.0 - step(1.5, kind));
	  float mmoratGeologyWallRole = step(1.5, kind) * (1.0 - step(3.5, kind));
	  float mmoratWetBankRole = step(5.5, kind) * (1.0 - step(6.5, kind));
	  float mmoratChasmWallRole = step(6.5, kind) * (1.0 - step(7.5, kind));
	  diffuseColor.rgb *= 1.0
	    + mmoratFloorCapRole * 0.018
	    + mmoratRockCapRole * 0.034;
	  diffuseColor.rgb *= 1.0
	    - mmoratGeologyWallRole
	      * 0.075
	    - mmoratWetBankRole * 0.040
	    - mmoratChasmWallRole * 0.065;
	  if (uMmoratWeatherSurface.x > 0.0 && kind < 0.5 && vMmoratSurf.y >= 0.0) {
	    float wetField = smoothstep(0.34, 0.76, 0.5 + macro * 0.7 - mid * 0.18);
	    float wetInk = uMmoratWeatherSurface.x * mix(0.62, 1.0, wetField);
	    diffuseColor.rgb *= mix(vec3(1.0), vec3(0.79, 0.84, 0.88), wetInk * 0.48);
	    float wetGlintBase = smoothstep(0.56, 0.92, fineSample);
	    float wetGlint = wetGlintBase * wetGlintBase * wetInk;
	    diffuseColor.rgb = mix(diffuseColor.rgb, uMmoratHaze, wetInk * 0.05);
	    diffuseColor.rgb += vec3(0.055, 0.054, 0.05) * wetGlint;
	  }
	  if (uMmoratWeatherSnow > 0.001 && kind < 1.5 && vMmoratSurf.y >= 0.0) {
	    float snowSlope = 1.0 - smoothstep(0.08, 0.7, vMmoratSideProjection);
	    float snowField = clamp(0.5 + macro * 0.44 + mid * 0.24 + fine * 0.08, 0.0, 1.0);
	    float snowLow = 0.76 - uMmoratWeatherSnow * 0.7;
	    float snowHigh = 0.86 - uMmoratWeatherSnow * 0.54;
	    float snowIsland = smoothstep(snowLow, max(snowLow + 0.035, snowHigh), snowField);
	    float snowInk = clamp(
	      snowIsland * snowSlope * (0.34 + uMmoratWeatherSnow * 0.72),
	      0.0,
	      0.94
	    );
	    vec3 snowPaper = mix(vec3(0.9, 0.925, 0.94), uMmoratHaze, 0.08);
	    diffuseColor.rgb = mix(diffuseColor.rgb, snowPaper, snowInk);
	    diffuseColor.rgb *= 1.0 + fine * snowInk * 0.035;
	  }
	  vec2 mmoratSheet = mmoratInkSheet(vMmoratWorld) * mmoratSheetScale;
	  float mmoratPulp = mmoratNoise(vec2(mmoratSheet.x / 9.0, mmoratSheet.y / 4.5)) - 0.5;
	  mmoratPaperTear = mmoratMaterialContrast(mmoratPulp, 0.5);
	  float mmoratLaidWeave =
	    0.300 * sin(mmoratSheet.x * 0.1130)
	    + 0.190 * sin(mmoratSheet.x * 0.2870 + 1.7);
	  float mmoratLaidWire = sin(fract(
	    mmoratSheet.y * 0.31250000
	      + mmoratPulp * 0.31830989
	      + mmoratLaidWeave
	  ) * 6.28318531);
	  float mmoratFibre = mmoratLaidWire * 0.72
	      * (0.75 + 0.25 * (mmoratPulp + 0.5))
	    + mmoratPaperTear * 0.35;
	  mmoratPaperShade = 1.0 + mmoratFibre * uMmoratInkB.x;
	  float mmoratCloudShadow = 0.0;
	  if (uMmoratWeatherSurface.y > 0.0015 && vMmoratSurf.y >= 0.0 && kind < 7.5) {
	    vec2 cloudDrift = uMmoratTerrainWindDirection
	      * uMmoratTerrainTime * (6.0 + uMmoratWeatherSurface.w * 15.0);
	    vec2 cloudUv = vMmoratWorld.xz - cloudDrift;
	    float cloudBroad = mmoratMaterialGradientNoise(
	      mat2(vec2(0.927, -0.375), vec2(0.375, 0.927)) * cloudUv / 560.0 + 3.1
	    );
	    float cloudDetail = mmoratMaterialGradientNoise(
	      mat2(vec2(0.839, -0.545), vec2(0.545, 0.839)) * cloudUv / 236.0 - 41.7
	    );
	    mmoratCloudShadow = smoothstep(0.44, 0.8, cloudBroad * 0.66 + cloudDetail * 0.34)
	      * uMmoratWeatherSurface.y;
	  }
	  if (vMmoratSurf.y >= 0.0 && kind < 7.5) {
	    float mmoratTornCloud = clamp(mmoratCloudShadow + mmoratPaperTear * 0.07, 0.0, 1.0);
	    float mmoratCloudPlane = uMmoratInkA.z > 0.5
	      ? floor(mmoratTornCloud * uMmoratInkA.z + 0.5) / uMmoratInkA.z
	      : mmoratTornCloud;
	    mmoratCloudShade = 1.0 - mmoratCloudPlane * 0.22;
	    mmoratCloudLift = 1.0;
	  }
	  float mmoratLum = dot(diffuseColor.rgb, vec3(0.2126, 0.7152, 0.0722));
	  diffuseColor.rgb = mix(vec3(mmoratLum), diffuseColor.rgb, 1.080);
	  mmoratAlbedoLum = dot(diffuseColor.rgb, vec3(0.2126, 0.7152, 0.0722));
	}
	float roughnessFactor = three_roughness;
	float mmoratFinishKind = vMmoratSurf.x;
	float mmoratFinishRoughness = mmoratFinishKind < 0.5
	  ? uMmoratTerrainMaterialFinish.x
	  : mmoratFinishKind < 3.5
	    ? uMmoratTerrainMaterialFinish.y
	    : mmoratFinishKind < 4.5
	      ? uMmoratTerrainMaterialFinish.z
	      : mix(uMmoratTerrainMaterialFinish.x, uMmoratTerrainMaterialFinish.y, 0.5);
	roughnessFactor = mix(roughnessFactor, mmoratFinishRoughness, 0.78);
	float mmoratWeatherFloor = (1.0 - step(0.5, vMmoratSurf.x)) * step(0.0, vMmoratSurf.y);
	roughnessFactor = mix(
	  roughnessFactor,
	  0.52,
	  uMmoratWeatherSurface.x * mmoratWeatherFloor * 0.72
	);
	float mmoratSnowSurface = (1.0 - step(1.5, vMmoratSurf.x)) * step(0.0, vMmoratSurf.y);
	roughnessFactor = mix(
	  roughnessFactor,
	  0.94,
	  uMmoratWeatherSnow * mmoratSnowSurface * 0.82
	);
	float metalnessFactor = three_metalness;
	float faceDirection = (FRONT_FACING != fluitown_mirrored) ? 1.0 : - 1.0;
		vec3 normal = normalize( vNormal );
	vec3 nonPerturbedNormal = normal;
	{
	  float reliefKind = vMmoratSurf.x;
	  float reliefStrength = 0.0;
	  float familyGain = 1.0;
	  if (reliefKind < 0.5) {
	    reliefStrength = 2.580;
	    familyGain = clamp(uMmoratStyle.x, 0.72, 1.28);
	  } else if (reliefKind < 1.5) {
	    reliefStrength = 3.150;
	    familyGain = clamp(uMmoratStyle.y, 0.72, 1.34);
	  } else if (reliefKind < 2.5) {
	    reliefStrength = 2.760;
	    familyGain = clamp(uMmoratStyle.z, 0.68, 1.34);
	  } else if (reliefKind < 3.5) {
	    reliefStrength = 3.650;
	    familyGain = clamp(uMmoratStyle.z, 0.68, 1.38);
	  } else if (reliefKind < 4.5) {
	    reliefStrength = 2.040;
	  } else if (reliefKind < 5.5) {
	    reliefStrength = 1.420;
	  } else if (reliefKind < 6.5) {
	    reliefStrength = 2.720;
	    familyGain = clamp(uMmoratStyle.z, 0.68, 1.34);
	  } else if (reliefKind < 7.5) {
	    reliefStrength = 3.650;
	    familyGain = clamp(uMmoratStyle.z, 0.68, 1.38);
	  } else if (reliefKind < 8.5) {
	    reliefStrength = 2.580;
	    familyGain = clamp(uMmoratStyle.y, 0.72, 1.34);
	  }
	  float staticSurface = step(0.0, vMmoratSurf.y);
	  float authoredGain = clamp(abs(vMmoratSurf.y) / 0.15, 0.48, 1.08);
	  float reliefFade = (reliefKind > 1.5 && reliefKind < 3.5) || (reliefKind > 5.5 && reliefKind < 7.5)
	    ? mmoratSharedWallDetailFade
	    : mmoratSharedDetailFade;
	  float fluitownReliefPixel = max(length(dFdx(vMmoratWorld)), length(dFdy(vMmoratWorld)));
	  float fluitownReliefResolve = 1.0 - smoothstep(1.2, 3.8, fluitownReliefPixel);
	  float relief = reliefStrength * familyGain * authoredGain * staticSurface * reliefFade
	    * (reliefKind < 0.5 ? 0.45 : reliefKind < 1.5 ? 0.7 : 1.0) * fluitownReliefResolve;
#ifndef FLUITOWN_TERRAIN_NO_RELIEF
	  if (relief > 0.001) {
	    normal = mmoratPerturbTerrainNormal(
	      -vViewPosition,
	      normal,
	      mmoratSharedReliefHeight,
	      relief
	    );
	  }
#endif
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

	// ── godot-flui's comic pipeline (assets/shaders/comic_style.gdshaderinc) ──
	// u_three_view is the scene yaw times the inverse compile scale: its transpose maps three view space to the world.
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
	// Weather cloud shade drifts over the ground as a pigment shade (the central light ramp has no per-surface term).
	fluitownPigment *= mmoratCloudShade;
	float fluitownKind = vMmoratSurf.x;
	float fluitownChasmFloorStep = (1.0 - step(0.5, abs(fluitownKind - 7.0))) * (1.0 - step(0.145, abs(vMmoratSurf.y)));
	float fluitownChasmFloor = step(7.5, fluitownKind) * (1.0 - step(8.5, fluitownKind));
	if (fluitownKind > 6.5 && fluitownKind < 7.5) {
	  // Chasm walls sink into the abyss (the original's depth extinction).
	  float fluitownChasmDepth = smoothstep(0.0, 1.0, clamp(-vMmoratWorld.y / 510.0, 0.0, 1.0));
	  fluitownPigment *= mix(1.0, 0.24, fluitownChasmDepth) * mix(1.0, 0.52, fluitownChasmFloorStep);
	}
	if (fluitownChasmFloor > 0.5) {
	  // The chasm floor keeps the original's dark, half-desaturated value.
	  float fluitownFloorLuminance = max(dot(fluitownPigment, vec3(0.2126, 0.7152, 0.0722)), 0.001);
	  vec3 fluitownFloorHue = mix(fluitownPigment, vec3(fluitownFloorLuminance), 0.5);
	  float fluitownFloorValue = clamp(
	    max(0.064, 0.078 - clamp(-9.0 - vMmoratWorld.y / 15.0, 0.0, 25.0) * 0.00056) * mmoratSharedChasmFloorVariation,
	    0.056,
	    0.116
	  );
	  fluitownPigment = fluitownFloorHue * (fluitownFloorValue / max(dot(fluitownFloorHue, vec3(0.2126, 0.7152, 0.0722)), 0.001));
	}
	if (vMmoratSurf.y < -0.0005 && vMmoratGround.w < -0.0001) {
	  // The ground's w is a signed path tangent, not a wind facet size. Testing w alone darkens entire
	  // negative-tangent cells into rectangular patches on otherwise continuous soil.
	  // Wind facets narrower than a pixel resolve to a calm value instead of sparkling (the original's facet bound).
	  float fluitownFacetVisibility = smoothstep(0.85, 2.60, -vMmoratGround.w * uMmoratViewZoom);
	  float fluitownFacetLuminance = max(dot(fluitownPigment, vec3(0.2126, 0.7152, 0.0722)), 0.001);
	  vec3 fluitownFacetResolved = fluitownPigment
	    * (min(fluitownFacetLuminance, 0.32 + fluitownFacetLuminance * 0.20) / fluitownFacetLuminance);
	  fluitownPigment = mix(fluitownFacetResolved, fluitownPigment, fluitownFacetVisibility);
	}
	// ── Fluitown comic stylisation (not in the original): pigment painted for the comic light ramp ──
	float fluitownFill = 0.0;
	// Pixel footprint in compile units: tiny pigment marks resolve before they become moving speckles.
	float fluitownFineLod = 1.0 - smoothstep(0.65, 1.8, max(length(dFdx(vMmoratWorld)), length(dFdy(vMmoratWorld))));
	if (!fluitown_ground_layers && fluitownKind < 0.5 && vMmoratSurf.y >= 0.0) {
	  // A trodden path lives in the cap's vertex pigment (ground.y = wear), which the coarse cap grid smears into a
	  // blurred stain. Its edge is painted: a crisp wear contour broken by noise, a thin darker rim, a deeper inside.
	  float fluitownWear = clamp(vMmoratGround.y, 0.0, 1.0);
	  float fluitownPathField = fluitownWear + (mmoratNoise(vMmoratWorld.xz / 21.0) - 0.5) * 0.12;
	  float fluitownPathAa = max(fwidth(fluitownPathField), 0.004);
	  float fluitownPathInside = smoothstep(0.16 - fluitownPathAa, 0.16 + fluitownPathAa, fluitownPathField);
	  float fluitownPathRim = (1.0 - smoothstep(fluitownPathAa, fluitownPathAa * 2.0 + 0.025, abs(fluitownPathField - 0.16)))
	    * step(0.02, fluitownWear);
	  // The rim stays a hint (0.1): along shores the wear field forms a trodden bank strip, and a 0.3 rim drew a dark
	  // trench a hand's width inland beside every waterline.
	  fluitownPigment *= mix(1.0, 0.9, fluitownPathInside) * (1.0 - fluitownPathRim * 0.1);
	  // Painted ground: a broad, soft value and warmth drift and a fine grain.
	  float fluitownBroad = mmoratMaterialGradientNoise(vMmoratWorld.xz / 190.0 + 3.7) - 0.5;
	  float fluitownGrain = mmoratNoise(vMmoratWorld.xz / 3.2) - 0.5;
	  fluitownPigment *= (1.0 + fluitownBroad * 0.18 + fluitownGrain * 0.045 * mmoratSharedDetailFade * fluitownFineLod)
	    * mix(vec3(0.97, 1.0, 1.04), vec3(1.04, 1.0, 0.95), clamp(fluitownBroad * 1.6 + 0.5, 0.0, 1.0));
	  if (fluitown_ground_paint) {
	    // Fluitown ground paint (world look; not in the original): bare earth seen from Flui height gets brush strokes
	    // along a slowly turning direction and scattered pebbles. Only within ~25 m of the eye (the bird's-eye camera is
	    // far away: never there) and only where the turf is thin.
	    float fluitownNear = 1.0 - smoothstep(12.0, 26.0, length(VERTEX));
	    float fluitownBare = 1.0 - smoothstep(0.08, 0.45, clamp(vMmoratGround.x, 0.0, 1.0));
	    if (fluitownNear * fluitownBare > 0.01) {
	      vec2 fluitownQ = vMmoratWorld.xz;
	      float fluitownAngle = mmoratNoise(fluitownQ / 420.0) * 6.2831853;
	      vec2 fluitownDir = vec2(cos(fluitownAngle), sin(fluitownAngle));
	      float fluitownStroke = mmoratNoise(vec2(dot(fluitownQ, fluitownDir) / 11.0, dot(fluitownQ, vec2(-fluitownDir.y, fluitownDir.x)) / 2.4)) - 0.5;
	      fluitownPigment *= 1.0 + fluitownStroke * 0.12 * fluitownNear * fluitownBare;
	      vec2 fluitownCell = floor(fluitownQ / 7.0);
	      vec3 fluitownPebble = fluitownUnpack3(fluitownHash3(ivec3(ivec2(fluitownCell), 77)));
	      if (fluitownPebble.z > 0.84) {
	        vec2 fluitownCentre = (fluitownCell + 0.25 + fluitownPebble.xy * 0.5) * 7.0;
	        float fluitownRadius = 0.7 + 1.3 * (fluitownPebble.z - 0.8) / 0.2;
	        float fluitownStone = 1.0 - smoothstep(fluitownRadius * 0.6, fluitownRadius, length(fluitownQ - fluitownCentre));
	        // Pale pebbles only: dark ones read as mottles on the pale ground of the Flui look.
	        fluitownPigment = mix(fluitownPigment, fluitownPigment * 1.16, fluitownStone * fluitownNear * fluitownBare);
	      }
	    }
	  }
	}
	if (!fluitown_rock_layers && fluitownKind > 0.5 && fluitownKind < 1.5 && vMmoratSurf.y >= 0.0) {
	  // Rock caps: the same broad drift as the ground, a coarser stone mottle and a fine grain.
	  float fluitownCapBroad = mmoratMaterialGradientNoise(vMmoratWorld.xz / 150.0 - 11.3) - 0.5;
	  float fluitownCapMottle = mmoratNoise(vMmoratWorld.xz / 13.0 + 5.1) - 0.5;
	  float fluitownCapGrain = mmoratNoise(vMmoratWorld.xz / 2.8 - 7.7) - 0.5;
	  fluitownPigment *= 1.0 + fluitownCapBroad * 0.14 + fluitownCapMottle * 0.06 + fluitownCapGrain * 0.04 * mmoratSharedDetailFade * fluitownFineLod;
	}
	if (!fluitown_rock_layers && fluitownKind > 1.5 && fluitownKind < 3.5) {
	  // Cliffs: a sunlit lip, a face that deepens towards its foot, soft strata one terrace level apart, and a fill so a
	  // face turned from the sun keeps its rock colour instead of going black.
	  float fluitownDrop = max(vMmoratGround.y, 0.0);
	  fluitownPigment *= mix(1.16, 0.8, smoothstep(3.0, 130.0, fluitownDrop));
	  // The mountain style below owns its bedding. Stacking three independent stripe fields made cliffs busy.
	  if (!fluitown_mountain) {
	    float fluitownStrata = sin((vMmoratWorld.y + (mmoratNoise(vMmoratWorld.xz / 38.0) - 0.5) * 9.0) / 15.0 * 6.2831853);
	    fluitownPigment *= 1.0 + fluitownStrata * 0.05;
	  }
	  // This is reflected fill, not a luminous rock material. Preserve the daylight pigment but let the
	  // existing day/night cycle dim it with the surrounding terrain; actual lamps and chasm glow stay separate.
	  fluitownFill = mix(1.0, 0.08, clamp(uMmoratNight, 0.0, 1.0));
	}
	if (fluitown_organic && vMmoratSurf.y >= 0.0 && fluitownKind > 0.5 && fluitownKind < 3.5
	    && (fluitownKind < 1.5 || fluitownKind > 2.5)) {
	  // Organic terrain: large angular rock facets on rock caps and rock faces (no joint network: that reads as masonry;
	  // the low earth risers and the chasm walls keep their plain surface, facets there cost pixels without showing).
	  // Their tilted normals are banded by the light ramp; each facet keeps its own value and warmth.
	  bool fluitownCapFacet = fluitownKind < 1.5;
	  vec3 fluitownFacetP = vMmoratWorld / (fluitown_rock_layers ? vec3(104.0, 76.0, 104.0)
	    : fluitownCapFacet ? vec3(84.0) : vec3(66.0, 52.0, 66.0));
	  vec3 fluitownFacetRandom = fluitownFacet(fluitownFacetP);
	  vec3 fluitownFacetStep = fwidth(fluitownFacetP);
	  // Facets smaller than a few pixels resolve to their average instead of sparkling.
	  float fluitownFacetLod = 1.0 - smoothstep(0.06, 0.2, max(max(fluitownFacetStep.x, fluitownFacetStep.y), fluitownFacetStep.z) * fluitown_lod_scale);
	  // The mountain form lets its strata carry the face: smaller facet tilts there.
	  float fluitownTilt = (fluitown_rock_layers ? (fluitownCapFacet ? 0.12 : 0.20)
	    : fluitownCapFacet ? 0.22 : fluitown_mountain ? 0.3 : 0.52) * fluitownFacetLod;
	  vec3 fluitownTiltWorld = fluitownFacetRandom * 2.0 - 1.0;
	  fluitownTiltWorld -= dot(fluitownTiltWorld, fluitownForm) * fluitownForm;
	  fluitownForm = normalize(fluitownForm + fluitownTiltWorld * fluitownTilt);
	  NORMAL = normalize(NORMAL + (VIEW_MATRIX * vec4(fluitownTiltWorld, 0.0)).xyz * fluitownTilt);
	  float fluitownFacetValue = fluitown_rock_layers ? 0.045 : fluitownCapFacet ? 0.07 : fluitown_mountain ? 0.07 : 0.13;
	  fluitownPigment *= mix(1.0, 1.0 + (fluitownFacetRandom.x - 0.5) * 2.0 * fluitownFacetValue, fluitownFacetLod);
	  fluitownPigment *= mix(vec3(1.0), mix(vec3(0.95, 1.0, 1.06), vec3(1.06, 1.0, 0.94), fluitownFacetRandom.y), fluitownFacetLod);
	  if (!fluitownCapFacet) {
	    // Bedding: bands of unequal thickness in value and warmth, gently tilted and bent along the cliff (two sines).
	    // Weathering: vertical streaks (the mountain form reads horizontally: its strata replace them).
	    if (!fluitown_mountain && !fluitown_rock_layers) {
	      float fluitownBed = sin(vMmoratWorld.y / 26.0 + sin(dot(vMmoratWorld.xz, vec2(0.0113, 0.0081))) * 2.3) * 0.5;
	      fluitownPigment *= (1.0 + fluitownBed * 0.14) * mix(vec3(0.98, 1.0, 1.03), vec3(1.04, 1.0, 0.95), fluitownBed + 0.5);
	      float fluitownStreak = mmoratNoise(vec2((vMmoratWorld.x + vMmoratWorld.z) / 17.0, vMmoratWorld.y / 190.0));
	      fluitownPigment *= 1.0 - smoothstep(0.55, 0.95, fluitownStreak) * 0.14;
	    }
	    // Shaded facets read through the light ramp instead of the flat fill.
	    fluitownFill *= 0.72;
	  }
	}
	if (!fluitown_rock_layers && fluitown_mountain && vMmoratSurf.y >= 0.0) {
	  if (fluitownKind > 1.5 && fluitownKind < 3.5) {
	    float fluitownAbove = max(vMmoratGround.z, 0.0);
	    float fluitownCrestDrop = max(vMmoratGround.y, 0.0);
	    // Strata: layers of their own value and warmth, gently tilted and bent along the massif.
	    // Layers of unequal thickness: the height is warped along itself before it is cut into bands.
	    float fluitownBandWarp = (mmoratNoise(vec2(vMmoratWorld.y / 41.0, dot(vMmoratWorld.xz, vec2(0.0023, 0.0017)))) - 0.5) * 22.0;
	    // Beds dip about 14° (along a fixed strike): on a round pillar they wind as slanted bands, not silo rings.
	    float fluitownBand = (vMmoratWorld.y + fluitownBandWarp + dot(vMmoratWorld.xz, vec2(0.21, 0.12))
	      + sin(dot(vMmoratWorld.xz, vec2(0.0071, 0.0049))) * 14.0) / 23.0;
	    float fluitownBandId = floor(fluitownBand);
	    float fluitownBandT = fluitownBand - fluitownBandId;
	    vec3 fluitownBandRandom = fluitownUnpack3(fluitownHash3(ivec3(int(fluitownBandId), 71, 29)));
	    float fluitownBandAa = fwidth(fluitownBand) * fluitown_lod_scale;
	    float fluitownBandLod = 1.0 - smoothstep(0.15, 0.45, fluitownBandAa);
	    vec3 fluitownBandTint = (0.9 + 0.2 * fluitownBandRandom.x) * mix(vec3(0.97, 1.0, 1.04), vec3(1.05, 1.0, 0.94), fluitownBandRandom.y);
	    float fluitownJoint = (1.0 - smoothstep(0.0, 0.07 + fluitownBandAa, fluitownBandT)) * fluitownBandLod;
	    float fluitownLip = smoothstep(0.84 - fluitownBandAa, 0.94, fluitownBandT) * fluitownBandLod;
	    fluitownPigment *= mix(vec3(1.0), fluitownBandTint, fluitownBandLod) * (1.0 - fluitownJoint * 0.23) * (1.0 + fluitownLip * 0.12);
	    // Damp and moss at the foot, in patches.
	    vec3 fluitownMoss = fluitownRegionSplat(fluitown_moss.rgb, 4, vMmoratWorld.xz);
	    vec3 fluitownCapGrass = fluitownRegionSplat(fluitown_cap_grass.rgb, 5, vMmoratWorld.xz);
	    float fluitownFoot = 1.0 - smoothstep(4.0, 42.0, fluitownAbove);
	    float fluitownPatch = smoothstep(0.38, 0.62, mmoratNoise(vMmoratWorld.xz / 23.0 + vMmoratWorld.y / 31.0));
	    fluitownPigment *= 1.0 - fluitownFoot * 0.16;
	    float fluitownValue = dot(fluitownPigment, vec3(0.2126, 0.7152, 0.0722));
	    fluitownPigment = mix(fluitownPigment, fluitownMoss * (0.55 + fluitownValue * 1.6), fluitownFoot * fluitownPatch * fluitown_moss.a * 0.8);
	    // Grass over the crest: a ragged fringe of the caps' green hanging down the top of the face.
	    float fluitownFringe = 4.0 + 10.0 * mmoratNoise(vMmoratWorld.xz / 9.0 + 3.3);
	    float fluitownOver = (1.0 - smoothstep(fluitownFringe - 1.5, fluitownFringe + 1.5, fluitownCrestDrop)) * fluitown_cap_grass.a;
	    fluitownPigment = mix(fluitownPigment, fluitownCapGrass * (0.72 + 0.28 * smoothstep(0.0, fluitownFringe, fluitownCrestDrop)), fluitownOver);
	  } else if (fluitownKind > 0.5 && fluitownKind < 1.5) {
	    // Rock caps: sparse tufts of grass and moss (the cap still reads as clean rock from above; a patch pattern read
	    // as camouflage there).
	    float fluitownCapTuft = mmoratNoise(vMmoratWorld.xz / 7.0 + 19.7) * 0.6 + mmoratNoise(vMmoratWorld.xz / 43.0 - 4.1) * 0.4;
	    float fluitownCapPatch = smoothstep(0.62, 0.74, fluitownCapTuft) * fluitown_cap_grass.a;
	    fluitownPigment = mix(fluitownPigment, fluitownRegionSplat(fluitown_cap_grass.rgb, 5, vMmoratWorld.xz) * (0.9 + 0.2 * fluitownCapTuft), fluitownCapPatch * 0.55);
	  }
	}
	// Wind-driven geometry (blades, canopies, hanging plants) is foliage for the comic style and its world ink.
	bool fluitownFoliage = vMmoratSurf.y < -0.0005;
	if (fluitownFoliage) {
	  // Fluitown comic stylisation: leafy break-up of smooth crowns and blades (small blossom clusters in value).
	  float fluitownLeaf = mmoratNoise(vMmoratWorld.xz / 3.6 + vMmoratWorld.y / 4.4) - 0.5;
	  float fluitownCluster = mmoratNoise(vMmoratWorld.xz / 10.5 - vMmoratWorld.y / 12.0 + 17.0) - 0.5;
	  fluitownPigment *= 1.0 + fluitownLeaf * 0.12 * fluitownFineLod + fluitownCluster * 0.10;
	}
	// STUDIO: live colour grade per material family (shaders/studio/studio_grade.gdshaderinc).
fluitownPigment = studio_grade(fluitownPigment, fluitownFoliage ? 3 : fluitownKind < 0.5 ? 0 : fluitownKind < 1.5 ? 1 : 2);
vec3 fluitownComicPigment = fluitownPigment * 0.3183098861837907;
	ALBEDO = comic_surface(fluitownComicPigment, comic_position, fluitownForm, NORMAL, VIEW, fluitownFoliage ? 2 : comic_role);
	// The world ink reads roughness: foliage (0.955-0.985) gets lighter ink and no creases, every other surface stays below.
	ROUGHNESS = fluitownFoliage ? 0.975 : min(material.roughness, 0.94);
	// The original's distance haze as a wash (the way godot-flui's landscape applies its atmosphere); lamps, windows and
	// the chasm glow shine on top of it.
	vec3 fluitownAir = mmoratAirMix(fluitownPigment, uMmoratHaze, mmoratHazeF(vMmoratWorld) * mix(1.0, 0.04, fluitownChasmFloor) * (1.0 - fluitownChasmFloorStep));
	float fluitownAirRange = dot(uMmoratHaze - fluitownPigment, vec3(0.2126, 0.7152, 0.0722));
	float fluitownHaze = abs(fluitownAirRange) > 0.001
	  ? clamp(dot(fluitownAir - fluitownPigment, vec3(0.2126, 0.7152, 0.0722)) / fluitownAirRange, 0.0, 1.0)
	  : clamp(mmoratHazeF(vMmoratWorld) * mix(1.0, 0.04, fluitownChasmFloor) * (1.0 - fluitownChasmFloorStep), 0.0, 1.0);
	ALBEDO *= 1.0 - fluitownHaze;
	float fluitownGlowField = 0.78 + mmoratNoise(
	  vMmoratWorld.xz / 176.0 + vec2(uMmoratTerrainTime * 0.004, -uMmoratTerrainTime * 0.003)
	) * 0.22;
	EMISSION = uMmoratHaze * fluitownHaze
	  + fluitownPigment * 0.3183098861837907 * fluitownFill * (1.0 - fluitownHaze)
	  + max(diffuseColor.rgb, vec3(0.0)) * max(0.0, vMmoratEmissive) * (1.0 + uMmoratNight * 0.65)
	  + uMmoratChasmGlowColor * uMmoratChasmGlow * fluitownGlowField * (
	    fluitownChasmFloor * 0.130
	      + step(6.5, fluitownKind) * (1.0 - step(7.5, fluitownKind)) * 0.065
	  );
	if (fluitown_debug_backfaces && fluitownSide < 0.0 && vMmoratSurf.y >= -0.0005) {
	  ALBEDO = vec3(0.0);
	  EMISSION = vec3(0.0, 4.0, 4.0);
	}

}
