// Port of packages/client/src/render/environment/terrainTileLattice.ts — keep in lockstep with the original.

namespace Fluitown.Render;

/// <summary>
/// **The terrain bake lattice** — how the world is cut into bakeable tiles, in exactly one place.
///
/// ## Why this file exists
///
/// These two numbers were declared **twice**, independently, in two files that never referenced each other:
/// `threeTerrain.ts` used them to build each bake *frame*, and `terrainGeometryCompiler.ts` used them in
/// `ownCell` to decide which of that frame's cells actually emit geometry. They are two halves of one
/// contract and had no mechanical relationship at all.
///
/// The compiler states the guarantee that rests on them: *"adjacent tiles produce bit-identical vertices at
/// the seam, so the tiling is watertight with zero double-drawn overlap."* That guarantee held only because
/// two unrelated files happened to contain the same two literals. Change one and not the other and the
/// emitted rectangle stops matching the frame it was cut from — a border too small leaves **gaps between
/// tiles**, a border too large **double-draws every seam**. Neither throws, neither fails a type check, and
/// both are silent visual corruption discovered by looking at the ground.
///
/// It also made the granularity itself untunable in practice: raising `TILE_CELLS` is a real performance
/// lever (fewer, larger bakes mean proportionally fewer draw calls, since every tile costs up to five
/// meshes), but doing it meant editing two files in lockstep and trusting that nobody ever edited one.
///
/// So the lattice lives here, and the arithmetic that derives a frame from it lives here too — because the
/// relationship between "how big is a tile" and "where does its frame start" is the part that must never be
/// restated at a call site.
///
/// ## The trade, for whoever tunes it next
///
/// `TILE_CELLS` up:
///  - **fewer draw calls** — a visible window of area A needs A/TILE_CELLS² tiles, and each tile submits up
///    to five meshes (surface, water, mist, overlay, actor-wall). This is the reason to raise it.
///  - **less border sampling** — the border is context, not geometry (`ownCell` clips it), so it costs
///    materialisation and iteration rather than vertices. The retired 20-cell lattice spent 41 % of its walk
///    on context. The shipped 28-cell lattice walks 34² cells, of which 32 % are context;
///    at 32 cells it is 29 %, at 40 cells 24 %.
///  - **coarser invalidation** — one edited cell rebakes its whole tile, and the rebake is what a player
///    sees as a hitch. This is the reason not to raise it.
///  - **coarser streaming granularity** — a tile is also the unit of pop-in.
///
/// The first two are measurable from a bench; the last two are visual and need a real browser pass at
/// `+0`/mid/`+99` before the constant moves. That is why this file makes the change *possible* and does not
/// make it.
/// </summary>
public static partial class TerrainTileLattice
{
    /// <summary>Cells along one edge of a bake tile's own, geometry-emitting region.</summary>
    public const int TILE_CELLS = 28;

    /// <summary>
    /// Context cells sampled beyond the tile's own region on every side.
    ///
    /// Three, because the widest neighbour-dependent rule the compiler runs — wall growth and contour mitring —
    /// reaches two cells, and the third is the margin that keeps a diagonal reach inside the sampled window.
    /// The border emits nothing: <see cref="isOwnLatticeCell"/> is what makes the seam watertight.
    /// </summary>
    public const int TILE_BORDER = 3;

    /// <summary>
    /// Whether a cell of a sampled frame lies in the tile's **own** region and may therefore emit geometry.
    ///
    /// The single discriminator both halves of the contract now share. Coordinates are frame-local, exactly as
    /// a <see cref="TerrainBakeFrame"/>'s cells are indexed.
    /// </summary>
    public static bool isOwnLatticeCell(int x, int y)
    {
        return
            x >= TILE_BORDER &&
            x < TILE_BORDER + TILE_CELLS &&
            y >= TILE_BORDER &&
            y < TILE_BORDER + TILE_CELLS;
    }
}
