// Port of packages/client/src/render/environment/terrainGroundingGeometry.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/*
 * Shared, pure geometry emitters for terrain grounding.
 *
 * Both the synchronous Hub bake and the Endless worker compiler consume these helpers. Ground contact and
 * cliff-foot dressing therefore stay byte-deterministic without introducing a mesh, material, texture or
 * per-frame update. The builder contracts intentionally describe only the existing merged terrain batches.
 */

public sealed class TerrainGroundingPoint
{
    public double x;
    public double y;
    public double z;
}

/// <remarks>
/// PORT NOTE: structurally satisfied by the compiler's TileGeometryBuilder in TypeScript; in C# the builder has
/// to implement this interface (its own `addOverlayShaded` takes `P3[]`, so an explicit interface implementation
/// that forwards the points is the expected adapter).
/// </remarks>
public interface TerrainGroundingOverlayBuilder
{
    /// <param name="hex">A 0xRRGGBB colour.</param>
    void addOverlayShaded(
        List<TerrainGroundingPoint> points,
        int hex,
        double alpha,
        IReadOnlyList<double>? alphas = null,
        double edgeOnNormalX = 0);
}

/// <remarks>
/// PORT NOTE: the TS signature takes `number | readonly number[]` unions for `hex`, `kind` and `strength` and
/// `readonly number[] | number` for `wind`; this module only ever passes the scalar branch (and a per-vertex
/// `shade`), so the C# contract states exactly that. Structurally satisfied by the compiler's TileGeometryBuilder
/// in TypeScript; in C# the builder has to implement it (see <see cref="TerrainGroundingOverlayBuilder"/>).
/// </remarks>
public interface TerrainGroundingSurfaceBuilder
{
    /// <param name="hex">A 0xRRGGBB colour.</param>
    /// <param name="kind">A TERRAIN_SURFACE_PATTERN id.</param>
    void addSurface(
        List<TerrainGroundingPoint> points,
        double nx,
        double ny,
        double nz,
        int hex,
        int kind,
        double strength,
        IReadOnlyList<double>? shade = null,
        double? wind = null,
        bool? actorWall = null,
        bool? preserveEdgeOn = null,
        bool? orbitBackside = null);
}

// `export type TerrainFootDirection = 'n' | 'e' | 's' | 'w';` → string.

public sealed class GroundContactBandOptions
{
    public TerrainGroundingOverlayBuilder builder;
    /// <summary>TerrainFootDirection: "n" | "e" | "s" | "w".</summary>
    public string direction;
    /// <summary>First and last coordinate along the wall foot (X for N/S, Z for E/W).</summary>
    public double start;
    public double end;
    /// <summary>Z for N/S, X for E/W.</summary>
    public double edge;
    public double y;
    public double width;
    public double alpha;
    /// <summary>A 0xRRGGBB colour.</summary>
    public int color;
}

public sealed class CliffFootPebbleOptions
{
    public TerrainGroundingSurfaceBuilder builder;
    /// <summary>TerrainFootDirection: "n" | "e" | "s" | "w".</summary>
    public string direction;
    public double start;
    public double end;
    public double edge;
    public double floorY;
    public double tileSize;
    public double dropLevels;
    public double worldCellX;
    public double worldCellY;
    /// <summary>0 disables the family; 1 is the desktop authored density.</summary>
    public double density;
    /// <summary>A 0xRRGGBB colour.</summary>
    public int capColor;
    /// <summary>A 0xRRGGBB colour.</summary>
    public int sideColor;
}

public static partial class TerrainGroundingGeometry
{
    // Shared terrain shader surface-family ids (kept local to this pure worker-leaf module).
    private const int SURFACE_ROCK_CAP = 1;
    private const int SURFACE_ROCK_FACE = 3;

    /// <summary>
    /// Two-scale, texture-free contact AO in the existing overlay batch.
    ///
    /// A compact umbra locks the wall to the receiver; a contiguous low-opacity penumbra removes the former hard
    /// sticker edge. The two quads never overlap, so their integrated darkness remains close to the old one-quad
    /// gradient while the actual contact is more legible. Returned geometry is static and costs no extra draw call.
    /// </summary>
    public static bool addGroundContactBand(GroundContactBandOptions options)
    {
        var builder = options.builder;
        string direction = options.direction;
        double start = options.start;
        double end = options.end;
        double edge = options.edge;
        double y = options.y;
        int color = options.color;
        double width = finitePositive(options.width);
        double alpha = clamp(options.alpha, 0, 0.24);
        if (end - start <= 0.2 || width <= 0.1 || alpha <= 0.004) return false;

        double coreWidth = Math.min(width, Math.max(0.8, width * 0.38));
        double coreAlpha = Math.min(0.24, alpha * 1.18);
        double shoulderAlpha = alpha * 0.44;
        emitBandQuad(
            builder,
            direction,
            start,
            end,
            edge,
            edgeOffset(edge, direction, coreWidth),
            y,
            color,
            bandAlphas(coreAlpha, coreAlpha, shoulderAlpha, shoulderAlpha));
        if (width - coreWidth > 0.1)
        {
            emitBandQuad(
                builder,
                direction,
                start,
                end,
                edgeOffset(edge, direction, coreWidth),
                edgeOffset(edge, direction, width),
                y + 0.002,
                color,
                bandAlphas(shoulderAlpha, shoulderAlpha, 0, 0));
        }
        return true;
    }

    /// <summary>Pure coverage rule exposed for quality/performance regression tests.</summary>
    public static double cliffFootPebbleCoverage(double dropLevels, double density)
    {
        // A full one-level terrace is already a real cliff in the game's 15 px height language and is the most
        // common source of long ruler-straight Hub edges. Sub-level bevel/relief transitions remain undressed.
        if (!Number.isFinite(dropLevels) || dropLevels < 0.9) return 0;
        // One punctuation cluster roughly every 8-15 eligible cliff cells at desktop density. This is enough to
        // break a long ruler edge while keeping transferred/static vertex growth well below the existing dressing
        // variance; pebbles must never become a gravel carpet competing with actors.
        return clamp((0.055 + Math.min(7, dropLevels) * 0.01) * clamp(density, 0, 1), 0, 0.14);
    }

    /// <summary>
    /// Emit a sparse family of faceted cliff-foot stones into the existing lit terrain surface batch.
    ///
    /// This deliberately is not an InstancedMesh: terrain residency is tile-batched already, so one instanced mesh
    /// per tile would add a colour and shadow submission for every visible tile. A few baked polygons preserve the
    /// one-surface-draw contract, inherit the terrain shader/light/grade, and require no live instance maintenance.
    /// </summary>
    public static int addCliffFootPebbles(CliffFootPebbleOptions options)
    {
        double coverage = cliffFootPebbleCoverage(options.dropLevels, options.density);
        double span = options.end - options.start;
        if (coverage <= 0 || span < options.tileSize * 0.22) return 0;

        double directionSalt = directionIndex(options.direction) * 131;
        double presence = stableSample(options.worldCellX, options.worldCellY, 311 + directionSalt);
        if (presence >= coverage) return 0;

        double density = clamp(options.density, 0, 1);
        double second = stableSample(options.worldCellX, options.worldCellY, 617 + directionSalt);
        int count = second < 0.26 * density ? 2 : 1;
        double outwardSign = options.direction == "n" || options.direction == "w" ? -1 : 1;
        int emitted = 0;

        for (int index = 0; index < count; index++)
        {
            double seedSalt = 947 + directionSalt + index * 277;
            double alongSample = stableSample(options.worldCellX, options.worldCellY, seedSalt);
            double sizeSample = stableSample(options.worldCellX, options.worldCellY, seedSalt + 37);
            double shapeSample = stableSample(options.worldCellX, options.worldCellY, seedSalt + 79);
            double along =
                options.start +
                span *
                    (count == 1
                        ? 0.22 + alongSample * 0.56
                        : index == 0
                            ? 0.2 + alongSample * 0.22
                            : 0.58 + alongSample * 0.22);
            double outward = outwardSign * options.tileSize * (0.035 + shapeSample * 0.045);
            double cx =
                options.direction == "n" || options.direction == "s" ? along : options.edge + outward;
            double cz =
                options.direction == "n" || options.direction == "s" ? options.edge + outward : along;
            bool primary = index == 0;
            double radius =
                options.tileSize *
                (primary ? 0.045 + sizeSample * 0.035 : 0.028 + sizeSample * 0.018);
            double height =
                radius * (primary ? 1.05 + shapeSample * 0.5 : 0.72 + shapeSample * 0.35);
            double tangentStretch = primary ? 1.18 : 1.04;
            double rotation = shapeSample * Math.PI * 2 + directionSalt * 0.013;
            emitFacetedPebble(
                options.builder,
                cx,
                cz,
                options.floorY,
                radius * tangentStretch,
                radius * (0.72 + sizeSample * 0.16),
                height,
                rotation,
                options.capColor,
                options.sideColor);
            emitted++;
        }
        return emitted;
    }

    private static void emitFacetedPebble(
        TerrainGroundingSurfaceBuilder builder,
        double cx,
        double cz,
        double floorY,
        double radiusX,
        double radiusZ,
        double height,
        double rotation,
        int capColor,
        int sideColor)
    {
        const int sides = 5;
        var bottom = new List<TerrainGroundingPoint>();
        var top = new List<TerrainGroundingPoint>();
        const double topScale = 0.48;
        double topOffsetX = Math.cos(rotation * 1.7) * radiusX * 0.12;
        double topOffsetZ = Math.sin(rotation * 1.3) * radiusZ * 0.12;
        for (int side = 0; side < sides; side++)
        {
            double angle = rotation + ((double)side / sides) * Math.PI * 2;
            double irregular = 0.88 + stableUnit(side * 97 + Math.floor(rotation * 1009)) * 0.2;
            bottom.push(new TerrainGroundingPoint
            {
                x = cx + Math.cos(angle) * radiusX * irregular,
                y = floorY + 0.025,
                z = cz + Math.sin(angle) * radiusZ * irregular,
            });
            top.push(new TerrainGroundingPoint
            {
                x = cx + topOffsetX + Math.cos(angle) * radiusX * topScale * irregular,
                y = floorY + height,
                z = cz + topOffsetZ + Math.sin(angle) * radiusZ * topScale * irregular,
            });
        }
        builder.addSurface(top, 0, 1, 0, capColor, SURFACE_ROCK_CAP, 0.12);
        for (int side = 0; side < sides; side++)
        {
            int next = (side + 1) % sides;
            double midpoint = rotation + ((side + 0.5) / sides) * Math.PI * 2;
            builder.addSurface(
                new List<TerrainGroundingPoint> { top[side], top[next], bottom[next], bottom[side] },
                Math.cos(midpoint),
                0.24,
                Math.sin(midpoint),
                sideColor,
                SURFACE_ROCK_FACE,
                0.15,
                PEBBLE_SIDE_SHADE);
        }
    }

    /// <param name="direction">TerrainFootDirection: "n" | "e" | "s" | "w".</param>
    /// <param name="alphas">`readonly [number, number, number, number]`.</param>
    private static void emitBandQuad(
        TerrainGroundingOverlayBuilder builder,
        string direction,
        double start,
        double end,
        double inner,
        double outer,
        double y,
        int color,
        double[] alphas)
    {
        // PORT NOTE (allocation): the quad is per-thread scratch (TS: a fresh array literal per band). The only builder,
        // TileGeometryBuilder, copies the points synchronously and never retains them (see its `groundingPoints`).
        List<TerrainGroundingPoint> points = BAND_QUAD_SCRATCH;
        if (direction == "n" || direction == "s")
        {
            setPoint(points[0], start, y, inner);
            setPoint(points[1], end, y, inner);
            setPoint(points[2], end, y, outer);
            setPoint(points[3], start, y, outer);
        }
        else
        {
            setPoint(points[0], inner, y, start);
            setPoint(points[1], outer, y, start);
            setPoint(points[2], outer, y, end);
            setPoint(points[3], inner, y, end);
        }
        builder.addOverlayShaded(points, color, alphas[0], alphas);
    }

    private static void setPoint(TerrainGroundingPoint point, double x, double y, double z)
    {
        point.x = x;
        point.y = y;
        point.z = z;
    }

    [ThreadStatic] private static List<TerrainGroundingPoint>? _BAND_QUAD_SCRATCH;
    private static List<TerrainGroundingPoint> BAND_QUAD_SCRATCH => _BAND_QUAD_SCRATCH ??= new List<TerrainGroundingPoint>
    {
        new TerrainGroundingPoint(),
        new TerrainGroundingPoint(),
        new TerrainGroundingPoint(),
        new TerrainGroundingPoint(),
    };

    /// <summary>The four band alphas as per-thread scratch, consumed synchronously by <see cref="emitBandQuad"/>.</summary>
    [ThreadStatic] private static double[]? _BAND_ALPHAS_SCRATCH;

    private static double[] bandAlphas(double a, double b, double c, double d)
    {
        double[] alphas = _BAND_ALPHAS_SCRATCH ??= new double[4];
        alphas[0] = a;
        alphas[1] = b;
        alphas[2] = c;
        alphas[3] = d;
        return alphas;
    }

    /// <summary>Never written (TS: an array literal per pebble side).</summary>
    private static readonly double[] PEBBLE_SIDE_SHADE = { 1, 1, 0.76, 0.76 };

    private static double edgeOffset(double edge, string direction, double distance)
    {
        return edge + (direction == "n" || direction == "w" ? -distance : distance);
    }

    private static int directionIndex(string direction)
    {
        return direction == "n" ? 0 : direction == "e" ? 1 : direction == "s" ? 2 : 3;
    }

    private static double stableSample(double x, double y, double salt)
    {
        int value = Math.imul(Js.ToInt32(x) ^ Math.imul(Js.ToInt32(y), 0x9e3779b1) ^ Js.ToInt32(salt), 0x45d9f3b);
        value = Math.imul(value ^ (int)((uint)value >> 16), 0x45d9f3b);
        return (uint)(value ^ (int)((uint)value >> 16)) / 4294967296.0;
    }

    private static double stableUnit(double seed)
    {
        int value = Math.imul(Js.ToInt32(seed), 0x45d9f3b);
        value = Math.imul(value ^ (int)((uint)value >> 16), 0x45d9f3b);
        return (uint)(value ^ (int)((uint)value >> 16)) / 4294967296.0;
    }

    private static double finitePositive(double value)
    {
        return Number.isFinite(value) ? Math.max(0, value) : 0;
    }

    private static double clamp(double value, double min, double max)
    {
        return value < min ? min : value > max ? max : value;
    }
}
