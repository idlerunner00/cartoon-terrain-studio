// Port of packages/client/src/config.ts — keep in lockstep with the original.
//
// PORT NOTES
// * PARTIAL port: only the camera contract (`FRAMING`, `CAMERA` and the viewport-derived zoom helpers they read) and
//   the observer control-feel constants (`CAMERA_PAN_SPEED`, `EDGE_SCROLL_MARGIN_PX`). Not ported: the server
//   endpoints (`SERVER_HTTP`, `SERVER_WS`, `SERVER_WS_ENDPOINTS`), `INPUT_SEND_INTERVAL_MS`, `ENTITY_PREWARM_MARGIN`,
//   `VIEW_HINT`, `RENDER`, `RENDER_QUALITY`, `foregroundFpsPolicy`, `PERF`, `HIDDEN_KEEPALIVE_INTERVAL_MS` and the
//   `INTERP_DELAY_MS` re-export. `RENDER_QUALITY`/`PERF` additionally need performance/deviceWorkloadPolicy.ts
//   (`clientWorkloadPolicy.deviceClass`), which is not ported yet; renderQualityPolicy.ts itself is (RenderQualityPolicy.cs).
// * `CAMERA` is an object with getters → a nested static class with constants and computed properties; the getters
//   re-read the (host-bound) viewport on every read exactly like the original.
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.Scalar;
using static Fluitown.Render.Device;
using static Fluitown.Render.TerrainProjection;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// Client configuration.
///
/// The server's host/protocol are taken from the current page (so the client works on localhost and
/// on a deployed domain), while the PORT is a project-specific non-standard one (8090) so fluitown can
/// coexist with other stacks on the same host. Override fully with VITE_SERVER_URL, only the persistent
/// gameplay socket with VITE_REALTIME_URL, or just the local port with VITE_SERVER_PORT at build time.
/// </summary>
public static partial class Config
{
    /// <summary>
    /// Responsive framing: frame ~this many world units across the SHORT screen axis. A smaller screen
    /// therefore pulls the camera further out (sees more world), while the clamps keep actors from
    /// shrinking below readability. Phones frame a narrow span (big, legible actors on a cramped
    /// display); desktops a wide one (the shared-world overview: the map reads as a place, actors as
    /// figures in it). ONE mechanism drives both device classes.
    /// </summary>
    private const double MOBILE_VIEW_SPAN = 580;

    /// <summary>
    /// Desktop world span (world px across the short screen axis, ≈39 tiles) — and, since the desktop camera has
    /// no player-controlled zoom, THE desktop framing. This is the single number that decides how much world a
    /// desktop player sees, and it is the only place to tune the composition.
    ///
    /// 1571 is `1100 / 0.70`: the frame desktop actually SHIPPED for months, when the wheel ladder's ceiling was
    /// 0.70 and the ladder booted pinned to that ceiling. So this is not a new composition, it is the one every
    /// desktop player has been looking at — chosen by the owner from a live screenshot, after 1100 (the authored
    /// span the wheel could never present) and 1232 were both called too close.
    ///
    /// On a small window <see cref="DESKTOP_MIN_WORLD_ZOOM"/> binds before this span does, which is the readability
    /// floor doing its job: a 1366x768 laptop frames ~1310 units rather than shrinking actors to hold 1571.
    /// </summary>
    private const double DESKTOP_VIEW_SPAN = 1571;

    /// <summary>Absolute desktop world-zoom floor approved against the 2048x1024 gameplay reference. It cannot be undercut
    /// by a compact viewport or the ambient danger pull-out.</summary>
    private const double DESKTOP_MIN_WORLD_ZOOM = 0.88;

    /// <summary>Viewport-derived base zoom (`shortSide / span`, clamped). Recomputed on read so it always
    /// reflects the current viewport (resize, orientation, display-scale change). Measures the *visual*
    /// viewport (the pixels actually visible) so the zoom tracks the same rectangle the renderer sizes
    /// its canvas to — they must agree, or the world won't fill the screen at the right scale.</summary>
    private static double responsiveBaseZoom(double span, double minZoom, double maxZoom)
    {
        ClientViewport viewport = clientViewport();
        double w = viewport.width;
        double h = viewport.height;
        // The oblique orthographic camera foreshortens north/south ground distance. Compare the viewport
        // axes in WORLD space so `span` still means the short visible terrain axis, not the short CSS axis.
        double shortSide = Math.min(w, h / TERRAIN_VIEW_GROUND_SCALE);
        if (!Js.Truthy(shortSide)) shortSide = span;
        return clamp(shortSide / span, minZoom, maxZoom);
    }

    /// <summary>Phone framing: enough world to see the storm/enemies/terrain coming, actors stay legible.</summary>
    private static double mobileBaseZoom()
    {
        return responsiveBaseZoom(MOBILE_VIEW_SPAN, 0.6, 0.74);
    }

    /// <summary>Desktop framing — the fixed one. The floor keeps actors readable in tiny dev windows. Large displays scale
    /// farther so they retain the authored world span instead of silently gaining a wider field of view; the final
    /// radial floor below is the independent guard for unusually shaped/extreme viewports.</summary>
    private static double desktopBaseZoom()
    {
        return responsiveBaseZoom(DESKTOP_VIEW_SPAN, DESKTOP_MIN_WORLD_ZOOM, 4);
    }

    /// <summary>
    /// Authored framing offsets, as fractions of the SHORT half-viewport. They live here, beside the disclosure
    /// guard that has to reserve room for them, because they are one rule with two readers: the camera stamps them
    /// and <see cref="desktopRadiusZoomMin"/> budgets for them. Duplicating the number into `camera.ts` is exactly how a
    /// camera drifts outside the radius the server discloses.
    /// </summary>
    public static class FRAMING
    {
        /// <summary>Travel lead — the ground you are moving INTO, instead of the ground you just left.</summary>
        public const double travelLeadViewFraction = 0.12;
    }

    /// <summary>
    /// Minimum zoom that keeps the full projected viewport inside the server's gameplay disclosure radius.
    ///
    /// The frame is the half-diagonal PLUS the authored framing offset: the travel lead moves the camera centre
    /// off the player, so on a viewport where this guard (rather than the ambient pull-out) binds the wide end,
    /// the leading corner would otherwise sit outside `maxWorldRadius` — a strip of visible frame, in exactly the
    /// direction of travel, where hostiles are not streamed. Both terms scale as 1/zoom, so reserving the lead is
    /// a term in the same numerator rather than a clamp on the lead itself: the lead stays zoom-invariant and the
    /// guard stays a pure function of the viewport.
    /// </summary>
    private static double desktopRadiusZoomMin()
    {
        ClientViewport viewport = clientViewport();
        double width = viewport.width;
        double height = viewport.height;
        if (!(width > 0) || !(height > 0)) return DESKTOP_MIN_WORLD_ZOOM;
        double projectedHeight = height / TERRAIN_VIEW_GROUND_SCALE;
        double halfDiagonal = 0.5 * Math.hypot(width, projectedHeight);
        double framingOffset = 0.5 * Math.min(width, projectedHeight) * FRAMING.travelLeadViewFraction;
        return (halfDiagonal + framingOffset) / ConfigIndex.GAMEPLAY_VIEW.maxWorldRadius;
    }

    /// <summary>
    /// Camera feel. Zoom smoothing is frame-rate-independent (`1 - e^(-rate·dt)`). Planar focus stays pinned to
    /// the predicted player so input never gains follow lag; only the discrete terrain-height component uses a
    /// critically damped response, preventing a one-tile terrace change from stepping the whole rendered world.
    ///
    /// The framing is AUTHORED: <see cref="baseZoom"/> is the colony composition and authored camera terms can move it
    /// transiently. The observer's wheel scales that finished composition through Camera's separately bounded
    /// observation ladder; it does not rewrite the authored ambient target.
    /// </summary>
    public static class CAMERA
    {
        /// <summary>
        /// Fixed gameplay orbit around the vertical axis. Owned by the world→screen contract in
        /// `terrainProjection`, because geometry compiled in the terrain worker (which cannot reach this config)
        /// must be built against the same azimuth — see `terrainBillboardBasisInto`.
        /// </summary>
        public const double worldYaw = TERRAIN_CAMERA_WORLD_YAW;

        /// <summary>THE world zoom — the fixed gameplay framing. BOTH device classes derive it from the viewport (short axis
        /// / designed world span, clamped), so every display frames a comparable amount of world — a scaled laptop
        /// window no longer crops the view down until actors dominate the frame. A getter so
        /// orientation/resize/display-scale is always reflected.</summary>
        public static double baseZoom => isMobile ? mobileBaseZoom() : desktopBaseZoom();

        /// <summary>The lane event frames more simultaneous actors and both approach flanks. Keep its ambient view one
        /// deliberate step wider, while respecting the global readability floor on compact desktop windows.</summary>
        public static double factionWarZoom
        {
            get
            {
                double @base = isMobile ? mobileBaseZoom() : desktopBaseZoom();
                double floor = isMobile ? mobileBaseZoom() * 0.9 : DESKTOP_MIN_WORLD_ZOOM;
                return Math.max(floor, @base * (isMobile ? 0.92 : 0.9));
            }
        }

        /// <summary>Subtle further pull-out applied when the storm is bearing down (target for the danger ease).</summary>
        public static double dangerZoom => isMobile ? mobileBaseZoom() * 0.9 : desktopBaseZoom() * 0.913;

        /// <summary>Critically damped terrain-focus response (1/sec). At 30 Hz a normal one-level mobile transition remains
        /// near one CSS pixel per frame, then settles in well under a second without oscillation.</summary>
        public const double elevationStiffness = 14.5;

        /// <summary>Absolute zoom nudge while the local squad is boarding an Aether carrier — a small lean toward the berth,
        /// added to the ambient target by the renderer. Lives here because <see cref="resolvedZoomMax"/> has to reserve
        /// room for it: a ceiling that does not know about an authored term silently clips that term.</summary>
        public const double boardingFocusZoom = 0.075;

        /// <summary>Hard cap on the authored impact zoom punch (relative), read by both `Camera.addZoomPunch` and the
        /// ceiling below — the punch is the ONE term allowed to frame tighter than the authored composition.</summary>
        public const double impactPunchMax = 0.05;

        /// <summary>
        /// Absolute floor for the final composed zoom — the widest frame production can ever present.
        ///
        /// With the framing fixed, that is simply the widest AMBIENT target a space can ask for, so it is derived
        /// from those two getters instead of being restated as a literal: restating it is how a floor ends up
        /// clamping the very pull-out it was written to permit. The server-backed radial envelope remains the
        /// independent guard on extreme aspect ratios, where it may bind tighter than the ambient pull-out.
        /// </summary>
        public static double resolvedZoomMin
        {
            get
            {
                double widestAmbient = Math.min(CAMERA.factionWarZoom, CAMERA.dangerZoom);
                return isMobile ? widestAmbient : Math.max(widestAmbient, desktopRadiusZoomMin());
            }
        }

        /// <summary>
        /// Absolute ceiling for the final composed zoom. Nothing may frame TIGHTER than the authored composition
        /// except two bounded authored terms — the boarding lean and the impact punch — so the ceiling is composed
        /// from exactly those two, and is a guard rather than a range a player travels.
        /// </summary>
        public static double resolvedZoomMax =>
            isMobile
                ? double.PositiveInfinity
                : Math.max(
                    CAMERA.resolvedZoomMin,
                    (desktopBaseZoom() + CAMERA.boardingFocusZoom) * (1 + CAMERA.impactPunchMax));
    }

    // Observer control feel. The player is a camera, so these three numbers are the whole of "how it handles":
    // how fast the viewpoint travels, how close to the window edge the cursor starts pushing it, and how much a
    // screen-space drag moves the world.
}
