// Port of packages/shared/src/domain/world/worldIdentity.ts — keep in lockstep with the original.

namespace Fluitown.Domain;

/// <summary>
/// The identity of **the** world.
///
/// This game has one permanent, shared, unbounded space, and every player lives in it at once. So there is
/// one id and one descriptor, and they are declared here rather than parsed out of an instance-id grammar.
///
/// The router still exists, though, and the CLIENT is its caller: a client is handed an instance id on the
/// wire and nothing else, so `descriptorFromInstanceId` resolves <see cref="SHARED_WORLD_ID"/> through this
/// very function. The two must never be allowed to answer differently — that is the difference between a
/// client that streams the world and one that renders nothing at all.
/// </summary>
public static class WorldIdentity
{
    /// <summary>Stable id of the one permanent world. Also its default terrain seed, so the world is reproducible.</summary>
    public const string SHARED_WORLD_ID = "world";

    /// <summary>
    /// The world's terrain theme.
    ///
    /// `sakura_temple_dream` is the sanctuary landform: open temple gardens, pond courts, moss terraces,
    /// lacquer bridges and Sakura groves. It is the standard identity of the Flui world, shared by terrain
    /// generation and every client presentation that renders that terrain.
    /// </summary>
    public const string SHARED_WORLD_BIOME = "sakura_temple_dream";

    /// <summary>
    /// Geometry tier. Tier scales the generator's size and *threat* budget. This world has no combat, so it
    /// takes the lowest tier: the calmest structural dressing, and nothing generated for encounters that will
    /// never happen.
    /// </summary>
    private const int SHARED_WORLD_TIER = 1;

    /// <summary>
    /// The permanent world's terrain descriptor.
    ///
    /// `Endless` is not a game mode here — it is the streaming contract: terrain is a 2-D grid of chunks
    /// generated on demand from the seed, in both directions on both axes, forever. `Open` access because
    /// there is nothing to unlock. The seed defaults to the world id so a stock server is reproducible, and is
    /// a parameter so a test fixture or a second shard can be a different world without a second code path.
    /// </summary>
    public static DungeonDescriptor sharedWorldDescriptor(string seed = SHARED_WORLD_ID)
    {
        return new DungeonDescriptor
        {
            seed = seed,
            biomeKey = SHARED_WORLD_BIOME,
            tier = SHARED_WORLD_TIER,
            mode = DungeonMode.Endless,
            access = DungeonAccess.Open,
            generationVersion = EndlessCountry.ENDLESS_GENERATION_VERSION,
        };
    }
}
