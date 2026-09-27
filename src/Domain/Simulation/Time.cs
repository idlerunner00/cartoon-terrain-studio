// Port of packages/shared/src/domain/simulation/time.ts — keep in lockstep with the original.

namespace Fluitown.Domain;

// **Simulated world time** — the one clock every rule in this game is written against.
//
// The world runs permanently and holds every player at once, so almost no colony is ever being watched.
// A colony the Spirit is not attending to must be advanceable by six hours in a single call, with the
// same code that advances an attended one by a single tick (Simulation doctrine 1). That is only
// possible if time enters a rule as a **span**, never as an implicit step: a system integrates over
// `[from, to)` and may never assume how long that span is.
//
// Everything here is therefore about spans rather than instants. The single unit conversion lives in
// `intervalSeconds`: instants are milliseconds (the resolution the wire and the wall clock work
// in), while every authored rate in the domain — need decay per second, yield per second, growth per
// second — is expressed per second. Keeping both units in one place is what stops a system from
// quietly authoring a rate "per tick".
//
// Porting note: `type SimInstant = number` (a point in simulated world time, in milliseconds since the world
// epoch) has no cross-file alias in C#; ported code spells it `double`.

public static class Time
{
    /// <summary>
    /// Milliseconds in one second.
    ///
    /// Instants are milliseconds because that is the resolution the wall clock, the wire and persistence all
    /// speak; rates are per second because that is the unit a designer authors in. This is the only place the
    /// two meet, so no rule can drift into "per tick" or "per millisecond" without saying so out loud.
    /// </summary>
    public const double SECOND_MS = 1000;

    /// <summary>
    /// The larger units, derived rather than written down.
    ///
    /// They live beside the second because a tuning table that spells `60 * 60 * 1000` is a tuning table that
    /// will eventually spell it wrong, and because two contexts that each define their own hour are two
    /// contexts that can disagree about how long an hour is. Every duration in the domain is authored from
    /// these three.
    /// </summary>
    public const double MINUTE_MS = 60 * SECOND_MS;
    public const double HOUR_MS = 60 * MINUTE_MS;
}
