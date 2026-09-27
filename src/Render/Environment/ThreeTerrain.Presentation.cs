// Port of packages/client/src/render/environment/threeTerrain.ts — module-level part used by the presentation state
// (lights, shadow window, backdrop, world yaw, colour grading). Keep in lockstep with the original.
//
// PORT NOTES
// * `ThreeTerrain` is the module class of threeTerrain.ts; this file is one `partial` part of it. The
//   integrator's tile-management part (ThreeTerrain.cs) must not re-declare these members.
// * `SHADOW_MAP_SIZE` is `terrainShadowMapSize(RENDER_QUALITY.terrainShadowMapSize, RENDER_QUALITY.isMobile)` in TS.
//   config.ts is not ported, so the value is resolved from the injected TerrainRenderQuality instead
//   (TerrainPresentationState.SHADOW_MAP_SIZE) — same expression, same value for the same device policy.
// * `gradeCache` is a pure memo (hex → graded hex); it is [ThreadStatic] because tile baking (deriveMaterialForCell)
//   may call gradeColor off the main thread in the Godot port.
// * `richStreamedTerrainRequested()` reads `location.search`; the engine-free port has no URL, so (exactly like the
//   original without `location`) it is always true.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainProjection;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public static partial class ThreeTerrain
{
    /// <summary>Full authored terrain is the hardware default. `compact` is retained only as an explicit diagnostic escape
    /// hatch; an actually detected software rasterizer is selected later from the production WebGL context.</summary>
    internal static bool richStreamedTerrainRequested()
    {
        // `globalThis.location?.search` is undefined without a browser location → `!search` → true.
        return true;
    }

    /* ── World-scale constants ─────────────────────────────────────────────────────────────────────────────── */
    /// <summary>Height (px of Y) of one elevation level — MUST equal the actor screen lift per level.</summary>
    internal const double ELEV = TERRAIN_ELEVATION_STEP_PX;

    /* ── Light rig ───────────────────────────────────────────────────────────────────────────────────────────
     * Concept: ONE warm key ("sun") from the north-west-high — the direction the 2D art always implied (bright
     * north/west rims, shaded south faces, contact shadows falling south-east) — casting the only real shadows;
     * a HEMISPHERE fills sky-vs-ground ambience so caps read brighter than faces even out of the sun; a soft cool
     * FILL from the south lifts the (sun-less) south faces so height reads plastically instead of going flat-black;
     * a tiny AMBIENT floors the darkest pixel. Intensities are balanced so a flat, fully-lit cap comes out at
     * ~1.0× its authored albedo — the palette stays the art direction, the lights add form. */
    // The physical light-rig constants moved to the pure leaf module `terrainLightRig.ts` (WP1 of the
    // Render3D migration) so the volumetric actor scene's rig (`render3d/core/lightRig.ts`) reads the SAME
    // truth without pulling this renderer module. All uses below are unchanged.

    /// <summary>Extra world-px around the view's bounding radius covered by the shadow camera (shake / pan headroom).</summary>
    internal const double SHADOW_PAD = 320;

    /// <summary>
    /// The minimum session-stable shadow texel size for every supported gameplay/cinematic framing. Resizing a
    /// directional depth map is indivisible and the former mitigation visibly faded every shadow out and back in.
    /// The live window may grow beyond this rung when a larger real viewport is presented, but never shrinks again
    /// during the terrain session: complete coverage takes priority over reclaiming texel density.
    /// </summary>
    public static readonly double TERRAIN_SHADOW_STABLE_RADIUS = ShadowLattice.shadowWindowRung(
        ConfigIndex.GAMEPLAY_VIEW.maxWorldRadius + SHADOW_PAD);

    /// <summary>
    /// Low night light can project a tall wall several tiles into the receiver window. Keep the depth camera and
    /// cache invalidation conservative without widening the orthographic map (and therefore without losing texel
    /// density). The light sits beyond the complete caster reach so sun-facing cached proxies are not near-clipped.
    /// </summary>
    internal const double SHADOW_CASTER_REACH = 4_000;
    internal const double SHADOW_LIGHT_DISTANCE = SHADOW_CASTER_REACH + 1_000;
    /// <summary>One cold-fill frame can end before the cooperative outer-ring compiler is scheduled on the next frame.</summary>
    internal const int INITIAL_SHADOW_STABLE_FRAMES = 2;

    /* ── Atmosphere ─────────────────────────────────────────────────────────────────────────────────────────── */
    // The aerial-perspective budget, ramp shape and GLSL now live in the pure leaf `aerialHaze.ts` — the ACTOR
    // scene evaluates the byte-identical function from the same uniform objects, so the whole picture stands in
    // ONE air. See that module for why the old (0.13, 1.7) pair was measurably invisible at mid-screen.

    /* ── Depth / backdrop ───────────────────────────────────────────────────────────────────────────────────── */
    internal const double BACKDROP_PAD = 900;
    internal const int TERRAIN_CLEAR_COLOR = 0x05070b;
    /// <summary>Cool destination sky used only behind the high Aether landing camera. It clears the complete render target,
    /// unlike a projected plane, so finite terrain can never reveal a rectangular white edge.</summary>
    internal const int AETHER_DESTINATION_SKY_CLEAR_COLOR = 0x91aeb5;
    // The view-sized plane is a streamed-world / Aether fallback only. Keep it several levels beneath the real
    // Chasm tile floor so depth testing can never substitute it for the authored per-cell geometry.
    internal const double BACKDROP_DROP_Y = -TerrainModel.CHASM_ABYSS_SURFACE_Z * ELEV + ELEV * 4;
    /// <summary>The fallback atmosphere is a background, not an occluder. Submit it before every world family and leave
    /// depth ownership to real terrain. Relying on its projected world depth while drawing it last allowed the
    /// full-view plane to win the depth test on some ANGLE paths and silently cover every otherwise valid tile.</summary>
    public const int TERRAIN_BACKDROP_RENDER_ORDER = -100;
    internal static readonly int BACKDROP_FALLBACK = mix(0x05070b, TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS.floorCool.side, 0.18);

    /// <summary>Grade the authored palette the way the (now bypassed) world colour-matrix did — saturation/contrast/
    ///  brightness lift baked into the terrain albedo so the hybrid keeps the same rich, painted look.</summary>
    [ThreadStatic] private static Dictionary<int, int>? _gradeCache;
    private static Dictionary<int, int> gradeCache => _gradeCache ??= new Dictionary<int, int>();

    internal static int gradeColor(int hex)
    {
        if (gradeCache.TryGetValue(hex, out int cached)) return cached;
        double sat = 1 + Theme.ARTDIRECTION.saturateBoost;
        double con = 1 + Theme.ARTDIRECTION.contrastBoost;
        double bri = Theme.ARTDIRECTION.brightnessBoost;
        double r = (double)((hex >> 16) & 255) / 255;
        double g = (double)((hex >> 8) & 255) / 255;
        double b = (double)(hex & 255) / 255;
        double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        r = lum + (r - lum) * sat;
        g = lum + (g - lum) * sat;
        b = lum + (b - lum) * sat;
        r = (0.5 + (r - 0.5) * con) * bri;
        g = (0.5 + (g - 0.5) * con) * bri;
        b = (0.5 + (b - 0.5) * con) * bri;
        static double c(double v) => Math.max(0, Math.min(255, Math.round(v * 255)));
        int @out = (Js.ToInt32(c(r)) << 16) | (Js.ToInt32(c(g)) << 8) | Js.ToInt32(c(b));
        gradeCache[hex] = @out;
        return @out;
    }

    /// <summary>A theme drift pole (hex) → a brightness-neutral rgb MULTIPLIER around 1.0: only the colour's deviation
    ///  from its own luminance survives, scaled by `amp` — hue variance, never brightness noise (the invariant the
    ///  painterly drift always had). The default poles reproduce the original warm/cool constants.</summary>
    internal static (double r, double g, double b) driftMultiplier(int hex, double amp)
    {
        double r = (double)((hex >> 16) & 255) / 255;
        double g = (double)((hex >> 8) & 255) / 255;
        double b = (double)(hex & 255) / 255;
        double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return (1 + (r - lum) * amp, 1 + (g - lum) * amp, 1 + (b - lum) * amp);
    }

    internal static void applyWorldYaw(ThreeScene scene, double yaw, double pivotX, double pivotZ)
    {
        double c = Math.cos(yaw);
        double s = Math.sin(yaw);
        scene.rotation.set(0, yaw, 0);
        // Three's Y rotation maps (x,z) to (c*x + s*z, -s*x + c*z). Translate by
        // pivot - R*pivot so the authored combat midpoint remains perfectly stationary on screen.
        scene.position.set(pivotX - c * pivotX - s * pivotZ, 0, pivotZ + s * pivotX - c * pivotZ);
        scene.updateMatrixWorld(true);
    }

    /* ── Small pure helpers ───────────────────────────────────────────────────────────────────────────────── */

    /// <summary>Whether a bake's owned cells can contribute a caster to the currently retained shadow window. Tiles in
    /// the wider show/cache ring do not invalidate the map until the guarded light anchor actually reaches them.</summary>
    public static bool terrainBakeIntersectsShadowWindow(
        TerrainBakeFrame frame,
        double centerX,
        double centerZ,
        double radius)
    {
        if (!Number.isFinite(centerX) || !Number.isFinite(centerZ) || !Number.isFinite(radius))
            return true;
        double x0 = frame.originX + (frame.i0 + TerrainTileLattice.TILE_BORDER) * frame.tileSize;
        double z0 = frame.originY + (frame.j0 + TerrainTileLattice.TILE_BORDER) * frame.tileSize;
        double x1 = x0 + TerrainTileLattice.TILE_CELLS * frame.tileSize;
        double z1 = z0 + TerrainTileLattice.TILE_CELLS * frame.tileSize;
        double nearestX = clamp(centerX, Math.min(x0, x1), Math.max(x0, x1));
        double nearestZ = clamp(centerZ, Math.min(z0, z1), Math.max(z0, z1));
        // One cell of caster overhang covers bevels, tree crowns and slanted shadow projection at a tile edge.
        double guardedRadius = radius + frame.tileSize;
        return Math.pow(nearestX - centerX, 2) + Math.pow(nearestZ - centerZ, 2) <= guardedRadius * guardedRadius;
    }

    /// <summary>Keep an initialized terrain shadow snapshot immutable while only speculative casters are still arriving.
    /// A cold complete viewport, a changed projection, or a completed visible caster batch always takes priority.</summary>
    public static bool terrainShadowSnapshotNeedsRefresh(
        bool mapInitialized,
        bool shadowDirty,
        bool casterSnapshotPending,
        bool casterWorkPending,
        bool visibleCasterBatchReady = false)
    {
        if (!mapInitialized) return true;
        // A moved/resized camera already publishes a new shadow matrix to every receiver. Sampling the old depth
        // texture through that matrix is never a valid intermediate state and is the source of viewport-sized
        // shadow holes. Rebuild immediately from the currently mounted proxy graph; speculative additions can
        // republish once more, in one batch, when their compiler work drains.
        if (shadowDirty || visibleCasterBatchReady) return true;
        if (casterSnapshotPending && casterWorkPending) return false;
        return casterSnapshotPending;
    }

    internal static double clamp(double value, double min, double max)
    {
        return Math.max(min, Math.min(max, value));
    }
}
