// Port of packages/client/src/render/environment/threeTerrain.ts (class ThreeTerrainLayer) — biome, weather, day/night,
// environment lighting, world yaw, camera sync and the post-frame seams. See TerrainPresentationState.cs for the notes.
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Render.EnvironmentWorldLighting;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainProjection;
using static Fluitown.Render.ThreeTerrain;
using static Fluitown.Render.VisualQuality;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed partial class TerrainPresentationState
{
    public void setBiome(
        Biome biome,
        int _instanceKind = 0,
        bool deferSourceDisposal = false,
        bool preparedBankActivated = false)
    {
        string? previousBiomeKey = this.biome?.key;
        _ = previousBiomeKey;
        // Tile half (hub-return prefetch reset, planner reset, readiness reset, retire/evict) — see the host contract.
        this.host?.resetForBiome(biome, deferSourceDisposal, preparedBankActivated);
        this.biome = biome;
        this.terrainChasmGlowColor.value.setHex(worldChasmGlowColor(biome));
        // A retained weather module publishes the destination climate after the switch. Until then, never carry a
        // source realm's rain/light scale into this biome; synchronized daylight is global and may remain valid.
        this.weatherPresentationScale = 0;
        this.tileset = TerrainTilesetModule.terrainTilesetForBiome(biome);
        this.host?.biomeTilesetChanged(biome, this.tileset);
        // The theme's tileset language tunes how the wall/floor bake shapes its terrain and which ground dressing it
        // scatters. Resolved once per biome (chunk-cache safe).
        string construction = Theme.tilesetOf(biome);
        this.cityTileset = construction == "city";
        this.olympianTileset = construction == "olympian";
        this.cathedralTileset = construction == "cathedral";
        this.carnivalTileset = construction == "carnival";
        this.clockworkTileset = construction == "clockwork";
        this.prismglassTileset = construction == "prismglass";
        this.shadowDirty = true;
        this.terrainShadowSnapshotInitialized = false;
        this.terrainShadowSnapshotStableFrames = 0;
        // A realm switch is already presentation-gated and owns a complete replacement shadow snapshot. Do not
        // let retiring source tiles defer that first authoritative destination map.
        this.shadowCasterSnapshotPending = false;
        this.visibleShadowCasterSnapshotPending = false;
        this.host?.resetCapturedShadowCasters();
        // Both hybrid scenes consume one derived state. In particular, hemisphere ground light represents
        // restrained biome ground bounce instead of the unrelated streamed-world fallback colour.
        WorldLightingState? lighting = this.applyEnvironmentLighting(true);
        // A direction change must refresh the snapped light anchor even if the camera stayed still across a
        // prepared-bank activation.
        this.lastShadowCx = double.NaN;
        this.lastShadowCz = double.NaN;
        this.lastShadowRadius = double.NaN;
        this.sun.shadow!.intensity = 1;
        WorldStyle style = Theme.worldStyleOf(biome);
        this.waterFoamColor.value.setHex(gradeColor(this.tileset.flood.foam));
        this.waterDeepColor.value.setHex(gradeColor(this.tileset.flood.deep));
        this.waterBasinColor.value.setHex(
            gradeColor(mix(this.tileset.flood.deep, this.tileset.flood.body, 0.74)));
        // Authored theme water (lava/ink/meltwater) states its own shallow band; derived water keeps the shared
        // chalk-shallow blend so the "one world" base survives on ordinary rivers.
        this.waterShallowColor.value.setHex(
            gradeColor(
                biome.water != null
                    ? mix(biome.water.surface, biome.water.foam, 0.3)
                    : mix(
                        TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS.water.shallow ?? 0x849095,
                        this.tileset.flood.surface,
                        0.5)));
        // Aerial haze colour (including the current day/night and climate state) was resolved with the shared light
        // rig above. The surface profile below only controls its density budget.
        // Three material poles, one existing surface shader. Their masks reuse macro/mid/fine noise that is already
        // paid for by the material relief pass, so anti-tiling gains ecological colour regions without textures,
        // texture fetches, another material or another draw call.
        this.floorSplatLush.value.setHex(
            gradeColor(mix(this.tileset.terrain.floorTone, this.tileset.decal.mid, 0.62)));
        this.floorTurf.value.setHex(
            gradeColor(TerrainFloorTurf.terrainFloorTurfPole(this.tileset.decal.mid, biome.groundAccentB)));
        this.floorSplatDry.value.setHex(
            gradeColor(
                mix(
                    this.tileset.terrain.floorTone,
                    mix(biome.groundAccentA, this.tileset.elevation.cliffFace, 0.45),
                    0.52)));
        this.floorSplatMineral.value.setHex(
            gradeColor(mix(this.tileset.elevation.cliffFace, this.tileset.terrain.wallFace, 0.58)));
        // Per-theme world style → shared uniforms (pattern gains, hue-drift poles, water temper). Pure uniform
        // writes: the theme's material character costs zero rebakes.
        this.styleStrata = style.strata;
        // .w carries the living-wall gain from the SAME construction-aware mapping as the bake. The shader can
        // never paint moss for a profile whose geometry emitter considers the wall barren.
        this.styleGains.value.set(
            style.floorGrain,
            style.rockGrain,
            style.strata,
            TerrainWallGrowth.terrainWallGrowthAbundance(style.groundAccent, this.tileset.construction, biome.pattern));
        (double ar, double ag, double ab) = driftMultiplier(style.driftA, style.driftAmp);
        (double br, double bg, double bb) = driftMultiplier(style.driftB, style.driftAmp);
        this.styleDriftA.value.setRGB(ar, ag, ab);
        this.styleDriftB.value.setRGB(br, bg, bb);
        TerrainSurfaceProfile surfaceProfile = TerrainSurfaceProfileModule.terrainSurfaceProfileForBiome(biome.key);
        this.terrainSurfaceProfile = surfaceProfile;
        this.floorDryPole = mix(
            this.tileset.terrain.floorTone,
            mix(biome.groundAccentA, this.tileset.elevation.cliffFace, 0.45),
            0.52);
        // Wall pigment now belongs to the shared compiler, which configures its own builder in setBiome.
        this.terrainFormA.value.set(
            surfaceProfile.formA[0],
            surfaceProfile.formA[1],
            TERRAIN_GPU_VERTEX_SHAPING && this.visualGroundingEnabled ? surfaceProfile.formA[2] : 0,
            surfaceProfile.formA[3]);
        this.terrainFormB.value.set(surfaceProfile.formB[0], surfaceProfile.formB[1], surfaceProfile.formB[2], surfaceProfile.formB[3]);
        this.terrainFormResolved.value.set(
            Math.cos(surfaceProfile.formA[3]),
            Math.sin(surfaceProfile.formA[3]),
            1 / Math.max(1, surfaceProfile.formA[0]),
            1 / Math.max(1, surfaceProfile.formA[1]));
        this.terrainRidgeInverse.value = 1 / Math.max(12, surfaceProfile.formB[3]);
        this.terrainMaterialA.value.set(
            surfaceProfile.materialA[0], surfaceProfile.materialA[1], surfaceProfile.materialA[2], surfaceProfile.materialA[3]);
        this.terrainMaterialRotation.value.set(
            Math.cos(surfaceProfile.materialA[3]),
            Math.sin(surfaceProfile.materialA[3]));
        this.terrainMaterialFrequency.value.set(
            1 / Math.max(16, surfaceProfile.materialA[0]),
            1 / Math.max(8, surfaceProfile.materialA[1]),
            1 / Math.max(5, surfaceProfile.materialA[2]));
        this.terrainMaterialB.value.set(
            surfaceProfile.materialB[0], surfaceProfile.materialB[1], surfaceProfile.materialB[2], surfaceProfile.materialB[3]);
        BiomeMaterialFinish materialFinish = BiomeMaterialLibrary.biomeMaterialFinish(biome);
        this.terrainMaterialFinish.value.set(
            materialFinish.floorRoughness,
            materialFinish.rockRoughness,
            materialFinish.timberRoughness,
            materialFinish.accentSheen);
        double contourAmplitude = TERRAIN_GPU_VERTEX_SHAPING
            ? construction == "natural"
                ? CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.contourWarp.maxAmplitudePx
                : construction == "combined-building"
                    ? 3.7
                    : construction == "prism"
                        ? 3.1
                        : construction == "viking-ship-village"
                            ? 3.4
                            : construction == "alien-ranch"
                                ? 3.6
                                : 0
            : 0;
        this.terrainContour.value.set(
            contourAmplitude,
            Math.max(CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.contourWarp.minimumScalePx, surfaceProfile.formA[0] * 0.32),
            0,
            0);
        this.terrainHazeGain = surfaceProfile.haze * (lighting?.aerialPerspective ?? 1);
        // Shallow-water caustic gain (→ uMmoratWaterStyle.w): only lively, sparkling water throws light nets
        // across its shallows, so the gain derives from the authored temper — a bright racing brook dances,
        // the frogmire's near-stagnant murk (waveSpeed 0.32) shows essentially none.
        double lively = Math.min(1, Math.max(0, (style.waveSpeed - 0.38) / 0.55));
        this.waterStyle.value.set(
            style.waveSpeed,
            style.foam,
            style.glint,
            Math.min(0.34, 0.26 * lively * Math.min(style.glint, 1.5)));
        // How much SKY open water carries (→ uMmoratWaterSky): themes whose hemisphere dome IS the light —
        // the drowned Abyss, the glass Archive — mirror more of it in their deep pools.
        this.waterSky.value = 0.16 * style.hemiLight;
        this.terrainWind.value.set(
            style.windDirection,
            style.windStrength,
            style.windSpeed,
            style.windGust);
        this.terrainWindDirection.value.set(
            Math.cos(style.windDirection),
            Math.sin(style.windDirection));
    }

    /// <summary>
    /// Move the existing key colour and grade for the shared Hub/Endless clock at 5 Hz. The cycle no longer
    /// rotates the key DIRECTION (see `worldLighting.ts`), so this can never invalidate the terrain depth map —
    /// a colour write is free, a republished 1024² world shadow is not.
    /// </summary>
    public void setDayNight(DayNightFrame dayNight)
    {
        this.dayNight = dayNight;
        this.terrainNight.value = dayNight.enabled ? dayNight.night : 0;
        this.terrainChasmGlow.value = worldChasmGlowStrength(dayNight);
        this.applyEnvironmentLighting(true);
    }

    private WorldLightingState? applyEnvironmentLighting(bool updateDirection)
    {
        Biome? biome = this.biome;
        if (biome == null) return null;
        double effect = this.weather != null ? this.weatherPresentationScale : 0;
        WorldLightingState lighting = worldLightingState(
            biome,
            this.weather,
            effect,
            this.weatherLightningFlashes,
            this.dayNight);
        this.sun.color.setHex(lighting.sunColor);
        this.skyLight.color.setHex(lighting.skyColor);
        this.skyLight.groundColor.setHex(
            VISUAL_QUALITY.worldLights ? lighting.groundColor : this.backdropColor());
        this.fill.color.setHex(lighting.fillColor);
        this.sun.intensity = lighting.sunIntensity;
        this.skyLight.intensity = lighting.skyIntensity;
        this.fill.intensity = lighting.fillIntensity;
        this.ambient.intensity = lighting.ambientIntensity;

        if (updateDirection)
        {
            bool directionChanged =
                Math.abs(this.sunDirection.x - lighting.sunDirection.x) > 0.000_01 ||
                Math.abs(this.sunDirection.y - lighting.sunDirection.y) > 0.000_01 ||
                Math.abs(this.sunDirection.z - lighting.sunDirection.z) > 0.000_01;
            this.sunDirection.set(
                lighting.sunDirection.x,
                lighting.sunDirection.y,
                lighting.sunDirection.z);
            this.fillDirection.set(
                lighting.fillDirection.x,
                lighting.fillDirection.y,
                lighting.fillDirection.z);
            if (directionChanged)
            {
                // A biome/weather change can still move the authored key. Re-seat the lights on the existing anchor
                // and mark the depth map dirty ONCE for that content change — this is not a per-tick clock path: the
                // day/night cycle holds the direction fixed, so `directionChanged` is false on every ordinary tick.
                if (this.terrainShadowsEnabled) this.shadowDirty = true;
                if (
                    Number.isFinite(this.lastShadowCx) &&
                    Number.isFinite(this.lastShadowCz) &&
                    Number.isFinite(this.lastShadowRadius))
                    this.applyLightDirectionsAtAnchor(this.lastShadowCx, this.lastShadowCz);
            }
        }

        WeatherFrame? weather = this.weather;
        double cooling = (weather?.cooling ?? 0) * effect;
        int backdrop = mix(
            gradeColor(this.backdropColor()),
            0xaebdca,
            effect * ((weather?.cloudCover ?? 0) * 0.08 + cooling * 0.12));
        int haze = mix(gradeColor(mix(biome.haze, 0xf0efe6, 0.42)), 0xc8d8e3, cooling * 0.28);
        int clear = TERRAIN_CLEAR_COLOR;
        if (this.dayNight?.enabled == true)
        {
            double night = this.dayNight.night;
            double twilight = this.dayNight.twilight;
            // `blueHour` / `moonlight` are required numbers of DayNightFrame, so their `??` fallbacks never apply.
            double blueHour = this.dayNight.blueHour;
            double moonlight = this.dayNight.moonlight;
            WorldNightPalette nightPalette = worldNightPalette(biome);
            double nightBrightness = Math.max(0.7, Math.min(1.3, this.dayNight.nightBrightness ?? 1));
            double cloudAtNight = (weather?.cloudCover ?? 0) * effect * night;
            double snowAtNight = (weather?.snowCover ?? 0) * effect * night;
            // Day air contains paper because a sunlit horizon is bright. Reusing that pole at night was the grey
            // veil: distant pigment converged on pale neutral paper even though no bright sky existed. Night now
            // converges on the biome's own compressed, chromatic air and the backdrop follows its dark ground pole.
            double nightAirMix = night * (0.87 - moonlight * 0.06);
            backdrop = mix(
                mix(backdrop, gradeColor(nightPalette.ground), nightAirMix),
                0x7d334f,
                twilight * 0.18);
            haze = mix(
                mix(mix(haze, gradeColor(nightPalette.air), nightAirMix), 0x587b9c, blueHour * 0.2),
                0xa64867,
                twilight * 0.22);
            clear = mix(
                mix(mix(clear, 0x294562, nightAirMix), 0x3e688c, blueHour * 0.16),
                0x60243d,
                twilight * 0.14);
            backdrop = mix(backdrop, 0x101725, cloudAtNight * 0.32);
            haze = mix(haze, 0x263247, cloudAtNight * 0.26);
            haze = mix(haze, 0x8fa9c2, snowAtNight * 0.18);
            // The accessibility control participates in the real sky/air solution alongside the light rig. It does
            // not touch daytime or UI and therefore cannot become a dark full-screen veil.
            double preferenceStrength = Math.abs(nightBrightness - 1) * night * 0.7;
            int preferencePole = nightBrightness >= 1 ? 0x526989 : 0x000000;
            backdrop = mix(backdrop, preferencePole, preferenceStrength);
            haze = mix(haze, preferencePole, preferenceStrength * 0.72);
            clear = mix(clear, preferencePole, preferenceStrength * 0.48);
        }
        ((ThreeMeshBasicMaterial)this.backdrop.material).color.setHex(backdrop);
        this.hazeColor.value.setHex(haze);
        this.terrainClearColor.setHex(clear);
        this.renderer?.setClearColor(
            this.aetherDestinationBlendedClearColor
                .copy(this.terrainClearColor)
                .lerp(this.aetherDestinationSkyColor.value, this.aetherDestinationSkyBlend),
            1);
        return lighting;
    }

    /// <summary>
    /// Rotate the complete terrain world around a ground-plane pivot. Under the game's orthographic
    /// projection this is the exact inverse transform of orbiting the camera around that pivot. The
    /// private terrain scene and its actor-wall depth scene must remain locked together.
    /// </summary>
    public void setWorldYaw(double yawRad, double pivotX, double pivotZ)
    {
        double yaw = Number.isFinite(yawRad) ? yawRad : 0;
        applyWorldYaw(this.scene, yaw, pivotX, pivotZ);
        applyWorldYaw(this.actorWallScene, yaw, pivotX, pivotZ);
        // The drawn sheet is oriented on the PAGE, not on the world axes: without this the laid paper line and
        // the hatch would shear with the world the moment the camera orbits. `.z` (the presentation scale) is
        // owned by syncCamera and must survive a yaw, so the two components are written independently.
        this.paperSheet.value.x = Math.cos(yaw);
        this.paperSheet.value.y = Math.sin(yaw);
    }

    public void syncCamera(TerrainCameraSync sync)
    {
        if (this.renderer == null) return;
        this.width = Math.max(1, Math.round(sync.width));
        this.height = Math.max(1, Math.round(sync.height));
        this.zoom = Js.Truthy(sync.zoom) ? sync.zoom : 1;
        this.terrainViewZoom.value = Math.max(0, this.zoom);
        this.offsetX = sync.offsetX;
        this.offsetY = sync.offsetY;
        double fallbackRatio = Math.min(this.quality.maxResolution, Js.Truthy(this.quality.devicePixelRatio) ? this.quality.devicePixelRatio : 1);
        double nextPixelRatio = Math.max(
            1,
            Js.Truthy(sync.resolution) ? sync.resolution : fallbackRatio);
        if (Math.abs(nextPixelRatio - this.pixelRatio) > 0.001)
        {
            this.pixelRatio = nextPixelRatio;
            this.renderer.setPixelRatio(nextPixelRatio);
            this.rendererWidth = 0;
            this.rendererHeight = 0;
        }
        // The drawn sheet's mip is authored in the pixels the player is SHOWN. `fwidth` measures render-target
        // fragments, so without this the quality tier silently rescaled the paper: at resolution 1.25 the laid
        // wire presented 20 % finer than authored and beat against the resolve.
        this.paperSheet.value.z = nextPixelRatio;
        if (this.width != this.rendererWidth || this.height != this.rendererHeight)
        {
            this.rendererWidth = this.width;
            this.rendererHeight = this.height;
            this.renderer.setSize(this.width, this.height, false);
        }
        // Visible world rect implied by the normalized ground-plane mapping (Y=0). Screen Y carries
        // `worldZ * GROUND_SCALE`; inverting without that factor would cull/prefetch too little terrain.
        double projectedTop = (0 - this.offsetY) / this.zoom;
        double projectedBottom = (this.height - this.offsetY) / this.zoom;
        double worldTop = projectedTop / TERRAIN_VIEW_GROUND_SCALE;
        double worldBottom = projectedBottom / TERRAIN_VIEW_GROUND_SCALE;
        double worldLeft = (0 - this.offsetX) / this.zoom;
        double worldRight = (this.width - this.offsetX) / this.zoom;
        // Rebuild the oblique projection + its inverse only when the camera actually moved/zoomed/resized. All inputs
        // (incl. the depth window, which derives from offsetY/zoom/height) live in this tuple, so an unchanged tuple
        // would reproduce the identical 4×4 matrix + inverse every frame — pure waste on a still camera. NaN
        // sentinels force the first build.
        if (
            this.width != this.lastProjW ||
            this.height != this.lastProjH ||
            this.zoom != this.lastProjZoom ||
            this.offsetX != this.lastProjOffX ||
            this.offsetY != this.lastProjOffY)
        {
            this.lastProjW = this.width;
            this.lastProjH = this.height;
            this.lastProjZoom = this.zoom;
            this.lastProjOffX = this.offsetX;
            this.lastProjOffY = this.offsetY;
            // Depth window: projected ground depth plus generous sunken/tall geometry headroom.
            double depthMin =
                worldTop * TERRAIN_VIEW_HEIGHT_SCALE - (BACKDROP_PAD + BACKDROP_DROP_Y + 600);
            double depthMax = worldBottom * TERRAIN_VIEW_HEIGHT_SCALE + BACKDROP_PAD + 600;
            configureOrthographicProjection(
                this.camera.projectionMatrix.elements,
                this.width,
                this.height,
                this.zoom,
                this.offsetX,
                this.offsetY,
                depthMin,
                depthMax);
            this.camera.projectionMatrixInverse.copy(this.camera.projectionMatrix).invert();
        }
        // Aerial haze is anchored on the camera FOCUS in projected pre-zoom world px, and its depth range is an
        // authored world-space constant — never the frame height. A viewport-normalised band made the cue a
        // screen gradient: zooming out added no air, zooming in fogged the world at two metres. The focus is the
        // midpoint of the projected view, which for a ground fragment is the same quantity the depth axis reads.
        // Publishing through the shared atmosphere hands the SAME band to the actor scene in the same write.
        AerialHazeModule.WORLD_AERIAL_HAZE.publishDepthBand(
            (projectedTop + projectedBottom) * 0.5,
            this.terrainHazeGain *
                this.weatherHazeScale *
                worldAerialPerspectiveDayNightScale(this.dayNight));
        CameraView? lightingView = sync.worldView;
        this.updateLighting(
            lightingView?.left ?? worldLeft,
            lightingView?.right ?? worldRight,
            lightingView?.top ?? worldTop,
            lightingView?.bottom ?? worldBottom);
    }

    /// <summary>Reset Three's cached GL state before an externally driven render pass.</summary>
    public void resetState()
    {
        this.renderer?.resetState();
    }

    /// <summary>Route the already-existing terrain + actor render sequence into the composer's shared HDR/depth target.</summary>
    public void beginPostFrame(bool forceTransitionSurface = false)
    {
        // A covered Aether destination deliberately keeps its terrain root hidden while the exact landing view is
        // installed. Its carrier and clouds must nevertheless stay on the same scene-linear target as every other
        // world frame. Falling back to the canvas for those few frames changes outputColorSpace and makes Three link
        // a second family of otherwise identical balloon and cloud programs in the middle of flight. The same rule
        // applies to the one atomic frame where destination coverage changes: root visibility can flip inside
        // `update`, after this method has selected the target. Always bind the stable HDR target for a presented
        // frame; cache-only reconnect submissions are the only caller which deliberately skips this method.
        _ = forceTransitionSurface;
    }

    /// <summary>Grade, tone-map and present the populated shared target.</summary>
    public void presentPostFrame(double deltaSeconds)
    {
        this.impactLights.advance(deltaSeconds);
        // This is the one seam where every quantity that scales the WHOLE frame is simultaneously live, and it
        // runs exactly once per PRESENTED frame — which is what makes an alternation here the same alternation
        // the player sees. Disabled builds pay one boolean compare (see presentationFlickerLog.ts).
        // (`FLICKER_LOG_ENABLED` is the opt-in `?flickerlog=1` diagnostic; the recorder is not ported, so the two
        // republication counters are simply reset here exactly as `recordFlickerSample` would.)
        this.flickerShadowRebuilds = 0;
        this.flickerVisibleShadowLatticeRebuilds = 0;
    }

    private int backdropColor()
    {
        TerrainPalette? terrain = this.tileset?.terrain;
        ElevationPalette? elevation = this.tileset?.elevation;
        if (terrain == null || elevation == null) return BACKDROP_FALLBACK;
        if (this.olympianTileset && this.biome != null)
        {
            int sky = mix(this.biome.haze, this.tileset?.flood.surface ?? 0xc9f5ff, 0.28);
            return mix(sky, 0xffffff, 0.18);
        }
        int abyss = this.tileset?.chasm.deep ?? terrain.wallDeep;
        int themeTrace = mix(terrain.wallDeep, elevation.cliffFace, 0.22);
        // This colour is visible only beyond streamed/authored geometry: keep a faint biome trace but anchor it in
        // near-black void. Real Chasm cells close above it with their own materialized deep-floor tiles.
        return mix(mix(0x05070b, abyss, 0.14), themeTrace, 0.025);
    }
}
