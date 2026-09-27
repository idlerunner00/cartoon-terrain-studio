// Port of packages/client/src/render/environment/waterfallVisual.ts — keep in lockstep with the original.

namespace Fluitown.Render;

public static partial class WaterfallVisual
{
    /// <summary>
    /// Chasm portals are open volumes with no backing wall, so their physical curtain cannot leave the shared
    /// boundary plane: any lateral roll creates a real sightline behind the sheet. Surface flow and normals still
    /// turn over the lip; geometry connects the organic crest to the cardinal receiver floor with zero offset.
    /// </summary>
    public const double WATERFALL_CHASM_CREST_ROLL_TILES = 0;
}
