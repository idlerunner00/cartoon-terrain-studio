// Port of packages/client/src/render/environment/terrainSurfaceProfile.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;

namespace Fluitown.Render;

/// <summary>
/// Authored procedural surface language for one biome family.
///
/// The values are consumed as shared shader uniforms: changing biome never creates a material per tile and
/// never changes the terrain draw-call count. Scales are world pixels, rotations are radians. The profile is
/// deliberately independent from palette data so two biomes remain recognisable by form and value structure
/// even when viewed in greyscale.
/// </summary>
/// <remarks>
/// PORT NOTE: the four `readonly [number, number, number, number]` tuples are length-4 <c>double[]</c> (hot-path
/// reads); the arrays of a frozen profile are never written.
/// </remarks>
public sealed class TerrainSurfaceProfile
{
    /// <summary>(broad scale, mound scale, maximum visual lift, prevailing form direction).</summary>
    public readonly double[] formA;
    /// <summary>(domain warp px, directed-ridge gain, terrace gain, directed-ridge period px).</summary>
    public readonly double[] formB;
    /// <summary>(macro, middle and tooth scale, material-field direction).</summary>
    public readonly double[] materialA;
    /// <summary>(lush, dry and mineral zone strength, zone-edge contrast).</summary>
    public readonly double[] materialB;
    /// <summary>
    /// This family's share of the shared atmosphere budget (`AERIAL_HAZE.maxAtTopEdge` in
    /// `aerialHaze.ts`), multiplied by the composition's `aerialPerspective` before it reaches the shader.
    /// These are RATIOS between places — how much air a drowned abyss owns against a paper-white citadel —
    /// so the overall strength is raised on the shared budget, never by re-typing seventeen numbers here.
    /// </summary>
    public readonly double haze;
    /// <summary>Whether ordinary Floor caps use the shared rolling-ground presentation/grounding field.</summary>
    public readonly bool organicGround;

    public TerrainSurfaceProfile(
        double[] formA,
        double[] formB,
        double[] materialA,
        double[] materialB,
        double haze,
        bool organicGround)
    {
        this.formA = formA;
        this.formB = formB;
        this.materialA = materialA;
        this.materialB = materialB;
        this.haze = haze;
        this.organicGround = organicGround;
    }
}

public static partial class TerrainSurfaceProfileModule
{
    private static readonly TerrainSurfaceProfile meadow = profile(
        new double[] { 360, 132, 2.55, -0.42 },
        new double[] { 42, 0.08, 0.08, 168 },
        new double[] { 390, 108, 30, -0.34 },
        new double[] { 0.2, 0.11, 0.08, 0.72 },
        0.92,
        true);

    // Aegis' procession courts are deliberately architectural: broad planar grades, laid-stone directionality
    // and restrained micro-relief keep the black paving calm beneath brilliant fortress silhouettes.
    private static readonly TerrainSurfaceProfile aegisCitadel = profile(
        new double[] { 520, 188, 1.86, -0.02 },
        new double[] { 20, 0.06, 0.4, 196 },
        new double[] { 540, 156, 44, 0 },
        new double[] { 0.02, 0.18, 0.2, 1.16 },
        0.86,
        false);

    /// <summary>
    /// `Readonly&lt;Record&lt;string, TerrainSurfaceProfile&gt;&gt;` (frozen). Insertion-ordered because
    /// <see cref="TERRAIN_SURFACE_PROFILE_BIOMES"/> is its `Object.keys` (no key is integer-like).
    /// </summary>
    private static readonly JsMap<string, TerrainSurfaceProfile> PROFILE_BY_BIOME = new JsMap<string, TerrainSurfaceProfile>()
        .set("hub", profile(
            new double[] { 360, 122, 2.65, -0.28 },
            new double[] { 42, 0.08, 0.12, 168 },
            new double[] { 382, 104, 28, -0.22 },
            new double[] { 0.24, 0.13, 0.1, 0.86 },
            0.78,
            true))
        .set("highland_pass", profile(
            new double[] { 338, 106, 2.75, -0.66 },
            new double[] { 54, 0.34, 0.24, 126 },
            new double[] { 342, 82, 24, -0.62 },
            new double[] { 0.12, 0.13, 0.2, 0.9 },
            1.06,
            true))
        .set("viking_ship_village", profile(
            new double[] { 372, 118, 2.58, -0.78 },
            new double[] { 48, 0.24, 0.22, 138 },
            new double[] { 386, 92, 26, -0.74 },
            new double[] { 0.12, 0.18, 0.16, 1.02 },
            1.12,
            true))
        // Alien Ranch: broad rolling pasture mounds combed by short-period directed ridges — PLOUGH FURROWS. The
        // furrow direction (+0.52 rad) is unique in the set and reads as farmed land even in greyscale.
        .set("alien_ranch", profile(
            new double[] { 392, 128, 2.6, 0.52 },
            new double[] { 46, 0.3, 0.18, 88 },
            new double[] { 402, 96, 26, 0.48 },
            new double[] { 0.22, 0.09, 0.12, 0.84 },
            0.98,
            true))
        .set("noir_sprawl", profile(
            new double[] { 480, 170, 1.8, 0.08 },
            new double[] { 18, 0.02, 0.18, 220 },
            new double[] { 520, 148, 42, 0.04 },
            new double[] { 0.04, 0.08, 0.18, 1.08 },
            1.18,
            true))
        .set("olympian_sky_borough", profile(
            new double[] { 440, 156, 2.25, -0.18 },
            new double[] { 30, 0.1, 0.32, 174 },
            new double[] { 460, 122, 31, -0.14 },
            new double[] { 0.08, 0.15, 0.17, 0.92 },
            1.08,
            true))
        .set("aegis_citadel", aegisCitadel)
        .set("raid_6", aegisCitadel)
        .set("sakura_temple_dream", profile(
            new double[] { 404, 142, 2.45, -0.48 },
            new double[] { 38, 0.05, 0.16, 186 },
            new double[] { 430, 126, 38, -0.42 },
            new double[] { 0.19, 0.1, 0.08, 0.66 },
            0.96,
            true))
        .set("abyssal_deepsea", profile(
            new double[] { 306, 96, 2.72, 0.24 },
            new double[] { 66, 0.28, 0.2, 112 },
            new double[] { 318, 74, 22, 0.2 },
            new double[] { 0.16, 0.05, 0.22, 1.12 },
            1.22,
            true))
        .set("rainbowland", profile(
            new double[] { 386, 136, 2.4, -0.36 },
            new double[] { 44, 0.08, 0.12, 178 },
            new double[] { 408, 112, 33, -0.3 },
            new double[] { 0.18, 0.14, 0.07, 0.82 },
            0.9,
            true))
        .set("clockwork_moon_bazaar", profile(
            new double[] { 350, 118, 2.2, 0.58 },
            new double[] { 24, 0.3, 0.42, 104 },
            new double[] { 356, 78, 20, 0.56 },
            new double[] { 0.04, 0.2, 0.2, 1.2 },
            1.04,
            true))
        .set("sugarstorm_carnival", profile(
            new double[] { 452, 164, 2.3, -0.08 },
            new double[] { 46, 0.06, 0.18, 204 },
            new double[] { 470, 138, 40, -0.04 },
            new double[] { 0.13, 0.17, 0.06, 0.74 },
            0.88,
            true))
        .set("prismglass_archive", profile(
            new double[] { 370, 116, 2.5, 0.72 },
            new double[] { 28, 0.38, 0.34, 94 },
            new double[] { 376, 84, 23, 0.7 },
            new double[] { 0.05, 0.08, 0.26, 1.28 },
            1.12,
            true))
        .set("starforged_cathedral_endrun", profile(
            new double[] { 326, 98, 2.62, -0.76 },
            new double[] { 42, 0.42, 0.38, 108 },
            new double[] { 332, 76, 21, -0.72 },
            new double[] { 0.03, 0.16, 0.24, 1.24 },
            1.16,
            true))
        .set("frogmire", profile(
            new double[] { 468, 176, 2.5, 0.12 },
            new double[] { 52, 0.03, 0.08, 216 },
            new double[] { 490, 144, 42, 0.08 },
            new double[] { 0.24, 0.04, 0.07, 0.76 },
            1.2,
            true))
        .set("wyrmforge", profile(
            new double[] { 298, 88, 2.7, -0.68 },
            new double[] { 58, 0.44, 0.22, 96 },
            new double[] { 306, 68, 19, -0.64 },
            new double[] { 0.01, 0.23, 0.26, 1.3 },
            1.08,
            true))
        .set("arena", profile(
            new double[] { 314, 92, 2.7, -0.7 },
            new double[] { 62, 0.4, 0.26, 102 },
            new double[] { 320, 72, 20, -0.66 },
            new double[] { 0.02, 0.24, 0.23, 1.26 },
            1.08,
            true));

    private static TerrainSurfaceProfile profile(
        double[] formA,
        double[] formB,
        double[] materialA,
        double[] materialB,
        double haze,
        bool organicGround)
    {
        return new TerrainSurfaceProfile(formA, formB, materialA, materialB, haze, organicGround);
    }

    /// <summary>Resolve the stable form/material language for a biome, falling back to broad meadow composition.</summary>
    public static TerrainSurfaceProfile terrainSurfaceProfileForBiome(string? biomeKey)
    {
        // `(biomeKey && PROFILE_BY_BIOME[biomeKey]) || meadow`
        if (!string.IsNullOrEmpty(biomeKey) && PROFILE_BY_BIOME.TryGetValue(biomeKey, out var found)) return found;
        return meadow;
    }

    public static readonly IReadOnlyList<string> TERRAIN_SURFACE_PROFILE_BIOMES = new List<string>(PROFILE_BY_BIOME.keys());
}
