// Port of packages/shared/src/domain/dungeon/terrainTerrace.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// **The terrace quantum** — the one rule that states how coarse a step in the ground is allowed to be.
//
// The elevation field is a lattice of integer levels, so every height change is a step. Nothing in the field
// itself stops a step from being one tile wide, and a per-cell smoothing operator (average the neighbours,
// round to the nearest level) reliably produces exactly that: a dither of single-tile treads that reads as
// noise, walks as a staircase, and cannot be authored deliberately. The fix is not a gentler average — a
// gentler average only moves the dither around. The fix is a *quantum*: the smallest tread the terrain may
// have, in tiles, in both axes.
//
// A level region satisfies the quantum when every one of its cells lies inside a fully in-bounds
// `quantum × quantum` window of cells at the same level ("terrace support"). At the default quantum of two,
// that means every tread is at least a connected 2×2 — so a slope becomes a readable flight of steps, a path
// is wide enough for a Flui to walk along rather than up, and an author gets the shape they aimed at.
//
// Two ways to satisfy it, both used here:
//  - **By construction.** An operator that only ever writes whole lattice blocks (see terrainTerraceBlocks)
//    cannot produce an unsupported cell in the map interior at all. This is how the editor's smooth tool
//    works, and why it needs no repair pass over open ground.
//  - **By repair.** Where an operator cannot own a whole block — the map border, or a block that also holds
//    water/rock it must not touch — terraceSupportLevelsAt names the levels that would restore support, and
//    the caller picks the one closest to what it wanted.
//
// The lattice is anchored at the map origin and is NOT chosen per stroke. A per-stroke best-fit phase would
// let two overlapping strokes disagree about where the blocks are and re-cut each other's treads into
// slivers — the very defect this module exists to remove.

/// <summary>One axis of a lattice block: where it starts and how many tiles it spans.</summary>
public sealed class TerraceBlockSpan
{
    public int origin;
    public int extent;
}

/// <summary>The shape a brush footprint has to have to be grouped into blocks — satisfied by <see cref="TerrainBrushCell"/>.</summary>
public sealed class TerraceFootprintCell
{
    public int tx;
    public int ty;
    /// <summary>0..1 blend amount this cell was painted with.</summary>
    public double weight;
}

/// <summary>One lattice block of the terrace grid, as a unit of editing.</summary>
public sealed class TerrainTerraceBlock
{
    /// <summary>Lattice origin (lowest tile coordinate) of the block.</summary>
    public int tx;
    public int ty;
    /// <summary>Extent in tiles — the quantum, or more where the block absorbs the map remainder (<see cref="TerrainTerrace.terraceBlockSpan"/>).</summary>
    public int tw;
    public int th;
    /// <summary>Every cell of the block, in row-major order. All in bounds by construction.</summary>
    public List<int> cells = new();
    /// <summary>Strongest blend amount any footprint cell contributed — see <see cref="TerrainTerrace.terrainTerraceBlocks"/>.</summary>
    public double weight;
}

public static class TerrainTerrace
{
    /// <summary>The default and smallest terrace tread, in tiles: a connected 2×2.</summary>
    public const int TERRAIN_TERRACE_QUANTUM = 2;

    /// <summary>The broadest tread an authoring tool may ask for. Beyond this a "smooth" gesture reads as a plateau stamp.</summary>
    public const int TERRAIN_TERRACE_MAX_QUANTUM = 4;

    /// <summary>`elevation[i] ?? 0` on a typed array: an index outside the layer reads undefined → 0.</summary>
    private static int levelAt(sbyte[] elevation, int index) =>
        (uint)index < (uint)elevation.Length ? elevation[index] : 0;

    /// <summary>Clamp any caller-supplied quantum into the authored band; `undefined` means the default 2×2.</summary>
    public static int normalizedTerraceQuantum(double? quantum = null)
    {
        if (quantum == null || !Number.isFinite(quantum.Value)) return TERRAIN_TERRACE_QUANTUM;
        return (int)Math.max(
            TERRAIN_TERRACE_QUANTUM,
            Math.min(TERRAIN_TERRACE_MAX_QUANTUM, Math.floor(quantum.Value)));
    }

    /// <summary>
    /// The lattice block a coordinate belongs to, along one axis.
    ///
    /// Blocks are anchored at the map origin and are `quantum` tiles wide — except the last one, which ABSORBS the
    /// remainder of a map that is not a whole number of blocks across (a 9-tile axis at quantum 2 gives blocks of
    /// 2, 2, 2 and 3 tiles, never a 1-tile sliver at the rim). A clipped block could not satisfy the quantum it
    /// exists to enforce, so it is not allowed to exist. An axis shorter than the quantum has no valid terrace at
    /// all and yields the whole axis as one block.
    /// </summary>
    public static TerraceBlockSpan terraceBlockSpan(int coord, int size, double quantum = TERRAIN_TERRACE_QUANTUM)
    {
        int q = normalizedTerraceQuantum(quantum);
        int blocks = (int)Math.max(1, Math.floor((double)size / q));
        int index = (int)Math.max(0, Math.min(blocks - 1, Math.floor((double)coord / q)));
        int origin = index * q;
        return new TerraceBlockSpan { origin = origin, extent = index == blocks - 1 ? Math.max(0, size - origin) : q };
    }

    /// <summary>
    /// Group a brush footprint into whole terrace blocks.
    ///
    /// A block joins the result as soon as the footprint touches ONE of its cells, and it then carries ALL of its
    /// cells: an operator that writes one level per block keeps the quantum by construction, which it could not do
    /// if the footprint were allowed to cut a block in half. The visible consequence — the tool reaching up to a
    /// block past the drawn circle — is why authoring cursors draw these cells rather than the raw brush footprint.
    ///
    /// The block weight is the STRONGEST weight the footprint contributed, not the mean: a size-1 brush covers one
    /// cell of one block and must still smooth that block fully, while a soft brush keeps its falloff because the
    /// rim cells it does cover are themselves weak.
    /// </summary>
    public static List<TerrainTerraceBlock> terrainTerraceBlocks(
        int width,
        int height,
        IEnumerable<TerraceFootprintCell> footprint,
        double quantum = TERRAIN_TERRACE_QUANTUM)
    {
        int q = normalizedTerraceQuantum(quantum);
        var blocks = new JsMap<int, TerrainTerraceBlock>();
        foreach (var cell in footprint)
        {
            if (!inBounds(width, height, cell.tx, cell.ty)) continue;
            var spanX = terraceBlockSpan(cell.tx, width, q);
            var spanY = terraceBlockSpan(cell.ty, height, q);
            int key = spanY.origin * width + spanX.origin;
            double weight = Math.max(0, Math.min(1, cell.weight));
            var existing = blocks.get(key);
            if (existing != null)
            {
                if (weight > existing.weight) existing.weight = weight;
                continue;
            }
            var cells = new List<int>();
            for (int ty = spanY.origin; ty < spanY.origin + spanY.extent; ty++)
                for (int tx = spanX.origin; tx < spanX.origin + spanX.extent; tx++)
                    cells.push(tileIndex(width, tx, ty));
            if (cells.Count > 0)
                blocks.set(key, new TerrainTerraceBlock
                {
                    tx = spanX.origin,
                    ty = spanY.origin,
                    tw = spanX.extent,
                    th = spanY.extent,
                    cells = cells,
                    weight = weight,
                });
        }
        var result = new List<TerrainTerraceBlock>(blocks.size);
        foreach (var block in blocks.values()) result.Add(block);
        // `a.ty - b.ty || a.tx - b.tx`
        return result.sort((a, b) => a.ty != b.ty ? a.ty - b.ty : a.tx - b.tx);
    }

    /// <summary>
    /// Does this cell sit inside a fully in-bounds `quantum × quantum` window of cells at its own level?
    ///
    /// Support is read from the elevation field alone, across every tile type: what makes a step read as a step is
    /// the height it stands at, not whether a Flui may walk on it.
    /// </summary>
    public static bool hasTerraceSupportAt(
        sbyte[] elevation,
        int width,
        int height,
        int tx,
        int ty,
        double quantum = TERRAIN_TERRACE_QUANTUM)
    {
        int q = normalizedTerraceQuantum(quantum);
        if (!inBounds(width, height, tx, ty)) return false;
        int level = levelAt(elevation, tileIndex(width, tx, ty));
        for (int oy = ty - q + 1; oy <= ty; oy++)
        {
            if (oy < 0 || oy + q > height) continue;
            for (int ox = tx - q + 1; ox <= tx; ox++)
            {
                if (ox < 0 || ox + q > width) continue;
                bool uniform = true;
                for (int y = oy; y < oy + q && uniform; y++)
                {
                    for (int x = ox; x < ox + q; x++)
                    {
                        if (levelAt(elevation, tileIndex(width, x, y)) != level)
                        {
                            uniform = false;
                            break;
                        }
                    }
                }
                if (uniform) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Levels this cell could take to become terrace-supported *without moving any other cell* — every level whose
    /// window is already uniform apart from this one cell. Ascending, deduplicated; empty when the cell's
    /// surroundings are so broken that support needs more than one move (the caller then keeps what it had).
    /// </summary>
    public static List<int> terraceSupportLevelsAt(
        sbyte[] elevation,
        int width,
        int height,
        int tx,
        int ty,
        double quantum = TERRAIN_TERRACE_QUANTUM)
    {
        int q = normalizedTerraceQuantum(quantum);
        if (!inBounds(width, height, tx, ty)) return new List<int>();
        int self = tileIndex(width, tx, ty);
        var levels = new JsSet<int>();
        for (int oy = ty - q + 1; oy <= ty; oy++)
        {
            if (oy < 0 || oy + q > height) continue;
            for (int ox = tx - q + 1; ox <= tx; ox++)
            {
                if (ox < 0 || ox + q > width) continue;
                int? level = null;
                bool uniform = true;
                for (int y = oy; y < oy + q && uniform; y++)
                {
                    for (int x = ox; x < ox + q; x++)
                    {
                        int index = tileIndex(width, x, y);
                        if (index == self) continue;
                        int other = levelAt(elevation, index);
                        if (level == null) level = other;
                        else if (other != level.Value)
                        {
                            uniform = false;
                            break;
                        }
                    }
                }
                if (uniform && level != null) levels.add(level.Value);
            }
        }
        return levels.ToList().sort((a, b) => a - b);
    }
}
