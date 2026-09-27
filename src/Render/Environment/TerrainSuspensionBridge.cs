// Port of packages/client/src/render/environment/terrainSuspensionBridge.ts — keep in lockstep with the original.
//
// PORT NOTE (structural types): TypeScript satisfies `SuspensionPoint` with any `{ x, y, z }` (the compiler hands
// in its `P3` scratch points and contour footprints) and `SuspensionBridgeMaterial` with the render plan's
// `TerrainMaterial` (the only producer). C# typing is nominal, so both names are global aliases of exactly those
// types: every existing caller keeps compiling unchanged and no copy is ever made.
global using SuspensionPoint = Fluitown.Render.P3;
global using SuspensionBridgeMaterial = Fluitown.Domain.TerrainMaterial;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainSuspensionStyle;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// The slice of the terrain tile builder the suspension bridge and deck soffit need.
///
/// PORT NOTE: the TS signature types <c>hex</c>/<c>kind</c>/<c>strength</c> as <c>number | readonly number[]</c> and
/// <c>wind</c> as <c>readonly number[] | number</c>, but every call made through this interface passes a scalar
/// colour, a scalar kind, a scalar strength and an array (or nothing) for wind, so the C# contract carries exactly
/// those. <c>null</c> for <c>shade</c>/<c>wind</c> is the TS <c>undefined</c> (the builder's own defaults apply).
/// <c>TileGeometryBuilder</c> implements it explicitly, forwarding to its general <c>addSurface</c>/<c>addOverlay</c>.
/// </summary>
public interface SuspensionSurfaceBuilder
{
    void addSurface(
        IReadOnlyList<SuspensionPoint> points,
        double nx,
        double ny,
        double nz,
        int hex,
        int kind,
        double strength,
        double[]? shade = null,
        double[]? wind = null,
        bool actorWall = false,
        bool preserveEdgeOn = false,
        bool orbitBackside = false);

    void addOverlay(IReadOnlyList<SuspensionPoint> points, int hex, double alpha);
}

public sealed class UnderpassSuspensionGeometryInput
{
    public SuspensionSurfaceBuilder builder;
    /// <summary>'horizontal' | 'vertical' (<see cref="TerrainPassageAxis"/>).</summary>
    public string passageAxis;
    public int span;
    public int spanIndex;
    public int worldTileX;
    public int worldTileY;
    public double x0;
    public double z0;
    public double tileSize;
    public double elevationStep;
    public double floorY;
    public double deckTopY;
    public double negativeSupportTopY;
    public double positiveSupportTopY;
    public SuspensionBridgeMaterial material;
    public int bridgeSurfaceKind;
    public int faceSurfaceKind;
}

public sealed class UnderpassSuspensionGeometryAudit
{
    public int planks;
    public int visibleGaps;
    public int cableSegments;
    public int hangers;
    public int anchorPosts;
}

public static partial class TerrainSuspensionBridge
{
    private static readonly double[] UNIT_SHADE = { 1, 1, 1, 1 };
    private static readonly double[] UNIT_ZERO = { 0, 0, 0, 0 };

    private static double hash2d(double x, double y)
    {
        int h = Math.imul(Js.ToInt32(x), 0x27d4eb2f) ^ Math.imul(Js.ToInt32(y), 0x165667b1);
        h = Math.imul(h ^ (int)((uint)h >> 15), 0x85ebca6b);
        h = Math.imul(h ^ (int)((uint)h >> 13), 0xc2b2ae35);
        return (double)(uint)(h ^ (int)((uint)h >> 16)) / 4294967296;
    }

    /// <summary>Pure interval contract used by geometry and tests: every neighbouring plank retains real negative space.</summary>
    public static IReadOnlyList<(double, double)> suspensionPlankIntervals(
        double planks,
        double gapCells = TERRAIN_SUSPENSION_STYLE.plankGapCells)
    {
        double count = Math.max(1, Math.trunc(planks));
        double halfGap = Math.min(0.45 / count, Math.max(0, gapCells) * 0.5);
        var @out = new List<(double, double)>();
        for (int index = 0; index < count; index++)
        {
            @out.push((index / count + halfGap, (index + 1) / count - halfGap));
        }
        return @out;
    }

    public static void addBridgeBeam(
        SuspensionSurfaceBuilder builder,
        SuspensionPoint from,
        SuspensionPoint to,
        double width,
        int topColor,
        int sideColor,
        int surfaceKind,
        bool actorWall)
    {
        double dx = to.x - from.x;
        double dy = to.y - from.y;
        double dz = to.z - from.z;
        double length = Math.hypot(dx, dy, dz);
        if (length <= 1e-4 || width <= 0) return;
        double horizontal = Math.hypot(dx, dz);
        double sx = horizontal > 1e-4 ? -dz / horizontal : 1;
        double sy = 0;
        double sz = horizontal > 1e-4 ? dx / horizontal : 0;
        double ux = (sy * dz - sz * dy) / length;
        double uy = (sz * dx - sx * dz) / length;
        double uz = (sx * dy - sy * dx) / length;
        double radius = width * 0.5;
        SuspensionPoint point(SuspensionPoint @base, double side, double up) => new SuspensionPoint
        {
            x = @base.x + sx * radius * side + ux * radius * up,
            y = @base.y + sy * radius * side + uy * radius * up,
            z = @base.z + sz * radius * side + uz * radius * up,
        };
        SuspensionPoint a = point(from, 1, 1);
        SuspensionPoint b = point(from, 1, -1);
        SuspensionPoint c = point(from, -1, -1);
        SuspensionPoint d = point(from, -1, 1);
        SuspensionPoint e = point(to, 1, 1);
        SuspensionPoint f = point(to, 1, -1);
        SuspensionPoint g = point(to, -1, -1);
        SuspensionPoint h = point(to, -1, 1);
        builder.addSurface(
            new[] { a, b, f, e },
            sx,
            sy,
            sz,
            sideColor,
            surfaceKind,
            0.08,
            UNIT_SHADE,
            UNIT_ZERO,
            actorWall,
            true);
        builder.addSurface(
            new[] { d, h, g, c },
            -sx,
            -sy,
            -sz,
            sideColor,
            surfaceKind,
            0.08,
            UNIT_SHADE,
            UNIT_ZERO,
            actorWall,
            true);
        builder.addSurface(
            new[] { a, e, h, d },
            ux,
            uy,
            uz,
            topColor,
            surfaceKind,
            0.06,
            UNIT_SHADE,
            UNIT_ZERO,
            actorWall,
            true);
        builder.addSurface(
            new[] { b, c, g, f },
            -ux,
            -uy,
            -uz,
            sideColor,
            surfaceKind,
            0.08,
            UNIT_SHADE,
            UNIT_ZERO,
            actorWall,
            true);
    }

    /// <summary>
    /// Emit one cell of a continuous overhead suspension bridge. Component-global coordinates drive every curve and
    /// variant, so independently baked terrain tiles meet exactly at cell boundaries without a render-only seam.
    /// </summary>
    public static UnderpassSuspensionGeometryAudit buildUnderpassSuspensionGeometry(UnderpassSuspensionGeometryInput input)
    {
        SuspensionSurfaceBuilder builder = input.builder;
        SuspensionBridgeMaterial material = input.material;
        string passageAxis = input.passageAxis;
        int span = input.span;
        int spanIndex = input.spanIndex;
        double ts = input.tileSize;
        double elev = input.elevationStep;
        if (span < 2 || spanIndex < 0 || spanIndex >= span)
        {
            return new UnderpassSuspensionGeometryAudit { planks = 0, visibleGaps = 0, cableSegments = 0, hangers = 0, anchorPosts = 0 };
        }

        // Doubles, not ints: `alongX * normal` must yield JS's -0 for a zero axis, exactly as the original does.
        double alongX = passageAxis == "horizontal" ? 1 : 0;
        double alongZ = passageAxis == "horizontal" ? 0 : 1;
        double bridgeX = passageAxis == "horizontal" ? 0 : 1;
        double bridgeZ = passageAxis == "horizontal" ? 1 : 0;
        double cx = input.x0 + ts * 0.5;
        double cz = input.z0 + ts * 0.5;
        SuspensionPoint point(double along, double cross, double y) => new SuspensionPoint
        {
            x = cx + (along - 0.5) * ts * alongX + (cross - 0.5) * ts * bridgeX,
            y = y,
            z = cz + (along - 0.5) * ts * alongZ + (cross - 0.5) * ts * bridgeZ,
        };
        double componentX = input.worldTileX - bridgeX * spanIndex;
        double componentY = input.worldTileY - bridgeZ * spanIndex;
        double variant = hash2d(componentX * 17 + (double)span * 29, componentY * 19 - (double)span * 31);
        double plankCount =
            TERRAIN_SUSPENSION_STYLE.minPlanksPerCell +
            Math.floor(
                variant *
                (TERRAIN_SUSPENSION_STYLE.maxPlanksPerCell - TERRAIN_SUSPENSION_STYLE.minPlanksPerCell + 1));
        double deckSag = TERRAIN_SUSPENSION_STYLE.deckSagLevels * elev * (0.86 + variant * 0.24);
        double deckY(double componentT) =>
            input.deckTopY - Math.pow(Math.sin(Math.PI * componentT), 2) * deckSag;
        double componentT(double localCross) => (spanIndex + localCross) / span;
        int topColor = mix(material.top, material.topLight, 0.2);
        int plankSide = mix(material.side, material.edgeDark, 0.22);
        int underside = mix(material.edgeDark, material.side, 0.36);
        int ropeColor = mix(material.edgeDark, material.detail, 0.3);
        int ropeLight = mix(ropeColor, material.edgeLight, 0.2);
        IReadOnlyList<(double, double)> intervals = suspensionPlankIntervals(plankCount);
        double deckHalfWidth = TERRAIN_SUSPENSION_STYLE.deckHalfWidthCells;
        double thickness = TERRAIN_SUSPENSION_STYLE.plankThicknessLevels * elev;

        void addLocalBox(
            double along0,
            double along1,
            double cross0,
            double cross1,
            double bottomY,
            double topY,
            int cap,
            int side,
            bool actorWall)
        {
            builder.addSurface(
                new[]
                {
                    point(along0, cross0, topY),
                    point(along1, cross0, topY),
                    point(along1, cross1, topY),
                    point(along0, cross1, topY),
                },
                0,
                1,
                0,
                cap,
                input.bridgeSurfaceKind,
                0.12,
                UNIT_SHADE,
                UNIT_ZERO,
                actorWall);
            builder.addSurface(
                new[]
                {
                    point(along1, cross0, bottomY),
                    point(along0, cross0, bottomY),
                    point(along0, cross1, bottomY),
                    point(along1, cross1, bottomY),
                },
                0,
                -1,
                0,
                underside,
                input.faceSurfaceKind,
                0.15,
                UNIT_SHADE,
                UNIT_ZERO,
                actorWall);
            foreach (double along in new[] { along0, along1 })
            {
                double normal = along == along0 ? -1 : 1;
                builder.addSurface(
                    new[]
                    {
                        point(along, cross0, bottomY),
                        point(along, cross1, bottomY),
                        point(along, cross1, topY),
                        point(along, cross0, topY),
                    },
                    alongX * normal,
                    0,
                    alongZ * normal,
                    side,
                    input.faceSurfaceKind,
                    0.12,
                    UNIT_SHADE,
                    UNIT_ZERO,
                    actorWall,
                    true);
            }
            foreach (double cross in new[] { cross0, cross1 })
            {
                double normal = cross == cross0 ? -1 : 1;
                builder.addSurface(
                    new[]
                    {
                        point(along0, cross, bottomY),
                        point(along1, cross, bottomY),
                        point(along1, cross, topY),
                        point(along0, cross, topY),
                    },
                    bridgeX * normal,
                    0,
                    bridgeZ * normal,
                    side,
                    input.faceSurfaceKind,
                    0.12,
                    UNIT_SHADE,
                    UNIT_ZERO,
                    actorWall,
                    true);
            }
        }

        for (int plank = 0; plank < intervals.Count; plank++)
        {
            var (cross0, cross1) = intervals[plank];
            double globalPlank = spanIndex * plankCount + plank;
            double plankVariant = hash2d(
                componentX * 101 + globalPlank * 13,
                componentY * 103 - globalPlank * 17);
            double lengthJitter = (plankVariant - 0.5) * TERRAIN_SUSPENSION_STYLE.plankLengthJitterCells;
            double along0 = 0.5 - deckHalfWidth - lengthJitter;
            double along1 = 0.5 + deckHalfWidth + lengthJitter * 0.55;
            double midT = componentT((cross0 + cross1) * 0.5);
            double plankTop =
                deckY(midT) + (plankVariant - 0.5) * TERRAIN_SUSPENSION_STYLE.plankLiftJitterLevels * elev;
            addLocalBox(
                along0,
                along1,
                cross0,
                cross1,
                plankTop - thickness,
                plankTop,
                mix(topColor, material.topDark, plankVariant * 0.16),
                plankSide,
                true);
            // Broken projected shade is deliberately congruent with the real boards: light reaches the floor through
            // every gap, making the open construction readable even before the camera catches the deck top.
            builder.addOverlay(
                new[]
                {
                    point(along0, cross0, input.floorY + 0.04),
                    point(along1, cross0, input.floorY + 0.04),
                    point(along1, cross1, input.floorY + 0.04),
                    point(along0, cross1, input.floorY + 0.04),
                },
                material.edgeDark,
                TERRAIN_SUSPENSION_STYLE.shadowAlpha * (0.88 + variant * 0.18));
        }

        // Two narrow, sagging stringers carry the disconnected boards without closing their see-through gaps.
        double stringerWidth = TERRAIN_SUSPENSION_STYLE.stringerWidthCells * ts;
        foreach (double along in new[]
                 {
                     0.5 - deckHalfWidth + TERRAIN_SUSPENSION_STYLE.stringerInsetCells,
                     0.5 + deckHalfWidth - TERRAIN_SUSPENSION_STYLE.stringerInsetCells,
                 })
        {
            for (int segment = 0; segment < 2; segment++)
            {
                double a = segment * 0.5;
                double b = (segment + 1) * 0.5;
                double ay =
                    deckY(componentT(a)) - thickness - TERRAIN_SUSPENSION_STYLE.stringerDepthLevels * elev;
                double by =
                    deckY(componentT(b)) - thickness - TERRAIN_SUSPENSION_STYLE.stringerDepthLevels * elev;
                addBridgeBeam(
                    builder,
                    point(along, a, ay),
                    point(along, b, by),
                    stringerWidth,
                    mix(material.topDark, material.edgeLight, 0.12),
                    underside,
                    input.bridgeSurfaceKind,
                    true);
            }
        }

        double cableWidth = TERRAIN_SUSPENSION_STYLE.cableWidthCells * ts;
        double postHeight = TERRAIN_SUSPENSION_STYLE.anchorPostHeightLevels * elev;
        double cableSag = TERRAIN_SUSPENSION_STYLE.cableSagLevels * elev * (0.92 + variant * 0.14);
        double cableY(double t) => input.deckTopY + postHeight - 4 * t * (1 - t) * cableSag;
        double[] railAlong = { 0.5 - deckHalfWidth, 0.5 + deckHalfWidth };
        int cableSegments = 0;
        foreach (double along in railAlong)
        {
            for (int segment = 0; segment < TERRAIN_SUSPENSION_STYLE.cableSegmentsPerCell; segment++)
            {
                double localA = segment / TERRAIN_SUSPENSION_STYLE.cableSegmentsPerCell;
                double localB = (segment + 1) / TERRAIN_SUSPENSION_STYLE.cableSegmentsPerCell;
                double tA = componentT(localA);
                double tB = componentT(localB);
                addBridgeBeam(
                    builder,
                    point(along, localA, cableY(tA)),
                    point(along, localB, cableY(tB)),
                    cableWidth,
                    ropeLight,
                    ropeColor,
                    input.bridgeSurfaceKind,
                    false);
                cableSegments++;
            }
        }

        // One hanger per cell and side gives several procedural rhythms as the span changes from two to six cells.
        double hangerT = componentT(0.5);
        double hangerBottom = deckY(hangerT) + TERRAIN_SUSPENSION_STYLE.hangerDeckClearanceLevels * elev;
        foreach (double along in railAlong)
        {
            addBridgeBeam(
                builder,
                point(along, 0.5, hangerBottom),
                point(along, 0.5, cableY(hangerT)),
                cableWidth * 0.62,
                ropeLight,
                ropeColor,
                input.bridgeSurfaceKind,
                false);
        }

        int anchorPosts = 0;
        void addAnchorPortal(double cross, double supportTopY)
        {
            double postWidth = TERRAIN_SUSPENSION_STYLE.anchorPostWidthCells;
            double baseY = Math.min(supportTopY - elev * 0.06, input.deckTopY - elev * 0.08);
            double topY = input.deckTopY + postHeight;
            foreach (double along in railAlong)
            {
                addLocalBox(
                    along - postWidth * 0.5,
                    along + postWidth * 0.5,
                    cross - postWidth * 0.5,
                    cross + postWidth * 0.5,
                    baseY,
                    topY,
                    mix(material.edgeLight, material.top, 0.24),
                    underside,
                    true);
                anchorPosts++;
            }
            addBridgeBeam(
                builder,
                point(railAlong[0], cross, topY - postWidth * ts * 0.25),
                point(railAlong[1], cross, topY - postWidth * ts * 0.25),
                postWidth * ts * 0.72,
                material.edgeLight,
                underside,
                input.bridgeSurfaceKind,
                true);
        }
        if (spanIndex == 0) addAnchorPortal(0, input.negativeSupportTopY);
        if (spanIndex == span - 1) addAnchorPortal(1, input.positiveSupportTopY);

        return new UnderpassSuspensionGeometryAudit
        {
            planks = intervals.Count,
            visibleGaps = intervals.Count,
            cableSegments = cableSegments,
            hangers = railAlong.Length,
            anchorPosts = anchorPosts,
        };
    }
}
