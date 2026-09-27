// Port of packages/client/src/render/environment/terrainLightRig.ts — keep in lockstep with the original.
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// The ONE physical light-rig truth of the hybrid frame (extracted from `threeTerrain.ts` as a pure
/// leaf module — WP1 of the Render3D migration). Two consumers, one set of numbers:
///
///  - `ThreeTerrainLayer` builds the TERRAIN scene's lights from it
///    (and scales them per biome in `setBiome`),
///  - `render3d/core/lightRig.ts` builds the volumetric ACTOR scene's lights from the same constants
///    and the same biome formulas, so 3D bodies stand in exactly the light the terrain is painted with.
///
/// Pure data, no imports — safely consumable from node tests and any bundle slice.
/// </summary>
public static partial class TerrainLightRig
{
    public static class LIGHT_RIG
    {
        // Intensities are authored as "fraction of albedo a fully-lit flat cap receives" and multiplied by π at
        // light construction (three ≥r155 physical lighting divides diffuse by π): caps sum to ~0.98× the authored
        // palette, so the art direction survives the switch to real lights. The rig is deliberately KEY-DOMINANT:
        // the warm sun carries ~55% of a lit cap so the frame always has one readable light direction, long cast
        // shadows and strong form separation on vertical faces — the flat "even pastel sheet" read came from a
        // near-equal key/ambience split. The sky/fill pair still carries enough shape information that pale caps do
        // not jump from flat shadow into a glaring sun band (the film shoulder and the 4-band wash both soften the
        // terminator), and the cap sum is held at the same ~0.98× so the rebalance cannot lift or drop exposure.
        public const double ambient = 0.045 * Math.PI;
        public const double hemi = 0.3 * Math.PI;
        public const double sun = 0.68 * Math.PI;
        public const double fill = 0.155 * Math.PI;

        /// <summary>FROM the scene TOWARD the sun (west + high + north). The elevation sits low enough (~53°) that walls
        /// and props throw visibly long NW→SE shadows — the strongest top-down "the sun exists" signal — while
        /// staying high enough that cliffs receive broad form light, never a grazing white razor.</summary>
        public static class sunDir
        {
            public const double x = -0.58;
            public const double y = 1.02;
            public const double z = -0.48;
        }

        /// <summary>From the scene toward the fill (slightly east + up + strongly south).</summary>
        public static class fillDir
        {
            public const double x = 0.18;
            public const double y = 0.7;
            public const double z = 0.95;
        }
    }
}
