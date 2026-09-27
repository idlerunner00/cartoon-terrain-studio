// Port of packages/shared/src/config/index.ts — keep in lockstep with the original.
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/// <summary>
/// Tunable engine/net/world constants shared by server and client. Gameplay *curves* (storm
/// speed, difficulty, drop rates) live with their domain (`domain/run`, `domain/world`); this
/// file holds the low-level knobs both sides must agree on (tick rate, AOI radius, protocol).
///
/// Coordinate system: a 2D plane in world units (~1 unit ≈ 1 "meter"). In a Run, "forward"/depth
/// is +X; the Storm is a vertical wall that advances in +X and damages anyone behind it (x &lt; stormX).
///
/// Porting notes: every `{ … } as const` object is a nested static class with the TS field names; its
/// `get` accessors are static properties. Values are `double` (they only ever enter floating arithmetic,
/// and an `int` would silently turn `1000 / SIM.tickRate` into integer division) except counts/sizes that
/// are used as such (tick budgets, entity caps, member counts, grid sizes).
/// </summary>
public static class ConfigIndex
{
    /// <summary>
    /// Authoritative gameplay-view envelope.
    ///
    /// The camera resolves its zoom locally for presentation, while the server independently uses
    /// `maxWorldRadius` as the disclosure boundary for hostile actors, events, targeting and streamed terrain. A
    /// modified client can therefore draw its canvas at any scale, but it cannot use SetView (or a patched bundle)
    /// to obtain combat-relevant world data beyond the supported camera view. Singular authored navigation markers
    /// (for example the current Aether extraction caller) may be force-streamed explicitly.
    ///
    /// There is deliberately no player-controlled zoom factor here any more. The framing is AUTHORED: desktop
    /// presents the designed world span and mobile derives its own from the viewport, so every client sees the
    /// composition the game was built for and the only thing that ever moves the presented scale is an authored
    /// camera term (the storm/faction-war ambient pull-out, an impact punch, a cutscene). One less multiplier in
    /// front of `maxWorldRadius` is also one less way for the presented frame to argue with the disclosure guard.
    /// </summary>
    public static class GAMEPLAY_VIEW
    {
        /// <summary>Maximum radial world extent a gameplay client may receive. Covers all supported mobile framing.</summary>
        public const double maxWorldRadius = 1800;
    }

    /// <summary>
    /// The authored camera direction that makes a world placement visibly readable.
    ///
    /// This is shared because the client draws through it while the authoritative server uses the same view axis
    /// when it reserves the first Flui's clear arrival floor. If those were separate constants, a harmless camera
    /// art pass could silently put a tutorial body behind the very foreground wall the old angle did not see.
    /// </summary>
    public static class WORLD_PRESENTATION
    {
        public const double cameraYaw = -Math.PI / 24;
    }
}
