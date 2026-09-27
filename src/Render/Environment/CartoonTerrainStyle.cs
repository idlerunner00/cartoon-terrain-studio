// Port of packages/client/src/render/environment/cartoonTerrainStyle.ts — keep in lockstep with the original.

namespace Fluitown.Render;

// `CartoonTerrainSurfaceKind` is the string union
// 'floor' | 'rockCap' | 'earthFace' | 'rockFace' | 'bridge' | 'basin' | 'waterBank' | 'chasmWall' | 'abyss' → `string`
// (so it does not collide with the identically named union in terrainGeometryCompilerStyle.ts).

public static partial class CartoonTerrainStyle
{
    // CARTOON_TERRAIN_STYLE is a deep `Object.freeze`d object. Ported like its worker twin in
    // TerrainGeometryCompilerStyle: the top level is a static class (scalars are consts), each sub-object is a sealed
    // instance with readonly fields so consumers can hold it in a local exactly like the TypeScript
    // (`const style = CARTOON_TERRAIN_STYLE.cameraAwayCrest;`). `cameraAwayCrest` IS the
    // TERRAIN_CAMERA_AWAY_CREST_STYLE object; `suspensionBridge` mirrors the static-class port of
    // TERRAIN_SUSPENSION_STYLE and is therefore a nested static class of constants.

    /// <summary>
    /// The environment's character-alignment contract.
    ///
    /// Characters are painted as a few broad, saturated colour blocks with a confident ink silhouette. Terrain
    /// must therefore avoid noisy PBR sparkle, per-cell colour switches and dense full-face hatching. Keep these
    /// high-level dials together so Hub, finite dungeons and endless chunks cannot drift into different looks.
    /// </summary>
    public static class CARTOON_TERRAIN_STYLE
    {
        /// <summary>Nearly matte pigment: highlights stay broad and graphic instead of reading as plastic.</summary>
        public const double surfaceRoughness = 0.96;
        /// <summary>Water retains one broad moving highlight, but never a mirror-like PBR sparkle.</summary>
        public const double waterRoughness = 0.46;
        /// <summary>
        /// Shared horizontal lattice warp for structural terrain boundaries. Surface, water, overlay, shadow depth
        /// and actor-wall depth all evaluate the same world field, so this softens ruler-straight tile contours
        /// without opening a seam or changing authoritative collision/navigation cells.
        /// </summary>
        public static readonly ContourWarpStyle contourWarp = new();
        /// <summary>
        /// Dense grass is represented by a tiny opaque crown plus a bounded number of hero blades. The crown carries
        /// the thousands of sub-pixel stems that would otherwise alias away in the gameplay camera; bent ribbons then
        /// provide the readable silhouette and wind motion. Both are baked into the existing terrain surface batch.
        /// </summary>
        public static readonly GroundCoverStyle groundCover = new();
    }

    public sealed class ContourWarpStyle
    {
        public readonly double maxAmplitudePx = 4.2;
        public readonly double minimumScalePx = 96;
    }

    public sealed class GroundCoverStyle
    {
        public readonly double bladeBend = 0.62;
        public readonly double bladeTipWidth = 0.08;
    }
}
