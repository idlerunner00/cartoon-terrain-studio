// Port of the "Checks" overlay colour logic of packages/client/src/generator.ts (drawOverlay, ELEVATION_RAMP /
// DEPTH_RAMP / sampleRamp / elevationOverlayColor, TILE_COLORS, issueCoord) — keep in lockstep with the original.
//
// Engine-free: the Godot UI turns the per-cell RGBA produced here into a texture / decal. Colours are packed
// 0xRRGGBB ints exactly as in TS; alphas are the TS fill alphas.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using Math = Fluitown.Runtime.JsMath;
using STANDARD = Fluitown.Domain.TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS;

namespace TerrainStudio.Core;

/// <summary>
/// The editor's "Checks" toggles (TS `OverlayKey`). A [Flags] set: several overlays may be on at once, exactly like
/// the TS `Set&lt;OverlayKey&gt;`; when combined they are drawn per cell in the fixed order Walk → Height → Collision →
/// Bridges → Connect → Problems (the order of the `if` blocks in `drawOverlay`).
/// </summary>
[Flags]
public enum OverlayKind
{
    None = 0,
    /// <summary>TS 'walkability' ("Walk").</summary>
    Walk = 1 << 0,
    /// <summary>TS 'elevation' ("Height").</summary>
    Height = 1 << 1,
    /// <summary>TS 'collision' ("Collision").</summary>
    Collision = 1 << 2,
    /// <summary>TS 'bridges' ("Bridges").</summary>
    Bridges = 1 << 3,
    /// <summary>TS 'connectivity' ("Connect").</summary>
    Connect = 1 << 4,
    /// <summary>TS 'problems' ("Problems"): validation issues first, then the analysis diagnostics.</summary>
    Problems = 1 << 5,
    /// <summary>
    /// TS 'probes' ("Probes"). NOT drawn by this palette (probe actors are scene objects, see the summary on
    /// <see cref="OverlayPalette"/>); present so the UI can keep all seven checkboxes in one flag set.
    /// </summary>
    Probes = 1 << 6,
}

/// <summary>Geometry of one overlay primitive inside a cell, as `drawOverlay` issues it.</summary>
public enum OverlayShape
{
    /// <summary>Filled `rect(x + inset, y + inset, ts - 2*inset, ts - 2*inset)`.</summary>
    Rect,
    /// <summary>Filled `circle(x + ts/2, y + ts/2, ts * radiusFactor)`.</summary>
    Circle,
    /// <summary>Outlined rect (same box as <see cref="Rect"/>), stroke `2 / zoom` world units = 2 screen pixels.</summary>
    RectStroke,
}

/// <summary>One primitive drawn for a cell. Insets are world units at the layout's tile size (62.5 by default).</summary>
public readonly struct OverlayMark
{
    public readonly OverlayKind kind;
    public readonly OverlayShape shape;
    /// <summary>Rect / RectStroke: inset from every cell edge in world units (TS literal pixels: 1, 2, 3, …).</summary>
    public readonly double inset;
    /// <summary>Circle: radius as a fraction of the tile size.</summary>
    public readonly double radiusFactor;
    /// <summary>0xRRGGBB.</summary>
    public readonly int color;
    public readonly float alpha;
    /// <summary>RectStroke: stroke width in SCREEN pixels (TS `width: 2 / this.zoom` world units).</summary>
    public readonly double strokeScreenPx;

    public OverlayMark(OverlayKind kind, OverlayShape shape, double inset, double radiusFactor, int color, float alpha, double strokeScreenPx = 0)
    {
        this.kind = kind;
        this.shape = shape;
        this.inset = inset;
        this.radiusFactor = radiusFactor;
        this.color = color;
        this.alpha = alpha;
        this.strokeScreenPx = strokeScreenPx;
    }
}

/// <summary>Everything `drawOverlay` reads: the compiled layout, its analysis and the per-cell validation mask.</summary>
public sealed class OverlayInputs
{
    public DungeonLayout layout;
    /// <summary>`analyzeTerrainLayout(layout, VALIDATION_OPTIONS)` — see <see cref="OverlayPalette.analyzeForOverlay"/>.</summary>
    public TerrainAnalysis analysis;
    /// <summary>Per cell: 0 none, 1 warning, 2 error (the LAST issue on a cell wins, as in TS).</summary>
    public byte[] issueMask;

    public static OverlayInputs Create(DungeonLayout layout, TerrainAnalysis analysis, TerrainValidationResult? validation)
    {
        return new OverlayInputs
        {
            layout = layout,
            analysis = analysis,
            issueMask = OverlayPalette.buildIssueMask(layout.width, layout.height, validation),
        };
    }
}

/// <summary>
/// Colour logic of the terrain editor's "Checks" overlays (generator.ts `drawOverlay`, ~lines 1572-1661).
///
/// What each overlay shows (TS insets are world px inside a 62.5-unit cell; α = fill alpha):
/// <list type="bullet">
/// <item><b>Walk</b> — every cell, rect inset 1: walkable (Floor/Bridge/Underpass) 0x45e08e α.14; Water 0x2aa8d6 α.22;
///   Chasm 0x241d3a α.22; anything else (Solid, Cleft) 0xff5d65 α.22.</item>
/// <item><b>Height</b> — every cell, rect inset 4, α.20, colour <see cref="elevationOverlayColor"/> of the stored level:
///   levels ≥ 0 sample <see cref="ELEVATION_RAMP"/> over 0..TERRAIN_MAX_ELEVATION (25), negative levels sample
///   <see cref="DEPTH_RAMP"/> over 0..TERRAIN_MIN_ELEVATION (-30).</item>
/// <item><b>Collision</b> — blocksMovement (Solid/Water/Chasm/Cleft): rect inset 2 0xff3456 α.18; then blocksSight
///   (Solid): rect inset 8 0x111111 α.28.</item>
/// <item><b>Bridges</b> — Bridge tile: rect inset 3 bridgeWood.topLight (0xc9bca0) α.34; then analysis bridge-width issue:
///   rect inset 6 0xff2fa3 α.55; then bridge-water issue (span missing / water not safely below the deck): rect inset 4
///   0x00e5ff α.62.</item>
/// <item><b>Connect</b> — walkable cells only, rect inset 5: reachable from the first walkable cell by climbable moves
///   0x43e093 α.16, unreachable 0xff2d55 α.62.</item>
/// <item><b>Problems</b> — first match wins: validation issue on the cell (rect inset 1; error 0xff243f α.48, warning
///   0xffc857 α.34) → steep walkable edge (circle r .28ts 0xff243f α.58) → bridge-water issue (rect inset 4 0x00e5ff
///   α.62) → chasm topology issue (rect inset 4 0xc026ff α.62) → narrow footprint (outline inset 7, 2 screen px,
///   0xffd23f α.80) → cliff edge (circle r .16ts 0x39d7ff α.42).</item>
/// </list>
/// TS drapes every mark at the rendered surface height of its cell (`visibleSurfaceHeightAtCell` = terrain surface
/// under the cell centre + 1.4 px), so the overlay follows terraces, water datums and chasm floors. Default
/// checks on a fresh editor: Problems + Probes; starting a map simulation clears all of them.
///
/// <para><b>Probes</b> (NOT ported — scene actors, summarised here): `drawProbeActors` places up to seven test bodies,
/// one per distinct cell, chosen by `buildProbeSpecs` with `findProbeCell` (row-major scan, strictly-greater score
/// wins, so ties keep the first cell): 9001 Player 0xf2f7ec r18 on the walkable cell closest to the map centre;
/// 9002 Monster 0xd45540 r18 on the first walkable cell; 9003 Player 0x88cfff r18 on a walkable cell whose north
/// neighbour blocks movement (score 400 − centreDist²); 9004 Pet 0x79d66f r15 on the highest raised walkable cell
/// (score level·1000 − centreDist²); 9005 Loot 0xffd468 r11 on the first Bridge cell; 9006 Projectile 0x8de7ff r8
/// on the most central walkable cell with a walkable cardinal neighbour exactly one level apart; 9007 Monster 0xb98bff
/// r18 on the most central walkable cell with any movement-blocking cardinal neighbour (out of bounds = Solid).
/// Each probe is pushed out of walls by `resolveCircleInto(collisionView, p, min(radius, ts·0.45))`, then drawn as a
/// ground shadow ellipse (0x000000 α.24, radius·0.78 × max(4, radius·0.26), offset radius·0.32 down), a stilt line
/// (0x20251c α.36, width 1.2) when the elevation level is &gt; 0, a body disc lifted by level·ELEVATION_STEP_PX
/// (probe colour α.96, 2 px outline 0x1d2318 α.78) and a highlight dot (0xf5ffe8 α.62, radius max(2, radius·0.18),
/// offset −0.26r/−0.28r). The probes are also registered as fake entities so the entity renderer shows real sprites.</para>
/// </summary>
public static class OverlayPalette
{
    /// <summary>Upper bound of <see cref="cellMarks"/> for one cell with every overlay on (1+1+2+3+1+1).</summary>
    public const int MAX_MARKS_PER_CELL = 9;

    /// <summary>
    /// The height overlay's two ramps, read as GRADIENTS across the domain rather than as one entry per level.
    ///
    /// As a per-level table this was silently wrong the moment the world grew: thirteen entries indexed by level
    /// clamped everything above 12 to the same grey, so a +25 summit and a +13 shelf painted identically and the
    /// overlay stopped being a height readout exactly where the new range begins. Sampled instead, the same
    /// authored colours stretch over whatever the domain currently is.
    /// </summary>
    public static readonly IReadOnlyList<int> ELEVATION_RAMP = new[]
    {
        0xeff7d4, 0xdcecab, 0xcbe28c, 0xb9d77e, 0xa9ca76, 0x99bd6e, 0x8fb167, 0x819e61, 0x778f5b,
        0x6b7657, 0x62645c, 0x5b5d61, 0x565967,
    };

    /// <summary>The negative half of the domain — sunken water datums and Chasm throats down to the floor.</summary>
    public static readonly IReadOnlyList<int> DEPTH_RAMP = new[] { 0x4a6f82, 0x395a6b, 0x2a4553, 0x1c313d, 0x101f28 };

    public static int sampleRamp(IReadOnlyList<int> ramp, double t)
    {
        double clamped = t <= 0 ? 0 : t >= 1 ? 1 : t;
        double pos = clamped * (ramp.Count - 1);
        int i = (int)Math.min(ramp.Count - 2, Math.floor(pos));
        double f = pos - i;
        int a = ramp[i];
        int b = ramp[i + 1];
        int mix(int shift) => Js.ToInt32(Math.round(((a >> shift) & 255) * (1 - f) + ((b >> shift) & 255) * f)) & 255;
        return (mix(16) << 16) | (mix(8) << 8) | mix(0);
    }

    /// <summary>Overlay colour for a signed authored level, spanning the complete vertical domain.</summary>
    public static int elevationOverlayColor(double level)
    {
        if (level < 0) return sampleRamp(DEPTH_RAMP, level / TerrainModel.TERRAIN_MIN_ELEVATION);
        return sampleRamp(
            ELEVATION_RAMP,
            TerrainModel.TERRAIN_MAX_ELEVATION > 0 ? level / (double)TerrainModel.TERRAIN_MAX_ELEVATION : 0);
    }

    /// <summary>
    /// TS `TILE_COLORS[tile] ?? 0xffffff` — the `--tile` swatch colour of the editor's block cards, taken from the
    /// shared standard terrain palette (terrainRenderPlan STANDARD_TERRAIN_MATERIALS).
    /// </summary>
    public static int tileCardColor(int tile)
    {
        switch (tile)
        {
            case TileType.Floor:
                return STANDARD.floorCool.top;
            case TileType.Solid:
                return STANDARD.wallChalk.top;
            case TileType.Water:
                return STANDARD.water.mid ?? STANDARD.water.top;
            case TileType.Bridge:
                return STANDARD.bridgeWood.top;
            case TileType.Chasm:
                return STANDARD.chasm.top;
            case TileType.Cleft:
                return STANDARD.wallChalk.edgeDark;
            case TileType.Underpass:
                return STANDARD.wallChalk.side;
            default:
                return 0xffffff;
        }
    }

    /// <summary>TS `issueCoord`: explicit tx/ty first, else the flat index; null when the issue has no cell.</summary>
    public static (int tx, int ty)? issueCoord(TerrainValidationIssue issue, int width)
    {
        if (issue.tx != null && issue.ty != null) return (issue.tx.Value, issue.ty.Value);
        if (issue.index != null)
        {
            int index = issue.index.Value;
            // `index % width`, `Math.floor(index / width)` (floor, not C# truncation, for negative indices).
            return (index % width, (int)Math.floor(index / (double)width));
        }
        return null;
    }

    /// <summary>The first half of `drawOverlay`: 2 for an error, 1 for a warning; a later issue overwrites an earlier one.</summary>
    public static byte[] buildIssueMask(int width, int height, TerrainValidationResult? validation)
    {
        var issueMask = new byte[width * height];
        if (validation == null) return issueMask;
        foreach (var issue in validation.issues)
        {
            var at = issueCoord(issue, width);
            if (at == null) continue;
            var (tx, ty) = at.Value;
            if (!(tx >= 0 && ty >= 0 && tx < width && ty < height)) continue;
            issueMask[ty * width + tx] = (byte)(issue.severity == "error" ? 2 : 1);
        }
        return issueMask;
    }

    /// <summary>
    /// The analysis the editor feeds its overlays: `analyzeTerrainLayout(layout, TERRAIN_EDITOR_VALIDATION_OPTIONS)`
    /// (TS passes the validation options structurally: maxClimb/minimumFootprint 5/minimumBridgeWidth 2).
    /// </summary>
    public static TerrainAnalysis analyzeForOverlay(DungeonLayout layout)
    {
        return Analysis.analyzeTerrainLayout(
            layout,
            Analysis.analysisOptionsFromValidation(TerrainArtifactModule.TERRAIN_EDITOR_VALIDATION_OPTIONS));
    }

    /// <summary>
    /// The exact primitives `drawOverlay` issues for cell (tx, ty) with the given overlays on, in draw order.
    /// Returns how many were written into <paramref name="into"/> (needs <see cref="MAX_MARKS_PER_CELL"/> slots).
    /// </summary>
    public static int cellMarks(OverlayKind kinds, OverlayInputs inputs, int tx, int ty, Span<OverlayMark> into)
    {
        var layout = inputs.layout;
        if (tx < 0 || ty < 0 || tx >= layout.width || ty >= layout.height) return 0;
        var ov = inputs.analysis.overlays;
        int idx = ty * layout.width + tx;
        int tile = layout.tiles[idx];
        int level = layout.elevation != null && idx < layout.elevation.Length ? layout.elevation[idx] : 0;
        int n = 0;
        if ((kinds & OverlayKind.Walk) != 0)
        {
            int color = isWalkable(tile)
                ? 0x45e08e
                : tile == TileType.Water
                    ? 0x2aa8d6
                    : tile == TileType.Chasm
                        ? 0x241d3a
                        : 0xff5d65;
            into[n++] = new OverlayMark(OverlayKind.Walk, OverlayShape.Rect, 1, 0, color, isWalkable(tile) ? 0.14f : 0.22f);
        }
        if ((kinds & OverlayKind.Height) != 0)
        {
            into[n++] = new OverlayMark(OverlayKind.Height, OverlayShape.Rect, 4, 0, elevationOverlayColor(level), 0.2f);
        }
        if ((kinds & OverlayKind.Collision) != 0)
        {
            if (blocksMovement(tile))
                into[n++] = new OverlayMark(OverlayKind.Collision, OverlayShape.Rect, 2, 0, 0xff3456, 0.18f);
            if (blocksSight(tile))
                into[n++] = new OverlayMark(OverlayKind.Collision, OverlayShape.Rect, 8, 0, 0x111111, 0.28f);
        }
        if ((kinds & OverlayKind.Bridges) != 0)
        {
            if (tile == TileType.Bridge)
                into[n++] = new OverlayMark(
                    OverlayKind.Bridges,
                    OverlayShape.Rect,
                    3,
                    0,
                    STANDARD.bridgeWood.topLight,
                    0.34f);
            if (ov.bridgeWidthIssues[idx] != 0)
                into[n++] = new OverlayMark(OverlayKind.Bridges, OverlayShape.Rect, 6, 0, 0xff2fa3, 0.55f);
            if (ov.bridgeWaterIssues[idx] != 0)
                into[n++] = new OverlayMark(OverlayKind.Bridges, OverlayShape.Rect, 4, 0, 0x00e5ff, 0.62f);
        }
        if ((kinds & OverlayKind.Connect) != 0 && isWalkable(tile))
        {
            bool reachable = ov.reachable[idx] != 0;
            into[n++] = new OverlayMark(OverlayKind.Connect, OverlayShape.Rect, 5, 0, reachable ? 0x43e093 : 0xff2d55, reachable ? 0.16f : 0.62f);
        }
        if ((kinds & OverlayKind.Problems) != 0)
        {
            byte issue = idx < inputs.issueMask.Length ? inputs.issueMask[idx] : (byte)0;
            if (issue != 0)
                into[n++] = new OverlayMark(OverlayKind.Problems, OverlayShape.Rect, 1, 0, issue == 2 ? 0xff243f : 0xffc857, issue == 2 ? 0.48f : 0.34f);
            else if (ov.steepEdges[idx] != 0)
                into[n++] = new OverlayMark(OverlayKind.Problems, OverlayShape.Circle, 0, 0.28, 0xff243f, 0.58f);
            else if (ov.bridgeWaterIssues[idx] != 0)
                into[n++] = new OverlayMark(OverlayKind.Problems, OverlayShape.Rect, 4, 0, 0x00e5ff, 0.62f);
            else if (ov.chasmIssues[idx] != 0)
                into[n++] = new OverlayMark(OverlayKind.Problems, OverlayShape.Rect, 4, 0, 0xc026ff, 0.62f);
            else if (ov.narrowFootprints[idx] != 0)
                into[n++] = new OverlayMark(OverlayKind.Problems, OverlayShape.RectStroke, 7, 0, 0xffd23f, 0.8f, 2);
            else if (ov.cliffEdges[idx] != 0)
                into[n++] = new OverlayMark(OverlayKind.Problems, OverlayShape.Circle, 0, 0.16, 0x39d7ff, 0.42f);
        }
        return n;
    }

    // ── colour helpers ────────────────────────────────────────────────────────────────────────────────
}
