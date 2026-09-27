// Port of packages/client/src/device.ts — keep in lockstep with the original.
//
// PORT NOTES
// * The original probes the browser `window` (touch support, `matchMedia('(pointer: coarse)')`, the visual
//   viewport). The engine-free port has no `window`: until the host binds one through <see cref="Device.bindHostWindow"/>
//   every function behaves exactly like the original's `typeof window === 'undefined'` branch (no touch, fine
//   pointer, a 1×1 viewport). The Godot layer binds the real window once at start-up and forwards every resize with
//   <see cref="Device.setHostViewportSize"/> — the replacement for the resize/orientationchange/visualViewport
//   listeners that `bindViewportCache` installs.
// * `hasTouch` / `coarsePointer` / `isMobile` are module-load constants in TS; here they are read-only properties over
//   the bound host facts (constant once bound, which must happen before the first reader).
// * Thread safety: this is genuinely main-thread presentation state (the UI viewport); CAMERA getters and the camera
//   read it on the render thread only. Deliberately NOT [ThreadStatic].
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class ClientViewport
{
    /// <summary>Visible CSS-pixel width/height. Rounded once so camera, canvas CSS and drawing buffer agree exactly.</summary>
    public readonly double width;
    public readonly double height;

    public ClientViewport(double width, double height)
    {
        this.width = width;
        this.height = height;
    }
}

/// <summary>
/// Code-only device-capability probe. One place that answers "is this a touch/phone-class device?"
/// so both the input layer (which backend to surface) and the renderer (how hard to push pixels/FX)
/// agree. Values are sampled once at load — capabilities don't change mid-session — but `touchSeen`
/// latches the first real touch so a desktop with a touchscreen only flips to touch chrome on use.
/// </summary>
public static partial class Device
{
    // ── Host stand-in for the browser `window` (see PORT NOTES) ──────────────────────────────────────────────────

    /// <summary>`typeof window !== 'undefined'`.</summary>
    private static bool hostWindowBound;
    /// <summary>`'ontouchstart' in window || navigator.maxTouchPoints > 0`.</summary>
    private static bool hostTouch;
    /// <summary>`window.matchMedia('(pointer: coarse)').matches`.</summary>
    private static bool hostCoarsePointer;
    /// <summary>`window.visualViewport?.width ?? window.innerWidth` (and height), in CSS px.</summary>
    private static double hostViewportWidth;
    private static double hostViewportHeight;

    /// <summary>
    /// Bind the host window (the Godot layer calls this once, before any camera/config read): touch hardware,
    /// coarse primary pointer, and the visible viewport in CSS px (Godot: the root viewport size / content scale).
    /// </summary>
    public static void bindHostWindow(bool touch, bool coarsePointer, double viewportWidth, double viewportHeight)
    {
        hostWindowBound = true;
        hostTouch = touch;
        hostCoarsePointer = coarsePointer;
        hostViewportWidth = viewportWidth;
        hostViewportHeight = viewportHeight;
        refreshClientViewport();
    }

    /// <summary>The resize / orientationchange / visualViewport listener: publish a new visible viewport.</summary>
    public static void setHostViewportSize(double viewportWidth, double viewportHeight)
    {
        hostViewportWidth = viewportWidth;
        hostViewportHeight = viewportHeight;
        refreshClientViewport();
    }

    // ── device.ts ─────────────────────────────────────────────────────────────────────────────────────────────

    private static bool media(string query)
    {
        return (
            hostWindowBound &&
            query == "(pointer: coarse)" && hostCoarsePointer
        );
    }

    /// <summary>Hardware can produce touch events (phone, tablet, or a touch-enabled laptop).</summary>
    public static bool hasTouch => hostWindowBound && hostTouch;

    /// <summary>The primary pointer is coarse (a finger) rather than a fine mouse — i.e. a phone/tablet.</summary>
    public static bool coarsePointer => media("(pointer: coarse)");

    /// <summary>
    /// "Mobile" for *tuning* purposes: a touch device whose primary pointer is coarse. A touchscreen
    /// laptop (touch + fine pointer) is treated as desktop so we don't needlessly cap its fidelity.
    /// </summary>
    public static bool isMobile => hasTouch && coarsePointer;

    private static ClientViewport cachedViewport = new ClientViewport(1, 1);

    /// <summary>
    /// Sample layout-owned viewport dimensions only on a viewport event. Firefox may synchronously flush style
    /// and layout when VisualViewport dimensions are read after HUD mutations; CAMERA getters are used by the
    /// render loop, so reading the DOM from clientViewport() turned that flush into steady-frame work.
    /// </summary>
    public static ClientViewport refreshClientViewport()
    {
        if (!hostWindowBound)
        {
            cachedViewport = new ClientViewport(1, 1);
            return cachedViewport;
        }
        cachedViewport = new ClientViewport(
            finiteViewportDimension(hostViewportWidth),
            finiteViewportDimension(hostViewportHeight));
        return cachedViewport;
    }

    /// <summary>
    /// The rectangle the player can actually see, in CSS pixels. Keeping this in one place prevents the mobile
    /// failure mode where the camera/drawing buffer use `visualViewport` while CSS stretches that image over the
    /// larger layout viewport. The fallback keeps tests/tools without VisualViewport deterministic.
    /// </summary>
    public static ClientViewport clientViewport()
    {
        if (!hostWindowBound) return new ClientViewport(1, 1);
        // PORT NOTE: the original re-binds its cache here when the window/VisualViewport identity changed; the port's
        // host binding refreshes the cache eagerly in bindHostWindow/setHostViewportSize instead.
        return cachedViewport;
    }

    /// <summary>Pure dimension normalization used by the live reader and viewport regression tests.</summary>
    public static double finiteViewportDimension(double value)
    {
        return Number.isFinite(value) ? Math.max(1, Math.round(value)) : 1;
    }
}
