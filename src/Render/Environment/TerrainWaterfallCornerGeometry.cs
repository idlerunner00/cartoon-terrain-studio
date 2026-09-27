// Port of packages/client/src/render/environment/terrainWaterfallCornerGeometry.ts — keep in lockstep with the original.
//
// Rounded corner returns joining perpendicular Chasm waterfall sheets.
//
// PORT NOTE: the TS builder parameter is `Pick<TileGeometryBuilder, 'addSurface' | 'addWaterFace'>`, i.e. the
// compiler's builder itself; the C# port takes `TileGeometryBuilder`.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainLiquidChasmContour;
using static Fluitown.Render.TerrainWaterGeometryScratch;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// One lateral edge of an emitted, possibly multi-cell Chasm curtain.
///
/// Orthogonal curtains do not meet after they have rolled out over the lip: each lower edge has moved along a
/// different cardinal axis. Retaining their exact emitted boundary profiles lets the corner pass bridge that
/// quarter turn without re-sampling heights, depth or the unfolded animation field.
/// </summary>
public sealed class WaterfallCurtainBoundary
{
    public int cornerGridX;
    public int cornerGridY;
    public double cornerX;
    public double cornerZ;
    /// <summary>Exact emitted crest endpoint after the Chasm-lip radius and world-space wander were applied.</summary>
    public double crestX;
    public double crestZ;
    /// <summary>Exact cardinal receiver-floor endpoint that the organic crest relaxes into down the ruled curtain.</summary>
    public double floorX;
    public double floorZ;
    /// <summary><see cref="TerrainEdgeDirection"/>.</summary>
    public string direction;
    public double topY;
    public double bottomY;
    public double landingOffset;
    public bool projectionCritical;
    public int sourceColor;
    public int targetColor;
    public double sourceDepth;
    public double targetDepth;
    public double fallingDepth;
    public float[] foldDistance;
    public int foldStride;
    public int foldColumn;
    /// <summary>Straight curtain's world-space lateral field, retained across a terminal sweep without a shader seam.</summary>
    public double curtainAcrossCenter;
    public double curtainHalfArc;
    /// <summary>Unit fall direction normal to the already-deformed crest at this endpoint.</summary>
    public double outwardX;
    public double outwardZ;
    /// <summary>Cardinal direction away from the run at this endpoint (west/east or north/south).</summary>
    public double terminalSideX;
    public double terminalSideZ;
    /// <summary>The diagonal quadrant whose volume must own the space beside this rolled-out sheet.</summary>
    public int terminalReceiverGridX;
    public int terminalReceiverGridY;
    /// <summary>Chasm/liquid receivers continue the waterfall; dry receivers expose their geological edge instead.</summary>
    public bool terminalUsesWater;
    /// <summary>A normal dry receiver already publishes the exact Chasm-facing wall on this terminal plane.</summary>
    public bool terminalUsesCanonicalWall;
    public int terminalColor;
    public int terminalKind;
    public double terminalStrength;
}

public static partial class TerrainWaterfallCornerGeometry
{
    // Mutable module scratch → one instance per compiler thread (see RENDER_AGENT_BRIEF "Thread safety").
    [ThreadStatic] private static double[]? _CORNER_SHADE4;
    private static double[] CORNER_SHADE4 => _CORNER_SHADE4 ??= new double[] { 0, 0, 0, 0 };

    /// <param name="direction">TerrainEdgeDirection.</param>
    private static double sheetAxis(string direction) =>
        direction == "n" || direction == "s" ? 1 : -1;

    /// <summary>`foldDistance[index] ?? 0` — a Float32Array read is undefined outside the array.</summary>
    private static double foldAt(float[] foldDistance, int index) =>
        (uint)index < (uint)foldDistance.Length ? foldDistance[index] : 0;

    /// <summary>
    /// Join perpendicular Chasm curtains over the receiving corner.
    ///
    /// Each straight sheet rolls out along its own fall direction. At a ninety-degree lip that separates their
    /// lower edges and opens a triangular view onto the wall/backdrop behind them. A short quarter-round carrier
    /// spans the exact recorded sheet boundaries, so pigment, height and unfolded flow stay continuous while the
    /// water visibly follows the geological corner.
    /// </summary>
    public static void addRoundedWaterfallCorners(
        TileGeometryBuilder builder,
        MaterializedTerrain terrain,
        IReadOnlyList<WaterfallCurtainBoundary> boundaries)
    {
        P3[] sheet = WATERFALL_SHEET;
        double[] normalX4 = WATERFALL_NORMAL_X4;
        double[] normalY4 = WATERFALL_NORMAL_Y4;
        double[] normalZ4 = WATERFALL_NORMAL_Z4;
        double[] color4 = WATERFALL_COLOR4;
        double[] cornerShade4 = CORNER_SHADE4;
        double[] marker4 = WATERFALL_MARKER4;
        double[] axis4 = WATERFALL_AXIS4;
        double[] crest4 = WATERFALL_CREST4;
        double[] depth4 = WATERFALL_DEPTH4;
        double[] foldX4 = WATERFALL_FOLD_X4;
        double[] foldZ4 = WATERFALL_FOLD_Z4;
        IReadOnlyList<double> curveRows = WATERFALL_CURVE_ROWS;

        var byCorner = new JsMap<string, List<WaterfallCurtainBoundary>>();
        foreach (WaterfallCurtainBoundary boundary in boundaries)
        {
            string key = $"{Js.Str(boundary.cornerGridX)}:{Js.Str(boundary.cornerGridY)}";
            List<WaterfallCurtainBoundary>? candidates = byCorner.get(key);
            if (candidates != null) candidates.push(boundary);
            else byCorner.set(key, new List<WaterfallCurtainBoundary> { boundary });
        }

        const int cornerSegments = LIQUID_CHASM_EDGE_SEGMENTS;
        foreach (List<WaterfallCurtainBoundary> candidates in byCorner.values())
        {
            // Membership only (never iterated), so a reference-equality HashSet is exactly the JS Set.
            var joined = new HashSet<WaterfallCurtainBoundary>();
            for (int first = 0; first < candidates.Count; first++)
            {
                WaterfallCurtainBoundary a = candidates[first];
                double ax = a.outwardX;
                double az = a.outwardZ;
                for (int second = first + 1; second < candidates.Count; second++)
                {
                    WaterfallCurtainBoundary b = candidates[second];
                    double bx = b.outwardX;
                    double bz = b.outwardZ;
                    // Two curtain endpoints form a corner only when their exposed sides enter the SAME diagonal cell.
                    // Pairing every boundary that merely shares a grid point joined unrelated/opposite runs, marked their
                    // real terminal sides as handled and left a complete backdrop-facing sector without an owner.
                    if (
                        a.terminalReceiverGridX != b.terminalReceiverGridX ||
                        a.terminalReceiverGridY != b.terminalReceiverGridY)
                        continue;
                    TerrainCell? receiver = terrainCellAt(terrain, a.terminalReceiverGridX, a.terminalReceiverGridY);
                    // A dry diagonal is not a water corner. It is closed below by the terminal geological return emitted
                    // after this pair pass, using that dry cell's actual material.
                    if (receiver == null || !terrainCellCarriesChasmFloor(receiver))
                        continue;
                    // A macro-deformed digital stair can give two formerly perpendicular strips the same XZ direction.
                    // Direction alone does NOT make their emitted boundaries identical: unequal source datums leave a tall
                    // triangular opening between them. Omit the return only when the complete spatial profile is shared.
                    if (
                        ax == bx &&
                        az == bz &&
                        a.crestX == b.crestX &&
                        a.crestZ == b.crestZ &&
                        a.topY == b.topY &&
                        a.bottomY == b.bottomY &&
                        a.landingOffset == b.landingOffset &&
                        a.projectionCritical == b.projectionCritical)
                    {
                        joined.Add(a);
                        joined.Add(b);
                        continue;
                    }

                    joined.Add(a);
                    joined.Add(b);

                    void writeVertex(int index, int row, double cornerProgress)
                    {
                        double fallProgress = curveRows[row];
                        double descent = waterfallDescentCurve(fallProgress);
                        double angle = cornerProgress * Math.PI * 0.5;
                        double weightA = Math.cos(angle);
                        double weightB = Math.sin(angle);
                        // sin² gives the two joins a zero lateral height/pigment derivative, so the quarter turn reads as
                        // one folded sheet even when neighbouring Chasm throats or source-water samples differ slightly.
                        double blend = weightB * weightB;
                        double radiusA =
                            a.landingOffset * waterfallLandingCurve(fallProgress, a.projectionCritical);
                        double radiusB =
                            b.landingOffset * waterfallLandingCurve(fallProgress, b.projectionCritical);
                        double yA = a.topY + (a.bottomY - a.topY) * descent;
                        double yB = b.topY + (b.bottomY - b.topY) * descent;
                        P3 point = sheet[index];
                        // Both regular source grids author the same displaced corner. Blend only between those two exact
                        // crest endpoints; bending back through the undeformed lattice corner created a visible triangular
                        // notch and reintroduced the very facets this return is meant to hide.
                        double crestX = a.crestX + (b.crestX - a.crestX) * blend;
                        double crestZ = a.crestZ + (b.crestZ - a.crestZ) * blend;
                        double floorX = a.floorX + (b.floorX - a.floorX) * blend;
                        double floorZ = a.floorZ + (b.floorZ - a.floorZ) * blend;
                        double spineX = crestX + (floorX - crestX) * descent;
                        double spineZ = crestZ + (floorZ - crestZ) * descent;
                        point.x = spineX + ax * radiusA * weightA + bx * radiusB * weightB;
                        point.y = yA + (yB - yA) * blend;
                        point.z = spineZ + az * radiusA * weightA + bz * radiusB * weightB;

                        double verticalA = Math.abs((a.bottomY - a.topY) * waterfallDescentTangent(fallProgress));
                        double verticalB = Math.abs((b.bottomY - b.topY) * waterfallDescentTangent(fallProgress));
                        double upwardA =
                            a.landingOffset * waterfallLandingTangent(fallProgress, a.projectionCritical);
                        double upwardB =
                            b.landingOffset * waterfallLandingTangent(fallProgress, b.projectionCritical);
                        normalX4[index] = -ax * verticalA * weightA - bx * verticalB * weightB;
                        normalY4[index] = upwardA + (upwardB - upwardA) * blend;
                        normalZ4[index] = -az * verticalA * weightA - bz * verticalB * weightB;

                        int colorA = mix(a.sourceColor, a.targetColor, descent);
                        int colorB = mix(b.sourceColor, b.targetColor, descent);
                        color4[index] = mix(colorA, colorB, blend);
                        cornerShade4[index] = descent * 0.98;
                        marker4[index] = a.fallingDepth + (b.fallingDepth - a.fallingDepth) * blend;
                        axis4[index] =
                            sheetAxis(a.direction) + (sheetAxis(b.direction) - sheetAxis(a.direction)) * blend;
                        crest4[index] = a.topY + (b.topY - a.topY) * blend;
                        double depthA = a.sourceDepth + (a.targetDepth - a.sourceDepth) * descent;
                        double depthB = b.sourceDepth + (b.targetDepth - b.sourceDepth) * descent;
                        depth4[index] = depthA + (depthB - depthA) * blend;

                        double foldA = foldAt(a.foldDistance, row * a.foldStride + a.foldColumn);
                        double foldB = foldAt(b.foldDistance, row * b.foldStride + b.foldColumn);
                        foldX4[index] = crestX + ax * foldA * weightA + bx * foldB * weightB;
                        foldZ4[index] = crestZ + az * foldA * weightA + bz * foldB * weightB;
                    }

                    for (int row = 0; row < curveRows.Count - 1; row++)
                    {
                        for (int segment = 0; segment < cornerSegments; segment++)
                        {
                            double start = (double)segment / cornerSegments;
                            double end = (double)(segment + 1) / cornerSegments;
                            writeVertex(0, row, start);
                            writeVertex(1, row, end);
                            writeVertex(2, row + 1, end);
                            writeVertex(3, row + 1, start);
                            builder.addWaterFace(
                                sheet,
                                normalX4,
                                normalY4,
                                normalZ4,
                                color4,
                                cornerShade4,
                                marker4,
                                axis4,
                                crest4,
                                false,
                                false,
                                depth4,
                                false,
                                0,
                                0,
                                0,
                                foldX4,
                                foldZ4,
                                true);
                        }
                    }
                }
            }

            // A straight curtain is an unbacked sheet. Once it rolls out over the lip, an unpaired lateral edge no
            // longer touches the cardinal geological wall: the space between both profiles is a real view ray to the
            // global backdrop. Close EVERY such endpoint. Chasm/liquid quadrants keep the animated water language.
            // A dry quadrant with a canonical wall also uses Water for this swept sliver: the geological wall owns the
            // stationary terminal plane, while the waterfall owns the volume between that plane and its rolled-out
            // curve. Painting that volume as geology produced the old grey card; omitting it exposed the purple wedge
            // at (-56,-216). Only a genuinely ownerless dry terminal uses the geological fail-closed fallback.
            foreach (WaterfallCurtainBoundary boundary in candidates)
            {
                if (joined.Contains(boundary)) continue;
                bool waterfallOwnsTerminalSweep =
                    boundary.terminalUsesWater || boundary.terminalUsesCanonicalWall;
                void writeTerminalVertex(int index, int row, bool outer)
                {
                    double fallProgress = curveRows[row];
                    double descent = waterfallDescentCurve(fallProgress);
                    double y = boundary.topY + (boundary.bottomY - boundary.topY) * descent;
                    double radius =
                        boundary.landingOffset * waterfallLandingCurve(fallProgress, boundary.projectionCritical);
                    P3 point = sheet[index];
                    if (outer)
                    {
                        double spineX = boundary.crestX + (boundary.floorX - boundary.crestX) * descent;
                        double spineZ = boundary.crestZ + (boundary.floorZ - boundary.crestZ) * descent;
                        point.x = spineX + boundary.outwardX * radius;
                        point.z = spineZ + boundary.outwardZ * radius;
                    }
                    else
                    {
                        point.x = boundary.cornerX;
                        point.z = boundary.cornerZ;
                    }
                    point.y = y;

                    normalX4[index] = boundary.terminalSideX;
                    normalY4[index] = 0.12;
                    normalZ4[index] = boundary.terminalSideZ;
                    color4[index] = mix(boundary.sourceColor, boundary.targetColor, descent);
                    cornerShade4[index] = waterfallOwnsTerminalSweep ? descent * 0.98 : 1 - descent * 0.26;
                    marker4[index] = boundary.fallingDepth;
                    axis4[index] = sheetAxis(boundary.direction);
                    crest4[index] = boundary.topY;
                    depth4[index] =
                        boundary.sourceDepth + (boundary.targetDepth - boundary.sourceDepth) * descent;
                    double fold = foldAt(boundary.foldDistance, row * boundary.foldStride + boundary.foldColumn);
                    double foldOriginX = outer ? boundary.crestX : boundary.cornerX;
                    double foldOriginZ = outer ? boundary.crestZ : boundary.cornerZ;
                    foldX4[index] = foldOriginX + boundary.outwardX * fold;
                    foldZ4[index] = foldOriginZ + boundary.outwardZ * fold;
                }

                for (int row = 0; row < curveRows.Count - 1; row++)
                {
                    writeTerminalVertex(0, row, true);
                    writeTerminalVertex(1, row, false);
                    writeTerminalVertex(2, row + 1, false);
                    writeTerminalVertex(3, row + 1, true);
                    if (waterfallOwnsTerminalSweep)
                    {
                        builder.addWaterFace(
                            sheet,
                            normalX4,
                            normalY4,
                            normalZ4,
                            color4,
                            cornerShade4,
                            marker4,
                            axis4,
                            crest4,
                            false,
                            true,
                            depth4,
                            false,
                            0,
                            boundary.curtainAcrossCenter,
                            boundary.curtainHalfArc,
                            foldX4,
                            foldZ4,
                            true);
                    }
                    else
                    {
                        builder.addSurface(
                            sheet,
                            boundary.terminalSideX,
                            0.12,
                            boundary.terminalSideZ,
                            boundary.terminalColor,
                            boundary.terminalKind,
                            boundary.terminalStrength,
                            cornerShade4,
                            0,
                            false,
                            true);
                    }
                }
            }
        }
    }
}
