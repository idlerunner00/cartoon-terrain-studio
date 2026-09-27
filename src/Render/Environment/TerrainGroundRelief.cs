// Fluitown extension — NOT a port of the original. The comic look's uneven ground. Only reached with
// TerrainGroundRelief.Enabled (set before the first bake; the Style drawer's "uneven ground" switch); without it the
// ported floors stay.
using System;

namespace Fluitown.Render;

/// <summary>
/// Open floors of the comic look are not flat plates: they roll in broad swells and carry hummocks a Flui walks over
/// (centimetres of local texture over broad swells up to about ±0.8 m), so the ground catches the light ramp and reads as
/// ground from the Flui's own height.
///
/// The relief is baked into the floor lattice by the compiler (<c>TerrainGeometryCompiler.Relief.cs</c>), so the
/// collision soup — built from the surface lane — carries it, and the Flui, its companions and every probe walk on it.
/// Everything standing on a floor (grass, flowers, trees, bushes, props) is placed with the same lift.
///
/// Watertight by construction: the lift is a pure function of the absolute compile-space position, times a mask that
/// lives on the frame's cell corners (<see cref="TerrainGroundReliefMask"/>). A corner is 0 wherever any of its four
/// cells is not an open organic floor at the corner's height (walls, water, chasms, bridges, clefts, underpasses,
/// contour caps, crest bevels), so every edge a floor shares with anything else stays exactly at the terrace height;
/// the mask then rises over one ring of corners (½) to 1, with smoothstep weights (C¹). Terrace feet — where the
/// organic form anchors its lean and fillets — therefore never move.
/// </summary>
public static class TerrainGroundRelief
{
    /// <summary>Set before the first bake (comic look only); bakes run on worker threads.</summary>
    public static volatile bool Enabled;

    // Compile px (25 px = 1 m; one cell = 62.5 px).
    /// <summary>Broad swells: the meadow rolls.</summary>
    internal const double SWELL_AMPLITUDE = 18, SWELL_WAVELENGTH = 400, SWELL_ANGLE = 0.37;
    /// <summary>Hummocks: the unevenness a Flui steps over.</summary>
    internal const double HUMMOCK_AMPLITUDE = 3.2, HUMMOCK_WAVELENGTH = 110, HUMMOCK_ANGLE = -0.83;
    /// <summary>Lumps: small bumps, mostly carried by the vertex normals (the lattice samples them coarsely).</summary>
    internal const double LUMP_AMPLITUDE = 0.8, LUMP_WAVELENGTH = 40, LUMP_ANGLE = 1.21;
    /// <summary>Hummock and lump strength drift over 25 m: some stretches lie calm, others are lumpy.</summary>
    internal const double ROUGHNESS_WAVELENGTH = 620, ROUGHNESS_MIN = 0.45, ROUGHNESS_RANGE = 0.8;
    /// <summary>Baked curvature paint: curvature (px⁻¹) × scale → −1 crest … 1 hollow; shade and lush pole at 1.</summary>
    internal const double CAVITY_SCALE = 18, CAVITY_SHADE = 0.2, CAVITY_LUSH = 0.16;
    /// <summary>
    /// Baked form paint: the shade gained per unit of facing towards the comic style's fixed form light (the direction
    /// comic_surface hatches against, comic_style.gdshaderinc). Under a high sun the light ramp's lit band is wide, so
    /// a slope of a few degrees would not change a band; painted like a comic panel's modelling, the hummocks read.
    /// </summary>
    internal const double FORM_SHADE = 1.6;
    private static readonly double FormX = -0.45 / 1.0, FormY = 0.78, FormZ = 0.43;
    private static readonly double FormLength = Math.Sqrt(FormX * FormX + FormY * FormY + FormZ * FormZ);

    /// <summary>
    /// Facing of a ground normal (−∂h/∂x, 1, −∂h/∂z) towards the form light, relative to flat ground (0 on a level floor,
    /// positive on slopes turned towards the light). Compile space shares the Godot world's axes (scaled, no yaw).
    /// </summary>
    internal static double FormLight(double slopeX, double slopeZ)
    {
        double length = Math.Sqrt(slopeX * slopeX + 1 + slopeZ * slopeZ);
        double facing = (slopeX * FormX + FormY + slopeZ * FormZ) / (length * FormLength);
        return facing - FormY / FormLength;
    }
    /// <summary>Lattice subdivisions of a floor cell the relief reaches (the ported organic floor has 2). Even, so its
    /// edge points include the 2×2 neighbours' (3 left mutual T-junctions against them that the organic warp opened into
    /// 0.02–0.1 px cracks).</summary>
    public const int SUBDIVISIONS = 4;

    private static readonly double SwellCos = Math.Cos(SWELL_ANGLE), SwellSin = Math.Sin(SWELL_ANGLE);
    private static readonly double HummockCos = Math.Cos(HUMMOCK_ANGLE), HummockSin = Math.Sin(HUMMOCK_ANGLE);
    private static readonly double LumpCos = Math.Cos(LUMP_ANGLE), LumpSin = Math.Sin(LUMP_ANGLE);

    /// <summary>
    /// The Fluitown relief before the mask (px): swells plus hummocks and lumps of drifting strength. The lumps only
    /// shade (<paramref name="lumps"/> for normals and the baked paint): in the geometry — and so the collision — their
    /// tight crests would toss a rolling Flui into the air at speed.
    /// </summary>
    public static double Shape(double x, double z, bool lumps = false)
    {
        double swell = Noise((x * SwellCos - z * SwellSin) / SWELL_WAVELENGTH, (x * SwellSin + z * SwellCos) / SWELL_WAVELENGTH, 0x51e7_2b3du);
        double hummock = Noise((x * HummockCos - z * HummockSin) / HUMMOCK_WAVELENGTH, (x * HummockSin + z * HummockCos) / HUMMOCK_WAVELENGTH, 0x2c9f_61a7u);
        // A hummock is rounder on top than in its hollow: tussocks, not pits.
        hummock += 0.35 * (hummock * hummock - 0.2);
        double lump = lumps ? Noise((x * LumpCos - z * LumpSin) / LUMP_WAVELENGTH, (x * LumpSin + z * LumpCos) / LUMP_WAVELENGTH, 0x6b43_9e15u) : 0;
        double rough = ROUGHNESS_MIN + ROUGHNESS_RANGE * (0.5 + 0.5 * Noise(x / ROUGHNESS_WAVELENGTH + 17.3, z / ROUGHNESS_WAVELENGTH - 8.1, 0x7a31_c4e9u));
        return SWELL_AMPLITUDE * swell + (HUMMOCK_AMPLITUDE * hummock + LUMP_AMPLITUDE * lump) * rough;
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    /// <summary>Quintic value noise in [−1, 1].</summary>
    internal static double Noise(double u, double v, uint seed)
    {
        double fu = Math.Floor(u), fv = Math.Floor(v);
        int iu = (int)fu, iv = (int)fv;
        double su = Fade(u - fu), sv = Fade(v - fv);
        double a = Hash(iu, iv, seed), b = Hash(iu + 1, iv, seed), c = Hash(iu, iv + 1, seed), d = Hash(iu + 1, iv + 1, seed);
        double top = a + (b - a) * su, bottom = c + (d - c) * su;
        return top + (bottom - top) * sv;
    }

    private static double Hash(int x, int y, uint seed)
    {
        unchecked
        {
            uint h = seed ^ ((uint)x * 0x8da6b343u) ^ ((uint)y * 0xd8163841u);
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            return (h & 0xffffff) * (2.0 / 16777215.0) - 1;
        }
    }
}

/// <summary>
/// The relief mask of one bake frame on its cell corners (see <see cref="TerrainGroundRelief"/>). Built lazily by the
/// compiler on its first use in a bake and invalidated at the start of every bake (the worker reuses its terrain).
/// </summary>
public sealed class TerrainGroundReliefMask
{
    private float[] corner = Array.Empty<float>();
    private byte[] open = Array.Empty<byte>();
    private double[] height = Array.Empty<double>();
    private int cellsW, cellsH, cornersW, cornersH;

    public bool Built { get; private set; }

    public void Invalidate() => Built = false;

    /// <summary>Starts a build for a frame of w × h cells: then <see cref="SetCell"/> every cell and <see cref="Finish"/>.</summary>
    public void Begin(int w, int h)
    {
        cellsW = w;
        cellsH = h;
        cornersW = w + 1;
        cornersH = h + 1;
        if (open.Length < w * h) open = new byte[w * h];
        if (height.Length < w * h) height = new double[w * h];
        if (corner.Length < cornersW * cornersH) corner = new float[cornersW * cornersH];
        Array.Clear(open, 0, w * h);
    }

    /// <summary>Cell <paramref name="index"/> is an open organic floor at <paramref name="surfaceZ"/> (levels).</summary>
    public void SetCell(int index, double surfaceZ)
    {
        open[index] = 1;
        height[index] = surfaceZ;
    }

    /// <summary>
    /// A corner is 1 when its four cells are open floors at one height, 0 otherwise, and ½ when a corner around it is 0.
    /// </summary>
    public void Finish()
    {
        int w = cellsW;
        int corners = cornersW * cornersH;
        // Pass 1: base corners (0/1) into corner[].
        for (int cy = 0; cy < cornersH; cy++)
            for (int cx = 0; cx < cornersW; cx++)
            {
                bool all = cx > 0 && cy > 0 && cx < cornersW - 1 && cy < cornersH - 1;
                double z = double.NaN;
                for (int k = 0; all && k < 4; k++)
                {
                    int x = cx - 1 + (k & 1), y = cy - 1 + (k >> 1);
                    int index = y * w + x;
                    if (open[index] == 0) { all = false; break; }
                    double cz = height[index];
                    if (k == 0) z = cz;
                    else if (Math.Abs(cz - z) > 0.001) all = false;
                }
                corner[cy * cornersW + cx] = all ? 1 : 0;
            }
        // Pass 2: a corner next to a 0 corner carries half (the relief fades in over two cells).
        Span<byte> zero = corners <= 4096 ? stackalloc byte[corners] : new byte[corners];
        for (int i = 0; i < corners; i++) zero[i] = corner[i] == 0 ? (byte)1 : (byte)0;
        for (int cy = 0; cy < cornersH; cy++)
            for (int cx = 0; cx < cornersW; cx++)
            {
                int c = cy * cornersW + cx;
                if (zero[c] == 1) continue;
                bool near = false;
                for (int dy = -1; dy <= 1 && !near; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= cornersW || y >= cornersH || zero[y * cornersW + x] == 1) { near = true; break; }
                    }
                if (near) corner[c] = 0.5f;
            }
        Built = true;
    }

    /// <summary>Whether the relief can move any point of frame cell (x, y).</summary>
    public bool CellActive(int x, int y)
    {
        if (!Built || x < 0 || y < 0 || x >= cellsW || y >= cellsH) return false;
        int c = y * cornersW + x;
        return corner[c] + corner[c + 1] + corner[c + cornersW] + corner[c + cornersW + 1] > 0;
    }

    /// <summary>The mask at frame grid position (gx, gz) in cells (smoothstep-bilinear over the cell's corners).</summary>
    public double At(double gx, double gz)
    {
        if (!Built) return 0;
        double fx = Math.Floor(gx), fz = Math.Floor(gz);
        int x = (int)fx, y = (int)fz;
        // A point on the frame's last corner line belongs to the cell before it.
        if (x == cellsW && gx == fx) { x--; fx--; }
        if (y == cellsH && gz == fz) { y--; fz--; }
        if (x < 0 || y < 0 || x >= cellsW || y >= cellsH) return 0;
        int c = y * cornersW + x;
        double m00 = corner[c], m10 = corner[c + 1], m01 = corner[c + cornersW], m11 = corner[c + cornersW + 1];
        if (m00 + m10 + m01 + m11 <= 0) return 0;
        double tx = gx - fx, tz = gz - fz;
        double sx = tx * tx * (3 - 2 * tx), sz = tz * tz * (3 - 2 * tz);
        double top = m00 + (m10 - m00) * sx, bottom = m01 + (m11 - m01) * sx;
        return top + (bottom - top) * sz;
    }
}
