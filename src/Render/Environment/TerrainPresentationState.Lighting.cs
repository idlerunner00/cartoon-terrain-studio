// Port of packages/client/src/render/environment/threeTerrain.ts (class ThreeTerrainLayer) — lighting (shadow window,
// light anchors), backdrop, the shadow-map refresh and `render()`, plus the presentation tail of `update()`.
// See TerrainPresentationState.cs for the notes.
using Fluitown.Runtime;
using static Fluitown.Render.ThreeTerrain;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed partial class TerrainPresentationState
{
    /// <summary>
    /// The presentation tail of `update(view, state, nowSec, …)` (TS lines 6444–6455, identical in the early-out paths
    /// at 5830–5832 and 5979–5990): `updateBackdrop(view, ts)`, `waterTime.value = nowSec`, `render()`. The tile half of
    /// `update()` (residency, planning, installs, `root.visible`) runs in the host first and hands over `ts`
    /// (`anchor.tileSize || TILE_SIZE`).
    /// </summary>
    public void presentFrame(CameraView view, double ts, double nowSec)
    {
        this.ts = ts;
        this.updateBackdrop(view, ts);
        this.waterTime.value = nowSec;
        this.render();
    }

    /* ── Lighting ───────────────────────────────────────────────────────────────────────────────────────── */

    /// <summary>
    /// Follow the view with a LATTICE-STABLE shadow window.
    ///
    /// Both halves of "stable" are the point, and the map used to have neither:
    ///
    ///  - Its half-extent was fitted to the view. Even a coarse zoom ladder still needs an indivisible depth-map
    ///    resize, and hiding that resize by fading shadow strength to zero is itself visible flicker. One fixed
    ///    rung covers the complete supported gameplay framing. If an unusual viewport or camera override exceeds
    ///    that envelope, the map grows once to the next rung and is retained for the rest of the terrain session.
    ///  - Its anchor was quantised on a WORLD-axis grid. The texel lattice is a light-space grid and the sun is
    ///    tilted, so that quantisation never landed on a texel boundary: every re-anchor put all of the world's
    ///    shadow edges at a fresh sub-texel phase. It now uses the same `snapAnchorToShadowTexels` the actor sun
    ///    has always used, so a republication at an unchanged rung is bit-identical and therefore invisible.
    /// </summary>
    private void updateLighting(
        double worldLeft,
        double worldRight,
        double worldTop,
        double worldBottom)
    {
        double viewRadius = 0.5 * Math.hypot(worldRight - worldLeft, worldBottom - worldTop);
        this.flickerViewRadius = viewRadius;
        // `GAMEPLAY_VIEW` is the ordinary disclosure/camera contract, not a geometric guarantee about every canvas
        // the renderer can ever be asked to present (dev overview cameras, resized ultra-wide windows and authored
        // transitions can all be larger). Keeping the fixed radius in those cases clips the receiver projection to
        // the shadow camera's square, which appears on the ground as the reported shadowless diamond. Grow to cover
        // the ACTUAL corner radius plus the same pan guard, then retain that larger lattice so zooming back in cannot
        // republish every shadow at a new texel phase.
        double requiredRadius = ShadowLattice.shadowWindowRung(viewRadius + SHADOW_PAD);
        double radius = Math.max(
            TERRAIN_SHADOW_STABLE_RADIUS,
            requiredRadius,
            Number.isFinite(this.lastShadowRadius) ? this.lastShadowRadius : 0);
        double desiredCx = (worldLeft + worldRight) * 0.5;
        double desiredCz = (worldTop + worldBottom) * 0.5;
        bool activeWindowAlreadyFits =
            radius == this.lastShadowRadius &&
            !TerrainRuntimeHelpers.terrainShadowAnchorNeedsUpdate(
                this.lastShadowCx,
                this.lastShadowCz,
                this.lastShadowRadius,
                desiredCx,
                desiredCz,
                viewRadius,
                radius);
        if (activeWindowAlreadyFits)
        {
            return;
        }
        // The texel the anchor lands on is the one the map is ACTUALLY rendered with, so it is derived from the
        // rung above rather than from the view's raw requirement.
        (double x, double z) anchor = ShadowLattice.snapAnchorToShadowTexels(
            desiredCx,
            desiredCz,
            this.sunDirection,
            (2 * radius) / this.SHADOW_MAP_SIZE);
        TerrainShadowWindow nextWindow = new TerrainShadowWindow(anchor.x, anchor.z, radius);
        this.applyTerrainShadowWindow(nextWindow);
    }

    /// <summary>`interface TerrainShadowWindow { cx, cz, radius }`.</summary>
    internal readonly record struct TerrainShadowWindow(double cx, double cz, double radius);

    internal void applyTerrainShadowWindow(TerrainShadowWindow window)
    {
        this.lastShadowCx = window.cx;
        this.lastShadowCz = window.cz;
        this.lastShadowRadius = window.radius;
        this.shadowDirty = true;
        this.applyLightDirectionsAtAnchor(window.cx, window.cz);
        // One shared LightShadow: the visible sun and the wall proxy own the same depth camera.
        this.configureTerrainShadowCamera((ThreeOrthographicCamera)this.sun.shadow!.camera, window.radius);
    }

    private void configureTerrainShadowCamera(ThreeOrthographicCamera shadowCamera, double radius)
    {
        shadowCamera.left = -radius;
        shadowCamera.right = radius;
        shadowCamera.top = radius;
        shadowCamera.bottom = -radius;
        shadowCamera.near = 1;
        shadowCamera.far = SHADOW_LIGHT_DISTANCE + SHADOW_CASTER_REACH;
        shadowCamera.updateProjectionMatrix();
    }

    /// <summary>Move the live key/fill around an already guarded anchor without invalidating or resizing its depth map.</summary>
    private void applyLightDirectionsAtAnchor(double cx, double cz)
    {
        ThreeVector3 d = this.sunDirection;
        double dl = Math.hypot(d.x, d.y, d.z);
        this.sunTarget.position.set(cx, 0, cz);
        this.sun.position.set(
            cx + (d.x / dl) * SHADOW_LIGHT_DISTANCE,
            (d.y / dl) * SHADOW_LIGHT_DISTANCE,
            cz + (d.z / dl) * SHADOW_LIGHT_DISTANCE);
        this.sunTarget.updateMatrixWorld();
        this.sun.updateMatrixWorld();
        this.shadowProxySunTarget.position.copy(this.sunTarget.position);
        this.shadowProxySun.position.copy(this.sun.position);
        this.shadowProxySunTarget.updateMatrixWorld();
        this.shadowProxySun.updateMatrixWorld();
        ThreeVector3 f = this.fillDirection;
        this.fillTarget.position.set(cx, 0, cz);
        this.fill.position.set(cx + f.x * 1000, f.y * 1000, cz + f.z * 1000);
        this.fillTarget.updateMatrixWorld();
        this.fill.updateMatrixWorld();
    }

    private void updateBackdrop(CameraView view, double ts)
    {
        double pad = Math.max(BACKDROP_PAD, ts * 5);
        double left = view.left - pad;
        double top = view.top - pad;
        double width = view.right - view.left + pad * 2;
        double height = view.bottom - view.top + pad * 2;
        this.backdrop.position.set(left + width * 0.5, -BACKDROP_DROP_Y, top + height * 0.5);
        this.backdrop.scale.set(width, 1, height);
        this.backdrop.updateMatrixWorld();
        // Framing uniform for the backdrop shader: view centre + inverse half-extents (edge falloff domain).
        this.backdropView.value.set(
            (view.left + view.right) * 0.5,
            (view.top + view.bottom) * 0.5,
            2 / Math.max(1, view.right - view.left),
            2 / Math.max(1, view.bottom - view.top));
    }

    /// <summary>
    /// Refresh the shared sun depth target from the compact solid-wall scene. The visible surface mesh contains
    /// floors, foliage, decals and dressing that already carry baked grounding; replaying all of those vertices
    /// was the dominant walking hitch. The proxy scene writes its colour pass into the existing 1×1 scratch
    /// target while Three's shadow prepass updates the real map shared with the visible sun.
    ///
    /// The map is a CONTENT snapshot, never a clock tick: this runs only when the guarded light anchor moved or
    /// streamed casters inside the window changed. Nothing here is scheduled by the day/night cycle.
    /// </summary>
    private void refreshTerrainShadowMap()
    {
        ThreeWebGLRenderer? renderer = this.renderer;
        if (renderer == null) return;
        this.flickerShadowRebuilds++;
        bool latticeChanged =
            this.lastShadowRadius != this.lastRenderedShadowRadius ||
            this.lastShadowCx != this.lastRenderedShadowCx ||
            this.lastShadowCz != this.lastRenderedShadowCz;
        if (latticeChanged && this.sun.shadow!.intensity > 0.001)
            this.flickerVisibleShadowLatticeRebuilds++;
        bool previousAutoClear = renderer.autoClear;
        try
        {
            renderer.autoClear = true;
            renderer.shadowMap.needsUpdate = true;
            renderer.render(this.actorWallScene, this.camera);
        }
        finally
        {
            // Never let the following visible render replay the full scene into the same shared shadow resource.
            renderer.shadowMap.needsUpdate = false;
            renderer.autoClear = previousAutoClear;
            renderer.resetState();
        }
        this.host?.captureShadowCasters();
        this.lastRenderedShadowCx = this.lastShadowCx;
        this.lastRenderedShadowCz = this.lastShadowCz;
        this.lastRenderedShadowRadius = this.lastShadowRadius;
    }

    /// <summary>Keep the injected actor-shadow sampler WebGL-valid while no dynamic actor map is published. ANGLE rejects
    /// a custom `sampler2DShadow` bound to Three's generic null fallback before shader control flow can early-out,
    /// so the terrain sun's completed comparison-depth target is the stable inactive resource.</summary>
    private void bindActorShadowFallback()
    {
        this.actorShadows.bindFallback(this.sun.shadow!.map?.depthTexture);
    }

    /// <summary>`render()`: shadow-snapshot bookkeeping, the proxy shadow refresh and the visible scene submission.</summary>
    public void render()
    {
        if (this.renderer == null) return;
        this.bindActorShadowFallback();
        if (this.host?.terrainCachePrewarming == true)
        {
            ThreeWebGLRenderer renderer = this.renderer;
            bool previousAutoClear = renderer.autoClear;
            bool previousShadowUpdate = renderer.shadowMap.needsUpdate;
            bool previousSunCastShadow = this.sun.castShadow;
            try
            {
                // Buffer preparation is the contract here. The destination light anchor is rebuilt after the later
                // biome swap, so spending a full shadow-map pass now would be stale. A shadow-receiving program may
                // never be drawn while its sampler2DShadow has no depth target, however: ANGLE rejects that first draw
                // with GL_INVALID_OPERATION. Compile the cache-only submit in its valid unshadowed family; the first
                // covered live render restores the sun and builds the real, correctly anchored depth target.
                this.sun.castShadow = false;
                renderer.shadowMap.needsUpdate = false;
                renderer.autoClear = true;
                renderer.render(this.scene, this.camera);
            }
            finally
            {
                this.sun.castShadow = previousSunCastShadow;
                renderer.autoClear = previousAutoClear;
                renderer.shadowMap.needsUpdate = previousShadowUpdate;
                renderer.resetState();
            }
            return;
        }
        bool inspectCasterWork =
            this.shadowCasterSnapshotPending ||
            this.terrainShadowSnapshotStableFrames < INITIAL_SHADOW_STABLE_FRAMES;
        bool shadowCasterWorkPending = inspectCasterWork && (this.host?.shadowCasterWorkPending() ?? false);
        bool casterWorkPending = this.shadowCasterSnapshotPending && shadowCasterWorkPending;
        // The bootstrap target is already a valid empty comparison texture. Do not promote a one/two-tile cold
        // fill to the authoritative snapshot: streamed visible tiles can arrive over several frames, while a
        // speculative outer-ring worker may remain busy indefinitely. Publish only when the complete visible
        // rectangle is ready, so the initial presentation fence can never approve a partial chunk snapshot.
        bool viewportReady = this.host?.viewportReady ?? true;
        bool canInitializeTerrainShadow =
            this.root.visible && (this.host?.activeTileCount ?? 1) > 0 && viewportReady;
        bool shadowMapInitialized =
            this.terrainShadowSnapshotInitialized && this.sun.shadow!.map != null;
        bool visibleCasterBatchReady =
            this.visibleShadowCasterSnapshotPending && viewportReady;
        if (
            this.terrainShadowsEnabled &&
            (shadowMapInitialized || canInitializeTerrainShadow) &&
            terrainShadowSnapshotNeedsRefresh(
                shadowMapInitialized,
                this.shadowDirty,
                this.shadowCasterSnapshotPending,
                casterWorkPending,
                visibleCasterBatchReady))
        {
            this.refreshTerrainShadowMap();
            this.shadowDirty = false;
            if (canInitializeTerrainShadow) this.terrainShadowSnapshotInitialized = true;
            // A single proxy-scene pass always captures every caster mounted at submission time. Visible changes are
            // therefore complete now even if a wider speculative ring remains queued for a later batched refresh.
            this.visibleShadowCasterSnapshotPending = false;
            // The first visible frame must own a valid depth sampler even while speculative tiles are outstanding.
            // Keep the batch pending in that case; its one final refresh happens when the relevant compiler drains.
            if (!casterWorkPending) this.shadowCasterSnapshotPending = false;
        }
        else if (!this.terrainShadowsEnabled)
        {
            this.shadowDirty = false;
        }
        if (!this.terrainShadowsEnabled)
        {
            this.terrainShadowSnapshotStableFrames = INITIAL_SHADOW_STABLE_FRAMES;
        }
        else if (
            this.terrainShadowSnapshotInitialized &&
            !this.shadowDirty &&
            !this.visibleShadowCasterSnapshotPending &&
            // Once the two-frame presentation proof is complete, new visible mounts explicitly re-arm the flag
            // above. Avoid rescanning the bounded tile cache on every steady gameplay frame thereafter.
            (this.terrainShadowSnapshotStableFrames >= INITIAL_SHADOW_STABLE_FRAMES ||
                (this.host?.visibleShadowCastersCaptured() ?? true)) &&
            // Settled means **the visible bank** is in the snapshot — not that streaming has stopped.
            //
            // This used to also require no outstanding caster batch at all, which is the right condition for a
            // finite authored map: it loads once, the last tile lands, and the world is complete forever after.
            // In an unbounded streamed world there is no such moment. The camera always has a speculative ring
            // being generated beyond what it can see, so `shadowCasterSnapshotPending` is essentially always
            // true, the stable count never reaches its threshold, and the initial-world cover never lifts —
            // the player waits on a condition the world is structurally incapable of reaching.
            //
            // What the fence is actually for is not showing a half-lit world, and `viewportReadiness.ready`
            // already asserts exactly that: complete chunk coverage and no visible tile missing its bake. So
            // the visible rectangle is the scope, and the ring beyond it refreshes the map as it arrives the
            // same way it does for the rest of the session.
            viewportReady)
        {
            this.terrainShadowSnapshotStableFrames = Math.min(
                INITIAL_SHADOW_STABLE_FRAMES,
                this.terrainShadowSnapshotStableFrames + 1);
        }
        else
        {
            this.terrainShadowSnapshotStableFrames = 0;
        }
        // A first-frame shadow refresh above may have created the target this call needs.
        this.bindActorShadowFallback();
        this.renderer.render(this.scene, this.camera);
    }
}
