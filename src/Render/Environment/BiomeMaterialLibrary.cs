// Port of packages/client/src/render/environment/biomeMaterialLibrary.ts — keep in lockstep with the original.
using Fluitown.Runtime;

namespace Fluitown.Render;

/// <summary>
/// Semantic PBR finish shared by terrain and terrain-batched set pieces.
///
/// Pigment, grain and construction already come from TerrainTileset/WorldStyle. This final lane defines how
/// those authored materials return light. Values are deliberately texture-free BRDF scalars: changing biome
/// updates one uniform and never creates a material, texture, sampler or draw call.
/// </summary>
public sealed class BiomeMaterialFinish
{
    public readonly double floorRoughness;
    public readonly double rockRoughness;
    public readonly double timberRoughness;
    /// <summary>Strength of the restrained grazing response on caps/timber/basins; vertical cliffs never receive it.</summary>
    public readonly double accentSheen;

    public BiomeMaterialFinish(double floorRoughness, double rockRoughness, double timberRoughness, double accentSheen)
    {
        this.floorRoughness = floorRoughness;
        this.rockRoughness = rockRoughness;
        this.timberRoughness = timberRoughness;
        this.accentSheen = accentSheen;
    }
}

public static partial class BiomeMaterialLibrary
{
    private static BiomeMaterialFinish finish(
        double floorRoughness,
        double rockRoughness,
        double timberRoughness,
        double accentSheen)
    {
        return new BiomeMaterialFinish(floorRoughness, rockRoughness, timberRoughness, accentSheen);
    }

    /// <summary>Keyed by BiomeLightingComposition.id (every id has an entry). Iterated by BIOME_MATERIAL_FINISHES → JsMap.</summary>
    private static readonly JsMap<string, BiomeMaterialFinish> FINISH_BY_COMPOSITION = new JsMap<string, BiomeMaterialFinish>()
        .set("sanctuary", finish(0.82, 0.91, 0.72, 0.56))
        .set("alpine", finish(0.84, 0.95, 0.74, 0.48))
        .set("engineered", finish(0.56, 0.72, 0.52, 0.88))
        .set("celestial", finish(0.7, 0.74, 0.66, 0.76))
        // Chalk, limestone and black ink: deliberately matte so Aegis reads as an illustration, not chrome.
        .set("citadel", finish(0.94, 0.99, 0.9, 0.3))
        .set("dream", finish(0.78, 0.86, 0.7, 0.62))
        .set("drowned", finish(0.62, 0.8, 0.7, 0.74))
        .set("arcane", finish(0.68, 0.78, 0.68, 0.82))
        .set("infernal", finish(0.9, 0.96, 0.82, 0.38));

    public static BiomeMaterialFinish biomeMaterialFinish(Biome biome)
    {
        return FINISH_BY_COMPOSITION.get(BiomeLightingCompositionModule.biomeLightingComposition(biome).id)!;
    }
}
