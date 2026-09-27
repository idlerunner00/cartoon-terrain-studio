// Port of packages/client/src/render/environment/dayNightCycle.ts — keep in lockstep with the original.
//
// PORT NOTES
// * Ported because worldLighting.ts consumes
//   its `DayNightFrame` and the lighting parity tests drive the sampler. File/class follow the PORTING rules.
// * `calendarScratch` is a module-level scratch reading → [ThreadStatic] lazy (one per thread; the sampler is
//   synchronous, so a per-thread scratch reproduces the original exactly).
// * `dayNightPhaseOverride` parses a browser query string. `new URLSearchParams(search).get(...)`, JS `trim()` and
//   `Number(string)` are reproduced inline (see the helpers at the end); there is no `location` in the engine-free
//   port, so callers pass the search string explicitly exactly as the original's callers do.
using System;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.Calendar;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class DayNightFrame
{
    /// <summary>False outside the Hub and Endless Runs; the remaining values then describe neutral authored daylight.</summary>
    public bool enabled;
    /// <summary>Position in the world day: midnight = 0, noon = 0.5 — the calendar's own `timeOfDay`.</summary>
    public double phase;
    /// <summary>Physical sun height proxy (0 at both horizons and through the night, +1 at noon).</summary>
    public double solarElevation;
    /// <summary>Smooth authored daylight contribution, floored at <see cref="DayNightCycle.DAY_NIGHT_MIN_DAYLIGHT"/>.</summary>
    public double daylight;
    /// <summary>How deep into night the sky is, 0 through the day, 1 in the dead of night.</summary>
    public double night;
    /// <summary>Warm horizon band around dawn and dusk.</summary>
    public double twilight;
    /// <summary>Artistic moon altitude, 0 at the night horizons and 1 at midnight.</summary>
    public double moonElevation;
    /// <summary>Continuous nocturnal key contribution, including the low moon floor needed for navigation.</summary>
    public double moonlight;
    /// <summary>Cool horizon fill just outside dawn/dusk; distinct from the warm twilight band.</summary>
    public double blueHour;
    /// <summary>Rotation around the authored biome key direction, in radians.</summary>
    public double sunAzimuthOffset;
    /// <summary>Season sampled from the same authoritative world instant as the light (<see cref="Season"/>).</summary>
    public int? season;
    /// <summary>User presentation multiplier for night only; 1 is the authored calibration.</summary>
    public double? nightBrightness;
}

/// <summary>
/// **The sky reads the world clock.** (WP17 — CONCEPT §12, Simulation doctrine 8.)
///
/// One world day is twenty-four real minutes, so an ordinary session watches a complete
/// dawn-to-dark arc, and the length of that daylight is the SEASON's (`calendar.ts`,
/// WORLD_DAYLIGHT_HOURS): a winter evening falls visibly earlier than a summer one. The
/// cosmetic 45/15-minute wall-clock cycle this module used to run is gone — light is now a pure
/// function of the world instant, the same function on every client and on any future surface,
/// because it reads the shared calendar and holds no clock of its own.
///
/// The sampler keeps its shape: callers hand it a clock (now a WORLD instant) or a `?dayphase=`
/// override, and receive the same <see cref="DayNightFrame"/> the light rig, terrain and post pass
/// already consume. Phase 0 is midnight and 0.5 is noon — the calendar's own `timeOfDay`.
/// </summary>
public static partial class DayNightCycle
{
    /// <summary>Deep-night sky contribution. Local warm lights and moon modelling carry readability above this floor.</summary>
    public const double DAY_NIGHT_MIN_DAYLIGHT = 0.14;

    /// <summary>Sunrise as a phase of the world day, for a given season. Symmetric about noon (0.5).</summary>
    public static double dayNightSunrisePhase(int season)
    {
        return 0.5 - WORLD_DAYLIGHT_HOURS[season] / WORLD_HOURS_PER_DAY / 2;
    }

    /// <summary>Sunset as a phase of the world day, for a given season.</summary>
    public static double dayNightSunsetPhase(int season)
    {
        return 0.5 + WORLD_DAYLIGHT_HOURS[season] / WORLD_HOURS_PER_DAY / 2;
    }

    public static DayNightFrame createDayNightFrame()
    {
        return new DayNightFrame
        {
            enabled = false,
            phase = 0.5,
            solarElevation = 1,
            daylight = 1,
            night = 0,
            twilight = 0,
            moonElevation = 0,
            moonlight = 0,
            blueHour = 0,
            sunAzimuthOffset = 0,
            season = Season.Spring,
            nightBrightness = 1,
        };
    }

    /// <summary>Only the permanent Hub and every InstanceKind.Run participate in the shared world cycle.</summary>
    public static bool dayNightCycleEnabled(int instanceKind)
    {
        return instanceKind == InstanceKind.Hub || instanceKind == InstanceKind.Run;
    }

    /// <summary>Scratch reading reused across samples — the sampler sits on the frame path and must not allocate.</summary>
    [ThreadStatic] private static WorldCalendarReading? _calendarScratch;

    private static WorldCalendarReading calendarScratch => _calendarScratch ??= new WorldCalendarReading
    {
        year = 0,
        season = Season.Spring,
        seasonDay = 0,
        yearDay = 0,
        timeOfDay = 0,
        daylight = 0,
    };

    /// <summary>
    /// Sample the world's sky at a world instant, without allocating.
    ///
    /// `worldNowMs` is a **world instant** (`SimInstant` milliseconds — the clock every colony accrues
    /// on), not the realtime lane's wall clock; the renderer's world-clock estimate supplies it. A
    /// `?dayphase=` override replaces the instant's time-of-day for deterministic visual audits while
    /// keeping the instant's SEASON, so `?dayphase=dusk` shows this season's dusk, not a reference one.
    /// </summary>
    public static DayNightFrame sampleDayNightCycleInto(
        DayNightFrame @out,
        int instanceKind,
        double worldNowMs,
        double? phaseOverride = null)
    {
        bool enabled = dayNightCycleEnabled(instanceKind);
        if (!enabled)
        {
            @out.enabled = false;
            @out.phase = 0.5;
            @out.solarElevation = 1;
            @out.daylight = 1;
            @out.night = 0;
            @out.twilight = 0;
            @out.moonElevation = 0;
            @out.moonlight = 0;
            @out.blueHour = 0;
            @out.sunAzimuthOffset = 0;
            @out.season = Season.Spring;
            return @out;
        }

        WorldCalendarReading calendarScratch = DayNightCycle.calendarScratch;
        double clock = Number.isFinite(worldNowMs) && worldNowMs >= 0 ? worldNowMs : 0;
        readWorldCalendarInto(calendarScratch, clock);
        if (phaseOverride != null)
        {
            // Re-read the calendar at the override's time-of-day inside the SAME world day, so daylight,
            // season and phase stay one consistent instant rather than a phase glued onto foreign light.
            double wrapped = phaseOverride.Value - Math.floor(phaseOverride.Value);
            double dayStart = Math.floor(clock / WORLD_DAY_MS) * WORLD_DAY_MS;
            readWorldCalendarInto(calendarScratch, dayStart + wrapped * WORLD_DAY_MS);
        }
        int season = calendarScratch.season;
        double phase = calendarScratch.timeOfDay;
        double sunrise = dayNightSunrisePhase(season);
        double sunset = dayNightSunsetPhase(season);

        double solarElevation = 0;
        double sunAzimuthOffset;
        if (phase >= sunrise && phase < sunset)
        {
            // The visible arc: east horizon → authored noon → west horizon, across this season's daylight.
            double progress = (phase - sunrise) / (sunset - sunrise);
            solarElevation = Math.sin(progress * Math.PI);
            sunAzimuthOffset = -Math.PI * 0.5 + progress * Math.PI;
        }
        else
        {
            // Night: the key continues along the horizon back toward the next sunrise direction, so the
            // light's direction is continuous at both boundaries however long this season's night is.
            double nightSpan = 1 - (sunset - sunrise);
            double progress =
                phase >= sunset ? (phase - sunset) / nightSpan : (phase + 1 - sunset) / nightSpan;
            sunAzimuthOffset = Math.PI * 0.5 + progress * Math.PI;
        }

        @out.enabled = true;
        @out.phase = phase;
        @out.solarElevation = solarElevation;
        // The calendar's own smooth daylight (twilight ramps included), floored low enough to read as true night;
        // moon, snow bounce and bounded local lights provide the remaining spatial readability.
        @out.daylight = DAY_NIGHT_MIN_DAYLIGHT + (1 - DAY_NIGHT_MIN_DAYLIGHT) * calendarScratch.daylight;
        @out.night = 1 - calendarScratch.daylight;
        // The warm band lives exactly where the calendar's ramp does: fully day and fully night are both
        // zero, and the product peaks mid-ramp — dawn and dusk, at this season's own hours.
        @out.twilight = 4 * calendarScratch.daylight * (1 - calendarScratch.daylight);
        // The nocturnal key follows its own half-arc. This does not steer the shadow direction (which would force a
        // depth-map rebuild); it shapes colour and energy only. A low floor prevents the evening afterglow and the
        // pre-dawn world from collapsing before the moon reaches its visual apex at midnight.
        @out.moonElevation = Math.max(0, Math.cos(phase * Math.PI * 2));
        @out.moonlight = @out.night * (0.6 + @out.moonElevation * 0.4);
        // Twilight is the warm, sun-facing half of the transition. Blue hour sits lower on the same continuous
        // calendar ramp and supplies cool horizon readability after sunset and before sunrise.
        double lowDaylight = Math.max(0, Math.min(1, calendarScratch.daylight / 0.38));
        @out.blueHour = 4 * lowDaylight * (1 - lowDaylight);
        @out.sunAzimuthOffset = sunAzimuthOffset;
        @out.season = season;
        return @out;
    }

    // ── JS builtins used above (no runtime helper exists for them yet) ─────────────────────────────────────────
}
