// Port of packages/shared/src/domain/dungeon/endlessRelief.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.Scalar;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/// <summary>
/// The cheap live counterpart of the hydraulic/thermal state used by the full erosion solver.
///
/// Jako's virtual-pipe model evolves water depth, four outflow fluxes, velocity, suspended sediment and a
/// talus-angle soil flow. Solving that region iteratively in every streaming worker is much too expensive, but
/// the quantities that art direction needs can be derived from one smooth hydraulic-head field: downhill
/// pressure becomes runoff, net inflow becomes convergence, shallow fast flow becomes erosion, and slow deep
/// flow becomes deposition. `thermalRelaxation` is the signed eight-neighbour soil transfer around the talus
/// threshold. All outputs are continuous world-space fields, so they remain identical across chunk seams.
/// </summary>
public sealed class EndlessErosionSample
{
    /// <summary>Water moving through the cell, independent of whether the final raster exposes a visible water tile.</summary>
    public double runoff;
    /// <summary>Net virtual-pipe inflow: valleys approach 1, shedding ridges approach 0.</summary>
    public double convergence;
    /// <summary>Narrow, connected part of the drainage field that is allowed to incise a visible valley.</summary>
    public double channel;
    /// <summary>Shallow fast-flow sediment capacity, after local material hardness and depth limiting.</summary>
    public double erosion;
    /// <summary>Slow-flow alluvium: the fertile apron below a channel or steep slope.</summary>
    public double deposition;
    /// <summary>Signed local soil redistribution, negative on sharp shoulders and positive below them.</summary>
    public double thermalRelaxation;
    /// <summary>Broad ecological water availability used by vegetation composition.</summary>
    public double moisture;
    /// <summary>Exposed, fast-draining ground; useful as the inverse habitat mask.</summary>
    public double exposure;
}

/// <summary>
/// **Relief banding** — the pass that turns an endless Country's height field into a *landform*.
///
/// ## What was wrong
///
/// The height a Country quantises into walkable levels was a plain sum of three noise octaves put straight
/// through `floor(h * levels)`. Every octave spent part of the SAME 13-level range, so the local gradient was
/// the sum of all three and the field crossed a level every 15.6 tiles — with the two high-frequency octaves
/// contributing most of that rate, which is what made the contours ragged at a scale no shelf could survive.
/// Measured on the shipping raster of 36 `highland_pass` chunks, walkable ground came out as 5.5 same-level
/// regions per chunk with a **median area of 26 tiles** and **13.7 % of walkable cells on a level-change edge**.
/// That is a heightmap with steps in it, not a landform a player can read at a glance.
///
/// With the split below the same 36 chunks measure a median shelf of **47 tiles** and **7.6 %** edge cells, and
/// the underlying field travels **34.8** tiles per level change instead of 15.6.
///
/// ## The rule
///
/// A shelf's WIDTH is decided by one low-frequency body, and nothing else is allowed to vote on it. The
/// high-frequency octaves keep their job — breaking a contour out of its lattice so a shelf edge wanders like
/// geology instead of ruling a line — but they may only act **at the rim**, where the body is already within
/// half a level of changing. Inside a shelf their weight fades to zero, so no amount of detail can carve a new
/// ledge through the middle of a plateau.
///
/// That single constraint is what separates a landform from noise, and it is the reason this is a pointwise
/// function of world coordinates: a neighbourhood filter would have to agree across an immutable chunk seam,
/// whereas a pure `(x, y)` field is continuous across every seam **by construction**, on every machine.
///
/// The three tuning constants below are the whole contract, and they are stated in the units they are reasoned
/// about: tiles of lattice, and *levels*.
///
/// ## Scale
///
/// The body and shelf lattices are stated **relative to the band count**. When the world's vertical domain
/// expanded to 51 signed bands (`ELEVATION_LEVELS`, ground −25..+25), both lattices grew with it: banding the
/// same body into twice as many levels would otherwise halve the tiles travelled per level change and put
/// neighbouring tiles two levels apart — the exact fizz this module exists to remove, and a direct breach of
/// the `maxStep = 1` walkability contract. A Country now travels twice as far vertically over twice the ground.
/// The RIM lattice deliberately did NOT scale: it is
/// contour break-up, capped below one level by construction, and a coarser rim would read as a fourth shelf.
/// </summary>
public static class EndlessRelief
{
    /// <summary>
    /// Lattice of the **body** — the octave that alone decides which shelf a cell belongs to.
    ///
    /// At 150 tiles it is nearly five chunks wide, so a Country reads as one continuous climb rather than as a
    /// per-chunk height lottery. Reducing it is the one change that can bring the sliver fizz back.
    /// </summary>
    public const double ENDLESS_RELIEF_BODY_CELL = 486;
    /// <summary>Lattice of the **shelf** octave: the terrace rhythm inside a body, still low enough to carry plateaus.</summary>
    public const double ENDLESS_RELIEF_SHELF_CELL = 228;
    /// <summary>Lattice of the **rim** octave: contour break-up only, never a shelf of its own.</summary>
    public const double ENDLESS_RELIEF_RIM_CELL = 17;
    /// <summary>Body / shelf split. They sum to 1: together they are the entire vertical range of the world.</summary>
    public const double ENDLESS_RELIEF_BODY_WEIGHT = 0.56;
    public const double ENDLESS_RELIEF_SHELF_WEIGHT = 0.44;
    /// <summary>
    /// How far the rim octave may push a contour, **in levels**. Below one full level by construction: a rim that
    /// could reach a whole level would be a fourth shelf-deciding octave wearing a different name.
    /// </summary>
    public const double ENDLESS_RELIEF_RIM_LEVELS = 0.95;
    /// <summary>
    /// How much of a shelf counts as its rim, as a fraction of the half-shelf. At 0.4 the middle 60 % of every
    /// shelf is inviolable body and only its outer margin wanders.
    /// </summary>
    public const double ENDLESS_RELIEF_RIM_BAND = 0.64;
    /// <summary>
    /// Restrained contrast expansion, kept from the field this replaces: without it the mean gets richer while the
    /// genuine trenches and summit shelves disappear, and bridges end up near — but never integrated into — the
    /// highest band.
    /// </summary>
    public const double ENDLESS_RELIEF_CONTRAST = 1.22;
    /// <summary>Weight of the directional ridge inside the rim octave. It tilts break-up along the world's grain.</summary>
    private const double RIDGE_WEIGHT = 0.28;
    /// <summary>Wavelength of that ridge, in tiles.</summary>
    private const double RIDGE_WAVE = 15.5;

    /// <summary>
    /// Salts for the broad, fast live-landform field. Kept separate from the shelf field above. Stored as their
    /// ToInt32 images so `seed ^ salt` is a plain int XOR.
    /// </summary>
    private static class LANDFORM_SALT
    {
        public const int angle = 0x6a09e667;
        public const int warp = unchecked((int)0xbb67ae85);
        public const int massif = 0x3c6ef372;
        public const int basin = unchecked((int)0xa54ff53a);
        public const int plateau = 0x510e527f;
        public const int brokenShelf = 0x1f83d9ab;
        public const int brokenShoulder = 0x5be0cd19;
        public const int hydraulicHead = unchecked((int)0x9b05688c);
        public const int channel = unchecked((int)0xcbbb9d5d);
    }

    private const double HYDRAULIC_HEAD_CELL = 196;

    /// <summary>Resolve the analytic erosion state at one absolute tile coordinate.</summary>
    public static EndlessErosionSample endlessErosionAt(double seed, double x, double y)
    {
        double headSeed = (uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.hydraulicHead);
        double centre = valueNoise(headSeed, x, y, HYDRAULIC_HEAD_CELL);
        double channelBasis = valueNoise((uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.channel), x - 73, y + 109, 124);
        // The two smooth state textures do double duty, just as packed GPU layers do: their low-frequency mixture
        // is rainfall/material consistency while the ridged channel value below is fine drainage structure.
        double rainfall = clamp01(0.24 + centre * 0.28 + channelBasis * 0.48);
        double angle =
            latticeHash((uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.angle ^ LANDFORM_SALT.channel), 0, 0) * Math.PI;
        double along = x * Math.cos(angle) + y * Math.sin(angle);
        double flowPhase = (along + (centre - 0.5) * 76) / 47;
        double flowHeight = Math.sin(flowPhase);
        double slope = smoothstep(clamp01(Math.abs(Math.cos(flowPhase)) * 0.92));
        double lowland = smoothstep(clamp01((0.68 - centre) / 0.5));
        double runoff = smoothstep(clamp01(slope * (0.48 + rainfall * 0.62)));

        // A ridged contour of a second smooth field narrows the broad catchment into branching stream-scale lines.
        // It is gated by runoff/convergence, so the contour cannot draw an unrelated stripe across a dry ridge.
        double channelRidge = 1 - Math.abs(channelBasis * 2 - 1);
        double channelShape = smoothstep(clamp01((channelRidge - 0.68) / 0.32));
        // Steady-state equivalent of net pipe inflow: wet lowlands where a narrow drainage contour crosses the
        // downhill grain collect water; exposed shoulders shed it. This removes four hot-path neighbour samples
        // while retaining the same physical state variables and continuous chunk-seam behaviour.
        double convergence = smoothstep(clamp01(lowland * 0.56 + channelShape * 0.34 + (1 - slope) * 0.1));
        double waterDepth = clamp01(lowland * 0.48 + convergence * 0.34 + rainfall * 0.18);
        double channel =
            channelShape * smoothstep(clamp01(convergence * 0.58 + runoff * 0.55 + lowland * 0.24));

        // The paper's capacity is slope x velocity x a shallow-water depth limiter. Material hardness varies on a
        // broad filtered field so adjacent cells never alternate between rock and loose soil.
        double hardness = clamp01(0.22 + centre * 0.5 + (1 - channelBasis) * 0.28);
        double depthLimit = 1 - waterDepth * 0.68;
        double erosion = clamp01(
            channel * slope * (0.42 + runoff * 0.9) * depthLimit * (1.15 - hardness * 0.38) * 2.35);
        double deposition = clamp01(
            (waterDepth * 0.58 + convergence * 0.42) *
                (1 - slope * 0.72) *
                (1 - erosion * 0.8) *
                (0.38 + channel * 0.62));

        // Signed talus relaxation: the high shoulder of the directional fold loses material and its low foot gains
        // it. This is the stationary form of the paper's eight thermal pipes rather than an iterative cell write.
        double talus = smoothstep(clamp01((slope - 0.28) / 0.58));
        double thermalRelaxation = clamp(-flowHeight * talus, -1, 1);
        double moisture = clamp01(waterDepth * 0.58 + convergence * 0.22 + deposition * 0.2);
        double exposure = clamp01((1 - moisture) * 0.54 + slope * 0.3 + (1 - hardness) * 0.16);
        return new EndlessErosionSample
        {
            runoff = runoff,
            convergence = convergence,
            channel = channel,
            erosion = erosion,
            deposition = deposition,
            thermalRelaxation = thermalRelaxation,
            moisture = moisture,
            exposure = exposure,
        };
    }

    /// <summary>
    /// Large-scale relief added to the legacy live composer, in elevation levels.
    ///
    /// The erosion pipeline owns the most geological version of these forms, but its cold region solve currently
    /// costs tens of seconds. This analytic field carries the same readable hierarchy in the streaming hot path:
    /// one warped fold belt, broad uplifted plateaus and equally broad drainage basins. Every wavelength is hundreds
    /// of tiles and the combined derivative remains below one level per tile, so banding is seam-free and cannot
    /// create a two-level walkable step. Narrow cliffs and ravines remain the structural passes' responsibility.
    /// </summary>
    public static double endlessLandformOffsetAt(double seed, double x, double y)
    {
        double angle = latticeHash((uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.angle), 0, 0) * Math.PI;
        double along = x * Math.cos(angle) + y * Math.sin(angle);
        double across = -x * Math.sin(angle) + y * Math.cos(angle);
        double warp = (valueNoise((uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.warp), x + 97, y - 61, 310) - 0.5) * 72;
        // A wide fold belt: ~265 tiles crest-to-crest, pinched by a separate continental massif mask. The signed
        // wave supplies sustained climbs; the powered ridge above it turns selected crests into mountain chains.
        double foldPhase = (along + warp) / 42;
        double fold = Math.sin(foldPhase);
        double ridge = 1 - Math.abs(fold);
        double massif = valueNoise((uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.massif), x - 173, y + 89, 520);
        double massifGate = smoothstep(clamp01((massif - 0.27) / 0.5));
        double mountain = Math.pow(ridge, 2.05) * massifGate * 6.5;
        double foldedCountry = fold * (4.6 + massifGate * 3.4);
        double crossWarp =
            (valueNoise((uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.warp ^ LANDFORM_SALT.massif), x - 149, y + 203, 370) -
                0.5) *
            82;
        double rollingCountry = Math.sin((across + crossWarp) / 60) * 4.25;

        // Camera-scale broken country. The continental body establishes the long climb, while these two rotated,
        // smooth fields make each 32-tile traversal cross knuckles, hanging shelves and shallow hollows instead of
        // waiting several chunks for the next meaningful height event. Their combined derivative stays comfortably
        // below one level per tile; only the quantised terrace edge changes, never the walkability step contract.
        double brokenShelf =
            (valueNoise((uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.brokenShelf), x + 37, y - 83, 94) - 0.5) * 9;
        double brokenShoulder =
            (valueNoise(
                (uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.brokenShoulder),
                along * 0.72 - across * 0.31 + 191,
                across * 0.68 + along * 0.27 - 127,
                142) -
                0.5) *
            7;
        double fracturedRidges =
            Math.sin((along + warp * 0.2) / 19) * 0.7 + Math.sin((across - crossWarp * 0.16) / 23) * 0.55;

        // Low basins are independent of the ranges. Their scale is large enough to hold a lake district rather than
        // one pond, and their smooth shoulder prevents a circular crater cut-line.
        double basinField = valueNoise((uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.basin), x + 211, y + 157, 460);
        double basin = smoothstep(clamp01((0.5 - basinField) / 0.36)) * 28;

        // Slow horst/graben country makes whole views sit on a high plateau or in a low plain. The wide transition
        // is intentional: the structural wall grammar supplies the escarpment while walkable ground stays legal.
        double plateauField = valueNoise((uint)(Js.ToInt32(seed) ^ LANDFORM_SALT.plateau), x - 307, y - 233, 720);
        double highland = smoothstep(clamp01((plateauField - 0.5) / 0.34)) * 4.25;
        double lowland = smoothstep(clamp01((0.38 - plateauField) / 0.3)) * 9;
        var erosion = endlessErosionAt(seed, x, y);
        // Hydraulic incision is deliberately depth-limited; the depositional apron and signed thermal transfer
        // keep its banks from becoming the deep, vertical grooves produced by hydraulic erosion alone.
        double erodedLandform =
            -erosion.erosion * 1.8 + erosion.deposition * 0.9 + erosion.thermalRelaxation * 0.55;
        return (
            foldedCountry +
            rollingCountry +
            brokenShelf +
            brokenShoulder +
            fracturedRidges +
            mountain +
            highland -
            basin -
            lowland +
            erodedLandform
        );
    }

    /// <summary>
    /// The shelf-deciding body of the relief at one world tile, as a continuous 0..1 height.
    ///
    /// `profile` is the biome's authored elevation skew and is applied here rather than by the caller, so gain and
    /// bias act on the body ALONE — applying them to the composed field would scale the rim octave into a
    /// shelf-deciding voice again on any biome with gain > 1.
    /// </summary>
    public static double endlessReliefBodyAt(
        double bodySeed,
        double shelfSeed,
        ElevationProfile profile,
        double x,
        double y)
    {
        double body = valueNoise(bodySeed, x, y, ENDLESS_RELIEF_BODY_CELL);
        double shelf = valueNoise(shelfSeed, x + 41, y - 29, ENDLESS_RELIEF_SHELF_CELL);
        double height = body * ENDLESS_RELIEF_BODY_WEIGHT + shelf * ENDLESS_RELIEF_SHELF_WEIGHT;
        height = 0.5 + (height - 0.5) * profile.gain + profile.bias;
        return 0.5 + (height - 0.5) * ENDLESS_RELIEF_CONTRAST;
    }

    /// <summary>
    /// The rim octave at one world tile, as a signed -0.5..0.5 offset. Pure break-up: it never reaches the level
    /// quantiser except through <see cref="endlessReliefLevelAt"/>, which fades it out inside a shelf.
    /// </summary>
    public static double endlessReliefRimAt(double rimSeed, double ridgeAngle, double x, double y)
    {
        double detail = valueNoise(rimSeed, x - 19, y + 37, ENDLESS_RELIEF_RIM_CELL);
        double ridge =
            0.5 + Math.sin((x * Math.cos(ridgeAngle) + y * Math.sin(ridgeAngle)) / RIDGE_WAVE) * 0.5;
        return detail * (1 - RIDGE_WEIGHT) + ridge * RIDGE_WEIGHT - 0.5;
    }

    /// <summary>
    /// Band a continuous body height into a walkable level, letting the rim octave wander the contour only where
    /// the body is already close to changing.
    ///
    /// `interior` is 1 at a shelf's centre and 0 at its edge; the rim's authority is the complement of that,
    /// smoothed so there is no discontinuity where its influence switches on.
    /// </summary>
    public static double endlessReliefLevelAt(double body, double rim, double levels = ELEVATION_LEVELS)
    {
        double t = clamp01(body) * levels;
        double frac = t - Math.floor(t);
        double interior = 1 - Math.abs(frac - 0.5) * 2;
        double rimAuthority = 1 - smoothstep(interior / ENDLESS_RELIEF_RIM_BAND);
        double banded = t + rim * ENDLESS_RELIEF_RIM_LEVELS * rimAuthority;
        return Math.floor(clamp(banded, 0, levels - 1e-6));
    }
}
