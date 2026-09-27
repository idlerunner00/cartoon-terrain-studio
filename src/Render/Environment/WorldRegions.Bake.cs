// Fluitown extension — NOT a port of the original. The bake side of the regions: the rock of every tile takes its
// region's pigments on the bake worker (TerrainBakeWorker, after the organic form), so the GPU pays nothing for it.
using System;
using System.Buffers;
using Fluitown.Domain;

namespace Fluitown.Render;

public static partial class WorldRegions
{
    // Median luminance of the ported pigments at the start: the value each vertex keeps
    // relative to its kind's median.
    private const double FaceLuminance = 0.309, CapLuminance = 0.465, EarthLuminance = 0.249;
    /// <summary>Share of the ported pigment's own hue kept on top of the region's (its variation survives).</summary>
    private const float KeepHue = 0.12f, DecorationHue = 0.3f;

    /// <summary>
    /// Repaints one fresh merged payload in its regions' pigments: rock faces between the region's deep, mid and lit
    /// rock, caps in its weathered cap, earth risers in its soil — each vertex keeping its value relative to its kind's
    /// median, so the compiler's lit lips, deep feet and strata stay. The regions are sampled once per cell of the
    /// frame (a region changes over about 160 m; a cell is 2.5 m).
    /// </summary>
    public static void Apply(TerrainGeometryPayload geometry, TerrainBakeFrame frame, MaterializedTerrain terrain)
    {
        if (geometry.surface is not { } surface || surface.color.Length == 0) return;
        int w = terrain.width, h = terrain.height;
        double ts = frame.tileSize;
        // Per cell: the region pigments of faces (lit, mid, deep), caps and soil, already mixed by weight (pooled
        // scratch: garbage on a bake worker pauses the main thread).
        float[] mixed = ArrayPool<float>.Shared.Rent(w * h * 15);
        int[] cellRegion = ArrayPool<int>.Shared.Rent(w * h * 4);
        float[] cellWeight = ArrayPool<float>.Shared.Rent(w * h * 4);
        try
        {
        Span<int> region = stackalloc int[4];
        Span<double> weight = stackalloc double[4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Sample(frame.originX + (frame.i0 + x + 0.5) * ts, frame.originY + (frame.j0 + y + 0.5) * ts, region, weight);
                for (int k = 0; k < 4; k++)
                {
                    cellRegion[(y * w + x) * 4 + k] = region[k];
                    cellWeight[(y * w + x) * 4 + k] = (float)weight[k];
                }
                int c = (y * w + x) * 15;
                MixInto(mixed, c, region, weight, static r => r.RockLit);
                MixInto(mixed, c + 3, region, weight, static r => r.RockMid);
                MixInto(mixed, c + 6, region, weight, static r => r.RockDeep);
                MixInto(mixed, c + 9, region, weight, static r => r.Cap);
                MixInto(mixed, c + 12, region, weight, static r => r.Soil);
            }
        float[] color = surface.color, kinds = surface.surface, position = surface.position;
        int vertices = Math.Min(color.Length / 3, Math.Min(kinds.Length / 2, position.Length / 3));
        for (int v = 0; v < vertices; v++)
        {
            float kind = kinds[v * 2];
            if (kind > 3.5f) continue;
            // Decorations (negative surf.y) on floors — the ported growth strips a hair above the floor, patches, ink —
            // follow the floor's region too (on a recoloured floor the ported ones stood out as sharp-edged slabs), at
            // exactly their value (ink stays ink) and with more of their own hue; decorations on rock keep theirs.
            bool decoration = kinds[v * 2 + 1] < 0;
            if (decoration && kind > 0.5f) continue;
            int cx = Math.Clamp((int)Math.Floor((position[v * 3] - frame.originX) / ts) - frame.i0, 0, w - 1);
            int cz = Math.Clamp((int)Math.Floor((position[v * 3 + 2] - frame.originY) / ts) - frame.j0, 0, h - 1);
            int c = (cz * w + cx) * 15;
            float r = color[v * 3], g = color[v * 3 + 1], b = color[v * 3 + 2];
            double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (lum <= 1e-5) continue;
            float tr, tg, tb;
            float hue = decoration ? DecorationHue : KeepHue;
            if (kind < 0.5f)
            {
                // Floor: the region's base floor at the vertex's value; the start's own region keeps the ported one.
                int cell = cz * w + cx;
                double t = lum / FloorLuminance, sr = 0, sg = 0, sb = 0, native = 0, flui = 0;
                for (int k = 0; k < 4; k++)
                {
                    float weightK = cellWeight[cell * 4 + k];
                    if (weightK <= 0) continue;
                    var reg = Regions[cellRegion[cell * 4 + k]];
                    // The start's own region keeps the ported floor, except under godot-flui's pipeline (pale sand).
                    float[]? pigment = reg.Native ? (FluiPalette ? reg.FluiFloor : null) : reg.Floor;
                    if (pigment == null) { native += weightK; continue; }
                    // godot-flui's ground is pale and soft: its sand lifts the ported floor's values and flattens their
                    // steps (worn path cells, painted with sharp edges, read as grey slabs on sand otherwise).
                    double tk = reg.Native && !decoration ? 0.82 + 0.25 * t : t;
                    if (reg.Native && !decoration) flui += weightK;
                    sr += pigment[0] * tk * weightK;
                    sg += pigment[1] * tk * weightK;
                    sb += pigment[2] * tk * weightK;
                }
                if (native >= 0.999) continue;
                // The ported olive's hue stays out of the sand.
                hue = (float)(hue * (1 - flui));
                tr = (float)(sr + r * native);
                tg = (float)(sg + g * native);
                tb = (float)(sb + b * native);
            }
            else if (kind < 1.5f)
            {
                // Rock cap: the region's weathered cap, the vertex's value.
                double t = lum / CapLuminance;
                tr = mixed[c + 9]; tg = mixed[c + 10]; tb = mixed[c + 11];
                Scale(ref tr, ref tg, ref tb, t);
            }
            else if (kind < 2.5f)
            {
                // Earth riser: the region's soil.
                double t = lum / EarthLuminance;
                tr = mixed[c + 12]; tg = mixed[c + 13]; tb = mixed[c + 14];
                Scale(ref tr, ref tg, ref tb, t);
            }
            else
            {
                // Rock face: deep → mid → lit with the vertex's value, then exactly that value.
                double t = lum / FaceLuminance;
                double low = Math.Clamp((t - 0.45) / 0.55, 0, 1), high = Math.Clamp((t - 1) / 0.5, 0, 1);
                low = low * low * (3 - 2 * low);
                high = high * high * (3 - 2 * high);
                int from = t <= 1 ? c + 6 : c + 3, to = t <= 1 ? c + 3 : c;
                double s = t <= 1 ? low : high;
                tr = (float)(mixed[from] + (mixed[to] - mixed[from]) * s);
                tg = (float)(mixed[from + 1] + (mixed[to + 1] - mixed[from + 1]) * s);
                tb = (float)(mixed[from + 2] + (mixed[to + 2] - mixed[from + 2]) * s);
                double mid = 0.2126 * mixed[c + 3] + 0.7152 * mixed[c + 4] + 0.0722 * mixed[c + 5];
                double now = 0.2126 * tr + 0.7152 * tg + 0.0722 * tb;
                if (now > 1e-5)
                {
                    float k = (float)(mid * t / now);
                    tr *= k; tg *= k; tb *= k;
                }
            }
            // A little of the ported hue at the new value: its variation survives the repaint.
            double tl = 0.2126 * tr + 0.7152 * tg + 0.0722 * tb;
            float keep = (float)(tl / lum);
            color[v * 3] = tr + (r * keep - tr) * hue;
            color[v * 3 + 1] = tg + (g * keep - tg) * hue;
            color[v * 3 + 2] = tb + (b * keep - tb) * hue;
        }
        RecolourPlants(geometry, frame, terrain, cellRegion, cellWeight);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mixed);
            ArrayPool<int>.Shared.Return(cellRegion);
            ArrayPool<float>.Shared.Return(cellWeight);
        }
    }

    private const double FloorLuminance = 0.407;

    /// <summary>
    /// Plants take their region's species and pigments: each plant picks one of its cell's regions by its own seed,
    /// weighted by the regions' shares, so a border mixes both floras instead of blending one plant into grey. Trees take
    /// the region's crown forms (conifers in the mist groves) and blossom only as often as the region blooms; the start's
    /// own region keeps the ported plants.
    /// </summary>
    private static void RecolourPlants(TerrainGeometryPayload geometry, TerrainBakeFrame frame, MaterializedTerrain terrain,
        int[] cellRegion, float[] cellWeight)
    {
        if (geometry.vegetation is not { } lane || lane.records.Length == 0) return;
        const int S = FluitownVegetation.Stride;
        // The organic form hands over a fresh lane; without it the lane may be the dressing layer's own: copy.
        float[] records = TerrainOrganicForm.Enabled ? lane.records : (float[])lane.records.Clone();
        int w = terrain.width, h = terrain.height;
        double ts = frame.tileSize;
        for (int i = 0; i + S <= records.Length; i += S)
        {
            int kind = (int)MathF.Round(records[i]);
            float fx = records[i + FluitownVegetation.X], fz = records[i + FluitownVegetation.Z];
            int cx = Math.Clamp((int)Math.Floor((fx - frame.originX) / ts) - frame.i0, 0, w - 1);
            int cz = Math.Clamp((int)Math.Floor((fz - frame.originY) / ts) - frame.j0, 0, h - 1);
            int cell = cz * w + cx;
            double pick = Hash01((int)(fx * 7.31f), (int)(fz * 5.17f), 901);
            int chosen = cellRegion[cell * 4];
            double running = 0;
            for (int k = 0; k < 4; k++)
            {
                running += cellWeight[cell * 4 + k];
                if (cellWeight[cell * 4 + k] > 0 && pick <= running) { chosen = cellRegion[cell * 4 + k]; break; }
            }
            var region = Regions[chosen];
            if (region.Native) continue;
            switch (kind)
            {
                case FluitownVegetation.KindTree:
                {
                    records[i + FluitownVegetation.ColourLeaf] = Tone(records[i + FluitownVegetation.ColourLeaf], region.Leaf, 0.62);
                    records[i + FluitownVegetation.ColourShade] = Tone(records[i + FluitownVegetation.ColourShade], region.Shade, 0.4);
                    records[i + FluitownVegetation.ColourWood] = region.Bark;
                    records[i + FluitownVegetation.ColourAccent] = region.Bloom;
                    double seed = Hash01((int)(fx * 3.1f), (int)(fz * 2.9f), 902);
                    double[] crowns = region.Crowns;
                    double sum = 0;
                    int form = crowns.Length - 1;
                    for (int f = 0; f < crowns.Length; f++)
                    {
                        sum += crowns[f];
                        if (seed <= sum) { form = f; break; }
                    }
                    records[i + FluitownVegetation.Form] = form;
                    if (Hash01((int)(fx * 1.7f), (int)(fz * 1.3f), 903) >= region.BloomChance) records[i + FluitownVegetation.Bloom] = 0;
                    break;
                }
                case FluitownVegetation.KindBush:
                    records[i + FluitownVegetation.ColourLeaf] = Tone(records[i + FluitownVegetation.ColourLeaf], region.Leaf, 0.5);
                    records[i + FluitownVegetation.ColourShade] = Tone(records[i + FluitownVegetation.ColourShade], region.Shade, 0.4);
                    records[i + FluitownVegetation.ColourWood] = region.Bark;
                    records[i + FluitownVegetation.ColourAccent] = region.Bloom;
                    break;
                case FluitownVegetation.KindGrass:
                {
                    int turf = Hex(region.Turf);
                    records[i + FluitownVegetation.ColourLeaf] = Tone(records[i + FluitownVegetation.ColourLeaf], turf, 0.55);
                    records[i + FluitownVegetation.ColourShade] = Tone(records[i + FluitownVegetation.ColourShade], Darker(turf, 0.72), 0.4);
                    records[i + FluitownVegetation.Lean] = Tone(records[i + FluitownVegetation.Lean], region.GrassTip, 0.7);
                    break;
                }
            }
        }
        if (!ReferenceEquals(records, lane.records)) geometry.vegetation = new TerrainVegetationLane { records = records };
    }

    /// <summary>The region's sRGB pigment at the ported pigment's value relative to a reference value (keeps variation).</summary>
    private static float Tone(float original, int target, double referenceValue)
    {
        double k = Math.Clamp(Value((int)original) / referenceValue, 0.55, 1.5);
        int r = Math.Clamp((int)Math.Round(((target >> 16) & 255) * k), 0, 255);
        int g = Math.Clamp((int)Math.Round(((target >> 8) & 255) * k), 0, 255);
        int b = Math.Clamp((int)Math.Round((target & 255) * k), 0, 255);
        return (r << 16) | (g << 8) | b;
    }

    private static double Value(int hex) => (0.2126 * ((hex >> 16) & 255) + 0.7152 * ((hex >> 8) & 255) + 0.0722 * (hex & 255)) / 255.0;

    private static int Darker(int hex, double k) =>
        ((int)(((hex >> 16) & 255) * k) << 16) | ((int)(((hex >> 8) & 255) * k) << 8) | (int)((hex & 255) * k);

    /// <summary>A linear pigment as sRGB hex.</summary>
    private static int Hex(float[] linear)
    {
        static int C(float v)
        {
            double c = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            return Math.Clamp((int)Math.Round(c * 255), 0, 255);
        }
        return (C(linear[0]) << 16) | (C(linear[1]) << 8) | C(linear[2]);
    }

    private static void Scale(ref float r, ref float g, ref float b, double t)
    {
        float k = (float)Math.Clamp(t, 0.2, 2.2);
        r *= k; g *= k; b *= k;
    }

    private static void MixInto(float[] into, int at, ReadOnlySpan<int> region, ReadOnlySpan<double> weight, Func<Region, float[]> pick)
    {
        Mix(region, weight, pick, out float r, out float g, out float b);
        into[at] = r;
        into[at + 1] = g;
        into[at + 2] = b;
    }
}
