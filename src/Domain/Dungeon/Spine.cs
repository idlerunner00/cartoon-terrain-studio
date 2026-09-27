// Port of packages/shared/src/domain/dungeon/spine.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.RngModule;
using static Fluitown.Domain.Scalar;
using static Fluitown.Domain.SpineModule;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// The run's **Spine** — the single, generated, directed path the whole run is built around, and the thing
// the Flood chases along. It replaces the old free-rotating half-plane flood (which could turn, reverse and
// re-flood already-cleared ground — the "wild back-and-forth"). The spine is:
//
//  - **Directed & monotone.** The path advances along a fixed global forward axis (+X): its heading deviates
//    from that axis by less than 90° (SPINE_MAX_DEVIATION) everywhere, so the X coordinate strictly increases.
//    A curve that is monotone in one coordinate **cannot self-intersect and can never run backward** — the run
//    never overlaps itself and the cohort is always pushed *forward*.
//  - **Smoothly turning — no angle snap, ever.** The heading is a **continuous function of arc-length**. Each
//    deliberate bend (≤ SPINE_MAX_TURN) is not a corner but a SPINE_TURN_BAND-long smoothstep arc over which the
//    heading eases from the old leg's heading to the new one with zero turn-rate at both ends. So the tangent —
//    the cohort's escape direction and the orientation the client draws the whole waterline at — rotates
//    **gradually**; it can never jump from one frame to the next the way a piecewise-constant polyline tangent
//    did (that instant ≤34° rotation was the "angle snap").
//  - **Pure & deterministic.** Every leg (and the fine samples realizing its turn) is a pure function of
//    `(seed, i)`, so the authoritative server and every client regenerate byte-identical geometry from the
//    instance id alone — only the front's arc-length position is streamed. The path is realized as a dense
//    polyline of SpineVertex samples (one long chord per straight run, many short chords through each turn
//    band), each carrying the smooth heading at that point; Spine.tangentAt interpolates those headings, so the
//    returned tangent is itself continuous (not the chord's piecewise-constant direction).
//
// The **Flood** is then a front at arc-length `frontS` that advances **monotonically** along the spine (`frontS`
// only ever grows). A world point's relationship to the flood is its arc-length *projection* onto the spine:
// Spine.marginAt = `projectedS − frontS` (>0 dry/ahead, <0 submerged/behind). Because a point's projected
// arc-length is fixed and `frontS` only rises, **a point once flooded stays flooded** — the flood never retreats
// and never re-floods cleared ground. The local escape direction is the spine's tangent at the front, which
// turns *gradually* as the path bends — so the water visibly comes from "behind along the path",
// up/down/sideways, but always pushing one way: forward, and never with a visible jolt.
//
// This is the ONE definition of the run's geometry, shared by the server (damage, spawn-ahead, sanctuary
// placement, culling, streaming) and the client (the filling-water render + minimap). No axis or +X case is
// baked into any consumer — they all read arc-length / tangent off this spine.

/// <summary>The anonymous `{ x, y }` shape returned by <see cref="Spine.pointAt"/> (caller-owned in <see cref="Spine.pointAtInto"/>).</summary>
public sealed class SpinePoint
{
    public double x;
    public double y;
}

public static partial class SpineModule
{
    /* ── Spine shape tuning (retune the path here only) ────────────────────────────────────────────── */

    /// <summary>
    /// Shortest leg of the spine (world units) — bends are at least this far apart, so turns read as gentle. Long
    /// legs are deliberate: between two bends the cohort gets a long, straight, predictable stretch to read the
    /// next telegraphed turn and re-aim, and a leg must comfortably exceed SPINE_TURN_BAND so every turn still
    /// finishes into a straight run (the bend is a gentle curve, not a never-ending arc).
    /// </summary>
    public const double SPINE_SEG_MIN = 2300;
    /// <summary>Longest leg of the spine (world units).</summary>
    public const double SPINE_SEG_MAX = 3600;
    /// <summary>
    /// Max heading deviation from the global forward axis (+X). Strictly &lt; 90° so every leg advances the global
    /// axis (cos 64° ≈ 0.44 &gt; 0) → the spine is monotone in X → it never runs backward and never self-intersects.
    /// </summary>
    public const double SPINE_MAX_DEVIATION = (64 * Math.PI) / 180;
    /// <summary>
    /// Max heading change across one bend — a smooth, telegraphable turn (no reversals, ever). A real, characterful
    /// course-correction (up to 28°, down from the old 34°), but — combined with the long SPINE_TURN_BAND — spread
    /// so far that it reads as a long, shallow sweep, never a swerve. The gentleness comes from the BAND (the turn
    /// is eased over a huge distance), not from shrinking the turn to a micro-wiggle: the path still wanders the
    /// full SPINE_MAX_DEVIATION envelope up/down with genuine, well-telegraphed turns. The per-second rotation a
    /// player actually sees is this turn spread over the band as the front crosses it at flood speed.
    /// </summary>
    public const double SPINE_MAX_TURN = (28 * Math.PI) / 180;
    /// <summary>Mean-reversion toward the forward axis each leg, so the path wanders up/down but keeps coming back.</summary>
    public const double SPINE_REVERT = 0.3;
    /// <summary>
    /// Arc-length a bend's heading change is eased over (world units) — the single knob that sets **how gradual a
    /// direction change feels**. The heading smoothsteps from the old to the new leg heading across this band, so
    /// the tangent never jumps; it rotates over this many world-units of the front's travel. Because the front
    /// crosses the band at the live flood speed, the *time* a turn takes is `band / floodSpeed` and the on-screen
    /// rotation rate is `≈ 1.5·SPINE_MAX_TURN/band · floodSpeed`. The band is therefore sized for the **fastest
    /// flood the game can ever produce** — the deepest run with the `swiftStorm` mod (~335 u/s) — so that even
    /// there a full SPINE_MAX_TURN turn takes ≈5 s and peaks under ~8°/s (a long, readable curve, never a swerve);
    /// at every shallower run/depth it is gentler still. Kept below SPINE_SEG_MIN so every leg still finishes its
    /// turn into a straight stretch (the curve resolves; it is not an unending arc).
    /// </summary>
    public const double SPINE_TURN_BAND = 1800;
    /// <summary>
    /// Arc-length between samples within a turn band — small enough that each chord's heading step is sub-degree,
    /// so the realized curve is visually smooth and Spine.tangentAt interpolation has fine resolution.
    /// </summary>
    public const double SPINE_SAMPLE_STEP = 36;

    /* ── Flood pacing tuning (the front's behaviour along the spine; retune here only) ──────────────── */

    /* ── Flood front advance (the ONE rule for how the waterline moves; shared by server + verifier) ──
     * The flood is **player-independent**: it advances on its own deterministic clock. Each step it eases its
     * speed toward the run's authored `cruiseSpeed` — which the caller derives from the flood's OWN travelled
     * arc-length, so it escalates gently with run length, never with any player's position — through an
     * acceleration cap, then moves. There is deliberately NO catch-up to the leading edge: a dash, a blink or a
     * new front-most joiner can never speed the water up for the rest of the cohort (the balancing bug this
     * replaced). A cohort that keeps moving out-paces the cruise and earns breathing room; one that dawdles or
     * backtracks is overtaken — the same "keep moving forward" pressure, now fair to every member. */
}

/// <summary>
/// The run's directed, non-backtracking path, generated lazily and culled to a small in-play window. Each leg
/// (and the fine samples realizing its turn) is a pure function of `(seed, i)`, so a culled prefix regenerates
/// identically — the server and client always agree on the geometry from the seed alone, with only the front's
/// arc-length on the wire.
/// </summary>
public sealed class Spine
{
    /// <summary>
    /// One sample vertex of the realized spine polyline: a world point at a known arc-length, carrying the
    /// **smooth heading** of the path there. Straight runs need only their two endpoints; a turn band is sampled
    /// every ~SPINE_SAMPLE_STEP so its rounded shape is captured. The segment between consecutive vertices is a
    /// straight chord, but tangentAt interpolates the stored headings (not the chord direction), so the reported
    /// tangent varies continuously across the whole window — including straight↔turn junctions, where the shared
    /// endpoint heading matches on both sides. Internal; never crosses the wire.
    /// </summary>
    private sealed class SpineVertex
    {
        /// <summary>Cumulative arc-length (sum of chord lengths) at this vertex.</summary>
        public double s;
        /// <summary>World point.</summary>
        public double x;
        public double y;
        /// <summary>Smooth path heading (radians) at this vertex — interpolated between neighbours for a continuous tangent.</summary>
        public double theta;
    }

    /// <summary>
    /// A recorded deliberate bend in the generated path: where its turn begins and by how much it turns. Drives the
    /// flood's "the flood turns!" telegraph; pruned with the held window so the list stays bounded.
    /// </summary>
    private sealed class SpineBend
    {
        /// <summary>Arc-length at which the turn band begins.</summary>
        public double s;
        /// <summary>Magnitude of the heading change across the band (radians).</summary>
        public double turn;
    }

    /// <summary>Realized path samples in arc-length order; vertices fully behind the flood are culled off the front.</summary>
    private readonly List<SpineVertex> verts = new();
    /// <summary>Telegraphable bends (turn start + magnitude), in arc-length order; pruned with the held window.</summary>
    private readonly List<SpineBend> bends = new();
    // The IMMUTABLE opening — the world point and heading at arc-length 0 — captured when leg 0 is generated.
    // Unlike `verts[0]` this is never culled away, so geometry anchored to where the run *starts* (the sealed
    // entrance plane at `-FLOOD_HEAD_START`) stays put for the whole run instead of sliding forward with the
    // held window. Defaults match leg 0's authored opening, so a query before any generation still answers.
    private double originX = 0;
    private double originY = 0;
    private double originTheta = 0;
    // Generation cursors (carried forward; a culled prefix never affects future legs because generation only
    // needs the running end point + the previous leg's heading, both advanced as whole legs are appended).
    private int nextLeg = 0;
    private double endX = 0;
    private double endY = 0;
    private double endS = 0;
    private double prevHeading = 0;

    public readonly double seed;

    public Spine(double seed)
    {
        this.seed = seed;
    }

    /// <summary>
    /// Ensure the spine is generated out to at least arc-length `s` (a non-finite `s` is a no-op — never an
    /// unbounded generation loop).
    /// </summary>
    public void ensureLength(double s)
    {
        if (!Number.isFinite(s)) return;
        while (endS < s) appendLeg();
    }

    /// <summary>
    /// Generate the next leg deterministically from `(seed, index)` and append its realized samples. Leg 0 is a
    /// straight opening run along the forward axis; every later leg eases its heading from the previous leg's
    /// heading to a new gently-bent target across a SPINE_TURN_BAND, then runs straight for the rest of its
    /// length — so the heading is continuous (no corner) yet the path still deliberately turns.
    /// </summary>
    private void appendLeg()
    {
        int i = nextLeg++;
        uint h = Js.ToUint32(hashSeed($"{Js.Str(seed)}:spine:{Js.Str(i)}"));
        double uLen = (h & 0xffff) / (double)0x10000; // length roll
        double uTurn = ((h >> 16) & 0xffff) / (double)0x10000; // turn roll
        double length = SPINE_SEG_MIN + uLen * (SPINE_SEG_MAX - SPINE_SEG_MIN);

        if (i == 0)
        {
            // Seed the very first vertex at the origin, then a straight opening run (familiar, never surprising).
            verts.Add(new SpineVertex { s = 0, x = 0, y = 0, theta = 0 });
            originX = 0;
            originY = 0;
            originTheta = 0;
            prevHeading = 0;
            emitStraight(0, length);
            return;
        }

        // A gentle, deterministic bend: a bounded turn, mean-reverted toward the forward axis, then clamped so the
        // heading never strays ≥ 90° from +X (keeps the path monotone-forward — no reversal, no overlap).
        double turn = (uTurn * 2 - 1) * SPINE_MAX_TURN - prevHeading * SPINE_REVERT;
        turn = clamp(turn, -SPINE_MAX_TURN, SPINE_MAX_TURN);
        double target = clamp(prevHeading + turn, -SPINE_MAX_DEVIATION, SPINE_MAX_DEVIATION);

        // Record the bend (where it begins + its magnitude) so the flood can telegraph it before the front arrives.
        double band = Math.min(SPINE_TURN_BAND, length);
        bends.Add(new SpineBend { s = endS, turn = Math.abs(target - prevHeading) });
        // Realize the turn as a smooth arc (heading eases prevHeading→target over `band`), then a straight run.
        emitTurn(prevHeading, target, band);
        emitStraight(target, length - band);
        prevHeading = target;
    }

    /// <summary>
    /// Advance the running end by `len` along `moveHeading` and push a vertex tagged with the smooth heading
    /// `theta` at that point (the move heading and the stored heading differ only sub-degree per fine step).
    /// </summary>
    private void advance(double moveHeading, double len, double theta)
    {
        endX += Math.cos(moveHeading) * len;
        endY += Math.sin(moveHeading) * len;
        endS += len;
        verts.Add(new SpineVertex { s = endS, x = endX, y = endY, theta = theta });
    }

    /// <summary>
    /// Append a straight run of `len` at a constant heading as a single chord (one vertex). A zero/negative run
    /// is a no-op, so a turn band that fills the whole leg simply leaves no trailing straight.
    /// </summary>
    private void emitStraight(double heading, double len)
    {
        if (len > 0) advance(heading, len, heading);
    }

    /// <summary>
    /// Append a smooth turn band: the heading eases `from`→`to` via smoothstep over `band`, sampled in short
    /// sub-steps. Each chord moves along the sub-step's mid-heading; each vertex stores the smooth heading at its
    /// end, so neighbouring stored headings differ by a sub-degree step and the tangent stays continuous.
    /// </summary>
    private void emitTurn(double from, double to, double band)
    {
        if (band <= 0) return;
        double n = Math.max(2, Math.ceil(band / SPINE_SAMPLE_STEP));
        double stepLen = band / n;
        double thetaPrev = from;
        for (double k = 1; k <= n; k++)
        {
            double thetaK = lerp(from, to, smoothstep(k / n));
            advance((thetaPrev + thetaK) * 0.5, stepLen, thetaK);
            thetaPrev = thetaK;
        }
    }

    /// <summary>Index of the held segment (vertex pair) containing arc-length `s`, clamped to the held window's ends.</summary>
    private int indexAt(double s)
    {
        int n = verts.Count;
        if (n < 2) return 0;
        if (s <= verts[0].s) return 0;
        for (int k = 0; k < n - 1; k++)
        {
            if (s <= verts[k + 1].s) return k;
        }
        return n - 2;
    }

    /// <summary>Fraction (0..1) of arc-length `s` between held vertices `k` and `k+1`.</summary>
    private double fracIn(int k, double s)
    {
        SpineVertex a = verts[k];
        SpineVertex b = verts[k + 1];
        return b.s > a.s ? clamp((s - a.s) / (b.s - a.s), 0, 1) : 0;
    }

    /// <summary>
    /// World point at arc-length `s` (generates ahead as needed). Negative `s` — the sealed approach *behind* the
    /// run's start — is answered as the straight backward continuation of the opening leg from the immutable
    /// originX/originY, NOT from the held window: the window's first vertex marches forward as cullBefore trims
    /// the trail, and answering from it turned the fixed entrance plane into a wall that slid (and rotated) along
    /// with the flood. Behind-the-window arc-lengths `0 ≤ s &lt; baseS` still clamp — that ground is genuinely
    /// culled and nothing anchors to it.
    /// </summary>
    public SpinePoint pointAt(double s) => pointAtInto(s, new SpinePoint { x = 0, y = 0 });

    /// <summary>Allocation-free twin of <see cref="pointAt"/> for render/server hot paths with caller-owned scratch storage.</summary>
    public SpinePoint pointAtInto(double s, SpinePoint @out)
    {
        if (s < 0)
        {
            @out.x = originX + Math.cos(originTheta) * s;
            @out.y = originY + Math.sin(originTheta) * s;
            return @out;
        }
        ensureLength(s);
        if (verts.Count == 0)
        {
            @out.x = 0;
            @out.y = 0;
            return @out;
        }
        int k = indexAt(s);
        SpineVertex a = verts[k];
        SpineVertex b = k + 1 < verts.Count ? verts[k + 1] : a;
        double f = fracIn(k, s);
        @out.x = lerp(a.x, b.x, f);
        @out.y = lerp(a.y, b.y, f);
        return @out;
    }
}
