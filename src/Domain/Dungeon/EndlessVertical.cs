// Port of packages/shared/src/domain/dungeon/endlessVertical.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.Scalar;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

public class EndlessVerticalTraits
{
    public double altitudeBias;
    public double reliefContrast;
    public double reliefRuggedness;
    public double escarpmentStrength;
    public double openGroundCap;
    public double bridgeLandmarkDensity;
}

public sealed class EndlessVerticalSample : EndlessVerticalTraits
{
    /// <summary>Composed continuous height before the run ladder is applied.</summary>
    public double normalizedHeight;
    /// <summary>Authoritative stored level for walkable ground.</summary>
    public double level;
}

/// <summary>A cached global vertical field. Global tile coordinates are integers (typed-array indexed).</summary>
public sealed class EndlessVerticalField
{
    public double seed;
    public int baseTx;
    public int baseTy;
    public int width;
    public int height;
    public int halo;
    public Func<int, int, EndlessVerticalSample> sampleAt;
    public Func<int, int, double> levelAt;
    public Func<int, int, double> normalizedHeightAt;
    public Func<int, int, double> escarpmentStrengthAt;
    public Func<int, int, double> reliefRuggednessAt;
    public Func<int, int, double> openGroundCapAt;
    public Func<int, int, double> bridgeLandmarkDensityAt;
}

public sealed class EndlessVerticalFieldOptions
{
    public double seed;
    public int baseTx;
    public int baseTy;
    public int width;
    public int height;
    /// <summary>Endless chunk side in tiles. Required so this module does not duplicate the streaming constant.</summary>
    public int landscapeCellSize;
    public ElevationProfile profile;
    public MacroDna dna;
    public int? maxLevel;
    /// <summary>Neighbour radius needed by the escarpment and exposed-wall passes.</summary>
    public double? halo;
}

public sealed class ApplyEndlessEscarpmentOptions
{
    public double seed;
    public int baseTx;
    public int baseTy;
    /// <summary>Only the required seam-port route tree. Optional maze loops remain available for geological composition.</summary>
    public byte[] routeMask;
    public IReadOnlyList<DungeonRoom> rooms;
    public EndlessVerticalField field;
}

/// <summary>
/// Hand-authored vertical composition for the streamed Endless world.
///
/// The ordinary elevation octaves provide safe rolling ground, but on their own every height boundary remains
/// an equally soft one-step contour. Hand-built terrain has a stronger grammar: whole low/high countries,
/// readable same-height shelves, solid escarpment rims, and a small number of deliberate ramps through those
/// rims. This module adds that grammar without introducing presets or per-chunk discontinuities.
///
/// Landscape traits are sampled at chunk centres and smoothly interpolated in global tile space. The resulting
/// field is therefore a pure function of `(seed, global tile)`, agrees across independently generated chunks and
/// remains byte-identical on server and client. A small halo is prepared once per generated chunk so elevation,
/// topology and wall passes reuse the exact same samples instead of recomputing landscape blends per cell.
/// </summary>
public static class EndlessVertical
{
    private const int ESCARPMENT_COUNTRY_SALT = 0x510e527f;
    private const int ESCARPMENT_DETAIL_SALT = unchecked((int)0x9b05688c);
    private const double ESCARPMENT_COUNTRY_CELL = 61;
    private const double ESCARPMENT_DETAIL_CELL = 23;
    // Slow, independent relief below the landscape blend. One field forms whole upland/valley countries while the
    // ridged field gives those countries mountain shoulders. Both are global tile fields, never chunk-local noise.
    //
    // Every lattice below feeds the WALKABLE quantiser, so each is stated relative to the band count and was
    // doubled with it. The guarantee these amplitudes were chosen for — "the longest possible per-tile slope
    // stays far below one level" — is a statement in LEVELS, and banding the same surface into twice as many
    // of them would have turned "far below one" into "just over one": a two-level neighbour step, which is a
    // cliff where a ramp belongs and blocks a walkable crossing outright.
    private const int OROGRAPHIC_COUNTRY_SALT = 0x6ca6351d;
    private const int OROGRAPHIC_RIDGE_SALT = unchecked((int)0xb5c0fbcf);
    private const int OROGRAPHIC_FOLD_A_SALT = 0x1f83d9ab;
    private const int OROGRAPHIC_FOLD_B_SALT = 0x5be0cd19;
    private const int OROGRAPHIC_GRAIN_SALT = unchecked((int)0xa54ff53a);
    private const double OROGRAPHIC_COUNTRY_CELL = 208;
    private const double OROGRAPHIC_RIDGE_CELL = 94;
    private const double OROGRAPHIC_FOLD_A_CELL = 62;
    private const double OROGRAPHIC_FOLD_B_CELL = 38;
    private const double OROGRAPHIC_GRAIN_CELL = 26;
    private const double OROGRAPHIC_COUNTRY_AMPLITUDE = 0.46;
    private const double OROGRAPHIC_RIDGE_AMPLITUDE = 0.2;
    private const double OROGRAPHIC_FOLD_AMPLITUDE = 0.26;
    private const double OROGRAPHIC_GRAIN_AMPLITUDE = 0.14;

    private static readonly (int dx, int dy)[] CARDINAL_DIRECTIONS =
    {
        (1, 0),
        (-1, 0),
        (0, 1),
        (0, -1),
    };

    private static double clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private static double lerp(double a, double b, double t) => a + (b - a) * t;

    private static EndlessVerticalTraits verticalTraitsOf(EndlessLandscapeTraits traits) =>
        new EndlessVerticalTraits
        {
            altitudeBias = traits.altitudeBias,
            reliefContrast = traits.reliefContrast,
            reliefRuggedness = traits.reliefRuggedness,
            escarpmentStrength = traits.escarpmentStrength,
            openGroundCap = traits.openGroundCap,
            bridgeLandmarkDensity = traits.bridgeLandmarkDensity,
        };

    private static EndlessVerticalTraits blendTraits(
        EndlessVerticalTraits nw,
        EndlessVerticalTraits ne,
        EndlessVerticalTraits sw,
        EndlessVerticalTraits se,
        double tx,
        double ty)
    {
        double blend(Func<EndlessVerticalTraits, double> key) =>
            lerp(lerp(key(nw), key(ne), tx), lerp(key(sw), key(se), tx), ty);
        return new EndlessVerticalTraits
        {
            altitudeBias = blend(t => t.altitudeBias),
            reliefContrast = blend(t => t.reliefContrast),
            reliefRuggedness = blend(t => t.reliefRuggedness),
            escarpmentStrength = blend(t => t.escarpmentStrength),
            openGroundCap = blend(t => t.openGroundCap),
            bridgeLandmarkDensity = blend(t => t.bridgeLandmarkDensity),
        };
    }

    /// <summary>
    /// Build one cached global vertical field. The half-cell offset makes a landscape sample own its chunk centre;
    /// smooth interpolation then carries the composition through the neighbouring centres without a border switch.
    /// </summary>
    public static EndlessVerticalField createEndlessVerticalField(EndlessVerticalFieldOptions options)
    {
        double seed = options.seed;
        int baseTx = options.baseTx;
        int baseTy = options.baseTy;
        int width = options.width;
        int height = options.height;
        int landscapeCellSize = options.landscapeCellSize;
        var profile = options.profile;
        var dna = options.dna;
        int maxLevel = options.maxLevel ?? MAX_ELEVATION;
        int halo = (int)Math.max(0, Math.floor(options.halo ?? 3));
        int fieldWidth = width + halo * 2;
        int fieldHeight = height + halo * 2;
        var levels = new byte[fieldWidth * fieldHeight];
        var normalizedHeights = new float[fieldWidth * fieldHeight];
        var altitudeBiases = new float[fieldWidth * fieldHeight];
        var reliefContrasts = new float[fieldWidth * fieldHeight];
        var reliefRuggednesses = new float[fieldWidth * fieldHeight];
        var escarpmentStrengths = new float[fieldWidth * fieldHeight];
        var openGroundCaps = new float[fieldWidth * fieldHeight];
        var bridgeLandmarkDensities = new float[fieldWidth * fieldHeight];
        // The TS keys this never-iterated cache by the string `${cx},${cy}`; an integer pair is the same key.
        var traitCache = new Dictionary<(int, int), EndlessVerticalTraits>();

        EndlessVerticalTraits latticeTraitsAt(int cx, int cy)
        {
            var key = (cx, cy);
            if (traitCache.TryGetValue(key, out var cached)) return cached;
            var traits = verticalTraitsOf(EndlessLandscape.endlessLandscapeSampleAt(seed, cx, cy).traits);
            traitCache[key] = traits;
            return traits;
        }

        EndlessVerticalTraits traitsAt(int gtx, int gty)
        {
            double fx = (gtx + 0.5) / landscapeCellSize - 0.5;
            double fy = (gty + 0.5) / landscapeCellSize - 0.5;
            int ix = (int)Math.floor(fx);
            int iy = (int)Math.floor(fy);
            double tx = smoothstep(clamp01(fx - ix));
            double ty = smoothstep(clamp01(fy - iy));
            return blendTraits(
                latticeTraitsAt(ix, iy),
                latticeTraitsAt(ix + 1, iy),
                latticeTraitsAt(ix, iy + 1),
                latticeTraitsAt(ix + 1, iy + 1),
                tx,
                ty);
        }

        EndlessVerticalSample composeSample(int gtx, int gty)
        {
            var traits = traitsAt(gtx, gty);
            double baseHeight = EndlessMacro.endlessGroundHeightAt(seed, gtx, gty, profile, dna);
            // The original height source is safe and smooth, but a single FBM family lets a whole camera window land on
            // one shoulder and therefore read as flat. A second, much slower country field separates broad lowlands and
            // uplands. Its centred ridge transform adds long mountain backs without point peaks or tile noise. The
            // maximum analytic slope remains well below one 0..12 band per tile; the regression suite proves the exact
            // cardinal/diagonal <=1 contract across independently prepared chunk halos.
            double country =
                (valueNoise(
                    (uint)(Js.ToInt32(seed) ^ OROGRAPHIC_COUNTRY_SALT),
                    gtx - 19.5,
                    gty + 27.25,
                    OROGRAPHIC_COUNTRY_CELL) -
                    0.5) *
                OROGRAPHIC_COUNTRY_AMPLITUDE;
            double ridgeNoise = valueNoise(
                (uint)(Js.ToInt32(seed) ^ OROGRAPHIC_RIDGE_SALT),
                gtx + 41.75,
                gty - 13.5,
                OROGRAPHIC_RIDGE_CELL);
            double ridge = (1 - Math.abs(ridgeNoise * 2 - 1) - 0.5) * OROGRAPHIC_RIDGE_AMPLITUDE;
            // Two de-phased folded fields keep a complete camera view from landing on one smooth FBM shoulder. The
            // rotated second frame prevents an axis-aligned contour lattice, while the restrained 13-tile grain breaks
            // broad ramps into readable secondary shelves. All cells remain large enough that even the complete 0..12
            // ladder changes by at most one level between neighbouring walkable tiles.
            double foldAValue = valueNoise(
                (uint)(Js.ToInt32(seed) ^ OROGRAPHIC_FOLD_A_SALT),
                gtx - 7.25,
                gty + 11.5,
                OROGRAPHIC_FOLD_A_CELL);
            double rotatedX = gtx * 0.8191520443 + gty * 0.5735764364;
            double rotatedY = -(double)gtx * 0.5735764364 + gty * 0.8191520443;
            double foldBValue = valueNoise(
                (uint)(Js.ToInt32(seed) ^ OROGRAPHIC_FOLD_B_SALT),
                rotatedX + 29.75,
                rotatedY - 17.25,
                OROGRAPHIC_FOLD_B_CELL);
            double foldA = 1 - Math.abs(foldAValue * 2 - 1);
            double foldB = 1 - Math.abs(foldBValue * 2 - 1);
            double folds = (foldA * 0.62 + foldB * 0.38 - 0.5) * OROGRAPHIC_FOLD_AMPLITUDE;
            double grain =
                (valueNoise(
                    (uint)(Js.ToInt32(seed) ^ OROGRAPHIC_GRAIN_SALT),
                    gtx + 5.5,
                    gty - 37.75,
                    OROGRAPHIC_GRAIN_CELL) -
                    0.5) *
                OROGRAPHIC_GRAIN_AMPLITUDE;
            double broadOrographicStrength = 0.84 + clamp01(traits.escarpmentStrength) * 0.36;
            double foldedReliefStrength = 0.72 + clamp01(traits.reliefRuggedness) * 0.48;
            double rawHeight =
                0.5 +
                (baseHeight - 0.5) * traits.reliefContrast +
                traits.altitudeBias +
                (country + ridge) * broadOrographicStrength +
                (folds + grain) * foldedReliefStrength;
            double clippedHeight = clamp01(rawHeight);
            // Hard clamping is safe but can turn an exceptionally high or low 5x5 country into one enormous ceiling/
            // floor slab. Erode those saturated tails with the already-smooth folds: the main summit/valley remains,
            // while secondary shelves reappear inside it. This is continuous in world space and its longest possible
            // per-tile slope stays far below one level; the exact cardinal + diagonal contract is regression-tested.
            double summitInfluence = smoothstep(clamp01((clippedHeight - 0.86) / 0.14));
            double valleyInfluence = smoothstep(clamp01((0.14 - clippedHeight) / 0.14));
            double summitErosion =
                summitInfluence * (0.025 + (1 - foldB) * 0.12 * clamp01(traits.reliefRuggedness));
            double valleyShelf = valleyInfluence * (0.018 + foldA * 0.09 * clamp01(traits.reliefRuggedness));
            double normalizedHeight = clamp01(clippedHeight - summitErosion + valleyShelf);
            return new EndlessVerticalSample
            {
                altitudeBias = traits.altitudeBias,
                reliefContrast = traits.reliefContrast,
                reliefRuggedness = traits.reliefRuggedness,
                escarpmentStrength = traits.escarpmentStrength,
                openGroundCap = traits.openGroundCap,
                bridgeLandmarkDensity = traits.bridgeLandmarkDensity,
                normalizedHeight = normalizedHeight,
                level = EndlessMacro.endlessGroundLevelFromHeight(normalizedHeight, dna, maxLevel),
            };
        }

        for (int y = 0; y < fieldHeight; y++)
        {
            for (int x = 0; x < fieldWidth; x++)
            {
                int index = y * fieldWidth + x;
                var sample = composeSample(baseTx + x - halo, baseTy + y - halo);
                // Typed-array stores: the level byte wraps like a Uint8Array, the traits round to float32.
                levels[index] = Js.U8(sample.level);
                normalizedHeights[index] = (float)sample.normalizedHeight;
                altitudeBiases[index] = (float)sample.altitudeBias;
                reliefContrasts[index] = (float)sample.reliefContrast;
                reliefRuggednesses[index] = (float)sample.reliefRuggedness;
                escarpmentStrengths[index] = (float)sample.escarpmentStrength;
                openGroundCaps[index] = (float)sample.openGroundCap;
                bridgeLandmarkDensities[index] = (float)sample.bridgeLandmarkDensity;
            }
        }

        int preparedIndex(int gtx, int gty)
        {
            int x = gtx - baseTx + halo;
            int y = gty - baseTy + halo;
            return x >= 0 && y >= 0 && x < fieldWidth && y < fieldHeight ? y * fieldWidth + x : -1;
        }
        EndlessVerticalSample sampleAt(int gtx, int gty)
        {
            int index = preparedIndex(gtx, gty);
            if (index < 0) return composeSample(gtx, gty);
            return new EndlessVerticalSample
            {
                normalizedHeight = normalizedHeights[index],
                level = levels[index],
                altitudeBias = altitudeBiases[index],
                reliefContrast = reliefContrasts[index],
                reliefRuggedness = reliefRuggednesses[index],
                escarpmentStrength = escarpmentStrengths[index],
                openGroundCap = openGroundCaps[index],
                bridgeLandmarkDensity = bridgeLandmarkDensities[index],
            };
        }

        return new EndlessVerticalField
        {
            seed = seed,
            baseTx = baseTx,
            baseTy = baseTy,
            width = width,
            height = height,
            halo = halo,
            sampleAt = sampleAt,
            levelAt = (gtx, gty) =>
            {
                int index = preparedIndex(gtx, gty);
                return index < 0 ? composeSample(gtx, gty).level : levels[index];
            },
            normalizedHeightAt = (gtx, gty) =>
            {
                int index = preparedIndex(gtx, gty);
                return index < 0 ? composeSample(gtx, gty).normalizedHeight : normalizedHeights[index];
            },
            escarpmentStrengthAt = (gtx, gty) =>
            {
                int index = preparedIndex(gtx, gty);
                return index < 0 ? composeSample(gtx, gty).escarpmentStrength : escarpmentStrengths[index];
            },
            reliefRuggednessAt = (gtx, gty) =>
            {
                int index = preparedIndex(gtx, gty);
                return index < 0 ? composeSample(gtx, gty).reliefRuggedness : reliefRuggednesses[index];
            },
            openGroundCapAt = (gtx, gty) =>
            {
                int index = preparedIndex(gtx, gty);
                return index < 0 ? composeSample(gtx, gty).openGroundCap : openGroundCaps[index];
            },
            bridgeLandmarkDensityAt = (gtx, gty) =>
            {
                int index = preparedIndex(gtx, gty);
                return index < 0
                    ? composeSample(gtx, gty).bridgeLandmarkDensity
                    : bridgeLandmarkDensities[index];
            },
        };
    }

    /// <summary>
    /// Materialize selected high-side height contours as solid terrace rims. Guaranteed routes and encounter courts
    /// receive a one-tile collar, so where a route crosses a rim it becomes a deliberate broad ramp/switchback. The
    /// selection fields are slow and global: cliffs form long readable arcs rather than noisy one-cell walls.
    /// Returns the number of Floor cells raised to Solid for audit/tests.
    /// </summary>
    public static int applyEndlessEscarpments(
        byte[] tiles,
        int width,
        int height,
        ApplyEndlessEscarpmentOptions options)
    {
        double seed = options.seed;
        int baseTx = options.baseTx;
        int baseTy = options.baseTy;
        var routeMask = options.routeMask;
        var rooms = options.rooms;
        var field = options.field;
        var protectedMask = new byte[tiles.Length];
        for (int index = 0; index < routeMask.Length; index++)
        {
            if (routeMask[index] == 0) continue;
            int tx = index % width;
            int ty = (int)Math.floor((double)index / width);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx >= 0 && ny >= 0 && nx < width && ny < height) protectedMask[ny * width + nx] = 1;
                }
            }
        }
        foreach (var room in rooms)
        {
            for (
                int ty = Math.max(0, room.rect.ty - 2);
                ty < Math.min(height, room.rect.ty + room.rect.th + 2);
                ty++)
            {
                for (
                    int tx = Math.max(0, room.rect.tx - 2);
                    tx < Math.min(width, room.rect.tx + room.rect.tw + 2);
                    tx++)
                {
                    protectedMask[ty * width + tx] = 1;
                }
            }
        }

        var candidate = new byte[tiles.Length];
        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int index = ty * width + tx;
                if (tiles[index] != TileType.Floor || protectedMask[index] != 0) continue;
                int gtx = baseTx + tx;
                int gty = baseTy + ty;
                double ruggedness = clamp01(field.reliefRuggednessAt(gtx, gty));
                double highCountry = field.normalizedHeightAt(gtx, gty);
                double openCountryCalm = clamp01((field.openGroundCapAt(gtx, gty) - 0.56) / 0.12);
                // Calm lake/delta settings receive a geological frame without sacrificing their future water bed; their
                // lower escarpment trait deliberately matters. Ridge/canyon settings still saturate this pressure.
                double strength =
                    clamp01(0.14 + field.escarpmentStrengthAt(gtx, gty) * 0.68 + ruggedness * 0.12) *
                    (1 - openCountryCalm * 0.82);
                // Open water/moor countries reserve their low and middle shelves. Only their genuinely high enclosing rim
                // participates in solidification; this lets lakes remain broad while still gaining a visible mountain back.
                if (openCountryCalm > 0.08 && highCountry < 0.48 + openCountryCalm * 0.24) continue;
                double level = field.levelAt(gtx, gty);
                double radius = 2 + Math.round(strength * 3);
                double lowerNeighbour = level;
                for (int distance = 1; distance <= radius; distance++)
                {
                    foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
                    {
                        lowerNeighbour = Math.min(
                            lowerNeighbour,
                            field.levelAt(gtx + dx * distance, gty + dy * distance));
                    }
                }
                if (lowerNeighbour >= level) continue;

                double country = valueNoise(
                    (uint)(Js.ToInt32(seed) ^ ESCARPMENT_COUNTRY_SALT),
                    gtx,
                    gty,
                    ESCARPMENT_COUNTRY_CELL);
                double detail = valueNoise(
                    (uint)(Js.ToInt32(seed) ^ ESCARPMENT_DETAIL_SALT),
                    gtx + 17.25,
                    gty - 31.75,
                    ESCARPMENT_DETAIL_CELL);
                double continuity = country * 0.68 + detail * 0.32;
                // Preserve broad lowland bowls for the hydrology pass. Their higher surrounding contours remain eligible
                // and become the dramatic enclosing walls, instead of pre-emptively turning the lake floor into rock.
                if (highCountry < 0.28 + (1 - strength) * 0.12) continue;
                double threshold = 0.8 - strength * 0.34 - ruggedness * 0.1 - highCountry * 0.05;
                if (continuity >= threshold) candidate[index] = 1;
            }
        }

        int raised = 0;
        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int index = ty * width + tx;
                if (candidate[index] == 0) continue;
                int support = 0;
                bool touchesRock = false;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int neighbour = ny * width + nx;
                        if (candidate[neighbour] != 0) support++;
                        if (tiles[neighbour] == TileType.Solid) touchesRock = true;
                    }
                }
                // Interior candidates must belong to a short contour run or grow naturally from existing rock. At the
                // streamed edge the same global selector continues in the neighbour, so do not reject for missing local
                // support that merely lies outside this independently generated chunk.
                bool onBoundary = tx == 0 || ty == 0 || tx == width - 1 || ty == height - 1;
                if (!onBoundary && support < 2 && !touchesRock) continue;
                tiles[index] = TileType.Solid;
                raised++;
            }
        }
        return raised;
    }
}
