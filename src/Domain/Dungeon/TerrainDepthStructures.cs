// Port of packages/shared/src/domain/dungeon/terrainDepthStructures.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.TerrainModel;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Authoring for the two depth-tile structures — **Cleft** (a body-blocking split through a wall) and
// **Underpass** (walkable ground below a suspension-bridge deck).
//
// Both tiles carry a multi-cell topology contract (see `terrainModel.ts`): a Cleft needs two same-level
// walkable sight approaches, two Solid shoulders and 2.8 levels of wall mass; an Underpass needs a 2–6 cell
// span with open equal-level approaches on every span cell and two rooted Solid mountain banks. A single
// tile brush can therefore never author either one — it paints one cell, and one cell is never a structure.
//
// This module is the missing piece: given one target cell it **plans the complete structure**, writes it into
// a scratch grid and proves the result against the very same predicates the validator and the renderer use
// (`terrainCleftProfileAt` / `terrainUnderpassProfileAt`). A plan is therefore valid by construction — an
// authoring UI can preview it, and applying it can never produce the broken instances that used to render as
// a lone wall block (Cleft) or as nothing at all (Underpass).
//
// The rules live in `terrainModel.ts` and are imported, never restated.

/// <summary>`TerrainDepthStructureKind = 'cleft' | 'underpass'`.</summary>
public static class TerrainDepthStructureKind
{
    public const string Cleft = "cleft";
    public const string Underpass = "underpass";
}

/// <summary>What a planned cell contributes to the structure. Authoring UIs colour their preview by this.</summary>
public static class TerrainDepthStructureRole
{
    public const string Feature = "feature";
    public const string Shoulder = "shoulder";
    public const string Bank = "bank";
    public const string Approach = "approach";
}

public sealed class TerrainDepthStructureCell
{
    public int index;
    public int tx;
    public int ty;
    /// <summary>A TileType value.</summary>
    public int tile;
    public int elevation;
    /// <summary>A <see cref="TerrainDepthStructureRole"/> value.</summary>
    public string role = TerrainDepthStructureRole.Feature;
}

public sealed class TerrainDepthStructurePlan
{
    /// <summary>A <see cref="TerrainDepthStructureKind"/> value.</summary>
    public string kind = TerrainDepthStructureKind.Cleft;
    /// <summary>Direction sight (Cleft) or bodies (Underpass) travel through the structure (<see cref="TerrainPassageAxis"/>).</summary>
    public string passageAxis = TerrainPassageAxis.Horizontal;
    /// <summary>Feature cells: 1 for a Cleft, `span` for an Underpass deck.</summary>
    public int span;
    /// <summary>Ground datum of the approaches the structure serves.</summary>
    public int groundLevel;
    /// <summary>Base datum of the rock the structure is cut into / hangs from.</summary>
    public int wallLevel;
    /// <summary>Every cell the placement writes, feature cells first.</summary>
    public IReadOnlyList<TerrainDepthStructureCell> cells = Array.Empty<TerrainDepthStructureCell>();
}

public sealed class TerrainDepthStructureRejection
{
    /// <summary>Author-facing reason the structure cannot stand here.</summary>
    public string reason = "";
}

/// <summary>
/// `{ plan; rejection?: undefined } | { plan?: undefined; rejection }` — exactly one of the two is set.
/// </summary>
public sealed class TerrainDepthStructureResult
{
    public TerrainDepthStructurePlan? plan;
    public TerrainDepthStructureRejection? rejection;
}

public sealed class TerrainDepthStructureOptions
{
    /// <summary>Pin the passage axis instead of reading it from the terrain around the target cell (<see cref="TerrainPassageAxis"/>).</summary>
    public string? passageAxis;
    /// <summary>Underpass only: deck length in cells, clamped into the shared `UNDERPASS_MIN_SPAN..MAX_SPAN` band.</summary>
    public double? span;
}

public static class TerrainDepthStructures
{
    /// <summary>Default deck length. Five is the standard corridor width every Endless passage is cut to.</summary>
    public const int UNDERPASS_DEFAULT_SPAN = 5;

    private readonly struct Cell
    {
        public readonly int tx;
        public readonly int ty;

        public Cell(int tx, int ty)
        {
            this.tx = tx;
            this.ty = ty;
        }
    }

    private readonly struct AxisVectors
    {
        public readonly int alongX;
        public readonly int alongY;
        public readonly int crossX;
        public readonly int crossY;

        public AxisVectors(int alongX, int alongY, int crossX, int crossY)
        {
            this.alongX = alongX;
            this.alongY = alongY;
            this.crossX = crossX;
            this.crossY = crossY;
        }
    }

    /// <summary>`elevation[i] ?? 0` on a typed array: an index outside the layer reads undefined → 0.</summary>
    private static int levelAt(sbyte[] elevation, int index) =>
        (uint)index < (uint)elevation.Length ? elevation[index] : 0;

    /// <summary>
    /// Lowest wall base datum that clears `ground` by the required margin, using only the guaranteed wall rise.
    ///
    /// The validator resolves wall tops without the renderer's skyline variation, so eligibility must be proven
    /// against `TERRAIN_STANDARD_WALL_BASE_RISE` alone — decorative extra rise may only ever add clearance.
    /// </summary>
    private static int wallBaseFor(int ground, double clearance)
    {
        return ground + (int)Math.max(0, Math.ceil(clearance - TERRAIN_STANDARD_WALL_BASE_RISE));
    }

    private static bool inBounds(int width, int height, int tx, int ty)
    {
        return tx >= 0 && ty >= 0 && tx < width && ty < height;
    }

    private static TerrainDepthStructureResult reject(string reason)
    {
        return new TerrainDepthStructureResult { rejection = new TerrainDepthStructureRejection { reason = reason } };
    }

    // `AXIS_VECTORS[passageAxis]` — any other key is undefined in TS and the destructuring throws.
    private static AxisVectors axisVectors(string passageAxis) => passageAxis switch
    {
        TerrainPassageAxis.Horizontal => new AxisVectors(1, 0, 0, 1),
        TerrainPassageAxis.Vertical => new AxisVectors(0, 1, 1, 0),
        _ => throw new InvalidOperationException($"Unknown terrain passage axis \"{passageAxis}\"."),
    };

    private static readonly int[] SIDES = { -1, 1 };

    /// <summary>
    /// Read the passage axis from what already stands around the target cell.
    ///
    /// Walkable neighbours are the strongest signal — they are what the structure has to serve. A wall running
    /// through the cell is the next best: sight crosses a wall, it never travels along it. Absent both, an author
    /// clicking open ground gets the horizontal reading, and can pin the other axis explicitly.
    /// </summary>
    private static string inferPassageAxis(byte[] tiles, int width, int height, int tx, int ty)
    {
        int walkable(int dx, int dy)
        {
            int nx = tx + dx;
            int ny = ty + dy;
            return inBounds(width, height, nx, ny) && isWalkable(tiles[ny * width + nx]) ? 1 : 0;
        }
        int solid(int dx, int dy)
        {
            int nx = tx + dx;
            int ny = ty + dy;
            return inBounds(width, height, nx, ny) && tiles[ny * width + nx] == TileType.Solid ? 1 : 0;
        }
        int horizontalOpen = walkable(-1, 0) + walkable(1, 0);
        int verticalOpen = walkable(0, -1) + walkable(0, 1);
        if (horizontalOpen != verticalOpen)
            return horizontalOpen > verticalOpen ? TerrainPassageAxis.Horizontal : TerrainPassageAxis.Vertical;
        int horizontalWall = solid(-1, 0) + solid(1, 0);
        int verticalWall = solid(0, -1) + solid(0, 1);
        // A wall running north–south is crossed east–west, and vice versa.
        if (horizontalWall != verticalWall)
            return horizontalWall > verticalWall ? TerrainPassageAxis.Vertical : TerrainPassageAxis.Horizontal;
        return TerrainPassageAxis.Horizontal;
    }

    /// <summary>
    /// Ground datum the structure should serve, plus how far apart its approaches currently sit.
    ///
    /// Both roles need their approaches on one level. Bringing them together is the placement's job, but only
    /// within the ordinary climb: an author clicking across a real terrace must be told the ground disagrees, not
    /// have the map quietly re-terraced under a structure they only wanted in one spot.
    /// </summary>
    private static (int level, int spread) approachDatum(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        IReadOnlyList<Cell> cells,
        int alongX,
        int alongY,
        int fallback)
    {
        var levels = new List<int>();
        foreach (var cell in cells)
        {
            foreach (int side in SIDES)
            {
                int nx = cell.tx + alongX * side;
                int ny = cell.ty + alongY * side;
                if (!inBounds(width, height, nx, ny)) continue;
                int index = ny * width + nx;
                if (!isWalkable(tiles[index])) continue;
                levels.push(levelAt(elevation, index));
            }
        }
        if (levels.Count == 0) return (fallback, 0);
        int min = levels.reduce((low, level) => Math.min(low, level), levels[0]);
        int max = levels.reduce((high, level) => Math.max(high, level), levels[0]);
        return (min, max - min);
    }

    /// <summary>Wall datum for a structure cell: raise rock to meet the contract, never cut an authored massif down.</summary>
    private static int wallLevelAt(byte[] tiles, sbyte[] elevation, int width, int index, int required)
    {
        int existing = tiles[index];
        if (existing != TileType.Solid && existing != TileType.Cleft) return required;
        return Math.max(required, levelAt(elevation, index));
    }

    private static TerrainDepthStructureResult planCleft(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        int tx,
        int ty,
        TerrainDepthStructureOptions options)
    {
        string passageAxis = options.passageAxis ?? inferPassageAxis(tiles, width, height, tx, ty);
        var v = axisVectors(passageAxis);
        int alongX = v.alongX, alongY = v.alongY, crossX = v.crossX, crossY = v.crossY;
        int index = ty * width + tx;
        foreach (int side in SIDES)
        {
            if (
                !inBounds(width, height, tx + alongX * side, ty + alongY * side) ||
                !inBounds(width, height, tx + crossX * side, ty + crossY * side))
                return reject("A Cleft needs one cell of margin on all four sides.");
        }

        var datum = approachDatum(
            tiles,
            elevation,
            width,
            height,
            new[] { new Cell(tx, ty) },
            alongX,
            alongY,
            levelAt(elevation, index));
        if (datum.spread > TerrainRules.TERRAIN_STANDARD_MAX_CLIMB)
            return reject(
                $"The two sides of this wall sit {Js.Str(datum.spread)} levels apart — a Cleft needs the same ground on both.");
        int ground = datum.level;
        int requiredWall = wallBaseFor(ground, CLEFT_MIN_WALL_CLEARANCE);
        if (requiredWall > Elevation.MAX_ELEVATION)
            return reject("The approaches sit too high to carry the wall mass a Cleft needs.");

        int wallLevel = wallLevelAt(tiles, elevation, width, index, requiredWall);
        var cells = new List<TerrainDepthStructureCell>
        {
            new() { index = index, tx = tx, ty = ty, tile = TileType.Cleft, elevation = wallLevel, role = TerrainDepthStructureRole.Feature },
        };
        foreach (int side in SIDES)
        {
            int sx = tx + crossX * side;
            int sy = ty + crossY * side;
            int shoulderIndex = sy * width + sx;
            cells.push(new TerrainDepthStructureCell
            {
                index = shoulderIndex,
                tx = sx,
                ty = sy,
                tile = TileType.Solid,
                elevation = wallLevelAt(tiles, elevation, width, shoulderIndex, requiredWall),
                role = TerrainDepthStructureRole.Shoulder,
            });
        }
        foreach (int side in SIDES)
        {
            int ax = tx + alongX * side;
            int ay = ty + alongY * side;
            int approachIndex = ay * width + ax;
            int existing = tiles[approachIndex];
            cells.push(new TerrainDepthStructureCell
            {
                index = approachIndex,
                tx = ax,
                ty = ay,
                // A Bridge or Underpass approach is already walkable and carries its own contract; leave the tile
                // alone and only bring its datum onto the sight line.
                tile = isWalkable(existing) ? existing : TileType.Floor,
                elevation = ground,
                role = TerrainDepthStructureRole.Approach,
            });
        }

        return new TerrainDepthStructureResult
        {
            plan = new TerrainDepthStructurePlan
            {
                kind = TerrainDepthStructureKind.Cleft,
                passageAxis = passageAxis,
                span = 1,
                groundLevel = ground,
                wallLevel = wallLevel,
                cells = cells,
            },
        };
    }

    private static TerrainDepthStructureResult planUnderpass(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        int tx,
        int ty,
        TerrainDepthStructureOptions options)
    {
        string passageAxis = options.passageAxis ?? inferPassageAxis(tiles, width, height, tx, ty);
        var v = axisVectors(passageAxis);
        int alongX = v.alongX, alongY = v.alongY, crossX = v.crossX, crossY = v.crossY;
        // A NaN span stays NaN through Math.round/min/max in TS and leaves the deck empty, which then throws on
        // `deck[0]!.tx`; Js.ToInt32(NaN) is 0 here and throws on the same access.
        int span = Js.ToInt32(Math.max(
            UNDERPASS_MIN_SPAN,
            Math.min(UNDERPASS_MAX_SPAN, Math.round(options.span ?? UNDERPASS_DEFAULT_SPAN))));
        // The clicked cell anchors the deck; the remaining cells grow towards the positive side of the cross axis,
        // so a longer deck extends predictably instead of jumping around under the cursor.
        int back = (span - 1) >> 1;
        var deck = new List<Cell>();
        for (int offset = -back; offset < span - back; offset++)
            deck.push(new Cell(tx + crossX * offset, ty + crossY * offset));

        var bankNegative = new Cell(deck[0].tx - crossX, deck[0].ty - crossY);
        var bankPositive = new Cell(deck[deck.Count - 1].tx + crossX, deck[deck.Count - 1].ty + crossY);
        // Each end is a 3-wide, 2-deep rock shoulder. The shared profile only needs one rooted 2x2 quadrant, while
        // the symmetric authored form makes the mountain-to-mountain silhouette unambiguous from either camera side.
        var bankCells = new List<Cell>(12);
        foreach (int tangent in new[] { -1, 0, 1 })
            foreach (int depth in new[] { 0, 1 })
                bankCells.push(new Cell(
                    bankNegative.tx - crossX * depth + alongX * tangent,
                    bankNegative.ty - crossY * depth + alongY * tangent));
        foreach (int tangent in new[] { -1, 0, 1 })
            foreach (int depth in new[] { 0, 1 })
                bankCells.push(new Cell(
                    bankPositive.tx + crossX * depth + alongX * tangent,
                    bankPositive.ty + crossY * depth + alongY * tangent));
        foreach (var cell in deck.concat(bankCells))
        {
            if (!inBounds(width, height, cell.tx, cell.ty))
                return reject($"A {Js.Str(span)}-cell bridge plus both mountain anchor banks does not fit here.");
        }
        foreach (var cell in deck.concat(new[] { bankNegative, bankPositive }))
        {
            foreach (int side in SIDES)
            {
                if (!inBounds(width, height, cell.tx + alongX * side, cell.ty + alongY * side))
                    return reject("The passage below the bridge needs one open cell on each side.");
            }
        }

        var datum = approachDatum(
            tiles,
            elevation,
            width,
            height,
            deck,
            alongX,
            alongY,
            levelAt(elevation, ty * width + tx));
        if (datum.spread > TerrainRules.TERRAIN_STANDARD_MAX_CLIMB)
            return reject(
                $"The passage under this bridge climbs {Js.Str(datum.spread)} levels — level the ground first, a deck needs one floor.");
        int ground = datum.level;
        int requiredBank = wallBaseFor(ground, UNDERPASS_MIN_BANK_CLEARANCE);
        if (requiredBank > Elevation.MAX_ELEVATION)
            return reject("The passage sits too high to carry anchor banks for a suspension bridge.");

        var cells = deck.map(cell => new TerrainDepthStructureCell
        {
            index = cell.ty * width + cell.tx,
            tx = cell.tx,
            ty = cell.ty,
            tile = TileType.Underpass,
            elevation = ground,
            role = TerrainDepthStructureRole.Feature,
        });
        foreach (var bank in bankCells)
        {
            int bankIndex = bank.ty * width + bank.tx;
            cells.push(new TerrainDepthStructureCell
            {
                index = bankIndex,
                tx = bank.tx,
                ty = bank.ty,
                tile = TileType.Solid,
                elevation = wallLevelAt(tiles, elevation, width, bankIndex, requiredBank),
                role = TerrainDepthStructureRole.Bank,
            });
        }
        foreach (var cell in deck)
        {
            foreach (int side in SIDES)
            {
                int ax = cell.tx + alongX * side;
                int ay = cell.ty + alongY * side;
                int approachIndex = ay * width + ax;
                int existing = tiles[approachIndex];
                cells.push(new TerrainDepthStructureCell
                {
                    index = approachIndex,
                    tx = ax,
                    ty = ay,
                    // Underpass approaches must be walkable and NOT themselves Underpass, so a neighbouring deck is
                    // deliberately taken back to plain floor rather than left to invalidate both structures.
                    tile = isWalkable(existing) && existing != TileType.Underpass ? existing : TileType.Floor,
                    elevation = ground,
                    role = TerrainDepthStructureRole.Approach,
                });
            }
        }

        // `Math.min(...cells.filter(bank).map(elevation))` — always twelve bank cells, never the empty-spread Infinity.
        int wallLevel = int.MaxValue;
        foreach (var cell in cells)
            if (cell.role == TerrainDepthStructureRole.Bank) wallLevel = Math.min(wallLevel, cell.elevation);

        return new TerrainDepthStructureResult
        {
            plan = new TerrainDepthStructurePlan
            {
                kind = TerrainDepthStructureKind.Underpass,
                passageAxis = passageAxis,
                span = span,
                groundLevel = ground,
                // Report the deck's actual carrying datum: the lower of the two banks is what the deck sits on.
                wallLevel = wallLevel,
                cells = cells,
            },
        };
    }

    /// <summary>
    /// Plan a complete depth-tile structure at `tx,ty` and prove it against the shared topology predicates.
    ///
    /// The proof runs on a scratch copy of the grid, so a rejected plan never touches the caller's terrain and a
    /// returned plan is guaranteed to satisfy `terrainCleftProfileAt` / `terrainUnderpassProfileAt` once written.
    /// </summary>
    public static TerrainDepthStructureResult planTerrainDepthStructure(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        string kind,
        int tx,
        int ty,
        TerrainDepthStructureOptions? options = null)
    {
        options ??= new TerrainDepthStructureOptions();
        if (width <= 0 || height <= 0 || tiles.Length < width * height)
            return reject("The map is too small for a depth-tile structure.");
        if (!inBounds(width, height, tx, ty)) return reject("Target cell is outside the map.");

        var planned =
            kind == TerrainDepthStructureKind.Cleft
                ? planCleft(tiles, elevation, width, height, tx, ty, options)
                : planUnderpass(tiles, elevation, width, height, tx, ty, options);
        if (planned.plan == null) return planned;

        // Prove the plan with the same predicates validation and rendering use. Anything that survives this can be
        // written without the author ever meeting a `cleft_structure` / `underpass_structure` error.
        var proofTiles = tiles.slice();
        var proofElevation = elevation.slice();
        foreach (var cell in planned.plan.cells)
        {
            proofTiles[cell.index] = Js.U8(cell.tile);
            // Past the end of a short layer a typed-array store is a no-op.
            if ((uint)cell.index < (uint)proofElevation.Length) proofElevation[cell.index] = Js.I8(cell.elevation);
        }
        foreach (var cell in planned.plan.cells)
        {
            if (cell.role != TerrainDepthStructureRole.Feature) continue;
            object? profile =
                kind == TerrainDepthStructureKind.Cleft
                    ? terrainCleftProfileAt(proofTiles, proofElevation, width, height, cell.tx, cell.ty)
                    : terrainUnderpassProfileAt(proofTiles, proofElevation, width, height, cell.tx, cell.ty);
            if (profile == null)
                return reject(
                    kind == TerrainDepthStructureKind.Cleft
                        ? "No Cleft can stand here: the wall it would split is not one cell thick between two open approaches."
                        : "No suspension bridge can stand here: the passage below it is not open on both sides over its full length.");
        }
        return planned;
    }
}
