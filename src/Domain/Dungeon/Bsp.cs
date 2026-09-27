// Port of packages/shared/src/domain/dungeon/bsp.ts — keep in lockstep with the original.

namespace Fluitown.Domain;

/// <summary>
/// Binary-space-partition dungeon: recursively split the interior into leaves, drop one room in each
/// leaf, and connect sibling subtrees with carved L-corridors. Connecting *through the tree* means the
/// whole layout is connected by construction (every split's two halves are joined, recursively up to
/// the root); the generator's repair pass is then only a belt-and-braces guarantee.
///
/// Pure: all randomness flows through the provided <see cref="Rng"/>.
/// </summary>
public static class Bsp
{
    /// <summary>Corridor width in tiles: every standard passage must contain a full 5x5 readable footprint.</summary>
    public const int CORRIDOR_THICKNESS = 5;
}
