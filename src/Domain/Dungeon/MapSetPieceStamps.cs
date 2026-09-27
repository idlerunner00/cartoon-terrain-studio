// Port of packages/shared/src/domain/dungeon/mapSetPieceStamps.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * The **wonder stamping machine**: one implementation per MapWonderForm, reused by every registry entry that
 * asks for that form.
 *
 * This is the "mechanics are a shared platform, identity is data" rule applied to world composition. A new
 * wonder in the catalog (mapSetPieces) picks a form here and dresses it differently; it never brings its own
 * carving code. Everything is written through WonderCanvas, which owns the guards (map rim, the heart's plaza,
 * cells another wonder already claimed), so no stamp can quietly break an invariant the composer depends on.
 *
 * Every stamp is legal by construction on the two rules that matter:
 *  - a walkable→walkable edge never steps more than one level (stepped forms climb one level per ring), and
 *  - a stamp never seals its own interior away (rings carry gaps, courts carry doors, mazes are mazes).
 */

/// <summary>The working world a stamp is allowed to write into, plus the guards it must respect.</summary>
public sealed class WonderCanvas
{
    public int width;
    public int height;
    public byte[] tiles = Array.Empty<byte>();
    public sbyte[] elevation = Array.Empty<sbyte>();
    /// <summary>
    /// Dry ground level per cell, independent of what the cell currently is. The TS type is the union
    /// `TerrainElevationLayer | Uint8Array`, so this holds either an <c>sbyte[]</c> or a <c>byte[]</c>.
    /// </summary>
    public Array ground = Array.Empty<sbyte>();
    /// <summary>Cells later passes must never wall off, flood or tear open. Stamps set this on what they build.</summary>
    public byte[] protect = Array.Empty<byte>();
    /// <summary>Cells owned by a wonder — dressing passes leave them alone.</summary>
    public byte[] claim = Array.Empty<byte>();
    public int maxLevel;
    public int? minLevel;
    public double seed;
    /// <summary>False for the map rim and the heart's plaza: the two places composition may never touch.</summary>
    public Func<int, int, bool> canWrite = (tx, ty) => true;
}

/// <summary>Where a wonder stands, and how it is oriented if its form has a direction.</summary>
public sealed class WonderSite
{
    public int tx;
    public int ty;
    public double radius;
    /// <summary>Unit facing for oriented forms (causeway, gatehouse, switchback).</summary>
    public double dx;
    public double dy;
    /// <summary>Half-length of the gap an oriented form spans, in cells.</summary>
    public double span;
}

public sealed class WonderStamp
{
    public List<TerrainDecorationPlacement> decorations = new();
    /// <summary>True when the stamp actually wrote something worth calling a wonder.</summary>
    public bool built;
}

public static partial class MapSetPieceStamps
{
    private static readonly (int, int)[] NEIGHBOURS4 =
    {
        (1, 0),
        (-1, 0),
        (0, 1),
        (0, -1),
    };

    /// <summary>Stamp one wonder. The only public entry point; the composer never calls a form routine directly.</summary>
    public static WonderStamp stampMapWonder(WonderCanvas canvas, MapWonderDef def, WonderSite site, Rng rng)
    {
        var @out = new WonderStamp { decorations = new List<TerrainDecorationPlacement>(), built = false };
        var ctx = new StampContext(canvas, def, site, rng, @out);
        switch (def.form)
        {
            case MapWonderForm.Crater:
                ctx.crater();
                break;
            case MapWonderForm.Amphitheatre:
                ctx.bowl(false);
                break;
            case MapWonderForm.Sinkhole:
                ctx.bowl(true);
                break;
            case MapWonderForm.Ziggurat:
                ctx.ziggurat();
                break;
            case MapWonderForm.Causeway:
                ctx.causeway();
                break;
            case MapWonderForm.RuinField:
                ctx.ruinField();
                break;
            case MapWonderForm.LabyrinthCourt:
                ctx.labyrinthCourt();
                break;
            case MapWonderForm.Quarry:
                ctx.quarry();
                break;
            case MapWonderForm.Spire:
                ctx.spire();
                break;
            case MapWonderForm.Springs:
                ctx.springs();
                break;
            case MapWonderForm.SacredGrove:
                ctx.sacredGrove();
                break;
            case MapWonderForm.Gatehouse:
                ctx.gatehouse();
                break;
            case MapWonderForm.Switchback:
                ctx.switchback();
                break;
            case MapWonderForm.Observatory:
                ctx.observatory();
                break;
            default:
                break;
        }
        ctx.dress();
        // `{ ...out, built }`: a new record sharing the same decoration list.
        return new WonderStamp { decorations = @out.decorations, built = ctx.wrote > 0 };
    }

    /// <summary>
    /// One stamp in progress. It carries the tiny primitive set every form is built from — a level disc, a ring
    /// walk, a wall stub — so the forms read as compositions rather than as fifteen private rasterizers.
    /// </summary>
    private sealed class StampContext
    {
        public readonly WonderCanvas canvas;
        public readonly MapWonderDef def;
        public readonly WonderSite site;
        public readonly Rng rng;
        public readonly WonderStamp @out;
        public int wrote = 0;
        /// <summary>Base ground level the wonder is built from — the median of the ground it stands on.</summary>
        public readonly double @base;
        /// <summary>Interior cells the stamp opened, used for fill dressing.</summary>
        public readonly List<int> interior = new();
        /// <summary>Rim cells the stamp drew, used for rim dressing (already in ring order).</summary>
        public readonly List<int> rim = new();

        public StampContext(WonderCanvas canvas, MapWonderDef def, WonderSite site, Rng rng, WonderStamp @out)
        {
            this.canvas = canvas;
            this.def = def;
            this.site = site;
            this.rng = rng;
            this.@out = @out;
            this.@base = this.medianGround();
        }

        // -----------------------------------------------------------------------------------------------
        // Primitives

        private int index(int tx, int ty)
        {
            return tileIndex(this.canvas.width, tx, ty);
        }

        private bool writable(int tx, int ty)
        {
            if (!inBounds(this.canvas.width, this.canvas.height, tx, ty)) return false;
            if (!this.canvas.canWrite(tx, ty)) return false;
            return true;
        }

        /// <summary>`ground[index]` over the TS union `TerrainElevationLayer | Uint8Array`.</summary>
        private double groundAt(int index)
        {
            return this.canvas.ground is sbyte[] signedGround ? signedGround[index] : ((byte[])this.canvas.ground)[index];
        }

        /// <summary>`ground[index] = value` with the store conversion of whichever typed array the canvas carries.</summary>
        private void setGround(int index, double value)
        {
            if (this.canvas.ground is sbyte[] signedGround) signedGround[index] = Js.I8(value);
            else ((byte[])this.canvas.ground)[index] = Js.U8(value);
        }

        /// <summary>`tiles[index]`, with JS `undefined` (never walkable) outside the array.</summary>
        private int tileAt(int index)
        {
            return (uint)index < (uint)this.canvas.tiles.Length ? this.canvas.tiles[index] : -1;
        }

        /// <summary>Set one cell. Every write in this file goes through here, so the guards can never be bypassed.</summary>
        private bool put(int tx, int ty, int tile, double level, bool claim = true)
        {
            if (!this.writable(tx, ty)) return false;
            int index = this.index(tx, ty);
            this.canvas.tiles[index] = (byte)tile;
            this.canvas.elevation[index] = Js.I8(clampLevel(level, this.canvas.maxLevel, this.canvas.minLevel));
            if (isWalkable(tile))
                this.setGround(index, clampLevel(level, this.canvas.maxLevel, this.canvas.minLevel));
            if (claim) this.canvas.claim[index] = 1;
            if (isWalkable(tile)) this.canvas.protect[index] = 1;
            this.wrote++;
            return true;
        }

        /// <summary>Median ground under the footprint, so a wonder sits on the land instead of hovering over its own datum.</summary>
        private double medianGround()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            int r = (int)Math.max(1, Math.round(radius));
            var samples = new List<double>();
            for (int dy = -r; dy <= r; dy += Math.max(1, r >> 2))
            {
                for (int dx = -r; dx <= r; dx += Math.max(1, r >> 2))
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(this.canvas.width, this.canvas.height, nx, ny)) continue;
                    samples.push(this.groundAt(this.index(nx, ny)));
                }
            }
            if (samples.Count == 0) return 3;
            samples.sort((a, b) => a - b);
            return samples[samples.Count >> 1];
        }

        /// <summary>Ragged radius at an angle — every circular form breathes by the entry's own `ruin`.</summary>
        private double wobble(double tx, double ty)
        {
            return (latticeHash(this.canvas.seed, tx, ty) - 0.5) * this.def.ruin * 2.2;
        }

        /// <summary>Fill a disc at one level. Returns the cells written, in raster order.</summary>
        private List<int> disc(int cx, int cy, double radius, int tile, double level)
        {
            var cells = new List<int>();
            int r = (int)Math.ceil(radius) + 1;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    double edge = radius + this.wobble(cx + dx, cy + dy);
                    if (dx * dx + dy * dy > edge * edge) continue;
                    if (!this.put(cx + dx, cy + dy, tile, level)) continue;
                    cells.push(this.index(cx + dx, cy + dy));
                }
            }
            return cells;
        }

        /// <summary>Walk a ring in angular order, calling back with each cell. Ring order IS reveal order.</summary>
        private void ring(int cx, int cy, double radius, Action<int, int, double> visit)
        {
            double steps = Math.max(8, Math.round(radius * 6.5));
            double lastX = double.NaN;
            double lastY = double.NaN;
            for (int i = 0; i < steps; i++)
            {
                double angle = (i / steps) * Math.PI * 2;
                double r = radius + this.wobble(Math.round(cx + angle * 7), Math.round(cy + angle * 11));
                double tx = Math.round(cx + Math.cos(angle) * r);
                double ty = Math.round(cy + Math.sin(angle) * r);
                if (tx == lastX && ty == lastY) continue;
                lastX = tx;
                lastY = ty;
                visit((int)tx, (int)ty, angle);
            }
        }

        /// <summary>Square ring (ziggurat/observatory terraces) at Chebyshev radius `r`.</summary>
        private void squareRing(int cx, int cy, int r, Action<int, int> visit)
        {
            for (int d = -r; d <= r; d++)
            {
                visit(cx + d, cy - r);
                visit(cx + d, cy + r);
                visit(cx - r, cy + d);
                visit(cx + r, cy + d);
            }
        }

        /// <summary>A short wall stub, the ruin grammar's only brick.</summary>
        private void wallStub(int tx, int ty, int dx, int dy, int length)
        {
            for (int i = 0; i < length; i++)
            {
                this.put(tx + dx * i, ty + dy * i, TileType.Solid, this.@base + 2, true);
            }
        }

        private void addDecoration(int index, string kind)
        {
            int width = this.canvas.width;
            int tx = index % width;
            int ty = index / width;
            this.@out.decorations.push(new TerrainDecorationPlacement
            {
                kind = kind,
                tx = tx,
                ty = ty,
                seed = placementSeed(this.canvas.seed, tx, ty, 0x5a1d),
            });
        }

        // -----------------------------------------------------------------------------------------------
        // Forms

        /// <summary>Ring rampart around a sunken bowl, with two gaps so the bowl is never sealed.</summary>
        public void crater()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            string core = this.def.core ?? MapWonderCore.Ground;
            double inner = radius * 0.74;
            double floorLevel = clampLevel(this.@base - 1, this.canvas.maxLevel, this.canvas.minLevel);
            // Two opposed gaps, rolled once so the entrances are part of the wonder's identity.
            double gapA = this.rng.range(0, Math.PI * 2);
            double gapB = gapA + Math.PI * this.rng.range(0.7, 1.3);
            const double gapHalf = 0.24;

            // The bowl first: everything inside is one level below the surrounding land.
            this.interior.AddRange(this.disc(tx, ty, inner, TileType.Floor, floorLevel));
            if (core != MapWonderCore.Ground)
            {
                int coreTile = core == MapWonderCore.Water ? TileType.Water : TileType.Chasm;
                // A hazard core keeps a walkable shelf so the bowl is still a place you can stand in.
                this.disc(tx, ty, inner * 0.58, coreTile, floorLevel);
            }
            // Then the rampart, at a full cliff above the bowl.
            this.ring(tx, ty, radius, (rx, ry, angle) =>
            {
                if (angularNear(angle, gapA, gapHalf) || angularNear(angle, gapB, gapHalf))
                {
                    // A gap is a ramp: it steps the one level from the outside land down onto the bowl floor.
                    this.put(rx, ry, TileType.Floor, this.@base);
                    return;
                }
                if (this.put(rx, ry, TileType.Solid, this.@base + 3)) this.rim.push(this.index(rx, ry));
            });
        }

        /// <summary>
        /// Concentric walkable terraces descending to a floor — an arena you walk down into.
        ///
        /// Each ring drops exactly one level, so the whole form is legal without a single wall, which is precisely
        /// what makes it read as seating rather than as a hole.
        /// </summary>
        public void bowl(bool hazardCore)
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            int steps = Math.max(2, this.def.steps ?? 3);
            for (int step = 0; step <= steps; step++)
            {
                double r = radius * (1 - (double)step / (steps + 1));
                double level = clampLevel(this.@base - step, this.canvas.maxLevel, this.canvas.minLevel);
                List<int> cells = this.disc(tx, ty, r, TileType.Floor, level);
                if (step == 0) this.rim.AddRange(cells.filter((_, i) => i % 5 == 0));
                if (step == steps) this.interior.AddRange(cells);
            }
            if (hazardCore)
            {
                double coreLevel = clampLevel(this.@base - steps, this.canvas.maxLevel, this.canvas.minLevel);
                this.disc(tx, ty, radius * 0.22, TileType.Chasm, coreLevel);
            }
        }

        /// <summary>A square stepped pyramid: every ring climbs one level to a summit plaza.</summary>
        public void ziggurat()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            int steps = Math.max(2, this.def.steps ?? 4);
            int r0 = (int)Math.max(3, Math.round(radius));
            for (int step = 0; step <= steps; step++)
            {
                int r = (int)Math.max(1, Math.round(r0 * (1 - (double)step / (steps + 1))));
                double level = clampLevel(this.@base + step, this.canvas.maxLevel, this.canvas.minLevel);
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        this.put(tx + dx, ty + dy, TileType.Floor, level);
                    }
                }
                if (step == 0) this.squareRing(tx, ty, r, (px, py) => this.pushRim(px, py));
                if (step == steps)
                {
                    for (int dy = -r; dy <= r; dy++)
                        for (int dx = -r; dx <= r; dx++) this.pushInterior(tx + dx, ty + dy);
                }
            }
            int summit = (int)Math.max(1, Math.round(r0 * (1 - (double)steps / (steps + 1))));
        }

        /// <summary>
        /// A raised, dead-straight causeway across whatever the site found worth spanning.
        ///
        /// The deck is three cells thick and lands on real ground at both ends, which is exactly the bridge
        /// contract; the approaches are levelled so the crossing reads as engineered rather than as a fallen log.
        /// </summary>
        public void causeway()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double dx = this.site.dx;
            double dy = this.site.dy;
            double span = this.site.span;
            int ax = dx >= 0.5 ? 1 : dx <= -0.5 ? -1 : 0;
            int ay = ax == 0 ? (dy >= 0 ? 1 : -1) : 0;
            int px = ay;
            int py = ax;
            int half = (int)Math.max(3, Math.round(span));
            double level = this.@base;
            for (int s = -half - 3; s <= half + 3; s++)
            {
                for (int k = -1; k <= 1; k++)
                {
                    int cx = tx + ax * s + px * k;
                    int cy = ty + ay * s + py * k;
                    if (!this.writable(cx, cy)) continue;
                    int current = this.canvas.tiles[this.index(cx, cy)];
                    bool spanning = current == TileType.Water || current == TileType.Chasm;
                    this.put(cx, cy, spanning ? TileType.Bridge : TileType.Floor, level);
                    if (k == 0 && Math.abs(s) % 4 == 0) this.rim.push(this.index(cx, cy));
                }
            }
        }

        /// <summary>A city that is not there any more: wall stubs on a broken grid, with rubble between them.</summary>
        public void ruinField()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            int r = (int)Math.max(4, Math.round(radius));
            int pitch = (int)Math.max(3, Math.round(r / 2.6));
            double level = this.@base;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    if (this.put(tx + dx, ty + dy, TileType.Floor, level)) this.pushInterior(tx + dx, ty + dy);
                }
            }
            for (int gy = -r; gy <= r; gy += pitch)
            {
                for (int gx = -r; gx <= r; gx += pitch)
                {
                    if (gx * gx + gy * gy > r * r) continue;
                    double roll = latticeHash(Js.ToInt32(this.canvas.seed) ^ 0x2f1b, tx + gx, ty + gy);
                    if (roll < 0.28) continue;
                    bool horizontal = roll < 0.64;
                    int length = 2 + ((int)Math.floor(roll * 10) % Math.max(2, pitch));
                    this.wallStub(tx + gx, ty + gy, horizontal ? 1 : 0, horizontal ? 0 : 1, length);
                }
            }
        }

        /// <summary>
        /// A walled court carrying a real maze.
        ///
        /// Randomized-DFS on the odd lattice, which is a spanning tree by construction — the court is therefore
        /// always fully connected to its single door, and the composer's connectivity repair never has to touch it.
        /// </summary>
        public void labyrinthCourt()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            int half = (int)Math.max(4, Math.round(radius));
            double level = this.@base;
            int size = half * 2 + 1;
            // Fill the court solid, then carve the maze out of it.
            for (int dy = -half; dy <= half; dy++)
                for (int dx = -half; dx <= half; dx++) this.put(tx + dx, ty + dy, TileType.Solid, level + 2);

            int cols = Math.max(2, (size - 1) >> 1);
            int rows = Math.max(2, (size - 1) >> 1);
            var visited = new byte[cols * rows];
            var stack = new List<int> { 0 };
            visited[0] = 1;
            int cellX(int c) => tx - half + 1 + (c % cols) * 2;
            int cellY(int c) => ty - half + 1 + (c / cols) * 2;
            this.put(cellX(0), cellY(0), TileType.Floor, level);
            int guard = cols * rows * 8;
            while (stack.Count > 0 && guard-- > 0)
            {
                int current = stack[stack.Count - 1];
                int cx = current % cols;
                int cy = current / cols;
                List<(int, int)> order = this.rng.shuffle(NEIGHBOURS4);
                bool advanced = false;
                foreach (var (dx, dy) in order)
                {
                    int nx = cx + dx;
                    int ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                    int next = ny * cols + nx;
                    if (visited[next] != 0) continue;
                    visited[next] = 1;
                    // Knock out the wall between the two lattice cells, then the cell itself.
                    this.put(cellX(current) + dx, cellY(current) + dy, TileType.Floor, level);
                    this.put(cellX(next), cellY(next), TileType.Floor, level);
                    stack.push(next);
                    advanced = true;
                    break;
                }
                if (!advanced) stack.pop();
            }
            // One door, on a rolled side, so the court reads as entered rather than as found open.
            int side = (int)this.rng.@int(0, 3);
            int doorX = side == 0 ? tx : side == 1 ? tx + half : side == 2 ? tx : tx - half;
            int doorY = side == 0 ? ty - half : side == 1 ? ty : side == 2 ? ty + half : ty;
            for (int k = -1; k <= 1; k++)
            {
                this.put(
                    doorX + (side % 2 == 0 ? k : 0),
                    doorY + (side % 2 == 1 ? k : 0),
                    TileType.Floor,
                    level);
            }
            this.rim.push(this.index(doorX, doorY));
            this.pushInterior(tx, ty);
        }

        /// <summary>A stepped rock pit with spoil heaps on its lip.</summary>
        public void quarry()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            int steps = Math.max(2, this.def.steps ?? 3);
            for (int step = 0; step <= steps; step++)
            {
                double r = radius * (1 - step / (steps + 1.2));
                double level = clampLevel(this.@base - step, this.canvas.maxLevel, this.canvas.minLevel);
                List<int> cells = this.disc(tx, ty, r, TileType.Floor, level);
                if (step == steps) this.interior.AddRange(cells);
            }
            // Spoil: small rock heaps just outside the lip, on the side the work came from.
            double heaps = 3 + Math.floor(this.def.ruin * 5);
            for (int i = 0; i < heaps; i++)
            {
                double angle = this.rng.range(0, Math.PI * 2);
                int hx = (int)Math.round(tx + Math.cos(angle) * radius * this.rng.range(1.08, 1.32));
                int hy = (int)Math.round(ty + Math.sin(angle) * radius * this.rng.range(1.08, 1.32));
                this.disc(hx, hy, this.rng.range(1.1, 2.3), TileType.Solid, this.@base + 2);
                this.pushRim(hx, hy);
            }
        }

        /// <summary>One monumental rock tower on a cleared apron.</summary>
        public void spire()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            this.disc(tx, ty, radius, TileType.Floor, this.@base);
            double coreR = Math.max(1.4, radius * 0.34);
            this.disc(tx, ty, coreR, TileType.Solid, this.canvas.maxLevel);
            this.disc(
                tx,
                ty,
                coreR * 1.55,
                TileType.Solid,
                clampLevel(this.@base + 4, this.canvas.maxLevel, this.canvas.minLevel));
            this.ring(tx, ty, radius * 0.8, (rx, ry, _) => this.pushRim(rx, ry));
        }

        /// <summary>A ring of small water pockets, each with its own bank.</summary>
        public void springs()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            this.interior.AddRange(this.disc(tx, ty, radius, TileType.Floor, this.@base));
            int pools = 4 + (int)this.rng.@int(0, 3);
            for (int i = 0; i < pools; i++)
            {
                double angle = ((double)i / pools) * Math.PI * 2 + this.rng.range(-0.3, 0.3);
                double rr = radius * this.rng.range(0.38, 0.72);
                int px = (int)Math.round(tx + Math.cos(angle) * rr);
                int py = (int)Math.round(ty + Math.sin(angle) * rr);
                this.disc(
                    px,
                    py,
                    this.rng.range(1.1, 2.2),
                    TileType.Water,
                    clampLevel(this.@base - 1, this.canvas.maxLevel, this.canvas.minLevel));
                this.pushRim(px + 2, py);
            }
        }

        /// <summary>A canopy with a hard edge and a clearing at its heart. The trees are dressing; the shape is the ground.</summary>
        public void sacredGrove()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            this.interior.AddRange(this.disc(tx, ty, radius, TileType.Floor, this.@base));
            // The clearing: a smaller, empty disc the fill dressing must not cover.
            List<int> clearing = this.disc(tx, ty, radius * 0.3, TileType.Floor, this.@base);
            var cleared = new HashSet<int>(clearing);
            for (int i = this.interior.Count - 1; i >= 0; i--)
            {
                if (cleared.Contains(this.interior[i])) this.interior.splice(i, 1);
            }
            this.ring(tx, ty, radius * 0.98, (rx, ry, _) => this.pushRim(rx, ry));
        }

        /// <summary>Two towers facing each other across an open threshold.</summary>
        public void gatehouse()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double dx = this.site.dx;
            double dy = this.site.dy;
            double radius = this.site.radius;
            int ax = Math.abs(dx) >= Math.abs(dy) ? signOrOne(dx) : 0;
            int ay = ax == 0 ? signOrOne(dy) : 0;
            int px = ay;
            int py = ax;
            int reach = (int)Math.max(3, Math.round(radius));
            double level = this.@base;
            // The threshold: a levelled lane between the towers.
            for (int s = -reach; s <= reach; s++)
                for (int k = -2; k <= 2; k++)
                    this.put(tx + ax * s + px * k, ty + ay * s + py * k, TileType.Floor, level);
            foreach (int side in new[] { -1, 1 })
            {
                int cx = tx + px * side * (reach + 1);
                int cy = ty + py * side * (reach + 1);
                this.disc(
                    cx,
                    cy,
                    2.2,
                    TileType.Solid,
                    clampLevel(level + 4, this.canvas.maxLevel, this.canvas.minLevel));
                this.pushRim(cx - px * side, cy - py * side);
            }
        }

        /// <summary>A staircase climbing a slope in switchbacks: level runs joined by single-level risers.</summary>
        public void switchback()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double dx = this.site.dx;
            double dy = this.site.dy;
            double radius = this.site.radius;
            int steps = Math.max(3, this.def.steps ?? 4);
            int runLength = (int)Math.max(4, Math.round(radius));
            int ax = Math.abs(dx) >= Math.abs(dy) ? signOrOne(dx) : 0;
            int ay = ax == 0 ? signOrOne(dy) : 0;
            int px = ay;
            int py = ax;
            double level = this.@base;
            for (int step = 0; step < steps; step++)
            {
                int direction = step % 2 == 0 ? 1 : -1;
                for (int s = -runLength; s <= runLength; s++)
                {
                    int along = s * direction;
                    for (int k = 0; k < 3; k++)
                    {
                        int cx = tx + px * along + ax * (step * 3 + k);
                        int cy = ty + py * along + ay * (step * 3 + k);
                        this.put(
                            cx,
                            cy,
                            TileType.Floor,
                            clampLevel(level, this.canvas.maxLevel, this.canvas.minLevel));
                    }
                }
                this.pushRim(tx + px * runLength * direction, ty + py * runLength * direction);
                level += 1;
            }
        }

        /// <summary>Concentric rings of low wall and standing stone around a raised reading floor.</summary>
        public void observatory()
        {
            int tx = this.site.tx;
            int ty = this.site.ty;
            double radius = this.site.radius;
            int steps = Math.max(1, this.def.steps ?? 2);
            for (int step = 0; step <= steps; step++)
            {
                double r = radius * (1 - step / (steps + 1.3));
                this.disc(
                    tx,
                    ty,
                    r,
                    TileType.Floor,
                    clampLevel(this.@base + step, this.canvas.maxLevel, this.canvas.minLevel));
            }
            this.ring(tx, ty, radius * 0.9, (rx, ry, angle) =>
            {
                // Every third stone stands on the ring; the gaps are the sightlines.
                if (Math.round(angle * 6) % 2 == 0) this.pushRim(rx, ry);
            });
        }

        // -----------------------------------------------------------------------------------------------

        private void pushRim(int tx, int ty)
        {
            if (!inBounds(this.canvas.width, this.canvas.height, tx, ty)) return;
            this.rim.push(this.index(tx, ty));
        }

        private void pushInterior(int tx, int ty)
        {
            if (!inBounds(this.canvas.width, this.canvas.height, tx, ty)) return;
            this.interior.push(this.index(tx, ty));
        }

        /// <summary>Apply the entry's dressing recipe over whatever the form actually built.</summary>
        public void dress()
        {
            MapWonderDressing dressing = this.def.dressing;
            var used = new HashSet<int>();
            void place(int index, string? kind)
            {
                if (kind == null || used.Contains(index)) return;
                // An index outside the raster (the labyrinth door is pushed unguarded) reads `undefined` in JS,
                // which is never walkable.
                int tile = this.tileAt(index);
                // Records belong on ground you could stand on; rock carries its own silhouette already.
                if (!isWalkable(tile)) return;
                used.Add(index);
                this.addDecoration(index, kind);
            }

            if (dressing.core != null)
            {
                int centre = this.index(this.site.tx, this.site.ty);
                if (isWalkable(this.tileAt(centre))) place(centre, dressing.core);
                else
                {
                    if (this.interior.Count > 0)
                    {
                        int near = this.interior[this.interior.Count >> 1];
                        place(near, dressing.core);
                    }
                }
            }
            if (dressing.rim != null && this.rim.Count > 0)
            {
                double share = dressing.rimShare ?? 0.2;
                double want = Math.max(1, Math.round(this.rim.Count * share));
                int stride = (int)Math.max(1, Math.floor(this.rim.Count / want));
                for (int i = 0; i < this.rim.Count; i += stride) place(this.rim[i], dressing.rim);
            }
            if (dressing.fill != null && this.interior.Count > 0)
            {
                double share = dressing.fillShare ?? 0.08;
                double want = Math.max(1, Math.round(this.interior.Count * share));
                int stride = (int)Math.max(1, Math.floor(this.interior.Count / want));
                for (int i = 0; i < this.interior.Count; i += stride)
                    place(this.interior[i], dressing.fill);
            }
        }
    }

    /// <summary>`Math.sign(d) || 1`: a zero, negative-zero or NaN sign falls back to +1.</summary>
    private static int signOrOne(double d)
    {
        double sign = Math.sign(d);
        return Js.Truthy(sign) ? (int)sign : 1;
    }

    private static double clampLevel(double level, double maxLevel, int? minLevelArg = null)
    {
        double minLevel = minLevelArg ?? 0;
        double v = Math.round(level);
        return v < minLevel ? minLevel : v > maxLevel ? maxLevel : v;
    }

    /// <summary>True when `angle` sits within `half` radians of `target`, on the circle.</summary>
    private static bool angularNear(double angle, double target, double half)
    {
        double twoPi = Math.PI * 2;
        double delta = Math.abs(((angle - target) % twoPi) + twoPi) % twoPi;
        if (delta > Math.PI) delta = twoPi - delta;
        return delta <= half;
    }

    /// <summary>Stable per-cell variation seed, matching the terrain editor's own placement-seed contract (a signed int).</summary>
    private static int placementSeed(double seed, int tx, int ty, int salt)
    {
        int value = Js.ToInt32(Math.round(latticeHash(Js.ToUint32(Js.ToInt32(seed) ^ salt), tx, ty) * 0xffffffff));
        return value != 0 ? value : 1;
    }
}
