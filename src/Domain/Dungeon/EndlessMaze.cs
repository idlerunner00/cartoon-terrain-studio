// Port of packages/shared/src/domain/dungeon/endlessMaze.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/// <summary>
/// Tuning the caller derives from the run's macro DNA. All values are constants over a run, so the lattice
/// pitch never varies per chunk (the halo math stays trivial and seams provably agree).
/// </summary>
public sealed class MazeTuning
{
    /// <summary>Lattice pitch `P` in tiles (~5.5 dense … ~8 coarse). Site spacing; the maze's fundamental scale.</summary>
    public double pitch;
    /// <summary>Baseline openness pushed onto the whole run (−0.5..0.5) — biases braid density & room size.</summary>
    public double opennessBias;
    /// <summary>Base probability a Gabriel (non-tree) edge is braided in, before the openness term.</summary>
    public double braidBase;
    /// <summary>Extra braid probability at full local openness (loopier where the world opens up).</summary>
    public double braidRange;
}

/// <summary>One graph site: its lattice cell and its global tile position.</summary>
public sealed class MazeSite
{
    public int latX;
    public int latY;
    public double gx;
    public double gy;
}

/// <summary>
/// The **global node-graph labyrinth** — the endless world's connective backbone, rebuilt off the grid.
///
/// ── Why this replaces the cell grid ──
/// The old endless maze wired a regular n×n grid of junction cells with 4-neighbour, axis-aligned
/// L-corridors. Per-cell jitter/warp only *masked* a topology that stayed a grid, so the world kept reading
/// as a "checkerboard". This module carves the labyrinth from an **irregular global node graph** instead:
///  1. A jittered lattice scatters one **site** per lattice cell across the infinite plane — a Poisson-disk-ish
///     point set that is a pure function of the GLOBAL tile position (never per-chunk), so junctions never
///     line up into a raster and the pattern does not reset at a chunk seam.
///  2. Edges come from a deterministic **neighbourhood graph** over the global sites: the Relative-Neighbourhood
///     Graph (connected, sparse, organic base) plus a hashed subset of the extra **Gabriel** edges (braid
///     loops → a real labyrinth with choices, not a single thread). Membership is a pure function of the two
///     sites + the sites around them, so two chunks meeting at a seam compute the SAME seam-crossing edges.
///  3. Each edge carves as an **organic curved corridor** whose wobble is hashed from the two GLOBAL site
///     identities and PAINTED in chunk-local space — so both chunks paint byte-identical seam tiles. Corridors
///     wind in every direction with variable width; nothing is axis-aligned.
///
/// ── Determinism is structural, not a convention ──
/// NOTHING here consumes the per-chunk `Rng`. Every decision (site position, edge membership, braid roll,
/// wobble phase/amplitude, corridor radius) flows through the shared integer hashes
/// (latticeHash/terrainHash) on integer lattice coordinates or a **canonical edge key**
/// (the two sites' lattice cells ordered lexicographically) — so the server and every client regenerate the
/// same labyrinth, and seam-crossing edges are identical on both sides. The 32×32 chunk clip is applied only
/// when painting; the graph itself is windowed with a halo wide enough to see every blocker (see HALO).
///
/// Connectivity is NOT this module's job: it draws the aesthetic graph and records the corridor footprint into
/// `corridorMask`. The caller (endless.ts) guarantees ONE connected body (the four seam
/// ports + the dry connectivity closure), so the graph is free to be tuned purely for how the maze *looks*.
/// </summary>
public static class EndlessMaze
{
    /// <summary>
    /// Halo, in lattice cells, that a chunk gathers sites beyond its own bounds. A neighbourhood-graph blocker for
    /// an edge of length ≤ `D_CAP_FACTOR·pitch` sits within `ceil((D_CAP_FACTOR + 2·SITE_JITTER)) = ceil(2.48) = 3`
    /// lattice cells of a boundary-crossing edge's near endpoint — so HALO=3 makes both chunks at a seam gather
    /// every blocker for every seam-crossing edge, and their edge sets match exactly. Do not lower without redoing
    /// that bound.
    /// </summary>
    private const int HALO = 3;
    /// <summary>Max edge length as a multiple of the lattice pitch (longer pairs are never candidates).</summary>
    private const double D_CAP_FACTOR = 1.8;
    /// <summary>
    /// Max per-axis site displacement from its lattice-cell centre, as a fraction of the pitch (Poisson-disk feel).
    /// Kept ≤0.34 so the HALO=3 blocker bound above holds with margin.
    /// </summary>
    private const double SITE_JITTER = 0.34;

    private const int SITE_X_SALT = 0x1b9e77c3;
    private const int SITE_Y_SALT = 0x6d2f41a9;
    private const int OPEN_SALT = 0x3ca7e58b;
    private const int BRAID_SALT = 0x51f3bd27;
    private const int RADIUS_SALT = 0x274bd9e1;
    private const int ROOM_SALT = 0x0f5c8ab3;
    private const int WOBBLE_PHASE_SALT = 0x7a1c63d5;
    private const int WOBBLE_AMP_SALT = 0x2e9b4f17;
    private const int WOBBLE_FREQ_SALT = 0x59d2c86f;

    /// <summary>
    /// Smooth global maze-openness field cell (tiles) — drives regional braid density & junction room size, so
    /// tight districts read as dense warrens and open districts loosen up. Large & smooth ⇒ seam-consistent.
    /// </summary>
    private const double OPEN_CELL = 132;

    private static double clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>The single site of lattice cell `(latX,latY)` in GLOBAL tile coords — pure `(seed, cell)`.</summary>
    public static MazeSite mazeSiteAt(double seed, int latX, int latY, double pitch)
    {
        double jx = (latticeHash((uint)(Js.ToInt32(seed) ^ SITE_X_SALT), latX, latY) - 0.5) * 2 * SITE_JITTER;
        double jy = (latticeHash((uint)(Js.ToInt32(seed) ^ SITE_Y_SALT), latX, latY) - 0.5) * 2 * SITE_JITTER;
        return new MazeSite { latX = latX, latY = latY, gx = (latX + 0.5 + jx) * pitch, gy = (latY + 0.5 + jy) * pitch };
    }

    /// <summary>Smooth global maze-openness at a global tile → [0,1] (regional density signal, NOT the macro anchor field).</summary>
    public static double mazeOpennessAt(double seed, double gtx, double gty, double bias) =>
        clamp01(0.5 + bias + (valueNoise((uint)(Js.ToInt32(seed) ^ OPEN_SALT), gtx, gty, OPEN_CELL) - 0.5) * 1.15);

    /// <summary>Lexicographic order of two lattice cells (total order — one site per cell, so never equal).</summary>
    private static bool cellLess(int ax, int ay, int bx, int by) => ax < bx || (ax == bx && ay < by);

    /// <summary>Deterministic [0,1) hash of an undirected edge, keyed on the CANONICAL (lo,hi) lattice-cell pair + salt.</summary>
    private static double edgeHash(double seed, MazeSite a, MazeSite b, int salt)
    {
        var (lo, hi) = cellLess(a.latX, a.latY, b.latX, b.latY) ? (a, b) : (b, a);
        int mixed = Math.imul(lo.latX, 0x27d4eb2f) ^ Math.imul(lo.latY, unchecked((int)0x9e3779b9));
        return TerrainRules.terrainHash(hi.latX, hi.latY, (uint)(Js.ToInt32(seed) ^ salt ^ mixed));
    }

    /// <summary>
    /// Carve one edge as an organic curved corridor from global site `a` to global site `b`. The perpendicular
    /// wobble's phase/amplitude/frequency are hashed off the GLOBAL canonical edge key, and the brush is stamped in
    /// chunk-LOCAL space (`global − chunkBase`), so two chunks sharing this edge paint identical tiles on the seam.
    /// The wobble envelope is zero at both endpoints, so the corridor always meets the two sites. Records every
    /// painted tile into `corridorMask` (protects the maze from the macro massif-raise and feeds river bridging).
    /// </summary>
    private static void carveGlobalCorridor(
        byte[] tiles,
        int w,
        int h,
        int baseTx,
        int baseTy,
        MazeSite a,
        MazeSite b,
        double seed,
        byte[] corridorMask)
    {
        double dx = b.gx - a.gx;
        double dy = b.gy - a.gy;
        double len = Math.hypot(dx, dy);
        if (!Js.Truthy(len)) len = 1; // `Math.hypot(dx, dy) || 1`
        double nx = -dy / len;
        double ny = dx / len;
        double phase = edgeHash(seed, a, b, WOBBLE_PHASE_SALT) * Math.PI * 2;
        double amp = (0.6 + edgeHash(seed, a, b, WOBBLE_AMP_SALT) * 2.1) * Math.min(1, len / 5);
        double freq = 1 + Math.floor(edgeHash(seed, a, b, WOBBLE_FREQ_SALT) * 2.99); // 1..3 humps
        double radius = 0.85 + edgeHash(seed, a, b, RADIUS_SALT) * 0.55; // ~2–3 tiles wide → a corridor, not a hall
        int steps = (int)Math.max(2, Math.ceil(len * 2.3));
        // The TS passes a fresh `{ mask, minBorder: 0 }` literal per stamp; the brush only reads it, so one
        // shared options object is equivalent.
        var brush = new BrushOptions { mask = corridorMask, minBorder = 0 };
        for (int s = 0; s <= steps; s++)
        {
            double t = (double)s / steps;
            // Envelope sin(πt) is 0 at both ends → the corridor connects the sites; the inner wave bends it organically.
            double wob = Math.sin(t * Math.PI) * Math.sin(t * Math.PI * freq + phase) * amp;
            double gx = a.gx + dx * t + nx * wob;
            double gy = a.gy + dy * t + ny * wob;
            // minBorder:0 so a seam-crossing corridor may paint the outermost ring — both chunks paint the same global
            // tiles there, so the corridor flows across the seam instead of stopping at a chunk-boundary wall.
            TerrainKit.paintFloorBrush(tiles, w, h, gx - baseTx, gy - baseTy, radius, brush);
        }
    }

    /// <summary>
    /// Carve the global maze graph's slice for the chunk whose top-left tile is `(baseTx,baseTy)` into `tiles`
    /// (and `corridorMask`). Returns the graph sites whose centre lies INSIDE the chunk — the junctions the caller
    /// decorates (encounter rooms, cover pillars). Pure `(seed, base, tuning)`; consumes no Rng.
    /// </summary>
    public static List<MazeSite> carveEndlessMaze(
        byte[] tiles,
        int w,
        int h,
        int baseTx,
        int baseTy,
        double seed,
        MazeTuning tuning,
        byte[] corridorMask)
    {
        double pitch = tuning.pitch;
        double dCap = D_CAP_FACTOR * pitch;
        double dCap2 = dCap * dCap;
        // Lattice window covering the chunk + halo.
        int latMinX = (int)Math.floor(baseTx / pitch) - HALO;
        int latMaxX = (int)Math.floor((baseTx + w - 1) / pitch) + HALO;
        int latMinY = (int)Math.floor(baseTy / pitch) - HALO;
        int latMaxY = (int)Math.floor((baseTy + h - 1) / pitch) + HALO;

        var sites = new List<MazeSite>();
        for (int ly = latMinY; ly <= latMaxY; ly++)
        {
            for (int lx = latMinX; lx <= latMaxX; lx++)
            {
                sites.push(mazeSiteAt(seed, lx, ly, pitch));
            }
        }

        // Edges: for each canonical pair within the distance cap, decide membership from the neighbourhood graph.
        // Scanning every gathered site as a blocker candidate is correct (a site outside the edge's reach never
        // satisfies the blocker predicate, so extra far sites in one chunk's window can't change the boolean — only
        // the near blockers matter, and HALO=3 guarantees BOTH chunks see all of those).
        for (int i = 0; i < sites.Count; i++)
        {
            var a = sites[i];
            for (int j = i + 1; j < sites.Count; j++)
            {
                var b = sites[j];
                double ex = b.gx - a.gx;
                double ey = b.gy - a.gy;
                double d2 = ex * ex + ey * ey;
                if (d2 > dCap2) continue;
                // Single blocker scan → both the Relative-Neighbourhood (lune) and Gabriel (diameter-disk) predicates.
                bool rngBlocked = false;
                bool gabrielBlocked = false;
                for (int k = 0; k < sites.Count; k++)
                {
                    if (k == i || k == j) continue;
                    var c = sites[k];
                    double acx = c.gx - a.gx;
                    double acy = c.gy - a.gy;
                    double bcx = c.gx - b.gx;
                    double bcy = c.gy - b.gy;
                    double dac2 = acx * acx + acy * acy;
                    double dbc2 = bcx * bcx + bcy * bcy;
                    // RNG lune: a site strictly nearer to BOTH endpoints than they are to each other blocks the RNG edge.
                    if (!rngBlocked && dac2 < d2 && dbc2 < d2) rngBlocked = true;
                    // Gabriel disk (diameter a–b): c is inside iff the angle a–c–b is obtuse, i.e. (a−c)·(b−c) < 0.
                    if (!gabrielBlocked && acx * bcx + acy * bcy < 0) gabrielBlocked = true;
                    if (rngBlocked && gabrielBlocked) break;
                }
                if (gabrielBlocked) continue; // not even a Gabriel edge — never carve (keeps the graph planar-ish/organic)
                bool carve = !rngBlocked; // RNG base edges always carve (connected sparse skeleton)
                if (!carve)
                {
                    // Gabriel-but-not-RNG edge → braid loop with a locally-openness-scaled probability.
                    double mx = (a.gx + b.gx) * 0.5;
                    double my = (a.gy + b.gy) * 0.5;
                    double open = mazeOpennessAt(seed, mx, my, tuning.opennessBias);
                    double threshold = tuning.braidBase + tuning.braidRange * open;
                    carve = edgeHash(seed, a, b, BRAID_SALT) < threshold;
                }
                if (carve) carveGlobalCorridor(tiles, w, h, baseTx, baseTy, a, b, seed, corridorMask);
            }
        }

        // Junction rooms: a clean disk at each site (deterministic from the GLOBAL centre + a per-site radius, so a
        // seam-straddling junction is identical on both sides). Radius grows with regional openness → warrens stay
        // tight, open districts breathe. Also collect the sites whose centre lands inside this chunk (for decoration).
        var interior = new List<MazeSite>();
        var brush = new BrushOptions { mask = corridorMask, minBorder = 0 };
        foreach (var site in sites)
        {
            double open = mazeOpennessAt(seed, site.gx, site.gy, tuning.opennessBias);
            double rHash = latticeHash((uint)(Js.ToInt32(seed) ^ ROOM_SALT), site.latX, site.latY);
            double roomR = 0.35 + open * 0.55 + rHash * 0.5; // ~0.4..1.4 tiles — a junction node, widening in open districts
            TerrainKit.paintFloorBrush(tiles, w, h, site.gx - baseTx, site.gy - baseTy, roomR, brush);
            double lx = site.gx - baseTx;
            double ly = site.gy - baseTy;
            if (lx >= 0 && lx < w && ly >= 0 && ly < h) interior.push(site);
        }
        return interior;
    }
}
