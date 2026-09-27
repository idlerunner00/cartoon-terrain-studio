// Port of packages/shared/src/domain/dungeon/descriptor.ts — keep in lockstep with the original.
using System.Text.RegularExpressions;
using static Fluitown.Domain.RngModule;

namespace Fluitown.Domain;

/// <summary>
/// The unified terrain entry point. A <see cref="DungeonDescriptor"/> is the single source that lets server
/// (collision) and client (rendering) regenerate identical geometry; it is derived once from the instance
/// id, so there is nothing to stream beyond the id the Welcome already carries.
/// <see cref="generateEndlessChunk"/> generates the procedural world as an unbounded series of chunks.
///
/// STUDIO: the studio streams only procedural worlds (painted and generated maps are authored layouts), so the
/// router keeps these ids; the authored garden (`hub` / `tutorial:&lt;n&gt;`) and the finite generators are gone:
///  - `world`               → THE world: one permanent, shared, unbounded streamed space
///  - `theme:&lt;biomeKey&gt;`    → a procedural world of any registered theme, for the map generator
/// </summary>
public static class Descriptor
{
    // `/^theme:([a-z0-9_]+)(?::\d+)?$/`. ECMAScript semantics without RegexOptions.ECMAScript: JS `$` (no `m` flag)
    // matches only at the very end of the input, while .NET `$` also matches before a final "\n" — hence `\z`. JS `\d`
    // is exactly [0-9], while .NET `\d` matches every Unicode decimal digit — hence `[0-9]`. No IgnoreCase, so the
    // character classes are culture-independent; CultureInvariant is set anyway.
    // STUDIO: the variant suffix accepts any seed text (letters, digits, `_`, `.`, `-`), so the open world of the terrain
    // studio can roll a world from a typed seed. Numeric variants resolve exactly as before.
    private static readonly Regex THEME_ID = new("^theme:([a-z0-9_]+)(?::[A-Za-z0-9_.-]+)?\\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Derive the terrain descriptor for an instance id, or null (TS: undefined) for a non-terrain space.
    ///
    /// **The one answer to "what ground is this id?"** The server builds its world by handing the descriptor to
    /// the instance directly, and the client has only the id off the wire — so this is where the two must
    /// agree. A router with one caller on each side and two different answers is not a router.
    /// </summary>
    public static DungeonDescriptor? descriptorFromInstanceId(string id)
    {
        // THE WORLD. One permanent, shared, unbounded space that every player lives in at once; `Endless` is its
        // streaming contract rather than a game mode. Answered first because it is the only id that matters in
        // ordinary play — everything below it is a fixture or a tool.
        if (id == WorldIdentity.SHARED_WORLD_ID) return WorldIdentity.sharedWorldDescriptor(id);

        // A THEME PREVIEW world (`theme:<biomeKey>[:<variant>]`) — the standalone map generator's way to
        // synthesize a theme's true procedural landform (terrain profile + macro DNA + render plan) from one id.
        // The id IS the seed, so a variant suffix rolls a fresh world of the same theme. Unknown keys degrade to
        // the neutral landform through the registries' own fallbacks.
        Match themeMatch = THEME_ID.Match(id);
        if (themeMatch.Success)
        {
            return new DungeonDescriptor
            {
                seed = id,
                biomeKey = themeMatch.Groups[1].Value,
                tier = 2,
                mode = DungeonMode.Endless,
                access = DungeonAccess.Open,
                generationVersion = EndlessCountry.ENDLESS_GENERATION_VERSION,
            };
        }
        return null;
    }

    /// <summary>
    /// Generate procedural chunk (cx,cy) for a descriptor — the 2-D streamed world. The id IS the seed, so
    /// server and client regenerate identical geometry with nothing to stream.
    /// </summary>
    public static DungeonLayout generateEndlessChunk(DungeonDescriptor d, int cx, int cy)
    {
        return Endless.generateEndlessChunkAt(
            hashSeed(d.seed),
            cx,
            cy,
            d.biomeKey,
            d.tier,
            d.generationVersion ?? EndlessCountry.ENDLESS_GENERATION_VERSION,
            hashSeed($"{d.seed}:spine"));
    }
}
