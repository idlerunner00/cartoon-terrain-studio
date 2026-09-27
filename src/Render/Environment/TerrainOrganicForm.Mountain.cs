// Fluitown extension — NOT a port of the original. The mountain form of the comic look's organic terrain. Part of
// TerrainOrganicForm's one displacement field, so every lane, the collision and the vegetation feet follow it like the
// rest of the field.
using System;
using Fluitown.Domain;

namespace Fluitown.Render;

public static partial class TerrainOrganicForm
{
    /// <summary>Lean of a cliff: horizontal set-back of its crest per unit of height (tan 17°).</summary>
    internal const double LEAN_RATE = 0.3;
    /// <summary>Largest set-back of a crest (compile px, 25 px = 1 m): taller walls lean more steeply upright.</summary>
    internal const double LEAN_MAX_PX = 36;
    /// <summary>Drops lower than this (levels) do not lean: kerbs and single terrace steps stay crisp.</summary>
    internal const double LEAN_MIN_LEVELS = 1.5;
    /// <summary>A massif thinner than about two cells leans with this share of the rate (a needle stays a needle).</summary>
    internal const double LEAN_THIN = 0.5;
    /// <summary>Crest height variation: connected 11 m shoulders instead of a new mound on every other cell.</summary>
    internal const double CREST_MIN = 0.5, CREST_RANGE = 1.8, CREST_WAVELENGTH = 275;

    public sealed partial class Field
    {
        // Per corner: the lean term T(y) = R·y − Q (x and z components, compile px). Linear in y, so the displacement
        // of a vertical edge is linear along it: its image is the chord of its displaced ends, exactly, and every
        // T-junction on it stays on it. No wall needs a cut.
        private float[]? leanRx, leanRz, leanQx, leanQz;
        private bool[]? leanCell;
        private bool anyLean;

        private bool LeanNear(int cell) => leanCell != null && leanCell[cell];

        /// <summary>Diagnostics: the averaging of the corner at world cell corner (x, y).</summary>
        public static (int x, int y)? DebugLeanCorner;
        public static Action<string>? DebugLeanLog;

        // Diagnostics: the lean corners before the blur.
        private double[]? debugRate, debugDirX, debugDirZ, debugFoot;

        /// <summary>Whether the lean moves points in the cell containing (x, z).</summary>
        public bool LeanActive(double x, double z)
        {
            if (leanCell == null) return false;
            int cx = (int)System.Math.Floor(CellX(x)), cz = (int)System.Math.Floor(CellZ(z));
            return cx >= 0 && cz >= 0 && cx < cellsW && cz < cellsH && leanCell[cz * cellsW + cx];
        }

        /// <summary>
        /// Walls lean back into their massif: every corner where terrain of different height meets gets the uphill
        /// direction of its four cells, a rate (LEAN_RATE, capped by LEAN_MAX_PX over the drop, reduced for thin
        /// massifs) and the foot height it leans from. Each corner then averages the terms of the lean corners around
        /// it (itself 1, edge neighbours ½, diagonal ones .35) and fades with the nearest one — one corner into a cap the
        /// set-back is half, two corners in it is gone, so a cap bends instead of shearing. Pinned corners (bridges,
        /// underpasses, clefts) and their neighbours hold still like the rest of the field. Reads cells up to two
        /// away from a corner, like the fillets: the frame's three border cells cover every corner a tile's vertices
        /// can reach.
        /// </summary>
        private void BuildLean(MaterializedTerrain terrain, double[] height, bool[] pinnedCorner)
        {
            BuildFootLift(terrain);
            int w = cellsW, h = cellsH, corners = cornersW * cornersH;
            const double step = TerrainProjection.TERRAIN_ELEVATION_STEP_PX;
            // Lean heights (px): floors and rock at their surface. Chasms and water stop it (their walls and banks reach
            // far below any foot, where a term linear in height would push outward), as do missing cells.
            var lift = new double[w * h];
            for (int i = 0; i < w * h; i++)
            {
                TerrainCell? cell = i < terrain.cells.Length ? terrain.cells[i] : null;
                if (cell == null || cell.type == TileType.Chasm || cell.type == TileType.Water || double.IsNaN(height[i]))
                {
                    lift[i] = double.NaN;
                    continue;
                }
                lift[i] = cell.surfaceZ * step;
            }
            double LiftAt(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? double.NaN : lift[y * w + x];
            // The cells two steps uphill of corner (x, y) — one column/row beyond the high cells touching it — all
            // lower than `below` (and inside the block x0..x1, y0..y1).
            bool ThinUphill(int x, int y, double dx, double dz, double below, int x0, int y0, int x1, int y1)
            {
                const double axis = 0.38; // ≈ sin 22.5°
                int sx = dx > axis ? 1 : dx < -axis ? -1 : 0, sz = dz > axis ? 1 : dz < -axis ? -1 : 0;
                if (sx == 0 && sz == 0) return false;
                int ca = sx > 0 ? x + 1 : sx < 0 ? x - 2 : x - 1, cb = sx == 0 ? x : ca;
                int ra = sz > 0 ? y + 1 : sz < 0 ? y - 2 : y - 1, rb = sz == 0 ? y : ra;
                bool any = false;
                for (int ry = ra; ry <= rb; ry++)
                    for (int rx = ca; rx <= cb; rx++)
                    {
                        if (rx < x0 || rx > x1 || ry < y0 || ry > y1) return false;
                        double v = LiftAt(rx, ry);
                        if (double.IsNaN(v) || v >= below) return false;
                        any = true;
                    }
                return any;
            }

            var dirX = new double[corners];
            var dirZ = new double[corners];
            var rate = new double[corners];
            var foot = new double[corners];
            var crest = new double[corners];
            bool anyCorner = false;
            Span<double> around = stackalloc double[4];
            for (int cy = 1; cy < cornersH - 1; cy++)
                for (int cx = 1; cx < cornersW - 1; cx++)
                {
                    int corner = cy * cornersW + cx;
                    if (pinnedCorner[corner]) continue;
                    double min = double.MaxValue, max = double.MinValue;
                    bool complete = true;
                    for (int k = 0; k < 4; k++)
                    {
                        around[k] = LiftAt(cx - 1 + (k & 1), cy - 1 + (k >> 1));
                        if (double.IsNaN(around[k])) { complete = false; break; }
                        min = System.Math.Min(min, around[k]);
                        max = System.Math.Max(max, around[k]);
                    }
                    if (!complete || max - min < LEAN_MIN_LEVELS * step) continue;
                    // Uphill: x grows to the right (k & 1), z downwards (k >> 1).
                    double gx = around[1] + around[3] - around[0] - around[2];
                    double gz = around[2] + around[3] - around[0] - around[1];
                    double len = System.Math.Sqrt(gx * gx + gz * gz);
                    if (len < 1e-6) continue;
                    gx /= len;
                    gz /= len;
                    // Rate: capped for this corner's own drop (every corner averaged below re-caps it for its range).
                    // Every cell read here is chosen with integer arithmetic: a float residue of the direction must not
                    // pick another cell in the neighbour tile's local indices (measured: 1.1 px seam gaps).
                    double r = System.Math.Min(LEAN_RATE, LEAN_MAX_PX / System.Math.Max(1, max - min));
                    // Thin massif: two cells uphill the ground is lower again (a needle stays a needle).
                    if (ThinUphill(cx, cy, gx, gz, max - step * 0.5, cx - 2, cy - 2, cx + 1, cy + 1)) r *= LEAN_THIN;
                    dirX[corner] = gx;
                    dirZ[corner] = gz;
                    rate[corner] = r;
                    foot[corner] = min;
                    crest[corner] = max;
                    anyCorner = true;
                }
            debugRate = rate; debugDirX = dirX; debugDirZ = dirZ; debugFoot = foot;
            if (!anyCorner) return;

            leanRx = new float[corners];
            leanRz = new float[corners];
            leanQx = new float[corners];
            leanQz = new float[corners];
            for (int cy = 0; cy < cornersH; cy++)
                for (int cx = 0; cx < cornersW; cx++)
                {
                    int corner = cy * cornersW + cx;
                    // Held like the other parts: 0 on a pinned corner, ½ next to one.
                    double hold = pinnedCorner[corner] ? 0 : 1;
                    // The geometry around this corner lies between its lowest cell and the highest one ring further.
                    // A neighbour's term is re-based on that lowest cell (it never pushes a lower foot outward) and
                    // re-capped for that range (it never sets a crest back by more than LEAN_MAX_PX).
                    double low = double.MaxValue, high = double.MinValue;
                    bool open = cx > 0 && cy > 0 && cx < cornersW - 1 && cy < cornersH - 1;
                    for (int k = 0; k < 4 && open; k++)
                    {
                        double v = LiftAt(cx - 1 + (k & 1), cy - 1 + (k >> 1));
                        if (double.IsNaN(v)) open = false;
                        else low = System.Math.Min(low, v);
                    }
                    // Shores and chasm rims stay exactly as they are: no lean within two cells of water or a chasm
                    // (water sheets and banks reach under the land cells there, below any foot the lean could use).
                    for (int y = cy - 2; y <= cy + 1 && open; y++)
                        for (int x = cx - 2; x <= cx + 1; x++)
                        {
                            double v = LiftAt(x, y);
                            if (double.IsNaN(v)) { open = false; break; }
                            high = System.Math.Max(high, v);
                        }
                    if (!open) continue;
                    double rateCap = LEAN_MAX_PX / System.Math.Max(1, high - low);
                    double weight = 0, fade = 0, rx = 0, rz = 0, qx = 0, qz = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = cx + dx, y = cy + dy;
                            if (x < 0 || y < 0 || x >= cornersW || y >= cornersH) continue;
                            int n = y * cornersW + x;
                            if (pinnedCorner[n]) hold = System.Math.Min(hold, 0.5);
                            if (rate[n] <= 0) continue;
                            double omega = dx == 0 && dy == 0 ? 1 : dx == 0 || dy == 0 ? 0.5 : 0.35;
                            // The wall turns about its middle: the crest sets back half the lean, the foot steps out
                            // the other half (each side of the wall is compressed half as much as with a set-back alone).
                            double r = System.Math.Min(rate[n], rateCap), b = (foot[n] + crest[n]) * 0.5;
                            if (DebugLeanCorner is { } probe && probe.x == i0 + cx && probe.y == j0 + cy)
                                DebugLeanLog?.Invoke(System.FormattableString.Invariant(
                                    $"  n [{dx},{dy}] pinned {pinnedCorner[n]} rate {rate[n]:F4} r {r:F4} d ({dirX[n]:R},{dirZ[n]:R}) foot {foot[n]} crest {crest[n]} low {low} high {high}"));
                            weight += omega;
                            fade = System.Math.Max(fade, omega);
                            rx += omega * r * dirX[n];
                            rz += omega * r * dirZ[n];
                            qx += omega * r * b * dirX[n];
                            qz += omega * r * b * dirZ[n];
                        }
                    if (weight <= 0 || hold <= 0) continue;
                    double scale = fade * hold / weight;
                    leanRx[corner] = (float)(rx * scale);
                    leanRz[corner] = (float)(rz * scale);
                    leanQx[corner] = (float)(qx * scale);
                    leanQz[corner] = (float)(qz * scale);
                    anyLean = true;
                }
            if (!anyLean) return;
            leanCell = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int c = y * cornersW + x;
                    leanCell[y * w + x] = Lean(c) || Lean(c + 1) || Lean(c + cornersW) || Lean(c + cornersW + 1);
                }
        }

        private bool Lean(int corner) => leanRx![corner] != 0 || leanRz![corner] != 0 || leanQx![corner] != 0 || leanQz![corner] != 0;

        /// <summary>
        /// Adds the lean at (tx, tz) inside cell (cx, cz) for a point at height y (smoothstep weights of the four
        /// corners, C¹ across cells) and, if <paramref name="j"/> has 8 slots, its derivatives.
        /// </summary>
        private bool LeanAt(int cx, int cz, double tx, double tz, double y, ref double wx, ref double wz, Span<double> j)
        {
            int c = cz * cornersW + cx;
            double sx = tx * tx * (3 - 2 * tx), sz = tz * tz * (3 - 2 * tz);
            double dsx = 6 * tx * (1 - tx) / tileSize, dsz = 6 * tz * (1 - tz) / tileSize;
            bool moved = false;
            for (int k = 0; k < 4; k++)
            {
                int corner = c + (k & 1) + (k >> 1) * cornersW;
                double rx = leanRx![corner], rz = leanRz![corner];
                double qx = leanQx![corner], qz = leanQz![corner];
                if (rx == 0 && rz == 0 && qx == 0 && qz == 0) continue;
                double ax = (k & 1) == 1 ? sx : 1 - sx, az = (k >> 1) == 1 ? sz : 1 - sz;
                double phi = ax * az;
                double tX = rx * y - qx, tZ = rz * y - qz;
                wx += phi * tX;
                wz += phi * tZ;
                if (j.Length >= 8)
                {
                    double px = ((k & 1) == 1 ? dsx : -dsx) * az;
                    double pz = ax * ((k >> 1) == 1 ? dsz : -dsz);
                    // y is the height after the dome (j[6], j[7] already hold its gradient): chain rule.
                    j[0] += px * tX + phi * rx * j[6];
                    j[1] += phi * rx;
                    j[2] += pz * tX + phi * rx * j[7];
                    j[3] += px * tZ + phi * rz * j[6];
                    j[4] += phi * rz;
                    j[5] += pz * tZ + phi * rz * j[7];
                }
                moved = true;
            }
            return moved;
        }

        /// <summary>
        /// Crest variation: the dome of a rock cap is scaled along a 5 m noise, so the crest line of a massif rises
        /// and dips and neighbouring caps of one height stop reading as one lid. Returns the scale and its gradient
        /// per px; 1 without the mountain form.
        /// </summary>
        private static double CrestScale(double x, double z, out double dx, out double dz)
        {
            dx = dz = 0;
            Noise2Pair(x / CREST_WAVELENGTH, z / CREST_WAVELENGTH, 0x7c2b_91d3u,
                out double a, out double adu, out double adv, out _, out _, out _);
            // Sharpened towards its extremes: broad shoulders, a few notches.
            double t = System.Math.Clamp(a * 1.25, -1, 1);
            double sharp = t * (1.5 - 0.5 * t * t);
            double dSharp = System.Math.Abs(a * 1.25) < 1 ? 1.25 * (1.5 - 1.5 * t * t) : 0;
            double k = CREST_RANGE * 0.5 * dSharp / CREST_WAVELENGTH;
            dx = k * adu;
            dz = k * adv;
            return CREST_MIN + CREST_RANGE * 0.5 * (sharp + 1);
        }
    }
}
