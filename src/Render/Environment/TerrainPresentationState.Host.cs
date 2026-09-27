// Integration seam between the two halves of ThreeTerrainLayer: the presentation half (TerrainPresentationState, ported
// from threeTerrain.ts) and the tile half (TerrainTileManager). In the original both are one class, so the tile code
// reads the presentation fields directly; these accessors expose exactly those fields to the tile manager.

namespace Fluitown.Render;

public sealed partial class TerrainPresentationState
{
    /// <summary>`this.biome`.</summary>
    internal Biome? currentBiome => this.biome;

    /// <summary>`this.tileset`.</summary>
    internal TerrainTileset? currentTileset => this.tileset;

    /// <summary>`this.webglCapability?.executionClass === 'software'`.</summary>
    internal bool softwareExecution => this.webglExecutionClass == "software";
}
