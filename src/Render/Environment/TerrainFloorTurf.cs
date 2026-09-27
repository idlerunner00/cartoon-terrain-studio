// Port of packages/client/src/render/environment/terrainFloorTurf.ts — keep in lockstep with the original.
using System;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// Floor turf — grass as a PIGMENT LAYER OF THE GROUND, resolved exactly the way a trail is.
///
/// ## Why this module exists
///
/// A path in this world is not a surface. `livingGroundPigmentAt` folds it into the floor cap's own pigment,
/// so a trail inherits the cap's colour field, its baked shade, its pattern strength and its relief for free
/// and — by construction — cannot have an edge. Grass used to be the opposite: a separate turf mesh laid on
/// the cap, with its own tone, its own shade term and its own outline. However carefully that polygon was
/// matched to the ground beneath it, it stayed a second material sitting on the world.
///
/// Worse, the patch and the floor disagreed about where vegetation was. The floor's visible pattern — the
/// mottle, the lush/dry/mineral splat, the aggregate grain — is a world-anchored gradient-noise field
/// evaluated per fragment from the biome's material scales. The grass was placed from `plan.floor.grass`,
/// a completely unrelated CPU field. Two "grass" patterns, decorrelated: tufts landed wherever the habitat
/// field allowed, on ground the shader had painted for entirely different reasons.
///
/// ## What this establishes
///
/// ONE turf cover, on ONE foundation:
///
///  - **body** — `plan.floor.grass` gated by route wear (<see cref="terrainTurfBodyAt"/>). This is the world's
///    semantics: where a meadow belongs, and where traffic has worn it away.
///  - **contour** — the FLOOR'S OWN MOTTLE (<see cref="terrainFloorMottleAt"/>), the same rotated, domain-warped
///    gradient-noise octaves at the same biome scales the fragment shader uses to paint the ground. The body
///    only moves a threshold on that field, so a patch's shape, its edge and its internal breakup are the
///    floor's pattern rather than a second field competing with it.
///
/// The result is published per cap vertex (`aTurf`) and consumed by three places that can no longer drift
/// apart: the cap pigment, the fragment shader's ground treatment, and the blade emitter.
///
/// ## Why the mottle is mirrored on the CPU rather than evaluated in the shader
///
/// The blades must stand exactly in the visible patch. If the CPU placed them from the body alone while the
/// shader drew the contour from its own octaves, thin cover would put most tufts between the painted islands
/// — the original defect in a new costume. Mirroring the field in the shader instead would demand CPU/GPU
/// agreement on a chaotic hash at absolute world coordinates, which float32/float64 cannot honestly promise
/// far from the origin. So the CPU evaluates it once, at bake time, and the GPU simply receives the answer.
///
/// That fixes the cap's 20 px vertex lattice as the resolution of the patch OUTLINE, which is why only the
/// macro and middle octaves are used here: the tooth octave is finer than the lattice and would alias. The
/// shader adds that octave back as a per-fragment GRAIN inside the cover, where no agreement is needed.
/// </summary>
public static partial class TerrainFloorTurf
{
    /// <summary>Below this the habitat field is bare ground: no turf, no blades.</summary>
    public const double TURF_PRESENCE_FLOOR = 0.08;
    /// <summary>
    /// At and above this the body is saturated and the cover closes completely.
    ///
    /// Kept low on purpose. Measured on real Endless chunks the habitat field's median sits near 0.05 and its
    /// 90th percentile near 0.6, so a full-cover threshold up at 0.55 left the entire mid-range emitting a
    /// whisper — which is a speck, not cover.
    /// </summary>
    public const double TURF_PRESENCE_FULL = 0.45;
    /// <summary>
    /// Route wear above this kills vegetation outright — this is what cuts real paths through a meadow.
    ///
    /// Deliberately well above the trail's outer edge. `plan.floor.route` is the BROAD trail including its
    /// shoulders, and on open terrain roughly a third of walkable floor scores something; gating at the shoulder
    /// chopped every meadow into fragments. Only the compacted middle is bare, and the shoulder thins out
    /// continuously through <see cref="terrainTurfBodyAt"/>.
    /// </summary>
    public const double TURF_ROUTE_GATE = 0.6;

    /// <summary>
    /// How far the mat's pigment is pulled from its own ground toward the growth pole at full cover.
    ///
    /// Shared verbatim with the fragment shader (`uMmoratFloorTurf`) so a blade rooted on the CPU and the ground
    /// it is standing in are computed from the same number. This is the ONE place the strength of the green is
    /// decided; nothing downstream is allowed to add a second pull on top.
    /// </summary>
    public const double TURF_PIGMENT_PULL = 0.5;

    /// <summary>
    /// Cover threshold curve over the floor's own mottle.
    ///
    /// `cover = smoothstep(bias - body · gain, bias - body · gain + band, mottleField)`.
    ///
    /// A body of zero puts the threshold above the field's ceiling (never any cover), a body of one puts it below
    /// its floor (closed cover), and everything between grows the cover in along the floor's pattern. The band is
    /// the softness of the patch edge in field units — wide enough that the cap's vertex lattice never shows as a
    /// facet, narrow enough that a meadow still has a discernible shore.
    /// </summary>
    public const double TURF_COVER_BIAS = 1.12;
    public const double TURF_COVER_GAIN = 1.55;
    public const double TURF_COVER_BAND = 0.34;
    /// <summary>Spread applied to the floor's signed mottle before it is used as a 0..1 threshold field.</summary>
    public const double TURF_MOTTLE_SPREAD = 3.2;

    /* ── The floor's own material field, mirrored ──────────────────────────────────────────────────────────── */

    /// <summary>
    /// Mirror of the shader's `mmoratMaterialHash22`. The constants and the operation order are the contract:
    /// change one and the ground's pattern and its turf stop being the same field.
    /// </summary>
    private static (double, double) materialHash22(double x, double y)
    {
        double p0 = x * 0.1031;
        double p1 = y * 0.103;
        double p2 = x * 0.0973;
        p0 -= Math.floor(p0);
        p1 -= Math.floor(p1);
        p2 -= Math.floor(p2);
        double d = p0 * (p1 + 33.33) + p1 * (p2 + 33.33) + p2 * (p0 + 33.33);
        p0 += d;
        p1 += d;
        p2 += d;
        double a = (p0 + p1) * p2;
        double b = (p0 + p2) * p1;
        return (a - Math.floor(a), b - Math.floor(b));
    }

    /// <summary>Mirror of the shader's `mmoratMaterialGradient`: a unit vector per lattice point.</summary>
    private static void materialGradient(double x, double y, double[] @out)
    {
        (double, double) hash = materialHash22(x, y);
        double gx = hash.Item1 * 2 - 1;
        double gy = hash.Item2 * 2 - 1;
        double inverse = 1 / Math.sqrt(Math.max(gx * gx + gy * gy, 0.0001));
        @out[0] = gx * inverse;
        @out[1] = gy * inverse;
    }

    [ThreadStatic] private static double[]? _GRADIENT_SCRATCH;
    private static double[] GRADIENT_SCRATCH => _GRADIENT_SCRATCH ??= new double[] { 0, 0 };

    /// <summary>
    /// Mirror of the shader's `mmoratMaterialGradientNoise` — quintic-faded gradient noise in 0..1.
    ///
    /// Gradient rather than value noise for the same reason the shader uses it: interpolated corner values expose
    /// square plateaus in low-contrast ground, and a turf outline built on plateaus reads as tiling.
    /// </summary>
    public static double terrainMaterialGradientNoise(double x, double y)
    {
        double[] scratch = GRADIENT_SCRATCH;
        double latticeX = Math.floor(x);
        double latticeY = Math.floor(y);
        double localX = x - latticeX;
        double localY = y - latticeY;
        double fadeX = localX * localX * localX * (localX * (localX * 6 - 15) + 10);
        double fadeY = localY * localY * localY * (localY * (localY * 6 - 15) + 10);
        materialGradient(latticeX, latticeY, scratch);
        double nw = scratch[0] * localX + scratch[1] * localY;
        materialGradient(latticeX + 1, latticeY, scratch);
        double ne = scratch[0] * (localX - 1) + scratch[1] * localY;
        materialGradient(latticeX, latticeY + 1, scratch);
        double sw = scratch[0] * localX + scratch[1] * (localY - 1);
        materialGradient(latticeX + 1, latticeY + 1, scratch);
        double se = scratch[0] * (localX - 1) + scratch[1] * (localY - 1);
        double north = nw + (ne - nw) * fadeX;
        double south = sw + (se - sw) * fadeX;
        return clamp01(0.5 + (north + (south - north) * fadeY) * 0.82);
    }

    /// <summary>
    /// The floor's own mottle at a world point, signed, roughly -0.5 .. 0.5.
    ///
    /// This is the shader's floor `detail` term (`macro · 0.5 + mid · 0.36 + fine · 0.14`) with the tooth octave
    /// dropped and the remaining weights renormalised: the tooth scale is finer than the cap's vertex lattice, so
    /// carrying it here would alias, and the shader re-adds it per fragment as grain instead. Macro and middle
    /// carry the patch's shape, which is all the outline needs.
    /// </summary>
    public static double terrainFloorMottleAt(
        double worldX,
        double worldZ,
        TerrainSurfaceProfile profile)
    {
        // Same CPU-resolved rotation and frequencies the shader receives as uniforms.
        double angle = profile.materialA[3];
        double cos = Math.cos(angle);
        double sin = Math.sin(angle);
        double rotatedX = cos * worldX + sin * worldZ;
        double rotatedY = -sin * worldX + cos * worldZ;
        double macroFrequency = 1 / Math.max(16, profile.materialA[0]);
        double midFrequency = 1 / Math.max(8, profile.materialA[1]);
        double macroUvX = (0.819 * rotatedX + 0.574 * rotatedY) * macroFrequency + 3.1;
        double macroUvY = (-0.574 * rotatedX + 0.819 * rotatedY) * macroFrequency - 7.7;
        double macroSample = terrainMaterialGradientNoise(macroUvX, macroUvY);
        // The middle octave is displaced by the broad one, exactly as in the shader: neither the world axes nor
        // the gradient lattice may align into a visible checkerboard.
        double warpX = macroSample - 0.5;
        double warpY = 0.5 - macroSample;
        double midUvX = (0.438 * rotatedX - 0.899 * rotatedY) * midFrequency + 13.7 + warpX * 0.58;
        double midUvY = (0.899 * rotatedX + 0.438 * rotatedY) * midFrequency - 19.3 + warpY * 0.58;
        double midSample = terrainMaterialGradientNoise(midUvX, midUvY);
        return (macroSample - 0.5) * (0.5 / 0.86) + (midSample - 0.5) * (0.36 / 0.86);
    }

    /* ── Body, cover and pigment ───────────────────────────────────────────────────────────────────────────── */

    /// <summary>
    /// How much vegetation the WORLD wants at a point, once the path has had its say.
    ///
    /// Wear does not merely gate: it thins the body on a trail's shoulder, which is what makes a track read as
    /// trodden rather than as stamped out with a cookie cutter.
    /// </summary>
    public static double terrainTurfBodyAt(double habitat, double route)
    {
        if (habitat <= TURF_PRESENCE_FLOOR) return 0;
        if (route >= TURF_ROUTE_GATE) return 0;
        double worn = 1 - route / TURF_ROUTE_GATE;
        return clamp01(
            ((habitat - TURF_PRESENCE_FLOOR) / (TURF_PRESENCE_FULL - TURF_PRESENCE_FLOOR)) * worn);
    }

    /// <summary>Smoothstep with the shader's exact semantics, so cover means one thing on both sides of the wire.</summary>
    private static double smoothstep(double edge0, double edge1, double value)
    {
        double t = clamp01((value - edge0) / Math.max(1e-6, edge1 - edge0));
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The ONE cover number: how much of this point is turf.
    ///
    /// The body chooses a threshold; the floor's mottle decides which side of it this point falls. That is the
    /// whole trick — the patch cannot look like a foreign shape on the ground because its shape IS the ground's.
    /// </summary>
    public static double terrainTurfCover(double body, double mottle)
    {
        if (body <= 0) return 0;
        double field = clamp01(0.5 + mottle * TURF_MOTTLE_SPREAD);
        double low = TURF_COVER_BIAS - body * TURF_COVER_GAIN;
        return smoothstep(low, low + TURF_COVER_BAND, field);
    }

    /// <summary>Cover at a world point, from the world's habitat semantics and the floor's own pattern.</summary>
    public static double terrainTurfCoverAt(
        double habitat,
        double route,
        double worldX,
        double worldZ,
        TerrainSurfaceProfile profile)
    {
        double body = terrainTurfBodyAt(habitat, route);
        if (body <= 0) return 0;
        return terrainTurfCover(body, terrainFloorMottleAt(worldX, worldZ, profile));
    }

    /// <summary>
    /// The turf pole a floor grows toward — one definition for the cap pigment, the shader uniform and the blades.
    ///
    /// Two files used to derive their own green from the same two palette entries, which is how the mat and the
    /// things standing in it drifted apart in the first place.
    ///
    /// ## It has to be a GROWTH colour
    ///
    /// The retired pole was `mix(decal.mid, biome.groundAccentA, 0.36)` — and `groundAccentA` is the **dry/soil**
    /// accent: on highland_pass it is a warm khaki (hue 44°). Mixing a pale green with sand produced a pole barely
    /// distinguishable from the floor it was supposed to green: at FULL cover the mat came out three percent darker
    /// and no more saturated than bare ground. That is why a patch could be correct in every structural respect and
    /// still be invisible.
    ///
    /// `groundAccentB` is the biome's growth accent (hue 141° on highland_pass). Mixed with the theme's own decal
    /// mid it stays inside the HANDINK register — a wash of green pigment on pale paper, never a lawn-green decal —
    /// while moving saturation far enough (0.23 → 0.37) that the eye reads a meadow.
    /// </summary>
    public static int terrainFloorTurfPole(int decalMid, int growthAccent)
    {
        return mix(decalMid, growthAccent, 0.62);
    }

    /// <summary>
    /// The mat's pigment over its own ground — the CPU twin of the shader's turf mix.
    ///
    /// A blade roots in THIS, never in the bare cap colour: a blade whose base is a different colour from the mat
    /// it grows out of reads as a prop standing on a patch, which is exactly what we are removing.
    /// </summary>
    public static int terrainTurfMatPigment(int groundPigment, int turfPole, double cover)
    {
        return cover <= 0 ? groundPigment : mix(groundPigment, turfPole, cover * TURF_PIGMENT_PULL);
    }
}
