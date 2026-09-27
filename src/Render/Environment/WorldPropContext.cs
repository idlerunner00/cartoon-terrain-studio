// Port of packages/client/src/render/environment/worldPropContext.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `PropTileset` is `TerrainMaterialTileset` (see WorldPropPrimitives.cs); `felledTree?: TreeVisual & { id }` is the
//   render-side TerrainTreeLike (TreeGeometry.cs).
// * The context's `hash` arrow function and `ground` method are closures over the destructured locals in TS; they
//   stay closures here (delegate fields built inside worldPropContext), so they capture exactly what the original
//   captures. The inline ground-options type `{ sides?, taper?, alpha? }` has no TS name → WorldPropGroundOptions.
// * `PROP_UNITS` is a frozen plain object that is only ever indexed → Dictionary. (A plain JS object would also
//   answer inherited keys such as "constructor"; decoration kinds are registry keys, never those.)
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.TreeVisualModule;
using static Fluitown.Domain.WorldDecoration;
using static Fluitown.Render.WorldScale;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TreeGeometry;
using static Fluitown.Render.WorldPropPrimitives;
using Math = Fluitown.Runtime.JsMath;
using PropTileset = Fluitown.Render.TerrainMaterialTileset;

namespace Fluitown.Render;

public sealed class WorldDecorationGeometryOptions
{
    public PropTileset tileset;
    public WorldDecorationVisual decoration;
    public double x;
    public double z;
    public double y0;
    public double tileSize;
    /// <summary>
    /// The world's plant identity. Undergrowth, moss and vines are recoloured by the SAME registry row as the
    /// canopy, so a run can never read as two different ecologies. Absent means temperate.
    /// </summary>
    /// <remarks>A TreeVisualProfileId.</remarks>
    public string? foliageProfile;
    /// <summary>Mature source phenotype for a tree-life stump; absent on ordinary decorative stumps.</summary>
    public TerrainTreeLike? felledTree;
}

/// <summary>The inline `{ readonly sides?: number; readonly taper?: number; readonly alpha?: number }` of `ground`.</summary>
public sealed class WorldPropGroundOptions
{
    public int? sides;
    public double? taper;
    public double? alpha;
}

/// <summary>`hash(salt: number): number`.</summary>
public delegate double WorldPropHash(double salt);

/// <summary>`ground(builder, radius, height, options?)`.</summary>
public delegate void WorldPropGround(
    PropGeometryBuilder builder,
    double radius,
    double height,
    WorldPropGroundOptions? options = null);

public sealed class WorldPropContext
{
    public WorldDecorationGeometryOptions options;
    public PropTileset tileset;
    public WorldDecorationVisual decoration;
    public HubPropPalette palette;
    public double x;
    public double z;
    public double y0;
    public double tileSize;
    /// <summary>Deterministic per-placement salt. Every family samples it instead of inventing its own scheme.</summary>
    public int seed;
    public double rot;
    /// <summary>Standing height in world px, resolved from <see cref="WorldPropContextModule.PROP_UNITS"/> and the placement's normalised scale.</summary>
    public double height;
    /// <summary>The placement's own size hint, normalised to a ±variation around 1.</summary>
    public double sizeBias;
    /// <summary>The world's ONE saturated identity hue for this object.</summary>
    public int accent;
    public int stoneTop;
    public int stoneLit;
    public int stoneSide;
    public int ink;
    /// <summary>Bark for anything woody — the bridges' sunlit timber, never a pull toward ink.</summary>
    public int barkTop;
    public int barkSide;
    /// <summary>The world's leaf pigment, for moss, regrowth, vines and offerings.</summary>
    public int foliage;
    public int foliageSide;
    public WorldPropHash hash;
    /// <summary>
    /// Ground the prop: a tight ambient-occlusion contact patch AND a low-poly sun-shadow proxy.
    ///
    /// Both halves belong together, so no family can ship one without the other. `radius`/`height` describe
    /// the prop's main mass — keep the proxy inside the real silhouette.
    /// </summary>
    public WorldPropGround ground;
}

/// <summary>
/// The resolved standing context every world prop is built against.
///
/// Each prop family lives in its own module, but they must not each re-derive "what colour is stone here",
/// "how big is a person" or "which plant does this world grow". Those answers are computed once, here, and
/// handed down — which is what keeps a bush and a stump looking like they belong to the same world as the trees
/// standing behind them.
///
/// ## Heights are player-relative, footprints are tile-relative
/// A Hub tile is 40 px and an Endless tile is 62.5 px. Anything a player judges the size of by looking at it —
/// how tall a stump stands, how far a shrub reaches — is authored in player heights. Only the ground footprint
/// follows the carrier grid.
/// </summary>
public static partial class WorldPropContextModule
{
    /// <summary>
    /// Standing height of each prop family, in player heights. The one place a prop's real size is decided.
    ///
    /// This is the height the prop actually REACHES, not a reference number it is then a fraction of. Every
    /// family is expected to put its topmost geometry at `ctx.height`; a family that only reaches two thirds of
    /// it silently becomes a different, smaller object than the table says, and the table stops being readable.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (double, double)> PROP_UNITS = new Dictionary<string, (double, double)>
    {
        // Knee to hip: unmistakably the remains of something that used to be a tree.
        ["stump"] = (0.62, 1.05),
    };

    public static WorldPropContext worldPropContext(WorldDecorationGeometryOptions options)
    {
        PropTileset tileset = options.tileset;
        WorldDecorationVisual decoration = options.decoration;
        double x = options.x;
        double z = options.z;
        double y0 = options.y0;
        double tileSize = options.tileSize;
        HubPropPalette palette = propPaletteFor(tileset);
        string profile = options.foliageProfile ?? treeVisualProfileForBiome(null);
        int seed = Js.ToInt32(
            Js.ToInt32(Math.round(x * 3.7)) ^
            Js.ToInt32(Math.round(z * 5.1)) ^
            (decoration.variant * 733 + decoration.cluster * 91));
        double accentIndex =
            Math.floor(decoration.accent * palette.accents.Count) % palette.accents.Count;
        // `palette.accents[accentIndex]!` — an index outside the table (NaN/negative accent) reads `undefined`,
        // which `mix` reads through ToInt32 as 0.
        int accentBase = accentIndex >= 0 && accentIndex < palette.accents.Count
            ? palette.accents[(int)accentIndex]
            : 0;
        int accent = mix(
            accentBase,
            tileset.decal.accent,
            0.22 + decoration.accent * 0.18);
        (double, double) band = PROP_UNITS.TryGetValue(decoration.kind, out (double, double) authored)
            ? authored
            : (0.8, 1.4);
        double units =
            band.Item1 +
            (band.Item2 - band.Item1) * ((double)decoration.variant / 2) * 0.62 +
            (band.Item2 - band.Item1) * decoration.phase * 0.38;
        /*
         * The placement's `scale` is a RELATIVE hint, so it is normalised against its own kind's band before it is
         * allowed anywhere near an absolute height. Used raw it double-counts — that is how the old monoliths, whose
         * registry band was authored when heights were tile fractions, became eight player heights tall. Normalised,
         * PROP_UNITS stays the single source of size truth.
         */
        double sizeBias = Math.min(
            1.24,
            Math.max(0.8, decoration.scale / worldDecorationScaleMid(decoration.kind)));
        int bark = mix(tileset.bridge.lip, tileset.bridge.body, 0.34 + decoration.weathering * 0.28);
        int foliage = foliageColorFor(tileset, profile, decoration.accent);
        return new WorldPropContext
        {
            options = options,
            tileset = tileset,
            decoration = decoration,
            palette = palette,
            x = x,
            z = z,
            y0 = y0,
            tileSize = tileSize,
            seed = seed,
            rot = decoration.rotation,
            height = PLAYER_HEIGHT_PX * units * sizeBias,
            sizeBias = sizeBias,
            accent = accent,
            stoneTop = mix(palette.stoneTop, accent, 0.035 + decoration.accent * 0.035),
            stoneLit = palette.stoneLit,
            stoneSide = mix(palette.stoneSide, palette.ink, 0.08 + decoration.weathering * 0.1),
            ink = palette.ink,
            barkTop = mix(bark, tileset.terrain.wallLit, 0.16),
            barkSide = mix(bark, tileset.bridge.bodyShadow, 0.4 + decoration.weathering * 0.18),
            foliage = foliage,
            // ONE rule for a shaded leaf, shared with every canopy: the leaf deepened against itself.
            foliageSide = foliageSideColorFor(profile, foliage),
            hash = (double salt) => propHash(seed + salt * 61),
            ground = (PropGeometryBuilder builder, double radius, double height, WorldPropGroundOptions? options) =>
            {
                int sides = options?.sides ?? 5;
                double taper = options?.taper ?? 0.72;
                propShadow(builder, x, z, y0, radius * 1.55, radius * 0.8, options?.alpha ?? 0.12);
                propShadowVolume(
                    builder,
                    x,
                    z,
                    y0,
                    y0 + height,
                    radius * 0.92,
                    radius * 0.92 * taper,
                    sides,
                    decoration.rotation);
            },
        };
    }
}
