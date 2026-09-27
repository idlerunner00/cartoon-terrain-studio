// Port of packages/client/src/render/environment/terrainSuspensionStyle.ts — keep in lockstep with the original.
using Fluitown.Domain;

namespace Fluitown.Render;

public static partial class TerrainSuspensionStyle
{
    /// <summary>
    /// One code-only suspension-bridge vocabulary shared by the synchronous terrain baker and its worker compiler.
    /// Values are expressed in tile/elevation units so a tileset changes pigment, never structural proportions.
    ///
    /// PORT NOTE: the frozen TS object literal is a nested static class of constants (same convention as
    /// <c>TerrainModel.TERRAIN_PHYSICS</c>); every member is a JS number and therefore a <c>double</c>, the plank
    /// and segment counts included, so no consumer can fall into C# integer division.
    /// </summary>
    public static class TERRAIN_SUSPENSION_STYLE
    {
        public const double minPlanksPerCell = 4;
        public const double maxPlanksPerCell = 5;
        public const double plankGapCells = 0.038;
        public const double plankThicknessLevels = TerrainModel.BRIDGE_DECK_THICKNESS;
        public const double deckHalfWidthCells = 0.36;
        public const double plankLengthJitterCells = 0.026;
        public const double plankLiftJitterLevels = 0.025;
        public const double deckSagLevels = 0.16;
        public const double stringerInsetCells = 0.105;
        public const double stringerWidthCells = 0.045;
        public const double stringerDepthLevels = 0.12;
        public const double anchorPostWidthCells = 0.07;
        public const double anchorPostHeightLevels = 1.02;
        public const double cableWidthCells = 0.026;
        public const double cableSagLevels = 0.48;
        public const double cableSegmentsPerCell = 2;
        public const double hangerDeckClearanceLevels = 0.34;
        public const double shadowAlpha = 0.16;
    }
}
