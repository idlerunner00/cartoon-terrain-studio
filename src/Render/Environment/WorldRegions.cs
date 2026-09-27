// Fluitown extension — NOT a port of the original. Regions that feel like travel: the colour world and the vegetation
// change with the distance from the start. Only reached with WorldRegions.Enabled (Explore's "regions" option).
using System;
using System.Collections.Concurrent;
using Fluitown.Domain;

namespace Fluitown.Render;

/// <summary>
/// The world is one theme (sakura_temple_dream); the comic look paints it in regions after godot-flui's twelve biome
/// palettes (runtime/worldgen/WorldLandscape.cs): cherry-blossom meadows around the start, then meadows, conifer forest
/// and autumn woods, then highland, coast and steppe, and copper cliffs far out. A region belongs to one Country of the
/// generator (4 × 4 chunks, 320 m): its ring around the start and its landscape grammar (a lake country becomes coast, a
/// pass highland, a mesa country copper cliffs) choose it, a hash picks among the rest. Neighbouring Countries blend over
/// about 160 m around their border, along a borderline bent by a 75 m noise. Everything is a pure function of the world
/// seed and the position, so bake workers, the shader's region texture and the probes agree.
/// </summary>
public static partial class WorldRegions
{
    /// <summary>Set before the first bake (Explore's "regions" option).</summary>
    public static volatile bool Enabled;

    private static double _seed;
    private static string _biome = "";
    private static readonly ConcurrentDictionary<long, int> CountryRegion = new();

    /// <summary>The world's seed and theme (the dungeon descriptor's): call before the first bake.</summary>
    public static void Configure(double seed, string biomeKey)
    {
        _seed = seed;
        _biome = biomeKey ?? "";
        CountryRegion.Clear();
    }

    public sealed class Region
    {
        public required string Key, Name;
        /// <summary>Linear pigments: rock faces lit / mid / deep, weathered caps, earth risers.</summary>
        public required float[] RockLit, RockMid, RockDeep, Cap, Soil;
        /// <summary>Linear pigments of the rock's growth (moss at wall feet, grass over crests and on caps).</summary>
        public required float[] Moss, CapGrass;
        /// <summary>Linear pigments of the meadow: lush pole, turf, dry and mineral patches, the floor's base.</summary>
        public required float[] Lush, Turf, Dry, Mineral, Floor;
        /// <summary>sRGB plant pigments: leaf, shade, blossom, bark, grass tip.</summary>
        public required int Leaf, Shade, Bloom, Bark, GrassTip;
        /// <summary>Crown forms (round, conic, fan, columnar) and the share of blossoming trees.</summary>
        public required double[] Crowns;
        public required double BloomChance;
        /// <summary>sRGB sky of the Flui perspective: horizon and zenith at noon.</summary>
        public required int Horizon, Zenith;
        /// <summary>Whether the region keeps the ported pigments of the floor and the plants (the start's own look).</summary>
        public bool Native;
        /// <summary>
        /// The start's own region under godot-flui's image pipeline (<see cref="FluiPalette"/>): its floor, dry and
        /// mineral patches in godot-flui's pale cream and sand instead of the ported olive-brown dirt, its moss and
        /// crest grass teal (godot-flui's world pigment). Turf and lush patches stay the theme's own.
        /// </summary>
        public float[]? FluiFloor, FluiDry, FluiMineral, FluiMoss, FluiCapGrass;
    }

    /// <summary>The comic look runs godot-flui's image pipeline (TerrainSceneRenderer.FluiLook): the start's own region
    /// takes its Flui pigments. Set before the first bake.</summary>
    public static volatile bool FluiPalette;

    private static float[] Lin(int hex)
    {
        static float C(int v)
        {
            double c = v / 255.0;
            return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return new[] { C((hex >> 16) & 255), C((hex >> 8) & 255), C(hex & 255) };
    }

    /// <summary>The regions (index = region id). 0 is the start's own cherry-blossom world.</summary>
    public static readonly Region[] Regions =
    {
        new()
        {
            Key = "sakura", Name = "Cherry blossom groves", Native = true,
            RockLit = Lin(0xe6c795), RockMid = Lin(0xc49c6a), RockDeep = Lin(0x876248), Cap = Lin(0xdcc39a), Soil = Lin(0x9c7a58),
            Moss = Lin(0x6f7b3c), CapGrass = Lin(0x8c9a55),
            Lush = Lin(0xe58aa0), Turf = Lin(0xe796a8), Dry = Lin(0xc9a57a), Mineral = Lin(0xbfb2a2), Floor = Lin(0xd8a39c),
            Leaf = 0xe6b4c4, Shade = 0xb07c92, Bloom = 0xf4d2de, Bark = 0x5e4538, GrassTip = 0xf0b9c4,
            Crowns = new[] { .34, .08, .5, .08 }, BloomChance = .7, Horizon = 0xe9d4b4, Zenith = 0x6f9ab6,
            FluiFloor = Lin(0xe6d6b8), FluiDry = Lin(0xdcc9a2), FluiMineral = Lin(0xd2cbbd),
            FluiMoss = Lin(0x66886f), FluiCapGrass = Lin(0x8aa590),
        },
        new()
        {
            Key = "meadow", Name = "Jade meadows",
            RockLit = Lin(0xd9d3c2), RockMid = Lin(0xaba493), RockDeep = Lin(0x6a665a), Cap = Lin(0xcfc9b4), Soil = Lin(0x8a7a5c),
            Moss = Lin(0x5f7a3a), CapGrass = Lin(0x7fa052),
            Lush = Lin(0x8fbf62), Turf = Lin(0x9cc86c), Dry = Lin(0xc2b27a), Mineral = Lin(0xb8b2a0), Floor = Lin(0x9fb870),
            Leaf = 0x86b25c, Shade = 0x4f7a3e, Bloom = 0xf2e6a0, Bark = 0x5a4632, GrassTip = 0xc6dc86,
            Crowns = new[] { .56, .12, .22, .1 }, BloomChance = .12, Horizon = 0xe4dcbc, Zenith = 0x6a9cc0,
        },
        new()
        {
            Key = "taiga", Name = "Mist groves",
            RockLit = Lin(0xb9bdb2), RockMid = Lin(0x8a908a), RockDeep = Lin(0x525955), Cap = Lin(0xaeb3a6), Soil = Lin(0x6b5c47),
            Moss = Lin(0x4a6636), CapGrass = Lin(0x5f7f45),
            Lush = Lin(0x5f8a52), Turf = Lin(0x6b9658), Dry = Lin(0x9a8a62), Mineral = Lin(0x9d9a8c), Floor = Lin(0x6f8a5a),
            Leaf = 0x3f6b48, Shade = 0x24422f, Bloom = 0xdfe8c8, Bark = 0x4a3a2c, GrassTip = 0x9fbf7a,
            Crowns = new[] { .08, .72, .02, .18 }, BloomChance = 0, Horizon = 0xd6dccc, Zenith = 0x5d8aa6,
        },
        new()
        {
            Key = "alpine", Name = "Alpine peaks",
            RockLit = Lin(0xb3bcc6), RockMid = Lin(0x7f8a98), RockDeep = Lin(0x4b5462), Cap = Lin(0xc2c8cc), Soil = Lin(0x6e6a60),
            Moss = Lin(0x66734a), CapGrass = Lin(0x8e9a6e),
            Lush = Lin(0x9aae7c), Turf = Lin(0xa6b888), Dry = Lin(0xb9b49a), Mineral = Lin(0xc6c8c8), Floor = Lin(0xa3b08e),
            Leaf = 0x557a5e, Shade = 0x2f4a3c, Bloom = 0xe8e4f4, Bark = 0x4c4640, GrassTip = 0xcad6b0,
            Crowns = new[] { .06, .6, .0, .34 }, BloomChance = 0, Horizon = 0xdfe3e2, Zenith = 0x5f8fb8,
        },
        new()
        {
            Key = "coast", Name = "Coast & islands",
            RockLit = Lin(0xefe8d6), RockMid = Lin(0xcfc5ad), RockDeep = Lin(0x908670), Cap = Lin(0xe6ddc6), Soil = Lin(0xb8a47e),
            Moss = Lin(0x77844a), CapGrass = Lin(0x9aa864),
            Lush = Lin(0xa7c47a), Turf = Lin(0xb2cc86), Dry = Lin(0xe2d2a4), Mineral = Lin(0xe8e0cc), Floor = Lin(0xd9cfa6),
            Leaf = 0x6e9a58, Shade = 0x3f6a44, Bloom = 0xfff0d0, Bark = 0x6a5440, GrassTip = 0xd8e2a0,
            Crowns = new[] { .3, .1, .5, .1 }, BloomChance = .05, Horizon = 0xeae4cc, Zenith = 0x5aa2c8,
        },
        new()
        {
            Key = "autumn", Name = "Rose dusk forest",
            RockLit = Lin(0xd9aa86), RockMid = Lin(0xae7b5a), RockDeep = Lin(0x6d4a37), Cap = Lin(0xd2b08e), Soil = Lin(0x7e5a40),
            Moss = Lin(0x6c6e36), CapGrass = Lin(0xa0924e),
            Lush = Lin(0xc49a5a), Turf = Lin(0xc8a262), Dry = Lin(0xc49a6c), Mineral = Lin(0xb4a494), Floor = Lin(0xb8925e),
            Leaf = 0xd58a3c, Shade = 0x9a4c2a, Bloom = 0xf0c060, Bark = 0x4e3628, GrassTip = 0xe2c27a,
            Crowns = new[] { .52, .14, .24, .1 }, BloomChance = .3, Horizon = 0xecd2b0, Zenith = 0x6f94b0,
        },
        new()
        {
            Key = "steppe", Name = "Amber steppe",
            RockLit = Lin(0xe2d0a6), RockMid = Lin(0xbea77a), RockDeep = Lin(0x7c6a4a), Cap = Lin(0xdccaa0), Soil = Lin(0xa08058),
            Moss = Lin(0x7d7c42), CapGrass = Lin(0xb0a45c),
            Lush = Lin(0xc2b264), Turf = Lin(0xcab86e), Dry = Lin(0xd8be86), Mineral = Lin(0xcdbfa2), Floor = Lin(0xc8b074),
            Leaf = 0x9aa05a, Shade = 0x5e6a3a, Bloom = 0xf6e2a0, Bark = 0x5c4a36, GrassTip = 0xe6d692,
            Crowns = new[] { .4, .08, .44, .08 }, BloomChance = .08, Horizon = 0xefdcb4, Zenith = 0x6c9cbc,
        },
        new()
        {
            Key = "badlands", Name = "Copper cliffs",
            RockLit = Lin(0xe0906a), RockMid = Lin(0xb3643f), RockDeep = Lin(0x6b3a27), Cap = Lin(0xd8a07a), Soil = Lin(0x94583a),
            Moss = Lin(0x7a6a3a), CapGrass = Lin(0xa08c54),
            Lush = Lin(0xb8a064), Turf = Lin(0xc0a86c), Dry = Lin(0xd29a6c), Mineral = Lin(0xc49a80), Floor = Lin(0xc08a60),
            Leaf = 0x8c9a58, Shade = 0x566236, Bloom = 0xf0b080, Bark = 0x5a3a2a, GrassTip = 0xdcc68a,
            Crowns = new[] { .3, .1, .5, .1 }, BloomChance = .05, Horizon = 0xefcfae, Zenith = 0x7496b4,
        },
    };

    public const int Sakura = 0, Meadow = 1, Taiga = 2, Alpine = 3, Coast = 4, Autumn = 5, Steppe = 6, Badlands = 7;

    private static readonly int[] Ring1 = { Sakura, Meadow, Taiga, Autumn, Meadow, Taiga };
    private static readonly int[] Ring2 = { Taiga, Alpine, Coast, Steppe, Meadow, Autumn };
    private static readonly int[] Ring3 = { Alpine, Coast, Steppe, Badlands, Autumn, Taiga };

    /// <summary>The region of one generator Country (cached; a pure function of seed, theme and Country).</summary>
    public static int RegionOfCountry(int countryX, int countryY)
    {
        long key = ((long)countryX << 32) ^ (uint)countryY;
        if (CountryRegion.TryGetValue(key, out int cached)) return cached;
        int region = ChooseRegion(countryX, countryY);
        CountryRegion[key] = region;
        return region;
    }

    private static int ChooseRegion(int countryX, int countryY)
    {
        if (countryX == 0 && countryY == 0) return Sakura;
        double distance = Math.Sqrt(countryX * (double)countryX + countryY * (double)countryY);
        int ring = distance < 1.5 ? 1 : distance < 2.6 ? 2 : 3;
        double roll = Hash01(countryX, countryY, 17);
        string landscape = "";
        try { landscape = EndlessLandscape.endlessCountryLandscapeAt(_seed, countryX, countryY, _biome).primary; }
        catch (Exception) { /* a theme without a landscape grammar: rings and hashes alone */ }
        int affinity = landscape switch
        {
            EndlessLandscapeKind.GreatLake or EndlessLandscapeKind.IslandLake or EndlessLandscapeKind.Archipelago or
                EndlessLandscapeKind.RiverDelta or EndlessLandscapeKind.CalderaLake or EndlessLandscapeKind.GlacialFjord or
                EndlessLandscapeKind.FloodedCaverns => ring == 1 ? Meadow : Coast,
            EndlessLandscapeKind.AlpinePass or EndlessLandscapeKind.HighlandMoor or EndlessLandscapeKind.RidgeMaze =>
                ring == 1 ? Taiga : Alpine,
            EndlessLandscapeKind.MesaBadlands or EndlessLandscapeKind.GrandCanyon or EndlessLandscapeKind.StoneForest =>
                ring == 1 ? Autumn : ring == 2 ? Steppe : Badlands,
            EndlessLandscapeKind.OasisBasins => ring == 1 ? Meadow : Steppe,
            _ => -1,
        };
        if (affinity >= 0 && roll < 0.7) return affinity;
        int[] pool = ring == 1 ? Ring1 : ring == 2 ? Ring2 : Ring3;
        return pool[(int)(Hash01(countryX, countryY, 29) * pool.Length) % pool.Length];
    }

    /// <summary>Global tile coordinate of compile px (tile g spans [g·62.5 − 1000, +62.5)).</summary>
    private static double TileOf(double px) => (px + 1000) / 62.5;

    /// <summary>
    /// The regions at compile px (x, z) and their weights (up to four, summing to 1; unused slots weigh 0). Countries
    /// are 128 tiles; their centres sit at tiles 128·X. The position is bent by a 75 m noise, then the four Countries
    /// around it blend with smoothstep weights over the middle half of the gap between their centres.
    /// </summary>
    public static void Sample(double x, double z, Span<int> region, Span<double> weight)
    {
        region.Fill(0);
        weight.Clear();
        double tx = TileOf(x), tz = TileOf(z);
        // The borderline wanders: ±30 tiles along a ~200-tile noise.
        tx += (Noise(tx / 200.0, tz / 200.0, 3) - 0.5) * 60.0;
        tz += (Noise(tx / 200.0 + 17.3, tz / 200.0 - 9.1, 5) - 0.5) * 60.0;
        double fx = tx / 128.0, fz = tz / 128.0;
        int x0 = (int)Math.Floor(fx), z0 = (int)Math.Floor(fz);
        double sx = Smooth(0.25, 0.75, fx - x0), sz = Smooth(0.25, 0.75, fz - z0);
        int used = 0;
        for (int k = 0; k < 4; k++)
        {
            int cx = x0 + (k & 1), cz = z0 + (k >> 1);
            double w = ((k & 1) == 1 ? sx : 1 - sx) * ((k >> 1) == 1 ? sz : 1 - sz);
            if (w <= 0) continue;
            int r = RegionOfCountry(cx, cz);
            int slot = -1;
            for (int s = 0; s < used; s++) if (region[s] == r) { slot = s; break; }
            if (slot < 0) { slot = used++; region[slot] = r; }
            weight[slot] += w;
        }
        // Home: the start keeps the theme's own look out to 150 m and fades into the regions by 250 m (the wandering
        // position gives the edge its bends).
        double hx = tx - StartTile, hz = tz - StartTile;
        double home = 1 - Smooth(HomeTiles, HomeTiles + HomeFadeTiles, Math.Sqrt(hx * hx + hz * hz));
        if (home <= 0) return;
        int own = -1, lightest = 0;
        for (int s = 0; s < used; s++)
        {
            weight[s] *= 1 - home;
            if (region[s] == Sakura) own = s;
            if (weight[s] < weight[lightest]) lightest = s;
        }
        if (own < 0)
        {
            own = used < 4 ? used : lightest;
            if (used == 4) weight[own] = 0;
            region[own] = Sakura;
        }
        weight[own] += home;
        // Replacing the lightest of four countries must not lose its contribution: pigments and atmospheric
        // colours are convex blends, including the narrow five-country overlap at the edge of home.
        double total = weight[0] + weight[1] + weight[2] + weight[3];
        if (total > 0 && total < 0.999999999)
            for (int s = 0; s < 4; s++) weight[s] /= total;
    }

    /// <summary>The start's tile (compile px 0) and the home disc around it, in tiles (2.5 m).</summary>
    private const double StartTile = 16, HomeTiles = 60, HomeFadeTiles = 40;

    /// <summary>Weighted mix of a per-region linear colour.</summary>
    public static void Mix(ReadOnlySpan<int> region, ReadOnlySpan<double> weight, Func<Region, float[]> pick, out float r, out float g, out float b)
    {
        double sr = 0, sg = 0, sb = 0;
        for (int s = 0; s < region.Length; s++)
        {
            if (weight[s] <= 0) continue;
            float[] c = pick(Regions[region[s]]);
            sr += c[0] * weight[s];
            sg += c[1] * weight[s];
            sb += c[2] * weight[s];
        }
        r = (float)sr; g = (float)sg; b = (float)sb;
    }

    private static double Smooth(double a, double b, double t)
    {
        double u = Math.Clamp((t - a) / (b - a), 0, 1);
        return u * u * (3 - 2 * u);
    }

    internal static double Hash01(int x, int y, int salt)
    {
        unchecked
        {
            // Mixed in sequence (a plain xor of odd multiples maps (x, y) and (-x, -y) to the same value).
            uint h = (uint)x * 0x8da6b343u + 0x68e31da4u;
            h ^= h >> 15;
            h = (h + (uint)y) * 0xd8163841u;
            h ^= h >> 13;
            h = (h + (uint)salt) * 0x9e3779b9u;
            h ^= (uint)(long)_seed * 0x85ebca6bu;
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            return h / 4294967296.0;
        }
    }

    /// <summary>Smooth value noise in [0, 1] (quintic fade).</summary>
    private static double Noise(double x, double y, int salt)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        double ux = fx * fx * fx * (fx * (fx * 6 - 15) + 10), uy = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
        double a = Hash01(ix, iy, salt), b = Hash01(ix + 1, iy, salt), c = Hash01(ix, iy + 1, salt), d = Hash01(ix + 1, iy + 1, salt);
        return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
    }
}
