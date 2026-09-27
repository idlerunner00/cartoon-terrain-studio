// Port of packages/client/src/render/environment/worldLighting.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `nightPaletteCache` is a WeakMap keyed by Biome identity → ConditionalWeakTable (thread-safe; the palette is a
//   pure function of the biome, so a racing duplicate computation yields an equal record).
// * `DayNightFrame.blueHour` / `.moonlight` are required numbers in TS, so `?? 0` / `?? night * 0.72` never apply;
//   the optional `season` / `nightBrightness` stay nullable and keep their `??` fallbacks.
// * `Object.freeze` records → readonly fields / const classes.
using System.Runtime.CompilerServices;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Render.BiomeLightingCompositionModule;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainLightRig;
using static Fluitown.Render.Theme;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// The derived light values shared by the terrain and actor scenes. Keeping this calculation outside both
/// renderers prevents a weather or art-direction adjustment from making figures look composited over the
/// ground instead of standing in it.
/// </summary>
public sealed class WorldLightingState
{
    public int sunColor;
    public int skyColor;
    public int groundColor;
    public int fillColor;
    public double sunIntensity;
    public double skyIntensity;
    public double fillIntensity;
    public double ambientIntensity;
    public WorldLightDirection sunDirection;
    public WorldLightDirection fillDirection;
    public double aerialPerspective;
}

/// <summary>
/// The nocturnal palette is deliberately pigment-led rather than a set of grey-blue constants. A pale biome
/// haze is first compressed to a dark colour at the SAME hue, then given a restrained moon-blue bias. This is
/// what keeps a sakura night violet, a forest night green and a drowned night cyan instead of averaging every
/// place into the same slate veil.
/// </summary>
public sealed class WorldNightPalette
{
    public int key;
    public int sky;
    public int fill;
    public int ground;
    public int air;
}

public static partial class EnvironmentWorldLighting
{
    /// <summary>
    /// Scene-referred light budget for an upward, paper-white Lambert surface.
    ///
    /// Biome styles deliberately change the balance of key, sky and bounce light. Multiplying four independently
    /// authored channels can nevertheless turn that balance into exposure: before this guard the styled worlds
    /// ranged from 0.72 to 1.31 reflected-light units, and the brightest pale worlds sent almost half of that from
    /// lights that cannot cast a shadow. ACES then compressed their already pale pigments into one low-chroma
    /// highlight band, while a real sun shadow could remove too little of the total light to read.
    ///
    /// The ceiling is intentionally a ONE-SIDED guard. Dark/noir/submarine rigs below it retain their authored
    /// energy and broad-light staging. Above it, broad light yields first so the directional key keeps modelling
    /// the world; the floor prevents an exceptionally strong key from turning every occluded surface black.
    /// </summary>
    public static class WORLD_LIGHTING_ENERGY
    {
        /// <summary>Maximum reflected energy on a white upward surface in ordinary weather.</summary>
        public const double topSurfaceCeiling = 0.92;
        /// <summary>Minimum broad-light share while an over-budget rig is being balanced.</summary>
        public const double broadLightFloorShare = 0.28;
        /// <summary>A full lightning event may lift the bounded frame, but cannot turn it into sustained whiteout.</summary>
        public const double lightningHeadroom = 0.18;
        /// <summary>Night-brightness accessibility retains a visible lift without bypassing the highlight guard.</summary>
        public const double nightPreferenceHeadroom = 0.1;
    }

    private static double upwardDirectionResponse(WorldLightDirection direction)
    {
        double hypot = Math.hypot(direction.x, direction.y, direction.z);
        double length = Js.Truthy(hypot) ? hypot : 1;
        return Math.max(0, direction.y / length);
    }

    private sealed class BalancedLightIntensities
    {
        public double sun;
        public double sky;
        public double fill;
        public double ambient;
    }

    /// <summary>
    /// Enforce the white-cap budget without changing any light colour or direction.
    ///
    /// All broad channels share one scale: sky colour, opposed fill and the ambient floor therefore keep their
    /// authored relationship. The sun has its own scale only when preserving it would violate the guaranteed
    /// broad-light floor. This is a calibration step on immutable inputs, never auto-exposure or frame feedback.
    /// </summary>
    private static BalancedLightIntensities balanceWorldLightIntensities(
        double sun,
        double sky,
        double fill,
        double ambient,
        WorldLightDirection sunDirection,
        WorldLightDirection fillDirection,
        double ceiling)
    {
        double direct = (sun / Math.PI) * upwardDirectionResponse(sunDirection);
        double broad =
            sky / Math.PI + (fill / Math.PI) * upwardDirectionResponse(fillDirection) + ambient / Math.PI;
        double total = direct + broad;
        if (total <= ceiling || total <= 1e-9)
            return new BalancedLightIntensities { sun = sun, sky = sky, fill = fill, ambient = ambient };

        double broadFloor = ceiling * WORLD_LIGHTING_ENERGY.broadLightFloorShare;
        double targetBroad = Math.min(broad, Math.max(broadFloor, ceiling - direct));
        double targetDirect = Math.max(0, ceiling - targetBroad);
        double directScale = direct > 1e-9 ? Math.min(1, targetDirect / direct) : 1;
        double broadScale = broad > 1e-9 ? Math.min(1, targetBroad / broad) : 1;
        return new BalancedLightIntensities
        {
            sun = sun * directScale,
            sky = sky * broadScale,
            fill = fill * broadScale,
            ambient = ambient * broadScale,
        };
    }

    /// <summary>One coherent night calibration shared by lights, atmosphere and the post-effect audit contract.</summary>
    public static class WORLD_NIGHT_LIGHTING
    {
        /// <summary>Moonless bases plus moon-altitude gains. Every channel remains modelled instead of becoming exposure.</summary>
        public const double sunBase = 0.51;
        public const double sunMoonGain = 0.13;
        public const double skyBase = 0.67;
        public const double skyMoonGain = 0.15;
        public const double fillBase = 0.69;
        public const double fillMoonGain = 0.13;
        public const double ambientBase = 0.67;
        public const double ambientMoonGain = 0.11;
        /// <summary>Cool horizon energy around blue hour, applied before the ordinary weather response.</summary>
        public const double blueHourSun = 0.04;
        public const double blueHourSky = 0.12;
        public const double blueHourFill = 0.1;
        public const double blueHourAmbient = 0.1;
        /// <summary>Global aerial perspective scatters less without a daylight sky; local fog remains independently alive.</summary>
        public const double aerialPerspectiveFloor = 0.74;
    }

    private static readonly ConditionalWeakTable<Biome, WorldNightPalette> nightPaletteCache = new();

    /// <summary>Derive the biome's chromatic nocturnal light family without allocating on ordinary lighting ticks.</summary>
    public static WorldNightPalette worldNightPalette(Biome biome)
    {
        if (nightPaletteCache.TryGetValue(biome, out WorldNightPalette? cached)) return cached;
        BiomeLightingComposition composition = biomeLightingComposition(biome);
        int authoredAir = mix(biome.haze, composition.skyTint, 0.38);
        (double h, double s, double l) authoredHsl = rgbToHsl(authoredAir);
        int airPigment = hslToRgb(
            authoredHsl.h,
            Math.min(0.58, Math.max(0.36, authoredHsl.s * 1.15)),
            0.44);
        int air = mix(airPigment, 0x29496f, 0.22);
        (double h, double s, double l) airHsl = rgbToHsl(air);
        WorldNightPalette palette = new WorldNightPalette
        {
            // A shared moon establishes the time of day; a small share of the family's key retains place identity.
            key = mix(0x9dbdff, composition.keyTint, 0.18),
            // Sky stays on the air's own hue, while the opposed fill shifts around the wheel to preserve form colour.
            sky = hslToRgb(airHsl.h, Math.min(0.54, Math.max(0.3, airHsl.s)), 0.6),
            fill = hslToRgb(airHsl.h + 34, Math.min(0.64, Math.max(0.44, airHsl.s + 0.1)), 0.72),
            ground = hslToRgb(airHsl.h + 10, Math.min(0.5, Math.max(0.28, airHsl.s)), 0.26),
            air = air,
        };
        nightPaletteCache.AddOrUpdate(biome, palette);
        return palette;
    }

    /// <summary>Biome-aware cool emission used by both Chasm pixels and their real local lights.</summary>
    public static int worldChasmGlowColor(Biome biome)
    {
        return mix(worldNightPalette(biome).fill, 0x63d9cf, 0.56);
    }

    /// <summary>Smooth shared gate: no daytime fluorescence, no hard switch at dusk.</summary>
    public static double worldChasmGlowStrength(DayNightFrame? dayNight = null)
    {
        if (!(dayNight?.enabled ?? false)) return 0;
        double night = Math.max(0, Math.min(1, dayNight!.night));
        double blueHour = Math.max(0, Math.min(1, dayNight.blueHour));
        return Math.max(0, Math.min(1, night * night * (0.72 + night * 0.28) + blueHour * 0.12));
    }

    /// <summary>Darkness reduces broad sky scattering, but never removes the depth cue.</summary>
    public static double worldAerialPerspectiveDayNightScale(DayNightFrame? dayNight = null)
    {
        if (!(dayNight?.enabled ?? false)) return 1;
        return 1 - dayNight!.night * (1 - WORLD_NIGHT_LIGHTING.aerialPerspectiveFloor);
    }

    /// <summary>
    /// Hemisphere ground light represents broad reflected ground energy, not the colour visible through chasms.
    /// Pull the authored ground pigment into a dark cool anchor: downward-facing forms keep biome identity and
    /// readable bounce without receiving a second, flat-looking key light.
    /// </summary>
    public static int worldGroundBounceColor(Biome biome)
    {
        int groundPigment = biome.groundTint ?? biome.ground;
        return mix(darken(groundPigment, 0.58), 0x18202b, 0.32);
    }

    /// <summary>Derive the complete, idempotent biome + weather light state for both production scenes.</summary>
    public static WorldLightingState worldLightingState(
        Biome biome,
        WeatherFrame? weather = null,
        double presentationScale = 1,
        bool lightningFlashes = true,
        DayNightFrame? dayNight = null)
    {
        WorldStyle style = worldStyleOf(biome);
        BiomeLightingComposition composition = biomeLightingComposition(biome);
        WorldNightPalette nightPalette = worldNightPalette(biome);
        double effect = weather != null ? Math.max(0, Math.min(1, presentationScale)) : 0;
        double cooling = (weather?.cooling ?? 0) * effect;
        double lightScale = 1 + ((weather?.lightScale ?? 1) - 1) * effect;
        double ambientScale = 1 + ((weather?.ambientScale ?? 1) - 1) * effect;
        double flash = weather != null && lightningFlashes ? weather.lightning * effect : 0;

        // Wider warm-key / cool-fill temperature split: the sun base leans golden and the fill base leans
        // blue so the frame always carries the painterly warm-light/cool-shade contrast, before any biome tint.
        int sunColor = mix(
            mix(mix(0xffe8b2, biome.lightTint, 0.3), composition.keyTint, 0.18),
            0xb8cce3,
            cooling * 0.2);
        int skyColor = mix(
            mix(mix(0xf4f6ef, biome.lightTint, 0.24), composition.skyTint, 0.16),
            0xaec9df,
            cooling * 0.26);
        int fillColor = mix(
            mix(mix(0xb4d3ee, biome.lightTint, 0.16), composition.fillTint, 0.18),
            0x91b9dc,
            cooling * 0.22);
        int groundColor = worldGroundBounceColor(biome);
        double sunIntensityScale = 1;
        double skyIntensityScale = 1;
        double fillIntensityScale = 1;
        double ambientIntensityScale = 1;
        WorldLightDirection sunDirection = composition.sunDirection;
        WorldLightDirection fillDirection = composition.fillDirection;

        if (dayNight?.enabled ?? false)
        {
            double night = dayNight.night;
            double twilight = dayNight.twilight;
            double blueHour = dayNight.blueHour;
            double moonlight = dayNight.moonlight;
            double sunlight = 1 - night;
            double cloudAtNight = (weather?.cloudCover ?? 0) * effect * night;
            double snowAtNight = (weather?.snowCover ?? 0) * effect * night;
            double winterNight = dayNight.season == Season.Winter ? night : 0;
            double summerStorm =
                dayNight.season == Season.Summer
                    ? night * effect * Math.max(weather?.cloudCover ?? 0, weather?.lightning ?? 0)
                    : 0;
            double nightBrightness = Math.max(0.7, Math.min(1.3, dayNight.nightBrightness ?? 1));
            double nightExposure = 1 + night * (nightBrightness - 1);
            // Moon, biome air and opposed fill keep three distinct colour jobs. The retired generic blue anchors made
            // every light converge on the same hue, so low-light surfaces could only resolve as slate grey.
            sunColor = mix(
                mix(
                    mix(sunColor, nightPalette.key, night * (0.54 + moonlight * 0.12)),
                    0x8eb9d8,
                    blueHour * 0.18),
                0xff914f,
                twilight * 0.66);
            skyColor = mix(
                mix(mix(skyColor, nightPalette.sky, night * 0.68), 0x557aa4, blueHour * 0.24),
                0x98546f,
                twilight * 0.28);
            fillColor = mix(
                mix(mix(fillColor, nightPalette.fill, night * 0.5), 0x6f9ac0, blueHour * 0.2),
                0xdc6573,
                twilight * 0.16);
            groundColor = mix(
                mix(groundColor, nightPalette.ground, night * 0.62),
                0x482234,
                twilight * 0.18);
            // Clouds occlude the moon while snow returns a cold, low ground bounce. Winter remains crisp and blue;
            // a summer storm shifts toward heavy violet air until a real lightning event supplies its own flash.
            sunColor = mix(sunColor, 0x71809a, cloudAtNight * 0.36);
            skyColor = mix(skyColor, 0x60708b, cloudAtNight * 0.32);
            fillColor = mix(fillColor, 0xb9d3ff, snowAtNight * 0.24 + winterNight * 0.08);
            groundColor = mix(groundColor, 0x9fb8d2, snowAtNight * 0.34);
            skyColor = mix(skyColor, 0x4c405d, summerStorm * 0.2);

            // Four independently shaped channels preserve depth throughout the clock. Direct moonlight follows its
            // altitude, broad sky/fill remain generous enough to read silhouettes, and blue hour bridges the two
            // horizons without a binary day/night exposure jump.
            sunIntensityScale = Math.min(
                1,
                sunlight +
                    night * (WORLD_NIGHT_LIGHTING.sunBase + moonlight * WORLD_NIGHT_LIGHTING.sunMoonGain) +
                    blueHour * WORLD_NIGHT_LIGHTING.blueHourSun);
            skyIntensityScale = Math.min(
                1,
                sunlight +
                    night * (WORLD_NIGHT_LIGHTING.skyBase + moonlight * WORLD_NIGHT_LIGHTING.skyMoonGain) +
                    blueHour * WORLD_NIGHT_LIGHTING.blueHourSky);
            fillIntensityScale = Math.min(
                1,
                sunlight +
                    night * (WORLD_NIGHT_LIGHTING.fillBase + moonlight * WORLD_NIGHT_LIGHTING.fillMoonGain) +
                    blueHour * WORLD_NIGHT_LIGHTING.blueHourFill);
            ambientIntensityScale = Math.min(
                1,
                sunlight +
                    night *
                        (WORLD_NIGHT_LIGHTING.ambientBase + moonlight * WORLD_NIGHT_LIGHTING.ambientMoonGain) +
                    blueHour * WORLD_NIGHT_LIGHTING.blueHourAmbient);
            sunIntensityScale *= (1 - cloudAtNight * 0.42) * nightExposure;
            skyIntensityScale *= (1 - cloudAtNight * 0.18) * (1 + snowAtNight * 0.2) * nightExposure;
            fillIntensityScale *= (1 - cloudAtNight * 0.12) * (1 + snowAtNight * 0.32) * nightExposure;
            ambientIntensityScale *= (1 - cloudAtNight * 0.08) * (1 + snowAtNight * 0.18) * nightExposure;

            // The key DIRECTION is deliberately static: the cycle is expressed in colour, intensity and the twilight
            // band above, never by orbiting the light.
            //
            // Why: the terrain's sun shadow is one 1024² depth map over the whole view. A depth map cannot interpolate
            // between two light directions — it can only be re-rendered — so any rotation of this vector forces a full
            // republication of the terrain shadow as soon as the accumulated error reaches a texel. Measured on the
            // authored cycle that was ~5 publications per second (the 15-minute horizon handoff sweeps 180° with the
            // key pinned at y=0.2, which multiplies the shadow-slope rate by ~15× against the day arc). A rebuilt
            // world shadow every 200 ms is not something the frame can hide, and slowing the cadence only trades the
            // rate for a visible step in every shadow. A static direction keeps real, stable shadows and costs the
            // terrain depth map ZERO time-driven work: it is now rebuilt only when the view anchor moves or streamed
            // casters change. `sunAzimuthOffset`/`solarElevation` remain the cycle's own truth for anything that wants
            // the physical sun (see dayNightCycle.ts); they simply no longer steer the light rig.
            sunDirection = composition.sunDirection;
            fillDirection = composition.fillDirection;
        }
        if (flash > 0)
        {
            sunColor = mix(sunColor, 0xfff6dd, flash * 0.72);
            skyColor = mix(skyColor, 0xe9eeff, flash * 0.46);
        }

        double rawSunIntensity =
            LIGHT_RIG.sun * style.sunLight * lightScale * sunIntensityScale + LIGHT_RIG.sun * flash * 0.62;
        double rawSkyIntensity =
            LIGHT_RIG.hemi * style.hemiLight * ambientScale * skyIntensityScale +
            LIGHT_RIG.hemi * flash * 0.28;
        double rawFillIntensity =
            LIGHT_RIG.fill * style.fillLight * (1 + (ambientScale - 1) * 0.72) * fillIntensityScale;
        double rawAmbientIntensity =
            LIGHT_RIG.ambient * style.ambientLight * (1 + (ambientScale - 1) * 0.5) * ambientIntensityScale;
        double nightPreference = dayNight?.enabled ?? false
            ? (dayNight!.night * Math.max(0, Math.min(1.3, dayNight.nightBrightness ?? 1) - 1)) / 0.3
            : 0;
        double energyCeiling =
            WORLD_LIGHTING_ENERGY.topSurfaceCeiling +
            flash * WORLD_LIGHTING_ENERGY.lightningHeadroom +
            nightPreference * WORLD_LIGHTING_ENERGY.nightPreferenceHeadroom;
        BalancedLightIntensities balanced = balanceWorldLightIntensities(
            rawSunIntensity,
            rawSkyIntensity,
            rawFillIntensity,
            rawAmbientIntensity,
            sunDirection,
            fillDirection,
            energyCeiling);

        return new WorldLightingState
        {
            sunColor = sunColor,
            skyColor = skyColor,
            groundColor = groundColor,
            fillColor = fillColor,
            sunIntensity = balanced.sun,
            skyIntensity = balanced.sky,
            fillIntensity = balanced.fill,
            ambientIntensity = balanced.ambient,
            sunDirection = sunDirection,
            fillDirection = fillDirection,
            aerialPerspective = composition.aerialPerspective,
        };
    }
}
