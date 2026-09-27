// Fluitown extension — NOT a port of the original. The comic look's vegetation. `Enabled` follows the Style drawer's
// "3D vegetation" switch; without it the ported plant geometry stays.
using System;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerModule;
using static Fluitown.Render.TreeGeometry;
using Math = Fluitown.Runtime.JsMath;
using PropTileset = Fluitown.Render.TerrainMaterialTileset;

namespace Fluitown.Render;

/// <summary>
/// The comic look's trees, bushes and grass are not baked into the terrain's surface lane. The bake decides WHERE
/// something grows and in which pigment — exactly as the original does, from the same render plan, habitat fields and
/// world recipes — and writes one record per plant into the payload's vegetation lane. The Godot vegetation layer
/// (godot/src/Godot/Rendering/Vegetation) grows the plant from its record: godot-flui's low-poly trees and bushes and
/// its meadow grass, sized for this terrain (a Flui is 0.94 m; trees 3–8 m).
///
/// Why: the original's plants are drawn for one fixed oblique camera (faceted clumps, camera-culled halves, blades as
/// flat ribbons half a Flui tall), and the lane shader cannot give them what godot-flui's plants have — their own wind,
/// grass that parts around the Flui, closed crowns that read from every side of the Flui perspective.
///
/// Records are in compile space (world px, y up); <see cref="Stride"/> floats each. Colours are 24-bit hex values
/// (exact in float32).
/// </summary>
public static class FluitownVegetation
{
    /// <summary>Set before the first bake (TerrainSceneRenderer.Initialize); bakes run on worker threads.</summary>
    public static volatile bool Enabled;

    /// <summary>World px per metre in the Godot world (TerrainSceneRenderer.CompileToWorld: 1/25 m per px).</summary>
    public const double PxPerMetre = 25;

    public const int Stride = 16;

    public const int KindTree = 1;
    public const int KindBush = 2;
    public const int KindGrass = 3;

    // Record slots.
    public const int Kind = 0, X = 1, Y = 2, Z = 3, Height = 4, Radius = 5, Seed = 6, Rotation = 7;
    public const int ColourLeaf = 8, ColourShade = 9, ColourWood = 10, ColourAccent = 11;
    /// <summary>Tree: crown form (0 round, 1 conic, 2 fan, 3 columnar). Grass: turf cover 0..1.</summary>
    public const int Form = 12;
    /// <summary>Tree: blossom 0..1. Grass: lean direction (radians). Bush: maturity 0..1.</summary>
    public const int Bloom = 13;
    /// <summary>Tree: lean 0..1 along <see cref="Rotation"/>. Grass: tip pigment.</summary>
    public const int Lean = 14;
    /// <summary>Tree: age / density 0..1. Bush: stems.</summary>
    public const int Density = 15;

    private static void Push(TileGeometryBuilder builder, ReadOnlySpan<double> record)
    {
        FloatBuf lane = builder.vegetation;
        lane.ensure(Stride);
        for (int i = 0; i < Stride; i++) lane.push(i < record.Length ? record[i] : 0);
    }

    // ── Trees ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records one tree of the canopy slot (TreeGeometry.addTerrainTreeGeometry in the comic look). The phenotype keeps
    /// its authored size ladder (small 2.3–3.1, medium 3.2–4.4, large 4.6–6.2 player heights, ×1.2 for heroes), its
    /// crown form and width, and the world's leaf, shade, bark and blossom pigments.
    /// </summary>
    public static bool recordTree(PropGeometryBuilder builder, TerrainTreeGeometryOptions opts)
    {
        if (!Enabled || builder is not TileGeometryBuilder tile) return false;
        PropTileset tileset = opts.tileset;
        TerrainTreeLike tree = opts.tree;
        double height = treeHeightPx(tree);
        if (!(height > 1)) return true;
        int canopy = opts.canopy;
        int shade = foliageShadeColorFor(canopy, Math.max(0.2, foliageShadeFor(tree.profile)));
        int bark = mix(tileset.bridge.lip, tileset.bridge.body, 0.34 + tree.barkHue * 0.3);
        int wood = mix(bark, tileset.bridge.bodyShadow, 0.25 + tree.barkHue * 0.2);
        // Blossom accents that resolve near ink read as holes under the comic ramp: a light blossom of the crown instead.
        int bloom = tree.bloom > 0.5
            ? FluitownSoftFoliage.softBloomHex(foliageBloomColorFor(tileset, tree.profile, tree.canopyHue), canopy, tileset)
            : canopy;
        double form = tree.crownForm switch { "conic" => 1, "fan" => 2, "columnar" => 3, _ => 0 };
        double seed = propHashSeed(tree.id);
        Span<double> r = stackalloc double[Stride];
        r[Kind] = KindTree;
        r[X] = opts.x;
        r[Y] = opts.y0;
        r[Z] = opts.z;
        r[Height] = height;
        r[Radius] = Math.max(0.16, tree.crownRadius) * height;
        r[Seed] = seed;
        r[Rotation] = tree.phase * Math.PI * 2;
        r[ColourLeaf] = saturateHex(canopy, 1.12);
        r[ColourShade] = saturateHex(shade, 1.18);
        r[ColourWood] = wood;
        r[ColourAccent] = bloom;
        r[Form] = form;
        r[Bloom] = tree.bloom > 0.5 ? tree.bloom : 0;
        r[Lean] = Math.max(0, Math.min(1, tree.lean));
        r[Density] = Math.max(0, Math.min(1, tree.crownDensity * 0.6 + tree.age * 0.4));
        Push(tile, r);
        return true;
    }

    // ── Bushes ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records one thicket (WorldDecorationGeometry.addBushGeometry in the comic look): shin to shoulder of a player
    /// (0.5–1.2 player heights), in its world's undergrowth pigment.
    /// </summary>
    public static bool recordBush(PropGeometryBuilder builder, WorldPropContext ctx)
    {
        if (!Enabled || builder is not TileGeometryBuilder tile) return false;
        var decoration = ctx.decoration;
        double maturity = decoration.variant / 2.0;
        // A bush of this terrain stands below the Flui's shoulder to a little above its head (0.5–1.2 m): the
        // original's ladder (0.5–1.2 player heights of 1.29 m) scaled to the Flui.
        double units = 0.5 + 0.7 * (maturity * 0.6 + decoration.phase * 0.4);
        double height = units * PxPerMetre * ctx.sizeBias;
        double spread = height * (0.72 + decoration.weathering * 0.3);
        Span<double> r = stackalloc double[Stride];
        r[Kind] = KindBush;
        r[X] = ctx.x;
        r[Y] = ctx.y0;
        r[Z] = ctx.z;
        r[Height] = height;
        r[Radius] = spread;
        r[Seed] = (ctx.seed & 0xffff) / 65536.0;
        r[Rotation] = ctx.rot;
        r[ColourLeaf] = saturateHex(mix(ctx.foliage, ctx.tileset.peakTint, decoration.accent * 0.1), 1.12);
        r[ColourShade] = saturateHex(ctx.foliageSide, 1.15);
        r[ColourWood] = ctx.barkSide;
        r[ColourAccent] = FluitownSoftFoliage.softBloomHex(ctx.accent, ctx.foliage, ctx.tileset);
        r[Bloom] = maturity;
        r[Density] = Math.min(6, 3 + decoration.variant + (decoration.cluster >= 4 ? 1 : 0));
        Push(tile, r);
        return true;
    }

    // ── Grass ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Tuft spacing of the meadow in metres (godot-flui's meadow density, one tuft of blades per site).</summary>
    public const double GrassSpacingMetres = 0.42;

    /// <summary>
    /// Records the standing grass of one floor cell (FloorGrassGeometry.addFloorGrass in the comic look). Sites lie on
    /// a jittered lattice in ABSOLUTE world coordinates (no seams at cell or tile borders) and survive with the turf
    /// cover the ground is painted with, so grass stands exactly where the floor reads as meadow and paths cut through
    /// it. A tuft's pigment is the mat it grows out of, lifted towards the world's lush pole.
    /// </summary>
    public static bool recordFloorGrass(PropGeometryBuilder builder, FloorGrassOptions options)
    {
        if (!Enabled || builder is not TileGeometryBuilder tile) return false;
        if (options.bladeBudget <= 0) return true;
        double ts = options.tileSize;
        double centre = options.coverAt(0.5, 0.5);
        double corners =
            options.coverAt(0.08, 0.08) + options.coverAt(0.92, 0.08) +
            options.coverAt(0.08, 0.92) + options.coverAt(0.92, 0.92);
        if (centre <= 0 && corners <= 0.04) return true;
        int lattice = (int)Math.max(2, Math.round(ts / PxPerMetre / GrassSpacingMetres));
        double windNoise = smoothCellNoise(options.cellX + 0.5, options.cellY + 0.5, 11, 73);
        double windAngle = 0.58 + windNoise * 0.76;
        Span<double> r = stackalloc double[Stride];
        for (int j = 0; j < lattice; j++)
        {
            for (int i = 0; i < lattice; i++)
            {
                double siteX = (double)options.cellX * lattice + i;
                double siteY = (double)options.cellY * lattice + j;
                double jx = cellHash(siteX * 73 + 19, siteY * 61 - 11);
                double jz = cellHash(siteX * 89 - 7, siteY * 97 + 29);
                double u = clamp((i + 0.5 + (jx - 0.5) * 0.95) / lattice, 0.01, 0.99);
                double v = clamp((j + 0.5 + (jz - 0.5) * 0.95) / lattice, 0.01, 0.99);
                double cover = options.coverAt(u, v);
                if (cover <= 0.05) continue;
                // Broad glades and smaller groups share world coordinates across cells and streamed tiles.
                // Keep the authored turf/path mask; only the standing blades thin out inside it.
                double patch = smoothCellNoise(options.cellX + u, options.cellY + v, 5.6, 113) * 0.72
                    + smoothCellNoise(options.cellX + u, options.cellY + v, 1.8, 127) * 0.28;
                double growth = clamp((patch - 0.27) / 0.44, 0, 1);
                growth = growth * growth * (3 - 2 * growth);
                double keep = cellHash(siteX * 211 - 29, siteY * 197 + 43);
                if (keep > (0.25 + cover * 0.95) * (0.28 + growth * 0.72)) continue;
                double roll = cellHash(siteX * 307 + 11, siteY * 331 - 19);
                int mat = options.matPigmentAt(u, v);
                // Towards the growth pole, a touch of light: a living, richer tone of the ground it stands on.
                int mid = saturateHex(mix(mix(mat, options.palette.lush, 0.32), options.palette.lit, 0.12), 1.06);
                int root = mix(mid, mat, 0.22);
                int tip = mix(mid, options.palette.lit, 0.22);
                // Short young growth feathers paths into mature patches. Height varies within each group,
                // so the clearing is not bordered by a continuous hedge of equally tall blades.
                double maturity = cover * (0.35 + growth * 0.65);
                double metres = (0.10 + maturity * 0.22) * (0.65 + roll * 0.75)
                    + (roll > 0.94 ? growth * 0.12 : 0);
                r.Clear();
                r[Kind] = KindGrass;
                r[X] = options.originX + u * ts;
                r[Y] = options.y0 + options.groundLift(u, v);
                r[Z] = options.originZ + v * ts;
                r[Height] = metres * PxPerMetre;
                r[Radius] = (0.105 + maturity * 0.095) * PxPerMetre;
                r[Seed] = roll;
                r[Rotation] = keep * Math.PI * 2;
                r[ColourLeaf] = mid;
                r[ColourShade] = root;
                r[Form] = cover;
                r[Bloom] = windAngle + (jx - 0.5) * 0.5;
                r[Lean] = tip;
                Push(tile, r);
            }
        }
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A stable 0..1 salt from a placement id.</summary>
    private static double propHashSeed(double id)
    {
        double s = Math.sin(id * 12.9898 + 78.233) * 43758.5453;
        return s - Math.floor(s);
    }

    /// <summary>Scales a colour's chroma around its luminance (1 = unchanged), in sRGB encoding.</summary>
    public static int saturateHex(int hex, double amount) => FluitownSoftFoliage.saturate(hex, amount);
}

/// <summary>The comic look's plant records of one payload (see <see cref="FluitownVegetation"/>).</summary>
public sealed class TerrainVegetationLane
{
    public float[] records = Array.Empty<float>();

    public int Count => records.Length / FluitownVegetation.Stride;

    public static TerrainVegetationLane? Slice(FloatBuf buf, int start, int end) =>
        end > start ? new TerrainVegetationLane { records = buf.sliceRange(start, end) } : null;

    public static TerrainVegetationLane? Merge(TerrainVegetationLane? left, TerrainVegetationLane? right)
    {
        if (left == null) return right;
        if (right == null) return left;
        var records = new float[left.records.Length + right.records.Length];
        Array.Copy(left.records, records, left.records.Length);
        Array.Copy(right.records, 0, records, left.records.Length, right.records.Length);
        return new TerrainVegetationLane { records = records };
    }
}
