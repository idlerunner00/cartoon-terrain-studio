// Port of packages/client/src/render/environment/camera.ts — keep in lockstep with the original.
//
// PORT NOTES
// * The file exports `class Camera`, so the module
//   class is `CameraModule`; it is split into two partial declarations to keep the TS order (`initialGameplayZoom`
//   precedes the class, everything else follows it).
// * Structural TS types:
//   - `CameraTransformTarget` (`{ scale: { set(x, y?) }, position: { set(x, y) } }`) → an interface whose two members
//     are <see cref="CameraTransformSetter"/>s. <see cref="CameraTransform"/> is a ready-made implementation shaped like
//     renderer.ts's `cameraTransform` record (x, y, zoom) for callers that only need the stamped numbers.
//   - `viewBounds*<T extends { left, right, top, bottom }>` → <see cref="CameraView"/> (types.ts, EnvironmentTypes).
//   - `staticLayerOffsetInto<T extends { x, y }>` → <see cref="Vec2"/> (shared/math/vec2.ts).
//   - `poseInto<T extends CameraPose>` stays generic over <see cref="CameraPose"/>.
// * Optional numeric parameters that JS reads through `Number.isFinite` (killerX/killerY) → `double?` (undefined → null).
// * The camera is main-thread presentation state (one instance per renderer); it holds no module-level mutable state.
using System;
using Fluitown.Runtime;
using static Fluitown.Render.CameraModule;
using static Fluitown.Render.Config;
using static Fluitown.Render.TerrainProjection;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>The anonymous `{ set(x: number, y: number): void }` member type of <see cref="CameraTransformTarget"/>.</summary>
public interface CameraTransformSetter
{
    void set(double x, double y);
}

/// <summary>Minimal mutable transform target used by the renderer-independent camera math.</summary>
public interface CameraTransformTarget
{
    CameraTransformSetter scale { get; }
    CameraTransformSetter position { get; }
}

/// <summary>
/// PORT ADDITION: a plain <see cref="CameraTransformTarget"/> shaped like renderer.ts's `cameraTransform` record:
/// `position.set(x, y)` stores the stamped world offset in <see cref="x"/>/<see cref="y"/>, `scale.set(x)` the zoom.
/// </summary>
public sealed class CameraTransform : CameraTransformTarget
{
    public double x = 0;
    public double y = 0;
    public double zoom = 1;

    private readonly Setter positionSetter;
    private readonly Setter scaleSetter;

    public CameraTransform()
    {
        this.positionSetter = new Setter((x, y) =>
        {
            this.x = x;
            this.y = y;
        });
        this.scaleSetter = new Setter((x, _y) => { this.zoom = x; });
    }

    public CameraTransformSetter position => this.positionSetter;
    public CameraTransformSetter scale => this.scaleSetter;

    private sealed class Setter : CameraTransformSetter
    {
        private readonly Action<double, double> action;

        public Setter(Action<double, double> action)
        {
            this.action = action;
        }

        public void set(double x, double y) => this.action(x, y);
    }
}

public static partial class CameraModule
{
    internal static double initialGameplayZoom()
    {
        return Math.min(CAMERA.resolvedZoomMax, Math.max(CAMERA.resolvedZoomMin, CAMERA.baseZoom));
    }
}

/// <summary>
/// The world camera. Frames the predicted self position, follows the discrete terrain-height component with a
/// stable critically damped spring, eases its zoom, and clamps invalid out-of-space coordinates at finite world edges. Owns no render objects — it
/// just resolves a transform and stamps it onto the world container, so the renderer can use its resolved
/// centre/zoom for culling.
///
/// Framing offsets are expressed in SCREEN space, never world space. The old rule here was "planar focus is
/// exact, full stop", because a world-unit look-ahead is multiplied by zoom and can push the player almost off
/// a short landscape viewport — a real objection, but one that applies only to a world-unit offset. The travel
/// lead (<see cref="setFraming"/>) is authored as a fraction of the half-viewport and divided by the live zoom at
/// stamp time, so it displaces the subject by the same fraction of the frame at every zoom, on every viewport,
/// by construction — it cannot grow toward the edge. Terrain height stays the separate exception because its
/// tile sampler is intentionally discrete: applying that step directly to the global transform jumps every
/// terrain/actor pixel at once, so the spring turns it into continuous camera motion while the actor's real
/// depth anchor remains on the exact walk plane.
/// </summary>
public sealed class Camera
{
    /// <summary>Resolved logical centre — used for background culling.</summary>
    public double x = 0;
    public double y = 0;
    /// <summary>Presented terrain height at the focus anchor, in render-world pixels. Its target remains the exact sampled
    /// walk plane; only this camera-owned presentation value is continuous.</summary>
    private readonly CriticallyDampedValue focusLift = new CriticallyDampedValue();
    public double zoom = initialGameplayZoom();

    private double targetZoom = CAMERA.baseZoom;
    /// <summary>Eased ambient zoom — the authored base/danger/faction-war target. It is the authored composition, and
    /// in this game the observer's wheel scales it through <see cref="observerZoomFactor"/>.</summary>
    private double ambientZoom = CAMERA.baseZoom;
    /// <summary>
    /// Player-owned zoom multiplier, driven by the mouse wheel.
    ///
    /// This game is commanded, not piloted: reading a formation and picking a destination across the garden
    /// both need a scale the player chooses, so unlike the ancestor project the authored framing is a BASE
    /// here rather than the final word. It rides multiplicatively on top of the composed authored zoom
    /// (see <see cref="resolveZoom"/>), which keeps every authored term — the danger pull-out, the impact punch, the
    /// death beat — intact and merely re-scaled, instead of replacing the composition with a raw player value.
    /// 1 = exactly the authored framing.
    /// </summary>
    private double observerZoomFactor = 1;
    /// <summary>Review/preview pin — see <see cref="forceZoom"/>. 0 = production owns the scale.</summary>
    private double pinnedZoom = 0;
    private double prevZoom = initialGameplayZoom();
    /// <summary>Previous exact target retained only to detect teleports for the terrain-height spring.</summary>
    private double previousTargetX = 0;
    private double previousTargetY = 0;

    /// <summary>Half the SHORT viewport axis measured in world space at zoom 1 (`min(w, h / groundScale) / 2`) — the same
    /// quantity `config.responsiveBaseZoom` frames against, so "a fraction of the half-viewport" means the same
    /// thing to the ladder and to the lead. 0 until a viewport is published: with no screen reference there is no
    /// screen-space framing, so probes/demos/tests that never call <see cref="setFraming"/> stay exactly centred.</summary>
    private double viewportShortWorldHalf = 0;
    /// <summary>An authored cutscene owns the framing this frame — the gameplay travel lead stands down.</summary>
    private bool cinematicFraming = false;
    /// <summary>The resolved travel lead, one critically damped spring per world axis. Springing the OFFSET (not just its
    /// magnitude) is what makes a direction reversal glide through zero instead of mirroring instantly.</summary>
    private readonly CriticallyDampedValue leadX = new CriticallyDampedValue();
    private readonly CriticallyDampedValue leadY = new CriticallyDampedValue();

    /// <summary>Pixel-snap blends for the camera's FRAMING axes (1 = full pixel snap, 0 = continuous). Recomputed
    /// independently from screen-space X/Y speed: horizontal movement releases only X and keeps Y fixed.
    /// Both default to 1 so a `snapTo`-then-`applyTo` (the still-frame
    /// demos/probes) snaps as before. It deliberately measures the framing only: the shake is no longer routed
    /// through the quantiser at all (<see cref="applyTo"/>), so it neither needs to open this gate nor may close it.</summary>
    private double snapStrengthX = 1;
    private double snapStrengthY = 1;
    private double prevCX = 0;
    private double prevCY = 0;
    private double prevFocusLift = 0;

    /// <summary>The world offset resolved by the last <see cref="applyTo"/>.</summary>
    private double offX = 0;
    private double offY = 0;
    /// <summary>Local-space correction that lets static scenery render on the fully snapped device-pixel grid while the
    /// parent world may stay continuous for actor/FX motion.</summary>
    private double staticSnapX = 0;
    private double staticSnapY = 0;
    private bool applied = false;

    /// <summary>Fixed ground-plane orbit. Cached basis keeps every frame and pointer calculation in lockstep.</summary>
    private double worldYawValue;
    private double worldYawCos;
    private double worldYawSin;

    public Camera(double worldYaw = 0)
    {
        this.worldYawValue = Number.isFinite(worldYaw) ? worldYaw : 0;
        this.worldYawCos = Math.cos(this.worldYawValue);
        this.worldYawSin = Math.sin(this.worldYawValue);
    }

    /// <summary>
    /// Publish this frame's presentation viewport (CSS px) and say whether an authored cutscene owns the framing.
    /// The travel lead is a screen-space quantity, so it needs the screen; until a caller supplies one the camera
    /// frames the subject dead centre exactly as before.
    /// </summary>
    public void setFraming(double screenW, double screenH, bool cinematic = false)
    {
        double width = Number.isFinite(screenW) && screenW > 0 ? screenW : 0;
        double height = Number.isFinite(screenH) && screenH > 0 ? screenH : 0;
        this.viewportShortWorldHalf =
            width > 0 && height > 0 ? Math.min(width, height / TERRAIN_VIEW_GROUND_SCALE) / 2 : 0;
        this.cinematicFraming = cinematic;
    }

    /// <summary>Jump the camera straight to a point (on instance transfer, to avoid a swoop across the world).</summary>
    public void snapTo(double x, double y, double focusLift = 0)
    {
        this.x = x;
        this.y = y;
        this.previousTargetX = x;
        this.previousTargetY = y;
        this.focusLift.reset(finiteLift(focusLift));
        this.ambientZoom = this.targetZoom;
        // A teleport must not carry a travel lead aimed at the space it left: the ambient target was never touched,
        // so the authored framing is simply what the next frame resolves.
        this.leadX.reset(0);
        this.leadY.reset(0);
        this.zoom = this.resolveZoom(this.ambientZoom);
        this.prevZoom = this.zoom;
        // A teleport is stationary by definition — keep the full pixel snap (and seed the centre baseline so the
        // first subsequent update measures a true delta), so still-frame demos/probes render snapped as before.
        this.snapStrengthX = 1;
        this.snapStrengthY = 1;
        this.prevCX = x;
        this.prevCY = y;
        this.prevFocusLift = this.focusLift.value;
    }

    /// <summary>
    /// PIN the resolved zoom for review tooling, preview stages and still-frame probes: exactly `z`, with no
    /// easing, and held.
    ///
    /// It is a pin rather than a one-frame stamp because the production framing is now authored and narrow —
    /// the resolved envelope brackets `CAMERA.baseZoom` by roughly a tenth in each direction. A review tool that
    /// merely SET the zoom would have it eased back by the renderer's per-frame ambient target within a few
    /// frames, and clamped into that envelope besides: a perf probe asking for a deliberately wide 0.2 would
    /// silently render at the production floor and quietly invalidate its own benchmark. So the pin also
    /// bypasses the production clamp — deliberately, and only here. The server still enforces the disclosure
    /// radius, so a wide review frame shows more terrain, never more streamed hostiles.
    /// </summary>
    public void forceZoom(double z)
    {
        if (!Number.isFinite(z) || z <= 0) return;
        this.pinnedZoom = z;
        this.targetZoom = z;
        this.ambientZoom = z;
        this.zoom = z;
        this.prevZoom = z;
    }

    /// <summary>Resolve the presented scale: a <see cref="forceZoom"/> review pin wins outright, otherwise the composed
    /// production camera (authored ambient × transient punch) clamped into the presentation envelope.</summary>
    private double resolveZoom(double composed)
    {
        if (this.pinnedZoom > 0) return this.pinnedZoom;
        double minimum = CAMERA.resolvedZoomMin;
        double maximum = CAMERA.resolvedZoomMax;
        double authored = !Number.isFinite(composed)
            ? Math.min(maximum, Math.max(minimum, CAMERA.baseZoom))
            : Math.min(maximum, Math.max(minimum, composed));
        // The observer's wheel scales the finished authored composition. Applying it after the authored clamp
        // is what lets the wheel leave that narrow band at all — inside it, any factor would simply be clamped
        // away — while the wider absolute envelope below still bounds how far the view may travel.
        if (this.observerZoomFactor == 1) return authored;
        return Math.min(
            OBSERVER_ZOOM_ABSOLUTE_MAX,
            Math.max(OBSERVER_ZOOM_ABSOLUTE_MIN, authored * this.observerZoomFactor));
    }

    /// <summary>
    /// Stamp the resolved transform (centre + zoom) onto the world container, with the camera's own FRAMING
    /// **snapped to whole device pixels**. Sub-pixel camera motion is what makes fine procedural detail — and the
    /// bloom/colour-grade filters that re-sample the whole scene — shimmer and flicker as you pan: each frame the
    /// scene lands on a slightly different fractional grid. Quantising the pan to the physical pixel grid
    /// (`resolution` = the renderer's device-pixel ratio) renders static content identically frame-to-frame, so
    /// movement is smooth.
    /// STUDIO: the game's camera impulses (screen shake, impact push) are gone, so nothing is stamped outside the
    /// quantiser.
    /// </summary>
    public void applyTo(CameraTransformTarget world, double screenW, double screenH, double resolution = 1)
    {
        world.scale.set(this.zoom, this.zoom);
        double rotatedFocusX = this.worldYawCos * this.x + this.worldYawSin * this.y;
        double rotatedFocusY = -this.worldYawSin * this.x + this.worldYawCos * this.y;
        double baseX = screenW / 2 - rotatedFocusX * this.zoom;
        double projectedFocusY =
            rotatedFocusY * TERRAIN_VIEW_GROUND_SCALE - this.focusLift.value * TERRAIN_VIEW_HEIGHT_SCALE;
        double baseY = screenH / 2 - projectedFocusY * this.zoom;
        // Blend each framing axis toward its pixel-snapped value by that axis' own motion gate (see update).
        // A sub-1 backing density is a fill-rate policy, not a licence to quantise camera framing into blocks
        // larger than a CSS pixel. Invalid diagnostic inputs likewise fall back to the CSS pixel grid.
        double snapResolution =
            Number.isFinite(resolution) && resolution > 0 ? Math.max(1, resolution) : 1;
        double sx = Math.round(baseX * snapResolution) / snapResolution;
        double sy = Math.round(baseY * snapResolution) / snapResolution;
        double framedX = baseX + (sx - baseX) * this.snapStrengthX;
        double framedY = baseY + (sy - baseY) * this.snapStrengthY;
        world.position.set(framedX, framedY);
        // Only the framing residue, so a static layer applying this lands on the snapped framing.
        double rotatedSnapX = (sx - framedX) / this.zoom;
        double rotatedSnapY = (sy - framedY) / (this.zoom * TERRAIN_VIEW_GROUND_SCALE);
        this.staticSnapX = this.worldYawCos * rotatedSnapX - this.worldYawSin * rotatedSnapY;
        this.staticSnapY = this.worldYawSin * rotatedSnapX + this.worldYawCos * rotatedSnapY;
        // Remember the applied offset so viewBounds reports the view that was ACTUALLY rendered.
        this.offX = framedX;
        this.offY = framedY;
        this.applied = true;
    }

    /// <summary>
    /// Conservative source-world bounds for every surface in a rendered height band.
    ///
    /// A flat inverse projection is insufficient for terrain streaming: elevated ground farther along the
    /// camera depth axis can project upward into the viewport, while a chasm closer to the camera can project
    /// downward into it. Selecting tiles from the zero-height rectangle therefore leaves visible wedges without
    /// a chunk. This inverse-projects the complete screen rectangle at both height extrema before publishing its
    /// source-world AABB. Heights are render-world pixels, matching the height term in the terrain projection.
    /// </summary>
    public CameraView viewBoundsForHeightRangeInto(
        double screenW,
        double screenH,
        double minHeight,
        double maxHeight,
        CameraView @out)
    {
        double rotatedLeft;
        double rotatedRight;
        double rotatedTop;
        double rotatedBottom;
        if (this.applied)
        {
            // Screen (0,0)→(w,h) maps to world ((0,0)-off)/zoom → ((w,h)-off)/zoom under the applied offset.
            rotatedLeft = -this.offX / this.zoom;
            rotatedTop = -this.offY / (this.zoom * TERRAIN_VIEW_GROUND_SCALE);
            rotatedRight = (screenW - this.offX) / this.zoom;
            rotatedBottom = (screenH - this.offY) / (this.zoom * TERRAIN_VIEW_GROUND_SCALE);
        }
        else
        {
            double halfW = screenW / (2 * this.zoom);
            double halfH = screenH / (2 * this.zoom * TERRAIN_VIEW_GROUND_SCALE);
            double rotatedCenterX = this.worldYawCos * this.x + this.worldYawSin * this.y;
            double rotatedCenterY = -this.worldYawSin * this.x + this.worldYawCos * this.y;
            rotatedLeft = rotatedCenterX - halfW;
            rotatedRight = rotatedCenterX + halfW;
            rotatedTop = rotatedCenterY - halfH;
            rotatedBottom = rotatedCenterY + halfH;
        }
        double safeMinHeight = finiteLift(Math.min(minHeight, maxHeight));
        double safeMaxHeight = finiteLift(Math.max(minHeight, maxHeight));
        double heightToGround = TERRAIN_VIEW_HEIGHT_SCALE / TERRAIN_VIEW_GROUND_SCALE;
        rotatedTop += safeMinHeight * heightToGround;
        rotatedBottom += safeMaxHeight * heightToGround;
        // The displayed rectangle is axis-aligned in the yawed camera basis. Streaming and culling consume
        // source-world coordinates, so inverse-rotate its four corners and publish their conservative AABB.
        double x0 = this.worldYawCos * rotatedLeft - this.worldYawSin * rotatedTop;
        double y0 = this.worldYawSin * rotatedLeft + this.worldYawCos * rotatedTop;
        double x1 = this.worldYawCos * rotatedRight - this.worldYawSin * rotatedTop;
        double y1 = this.worldYawSin * rotatedRight + this.worldYawCos * rotatedTop;
        double x2 = this.worldYawCos * rotatedLeft - this.worldYawSin * rotatedBottom;
        double y2 = this.worldYawSin * rotatedLeft + this.worldYawCos * rotatedBottom;
        double x3 = this.worldYawCos * rotatedRight - this.worldYawSin * rotatedBottom;
        double y3 = this.worldYawSin * rotatedRight + this.worldYawCos * rotatedBottom;
        @out.left = Math.min(x0, x1, x2, x3);
        @out.right = Math.max(x0, x1, x2, x3);
        @out.top = Math.min(y0, y1, y2, y3);
        @out.bottom = Math.max(y0, y1, y2, y3);
        return @out;
    }
}

public static partial class CameraModule
{
    internal static double finiteLift(double value)
    {
        return Number.isFinite(value) ? value : 0;
    }
    internal const double OBSERVER_ZOOM_ABSOLUTE_MIN = 0.12;
    internal const double OBSERVER_ZOOM_ABSOLUTE_MAX = 3;
}
