// Port of packages/client/src/generatorControls.ts — keep in lockstep with the original.
// The two pure helpers the web generator's picker and mouse wheel use; they live beside the editor they read from.
using Fluitown.Runtime;

namespace Fluitown.Domain;

public sealed class TerrainBrushSample
{
    /// <summary>A TileType value.</summary>
    public int tile;
    /// <summary>Semantic editor height (<see cref="TerrainEditor.terrainEditorHeightFromStored"/>): Chasm reads negative.</summary>
    public int height;
}

public static partial class TerrainEditor
{
    /// <summary>Read the two physical brush values the generator's picker copies from one authored cell.</summary>
    /// <returns>Null (TS `undefined`) for a non-integer or out-of-bounds cell.</returns>
    public static TerrainBrushSample? sampleTerrainBrushAt(TerrainArtifact artifact, double tx, double ty)
    {
        if (
            !Number.isInteger(tx) ||
            !Number.isInteger(ty) ||
            tx < 0 ||
            ty < 0 ||
            tx >= artifact.width ||
            ty >= artifact.height)
            return null;
        int index = (int)ty * artifact.width + (int)tx;
        int tile = artifact.baseTiles[index];
        return new TerrainBrushSample
        {
            tile = tile,
            height = terrainEditorHeightFromStored(tile, at(artifact.elevation, index)),
        };
    }
}
