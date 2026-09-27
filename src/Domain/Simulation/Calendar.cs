// Port of packages/shared/src/domain/simulation/calendar.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Time;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// **The world calendar, and the one seam between the world's two paces** (contract §3.1, C-CLOCK).
//
// One world minute is one real second (CONCEPT §12). That single pacing decision splits every duration in
// this game into exactly two kinds, and this file is where the split is made once instead of everywhere:
//
//  - A rule that **accrues** — needs, growth, production, ageing, construction, learning — runs on
//    **world time**: the clock every `SimInstant` speaks, advanced sixty times as fast as the wall.
//  - A rule that moves a body **through** the world — walking, swept collision, the broad-phase,
//    animation — runs on **body time**: the wall clock's own pace, so a Flui crosses the ground on
//    screen exactly as fast as it always did and is sixty times slower measured against the day.
//
// There is exactly one seam between the two paces, `bodySecondsOf` and its inverse
// `worldSecondsOf`. Two shapes of caller legitimately cross it: the path-dependent strategy
// (`simulation/advance.ts`), the two body walkers and the world-weather presentation apply it to *live
// inside* body time, so every body rule they run already lives on body time without a second conversion
// site existing anywhere; the
// colony loop's farming landing beats (`colony/advance.ts`) apply the inverse once, forward, to schedule
// an authored **body**-time animation duration against the world clock a colony's `dueAt` speaks. Both
// are the same fact stated from opposite ends — never a third, ad hoc conversion. A rule that does not
// declare which side of the seam it is on is a defect (Simulation doctrine 8).
//
// Porting note: a `SimInstant` is a `double` (world milliseconds since the epoch).

/// <summary>Seasons are a property of the arc, not content — a closed enum, like LifePhase. The type is `int`.</summary>
public static class Season
{
    public const int Spring = 0;
    public const int Summer = 1;
    public const int Winter = 3;
}

public sealed class WorldCalendarReading
{
    /// <summary>World years since the epoch.</summary>
    public double year;
    /// <summary><see cref="Season"/>.</summary>
    public int season;
    /// <summary>0..WORLD_DAYS_PER_SEASON-1.</summary>
    public double seasonDay;
    /// <summary>0..WORLD_DAYS_PER_YEAR-1. The day inside the complete Flui year.</summary>
    public double yearDay;
    /// <summary>0..1, 0 = midnight — a pure function of the instant.</summary>
    public double timeOfDay;
    /// <summary>0..1, smooth, pure in (timeOfDay, season).</summary>
    public double daylight;
}

public static class Calendar
{
    // The calendar structure constants stay `double`: they are divided (`TWILIGHT_HOURS / WORLD_HOURS_PER_DAY`)
    // and an `int` pair would turn that into integer division.
    public const double WORLD_HOURS_PER_DAY = 24;
    /// <summary>Twenty-four world days per season — the calendar's authored heartbeat (CONCEPT §12).</summary>
    public const double WORLD_DAYS_PER_SEASON = 24;
    public const double WORLD_SEASONS_PER_YEAR = 4;
    /// <summary>The complete Flui year in world days. UI and rules import this instead of restating 96.</summary>
    public const double WORLD_DAYS_PER_YEAR = WORLD_DAYS_PER_SEASON * WORLD_SEASONS_PER_YEAR;

    /// <summary>World milliseconds — derived, never restated. A world day costs 24 wall minutes; a year, 38.4 hours.</summary>
    public const double WORLD_DAY_MS = WORLD_HOURS_PER_DAY * HOUR_MS;
    public const double WORLD_SEASON_MS = WORLD_DAYS_PER_SEASON * WORLD_DAY_MS;
    public const double WORLD_YEAR_MS = WORLD_SEASONS_PER_YEAR * WORLD_SEASON_MS;

    /// <summary>
    /// World hours of daylight per season, indexed by <see cref="Season"/>.
    ///
    /// Derivation: the seasons must be readable in the light alone, so neighbouring seasons differ by a full
    /// two world hours of day — at twenty-four real minutes per world day, sunset visibly moves at every turn.
    /// The swing is deliberately **half** of Earth's mid-latitude solstice range (~16 h/8 h at 50°N): light is
    /// seasonal *texture* in this wave, while the season's teeth arrive through weather effects (contract
    /// §3.4, WP12) — a winter that already halved the workable day AND slowed the land would punish twice for
    /// one season. Ten winter hours keep every authored shift inside daylight.
    /// </summary>
    // Exported (WP17) so the client's sky can place sunrise, sunset and the solar arc from the SAME
    // authored spans this file derives `daylight` from — one truth, two readers, no restatement.
    public static readonly IReadOnlyList<double> WORLD_DAYLIGHT_HOURS = new double[] { 12, 14, 12, 10 };
    private static readonly IReadOnlyList<double> DAYLIGHT_HOURS = WORLD_DAYLIGHT_HOURS;

    /// <summary>
    /// World hours of dawn and of dusk — the width of each smooth ramp between night and full day.
    ///
    /// One world hour is one real minute: long enough that a player *watches* the light turn (the same
    /// "a session can watch a whole shift" argument CONCEPT §12 paces everything by), short enough that the
    /// day never reads as permanently half-lit.
    /// </summary>
    private const double TWILIGHT_HOURS = 1;

    /// <summary>Hermite smoothstep on [0,1] — the standard C¹ ramp, so daylight has no corner at dawn's edges.</summary>
    private static double smooth01(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        return x * x * (3 - 2 * x);
    }

    /// <summary>
    /// Daylight as a pure function of (timeOfDay, season) — no state, no system, nothing to persist.
    ///
    /// The day is symmetric around noon (timeOfDay 0.5): sunrise and sunset sit half the season's daylight
    /// span either side of it, and each is crossed by a <see cref="TWILIGHT_HOURS"/>-wide smooth ramp centred on
    /// the crossing. Season is a discrete input, so day length steps exactly at the season turn — the turn is
    /// a world event (CONCEPT §12), not something to be smoothed away.
    /// </summary>
    private static double daylightOf(double timeOfDay, int season)
    {
        double half = DAYLIGHT_HOURS[season] / WORLD_HOURS_PER_DAY / 2;
        double twilight = TWILIGHT_HOURS / WORLD_HOURS_PER_DAY;
        double sunrise = 0.5 - half;
        double sunset = 0.5 + half;
        double dawn = smooth01((timeOfDay - (sunrise - twilight / 2)) / twilight);
        double dusk = smooth01((timeOfDay - (sunset - twilight / 2)) / twilight);
        return dawn * (1 - dusk);
    }

    /// <summary>
    /// A calendar instant must be a real point after the world epoch. Failing loud here keeps every reading
    /// honest: a NaN instant would otherwise poison year, season and daylight silently.
    /// </summary>
    private static void assertCalendarInstant(double at)
    {
        if (!Number.isFinite(at) || at < 0)
        {
            // TS `RangeError`.
            throw new ArgumentOutOfRangeException(
                null,
                $"world calendar read at {Js.Str(at)}; instants are finite and never precede the epoch");
        }
    }

    /// <summary>
    /// Read the whole calendar at one instant, into a caller-owned reading.
    ///
    /// Pure: the same instant produces the same reading on the server and in the browser, which is what lets
    /// the client light its scene without a single calendar byte crossing the wire. `Into` because readers
    /// sit on hot paths (rendering, incident planning) and must not allocate per read.
    /// </summary>
    public static void readWorldCalendarInto(WorldCalendarReading @out, double at)
    {
        assertCalendarInstant(at);
        double dayIndex = Math.floor(at / WORLD_DAY_MS);
        @out.year = Math.floor(at / WORLD_YEAR_MS);
        @out.season = (int)(Math.floor(at / WORLD_SEASON_MS) % WORLD_SEASONS_PER_YEAR);
        @out.seasonDay = dayIndex % WORLD_DAYS_PER_SEASON;
        @out.yearDay = dayIndex % WORLD_DAYS_PER_YEAR;
        @out.timeOfDay = (at - dayIndex * WORLD_DAY_MS) / WORLD_DAY_MS;
        @out.daylight = daylightOf(@out.timeOfDay, @out.season);
    }
}
