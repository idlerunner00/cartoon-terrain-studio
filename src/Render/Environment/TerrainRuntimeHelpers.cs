// Port of packages/client/src/render/environment/terrainRuntimeHelpers.ts — keep in lockstep with the original.
//
// PORT NOTES
// * Ported: the two pure shadow helpers (`terrainShadowMapSize`, `terrainShadowAnchorNeedsUpdate`) and SHADOW_EDGE_GUARD.
// * NOT ported (three.js object plumbing for tile buffers, owned by the tile-management integration):
//   `terrainGeometryArrayBuffers(payload)` (collects the transferable ArrayBuffers of a TerrainGeometryPayload lane by
//   lane: surface position/normal/color/surface/emissive/ground/index, water position/normal/color/water/fold/
//   reflection/index, mist position/color/mist/index, overlay position/color/index, actorWall position/index — only
//   non-empty `ArrayBuffer`s), `geometryArrayBuffers(...objects)`, `geometryResources(...objects)` and
//   `disposeObject(object)` (Object3D traversals over BufferGeometry attributes).
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public static partial class TerrainRuntimeHelpers
{
    private const double SHADOW_EDGE_GUARD = 64;

    public static double terrainShadowMapSize(double requestedSize, bool mobile)
    {
        double requested = Number.isFinite(requestedSize)
            ? Math.max(256, Math.floor(requestedSize))
            : 1_024;
        _ = mobile;
        return Math.min(requested, 1_024);
    }

    /// <summary>
    /// Whether the retained depth window still covers the view.
    ///
    /// COVERAGE only. Whether the window is the right SIZE is a separate question and belongs to the lattice
    /// ladder (`shadowLattice.shadowWindowRadius`) — this function used to answer both, and its "the map is
    /// bigger than it needs to be" clause is what re-fitted the map on almost every frame of a zoom, at a fresh
    /// texel size each time. Reclaiming texel density is worth one republication when a whole rung of it is
    /// being wasted; it is not worth one per 64 px of zoom.
    /// </summary>
    public static bool terrainShadowAnchorNeedsUpdate(
        double lastCx,
        double lastCz,
        double lastRadius,
        double desiredCx,
        double desiredCz,
        double viewRadius,
        double paddedRadius)
    {
        _ = paddedRadius;
        if (!Number.isFinite(lastCx) || !Number.isFinite(lastCz) || !Number.isFinite(lastRadius))
            return true;
        double shift = Math.hypot(desiredCx - lastCx, desiredCz - lastCz);
        return shift + viewRadius > lastRadius - SHADOW_EDGE_GUARD;
    }
}
