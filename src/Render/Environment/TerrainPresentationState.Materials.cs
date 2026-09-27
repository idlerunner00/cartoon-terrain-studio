// Port of packages/client/src/render/environment/threeTerrain.ts (class ThreeTerrainLayer) — the shared terrain
// materials: construction flags, the uniform objects each `onBeforeCompile` installs (by name, by identity) and the
// program cache keys. The GLSL each hook splices into three's shader chunks is ported separately
// (godot/shaders/terrain/*.gdshader, generated from the captured programs), so only its uniform half lives here.
// See TerrainPresentationState.cs for the notes.
using System;
using static Fluitown.Render.TerrainInteriorCutaway;
using static Fluitown.Render.TerrainProjection;
using static Fluitown.Render.VisualQuality;

namespace Fluitown.Render;

public sealed partial class TerrainPresentationState
{
    /* ── Materials (shared instances; compiled once, reused by every tile) ──────────────────────────────── */

    private ThreeMeshStandardMaterial surfaceMaterialFor()
    {
        if (this.surfaceMaterial != null) return this.surfaceMaterial;
        ThreeMeshStandardMaterial material = new ThreeMeshStandardMaterial
        {
            roughness = CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.surfaceRoughness,
            metalness = 0,
            vertexColors = true,
            side = ThreeConstants.FrontSide,
        };
        material.color.set(0xffffff);
        material.shadowSide = ThreeConstants.DoubleSide;
        if (this.webglExecutionClass == "software")
        {
            this.surfaceMaterial = material;
            return material;
        }
        ThreeUniform<ThreeColor> hazeColor = this.hazeColor;
        ThreeUniform<ThreeVector4> hazeCfg = this.hazeCfg;
        InkLookUniforms ink = this.ink;
        ThreeUniform<ThreeVector4> styleGains = this.styleGains;
        ThreeUniform<ThreeColor> styleDriftA = this.styleDriftA;
        ThreeUniform<ThreeColor> styleDriftB = this.styleDriftB;
        ThreeUniform<double> terrainTime = this.waterTime;
        ThreeUniform<ThreeVector4> terrainWind = this.terrainWind;
        ThreeUniform<ThreeVector2> terrainWindDirection = this.terrainWindDirection;
        ThreeUniform<double> terrainViewZoom = this.terrainViewZoom;
        ThreeUniform<ThreeColor> waterBasinColor = this.waterBasinColor;
        ThreeUniform<ThreeColor> floorSplatLush = this.floorSplatLush;
        ThreeUniform<ThreeColor> floorTurf = this.floorTurf;
        ThreeUniform<ThreeColor> floorSplatDry = this.floorSplatDry;
        ThreeUniform<ThreeColor> floorSplatMineral = this.floorSplatMineral;
        ThreeUniform<ThreeVector4> terrainFormA = this.terrainFormA;
        ThreeUniform<ThreeVector4> terrainFormB = this.terrainFormB;
        ThreeUniform<ThreeVector4> terrainFormResolved = this.terrainFormResolved;
        ThreeUniform<double> terrainRidgeInverse = this.terrainRidgeInverse;
        ThreeUniform<ThreeVector4> terrainMaterialA = this.terrainMaterialA;
        ThreeUniform<ThreeVector2> terrainMaterialRotation = this.terrainMaterialRotation;
        ThreeUniform<ThreeVector3> terrainMaterialFrequency = this.terrainMaterialFrequency;
        ThreeUniform<ThreeVector3> paperSheet = this.paperSheet;
        ThreeUniform<ThreeVector4> terrainMaterialB = this.terrainMaterialB;
        ThreeUniform<ThreeVector4> terrainMaterialFinish = this.terrainMaterialFinish;
        ThreeUniform<ThreeVector4> terrainContour = this.terrainContour;
        ThreeUniform<ThreeVector4> weatherSurface = this.weatherSurface;
        ThreeUniform<double> weatherSnow = this.weatherSnow;
        ThreeUniform<double> terrainNight = this.terrainNight;
        ThreeUniform<double> terrainChasmGlow = this.terrainChasmGlow;
        ThreeUniform<ThreeColor> terrainChasmGlowColor = this.terrainChasmGlowColor;
        ThreeUniform<ThreeVector4> hazeBody = this.hazeBody;
        material.onBeforeCompile = (shader) =>
        {
            bindTerrainInteriorCutawayUniforms(shader.uniforms);
            shader.uniforms["uMmoratHaze"] = hazeColor;
            shader.uniforms["uMmoratHazeCfg"] = hazeCfg;
            shader.uniforms["uMmoratHazeBody"] = hazeBody;
            shader.uniforms["uMmoratInkA"] = ink.inkA;
            shader.uniforms["uMmoratInkB"] = ink.inkB;
            shader.uniforms["uMmoratInkStructure"] = ink.inkStructure;
            shader.uniforms["uMmoratInkTint"] = ink.inkTint;
            shader.uniforms["uMmoratInkLine"] = ink.inkLine;
            shader.uniforms["uMmoratPaperSheet"] = paperSheet;
            shader.uniforms["uMmoratStyle"] = styleGains;
            shader.uniforms["uMmoratDriftA"] = styleDriftA;
            shader.uniforms["uMmoratDriftB"] = styleDriftB;
            shader.uniforms["uMmoratTerrainTime"] = terrainTime;
            shader.uniforms["uMmoratTerrainWind"] = terrainWind;
            shader.uniforms["uMmoratTerrainWindDirection"] = terrainWindDirection;
            shader.uniforms["uMmoratViewZoom"] = terrainViewZoom;
            shader.uniforms["uMmoratWaterBasin"] = waterBasinColor;
            shader.uniforms["uMmoratFloorSplatLush"] = floorSplatLush;
            shader.uniforms["uMmoratFloorTurf"] = floorTurf;
            shader.uniforms["uMmoratFloorSplatDry"] = floorSplatDry;
            shader.uniforms["uMmoratFloorSplatMineral"] = floorSplatMineral;
            shader.uniforms["uMmoratTerrainFormA"] = terrainFormA;
            shader.uniforms["uMmoratTerrainFormB"] = terrainFormB;
            shader.uniforms["uMmoratTerrainFormResolved"] = terrainFormResolved;
            shader.uniforms["uMmoratTerrainRidgeInverse"] = terrainRidgeInverse;
            shader.uniforms["uMmoratTerrainMaterialA"] = terrainMaterialA;
            shader.uniforms["uMmoratTerrainMaterialRotation"] = terrainMaterialRotation;
            shader.uniforms["uMmoratTerrainMaterialFrequency"] = terrainMaterialFrequency;
            shader.uniforms["uMmoratTerrainMaterialB"] = terrainMaterialB;
            shader.uniforms["uMmoratTerrainMaterialFinish"] = terrainMaterialFinish;
            shader.uniforms["uMmoratTerrainContour"] = terrainContour;
            shader.uniforms["uMmoratWeatherSurface"] = weatherSurface;
            shader.uniforms["uMmoratWeatherSnow"] = weatherSnow;
            shader.uniforms["uMmoratNight"] = terrainNight;
            shader.uniforms["uMmoratChasmGlow"] = terrainChasmGlow;
            shader.uniforms["uMmoratChasmGlowColor"] = terrainChasmGlowColor;
            // vertex/fragment chunk surgery (TERRAIN_GPU_VERTEX_SHAPING / wind / relief / ground detail / ink /
            // chasm extinction / haze): ported in shaders/terrain/terrain_surface_comic.gdshader.
        };
        material.customProgramCacheKey = () =>
            "fluitown-terrain-surface-v84-multiscale-chasm-floor-material-bounded-value-window-post-extinction-wall-strata-full-domain-chasm-depth-grade-completed-floor-guard-legible-maximum-depth-chasm-foundation-free-material-variation-textured-basin-bias-only-bound-bounded-chasm-floor-modulation-legible-deep-ground-continuum-matched-floor-datum-geometric-world-side-projection-all-material-families-readable-textured-shaft-depth-floor-seam-initialized-step-floor-variation-integrated-chasm-floor-step-corners-variation-ordered-no-black-wedges-absorptive-chasm-floor-rims-zero-step-haze-neutral-charcoal-floor-standard-chasm-terrace-corners-dark-value-clamped-no-haze-lift-depth-graded-chasm-floor-dark-terrace-edges-full-floor-composition-dark-growth-contrast-preserved-ordinary-floor-source-pigment-final-darkening-full-floor-material-darkened-at-final-chasm-floor-rounded-structured-deep-floor-continuous-world-height-extinction-owner-matched-chasm-walls-local-base-material-split-charcoal-no-haze-lift-real-chasm-floor-tiles-full-depth-shaft-walls-shared-detail-footprint-material-contrast-response-bounded-rib-presented-pitch-light-independent-sheet-sheet-locked-laid-paper-is-light-torn-cloud-wash-plane-unconditional-wall-erosion-cloud-is-light-continuous-fragment-cloud-gradient-material-fields-dealigned-domain-warp-shared-wall-cap-pigment-continuous-theme-shade-world-continuous-cap-pigment-smooth-shared-organic-normals-readable-geological-midtones-world-pigment-drift-normal-wall-material-continuation-no-foreign-masonry-zero-extra-noise-cpu-uniform-rotation-form-material-readable-waterfall-banks-night-amplified-hdr-geometry-emissive-reflection-safe-semantic-finish-weather-wet-glint-haze-body-exact-walk-plane-straight-chasm-wall-water-bank-closure-shared-relief-bare-ground-aggregate-turf-cover-is-ground-semantic-material-ink-no-extra-noise-air-mix-v2-time-curved-chasm-emission" +
            "-presented-pixel-wind-coverage-v1" +
            "-seasonal-snow-cover-v1" +
            "-lit-hydraulic-basin-support-v1" +
            "-embedded-room-cutaway-v1" +
            "-broad-quiet-terrain-planes-v1" +
            (VISUAL_QUALITY.microGeometryAntialias ? "-animated-microfacet-lod-v2" : "-raw-microfacets") +
            (VISUAL_QUALITY.reliefAntialias ? "-footprint-faded-relief-v3" : "-unfaded-relief");
        ActorShadowProjectionModule.installActorShadowProjection(material, this.actorShadows, this.quality.actorGroundShadowTaps);
        this.surfaceMaterial = material;
        return material;
    }

    private ThreeMaterial terrainSurfaceMaterialFor()
    {
        if (this.richStreamedTerrain) return this.surfaceMaterialFor();
        ThreeMeshBasicMaterial material =
            this.webglExecutionClass == "software"
                ? (this.softwareSurfaceMaterial ??= newVertexColorBasicMaterial())
                : (this.compactSurfaceMaterial ??= newVertexColorBasicMaterial());
        if (!(material.userData.TryGetValue("terrainInteriorCutawayInstalled", out object? installed) && installed is true))
        {
            material.userData["terrainInteriorCutawayInstalled"] = true;
            material.onBeforeCompile = (shader) =>
            {
                bindTerrainInteriorCutawayUniforms(shader.uniforms);
                // chunk surgery: TERRAIN_INTERIOR_CUTAWAY_{VERTEX,FRAGMENT}_PARS_GLSL + COMPACT_DISCARD_GLSL.
            };
            material.customProgramCacheKey = () => "fluitown-compact-terrain-embedded-room-cutaway-v1";
            material.needsUpdate = true;
        }
        return material;
    }

    /// <summary>`new MeshBasicMaterial({ color: 0xffffff, vertexColors: true, side: FrontSide })`.</summary>
    private static ThreeMeshBasicMaterial newVertexColorBasicMaterial()
    {
        ThreeMeshBasicMaterial material = new ThreeMeshBasicMaterial
        {
            vertexColors = true,
            side = ThreeConstants.FrontSide,
        };
        material.color.set(0xffffff);
        return material;
    }

    /// <summary>Custom depth deformation is unnecessary while authoritative walk planes remain exact.</summary>
    private ThreeMeshDepthMaterial surfaceDepthMaterialFor()
    {
        if (this.surfaceDepthMaterial != null) return this.surfaceDepthMaterial;
        ThreeMeshDepthMaterial material = new ThreeMeshDepthMaterial();
        if (!TERRAIN_GPU_VERTEX_SHAPING)
        {
            material.onBeforeCompile = (shader) =>
            {
                bindTerrainInteriorCutawayUniforms(shader.uniforms);
                // chunk surgery: TERRAIN_INTERIOR_CUTAWAY_{VERTEX,FRAGMENT}_PARS_GLSL + DISCARD_GLSL.
            };
            material.customProgramCacheKey = () =>
                "fluitown-terrain-surface-depth-v6-exact-walk-plane-embedded-room-cutaway";
            this.surfaceDepthMaterial = material;
            return material;
        }
        ThreeUniform<ThreeVector4> terrainFormA = this.terrainFormA;
        ThreeUniform<ThreeVector4> terrainFormB = this.terrainFormB;
        ThreeUniform<ThreeVector4> terrainFormResolved = this.terrainFormResolved;
        ThreeUniform<double> terrainRidgeInverse = this.terrainRidgeInverse;
        ThreeUniform<ThreeVector4> terrainContour = this.terrainContour;
        material.onBeforeCompile = (shader) =>
        {
            bindTerrainInteriorCutawayUniforms(shader.uniforms);
            shader.uniforms["uMmoratTerrainFormA"] = terrainFormA;
            shader.uniforms["uMmoratTerrainFormB"] = terrainFormB;
            shader.uniforms["uMmoratTerrainFormResolved"] = terrainFormResolved;
            shader.uniforms["uMmoratTerrainRidgeInverse"] = terrainRidgeInverse;
            shader.uniforms["uMmoratTerrainContour"] = terrainContour;
            // chunk surgery: contour warp + organic shape + cutaway (see the surface material).
        };
        material.customProgramCacheKey = () =>
            "fluitown-terrain-surface-depth-v6-cpu-uniform-organic-contour-embedded-room-cutaway";
        this.surfaceDepthMaterial = material;
        return material;
    }

    private ThreeMeshStandardMaterial waterMaterialFor()
    {
        if (this.waterMaterial != null) return this.waterMaterial;
        ThreeMeshStandardMaterial material = new ThreeMeshStandardMaterial
        {
            roughness = CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.waterRoughness,
            metalness = 0,
            vertexColors = true,
            side = ThreeConstants.FrontSide,
            // Pool and fall are one fully opaque liquid block. The Chasm-facing side keeps the complete source width
            // down to its depth datum; alpha-tapered silhouettes made a solid water block look like a hanging decal.
            alphaToCoverage = false,
            alphaTest = 0,
        };
        material.color.set(0xffffff);
        if (this.webglExecutionClass == "software")
        {
            this.waterMaterial = material;
            return material;
        }
        ThreeUniform<double> waterTime = this.waterTime;
        ThreeUniform<ThreeColor> foam = this.waterFoamColor;
        ThreeUniform<ThreeColor> shallow = this.waterShallowColor;
        ThreeUniform<ThreeColor> deep = this.waterDeepColor;
        ThreeUniform<ThreeColor> hazeColor = this.hazeColor;
        ThreeUniform<ThreeVector4> hazeCfg = this.hazeCfg;
        ThreeUniform<ThreeVector4> waterStyle = this.waterStyle;
        ThreeUniform<double> waterSky = this.waterSky;
        ThreeUniform<double> night = this.terrainNight;
        // `{ value: this.sunDirection }` — the SAME Vector3 object the light rig mutates in applyEnvironmentLighting.
        ThreeUniform<ThreeVector3> lightDirection = new(this.sunDirection);
        ThreeUniform<ThreeVector4> terrainContour = this.terrainContour;
        ThreeUniform<ThreeVector4> hazeBody = this.hazeBody;
        ThreeUniform<double> viewZoom = this.terrainViewZoom;
        material.onBeforeCompile = (shader) =>
        {
            shader.uniforms["uMmoratTime"] = waterTime;
            shader.uniforms["uMmoratFoam"] = foam;
            shader.uniforms["uMmoratShallow"] = shallow;
            shader.uniforms["uMmoratDeep"] = deep;
            shader.uniforms["uMmoratHaze"] = hazeColor;
            shader.uniforms["uMmoratHazeCfg"] = hazeCfg;
            shader.uniforms["uMmoratHazeBody"] = hazeBody;
            shader.uniforms["uMmoratWaterStyle"] = waterStyle;
            shader.uniforms["uMmoratWaterSky"] = waterSky;
            shader.uniforms["uMmoratNight"] = night;
            shader.uniforms["uMmoratWaterLightDirection"] = lightDirection;
            shader.uniforms["uMmoratTerrainContour"] = terrainContour;
            shader.uniforms["uMmoratViewZoom"] = viewZoom;
            // chunk surgery: ported in shaders/terrain/terrain_water_comic.gdshader.
        };
        material.customProgramCacheKey = () =>
            "fluitown-terrain-water-v69-projected-animated-paper-mark-gate-world-continuous-pigment-two-scale-shore-apron-crest-material-continuity-continuous-curtain-ceiling-palette-bound-curtain-quarter-rounded-contours-crest-response-continuity-static-analytic-rounded-meniscus-static-organic-soft-bank-curved-batched-transition-frame-authoritative-flat-datums-single-full-edge-transition-frameless-waterfall-sides-bounded-ballistic-landing-identical-liquid-response-fine-ridge-spectrum-shared-downward-field-local-pigment-night-cap-exact-carrier-appearance-smooth-chasm-curtain-absorption-phase-broken-cross-current-spectrum-flat-horizontal-normal-night-caustic-gate-domain-warped-isotropic-flow-authored-deep-absorption-directional-sun-moon-path-night-meniscus-readable-waterfall-cliffs-baked-bank-silhouette-reflection-safe-semantic-glass-continuous-field-shared-wave-contour-sky-crown-haze-body-surface-field-over-crest-down-curtain-air-mix-v2";
        Func<string> baseWaterProgramCacheKey = material.customProgramCacheKey;
        material.customProgramCacheKey = () =>
            "fluitown-terrain-water-v102-vertical-flank-value-floor-no-open-black-curtain-crest-carried-pattern-morph-longitudinal-filaments-single-signed-arc-clock-impact-cloud-entry-whitewater-white-budget-marks-not-amplitude-cross-ridge-folded-cell-net-pigment-space-folded-cell-net-dominant-folded-cell-net-water-stair-chasm-carrier-unopposed-downward-flow-visible-cartoon-cell-net-over-block-side-full-width-opaque-block-side-guaranteed-downward-stretched-arc-flow-arc-length-unfolded-real-crest-fold-world-distance-ground-dissolve-no-cell-shore-pigment-topological-path-gates-side-curtain-fold-continuity-longitudinal-flow-strands-and-mist-depth-continuous-crest-normal-turn-no-liquid-ink-stripes-no-crest-mask-full-spectrum-footprint-filter-projected-spectrum-antialias-unbanded-height-edge-honest-normal-immediate-downward-sheet-body-only-waterfall-ceiling-authoritative-cell-size-" +
            baseWaterProgramCacheKey();
        this.waterMaterial = material;
        return material;
    }

    private ThreeMaterial terrainWaterMaterialFor()
    {
        if (this.richStreamedTerrain) return this.waterMaterialFor();
        if (this.webglExecutionClass == "software")
            return (this.softwareWaterMaterial ??= newVertexColorBasicMaterial());
        return (this.compactWaterMaterial ??= newVertexColorBasicMaterial());
    }

    private ThreeMeshBasicMaterial overlayMaterialFor()
    {
        if (this.overlayMaterial != null) return this.overlayMaterial;
        // TRUE alpha blending (RGBA vertex colours): decals/AO dim the LIT, textured surface beneath instead of
        // painting a flat pre-composited patch over it — soft contact gradients become possible, and the surface
        // grain stays alive under every decal. depthWrite off + polygonOffset keep it flicker-free on its host.
        ThreeMeshBasicMaterial material = new ThreeMeshBasicMaterial
        {
            vertexColors = true,
            transparent = true,
            depthWrite = false,
            side = ThreeConstants.FrontSide,
            polygonOffset = true,
            polygonOffsetFactor = -1,
            polygonOffsetUnits = -2,
        };
        material.color.set(0xffffff);
        if (this.webglExecutionClass == "software")
        {
            material.onBeforeCompile = (shader) =>
            {
                bindTerrainInteriorCutawayUniforms(shader.uniforms);
                // chunk surgery: cutaway pars + COMPACT_DISCARD_GLSL.
            };
            material.customProgramCacheKey = () =>
                "fluitown-terrain-overlay-software-embedded-room-cutaway-v1";
            this.overlayMaterial = material;
            return material;
        }
        ThreeUniform<ThreeColor> hazeColor = this.hazeColor;
        ThreeUniform<ThreeVector4> hazeCfg = this.hazeCfg;
        ThreeUniform<ThreeVector4> hazeBody = this.hazeBody;
        ThreeUniform<ThreeVector4> terrainContour = this.terrainContour;
        ThreeUniform<double> terrainNight = this.terrainNight;
        material.onBeforeCompile = (shader) =>
        {
            bindTerrainInteriorCutawayUniforms(shader.uniforms);
            shader.uniforms["uMmoratHaze"] = hazeColor;
            shader.uniforms["uMmoratHazeCfg"] = hazeCfg;
            shader.uniforms["uMmoratHazeBody"] = hazeBody;
            shader.uniforms["uMmoratTerrainContour"] = terrainContour;
            shader.uniforms["uMmoratNight"] = terrainNight;
            // chunk surgery: ported in shaders/terrain/terrain_overlay_comic.gdshader.
        };
        material.customProgramCacheKey = () =>
            "fluitown-terrain-overlay-v6-contour-night-accent-haze-body-air-mix-v2-embedded-room-cutaway";
        this.overlayMaterial = material;
        return material;
    }

    /// <summary>Animated, feathered chasm atmosphere. This is deliberately a separate batch from decals: mist floats
    ///  through physical depth, drifts slowly, and must remain soft rather than inheriting ink polygon offsets.</summary>
    private ThreeMeshBasicMaterial mistMaterialFor()
    {
        if (this.mistMaterial != null) return this.mistMaterial;
        ThreeMeshBasicMaterial material = new ThreeMeshBasicMaterial
        {
            vertexColors = true,
            transparent = true,
            depthWrite = false,
            side = ThreeConstants.DoubleSide,
        };
        material.color.set(0xffffff);
        if (this.webglExecutionClass == "software")
        {
            this.mistMaterial = material;
            return material;
        }
        ThreeUniform<double> terrainTime = this.waterTime;
        ThreeUniform<double> terrainNight = this.terrainNight;
        ThreeUniform<ThreeColor> hazeColor = this.hazeColor;
        ThreeUniform<ThreeVector4> hazeCfg = this.hazeCfg;
        ThreeUniform<ThreeVector4> hazeBody = this.hazeBody;
        material.onBeforeCompile = (shader) =>
        {
            shader.uniforms["uMmoratMistTime"] = terrainTime;
            shader.uniforms["uMmoratMistNight"] = terrainNight;
            shader.uniforms["uMmoratHaze"] = hazeColor;
            shader.uniforms["uMmoratHazeCfg"] = hazeCfg;
            shader.uniforms["uMmoratHazeBody"] = hazeBody;
            // chunk surgery: ported in godot/shaders/terrain/terrain_mist_{front,back}.gdshader.
        };
        material.customProgramCacheKey = () =>
            "fluitown-terrain-chasm-mist-v13-soft-overlap-theme-bound-volume-night-tint-curl-advected-depth-currents-haze-body-air-mix-v2";
        this.mistMaterial = material;
        return material;
    }

    /// <summary>Keep actor depth and sun-shadow proxies on the exact same embedded-room aperture as visible terrain.</summary>
    private void installActorWallCutaway(ThreeMaterial material, string cacheKey)
    {
        bool contours = this.webglExecutionClass != "software";
        ThreeUniform<ThreeVector4> terrainContour = this.terrainContour;
        material.onBeforeCompile = (shader) =>
        {
            bindTerrainInteriorCutawayUniforms(shader.uniforms);
            if (contours) shader.uniforms["uMmoratTerrainContour"] = terrainContour;
            // chunk surgery: contour warp (when contours) + cutaway pars + WALL_DISCARD_GLSL.
        };
        material.customProgramCacheKey = () =>
            $"fluitown-terrain-actor-wall-{cacheKey}-v3-contour-embedded-room-cutaway";
    }

    /// <summary>Exact SOLID-wall mask for the actor pass. It writes depth only; visible wall colour was already rendered.</summary>
    private ThreeMeshBasicMaterial actorWallMaterialFor()
    {
        if (this.actorWallMaterial != null) return this.actorWallMaterial;
        ThreeMeshBasicMaterial material = new ThreeMeshBasicMaterial
        {
            colorWrite = false,
            depthTest = true,
            depthWrite = true,
            side = ThreeConstants.FrontSide,
        };
        material.shadowSide = ThreeConstants.DoubleSide;
        this.installActorWallCutaway(material, "actor-depth");
        this.actorWallMaterial = material;
        return material;
    }

    private ThreeMeshDepthMaterial actorWallDepthMaterialFor()
    {
        if (this.actorWallDepthMaterial != null) return this.actorWallDepthMaterial;
        ThreeMeshDepthMaterial material = new ThreeMeshDepthMaterial { side = ThreeConstants.DoubleSide };
        this.installActorWallCutaway(material, "sun-depth");
        this.actorWallDepthMaterial = material;
        return material;
    }
}
