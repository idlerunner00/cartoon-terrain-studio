// Port of packages/client/src/render/environment/terrainProjection.ts — keep in lockstep with the original.
using Fluitown.Domain;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// The renderer's one world→screen contract, shared by terrain, actors and effects.
///
/// Terrain geometry lives in true 3D: X = world x (east), Y = up (height, px), Z = world y (south), with one
/// elevation LEVEL = <see cref="TERRAIN_ELEVATION_STEP_PX"/> px of height. A normalized oblique orthographic
/// projection maps a world point to the screen as
///
///     screenX = X·zoom + offsetX
///     screenY = (Z·G − Y·H)·zoom + offsetY
///     depth   = Z·H + Y·G
///
/// `G` and `H` are the sine/cosine of the camera elevation and therefore form an orthonormal basis.
/// This is the important part: the old `(Z - 1.12·Y)` row had length > 1 and was not perpendicular to
/// its depth row, so every upright player, pet and creature was visibly stretched. Ground depth and world
/// height now receive the physically correct foreshortening while actors still land exactly on terraces.
///
/// Pure math, no renderer dependencies — unit-tested to pin the actor/terrain alignment.
/// </summary>
public static partial class TerrainProjection
{
    /// <summary>Screen lift (px) per fake-elevation level — terraces step by this, actors lift by this. THE step constant.</summary>
    public const double TERRAIN_ELEVATION_STEP_PX = 15;

    /// <summary>
    /// Keep authored walk planes and cliff junctions geometrically exact.
    ///
    /// The former GPU-only rolling-floor lift and contour warp were evaluated independently on duplicated cap,
    /// face and bake-tile vertices. Even though each displacement was small, their combination could expose a
    /// sub-pixel crack at a height corner; orthographic zoom then turned that crack into a flickering horizontal or
    /// vertical hairline. CPU-authored contour cuts, physical bevels and continuous fragment-normal relief retain
    /// the intended stylized depth without moving the authoritative surface, while disabling this path also removes
    /// two vertex-noise evaluations from both the colour and shadow passes.
    /// </summary>
    public const bool TERRAIN_GPU_VERTEX_SHAPING = false;

    /// <summary>Camera elevation above the ground plane. About 42° retains the approved mildly low gameplay view.</summary>
    public static readonly double TERRAIN_CAMERA_ELEVATION = Math.atan(1 / 1.12);

    /// <summary>
    /// Fixed gameplay orbit around the vertical axis. A small negative yaw places the virtual eye slightly
    /// east/right of the player and looks back toward the west/left, revealing the formerly edge-on wall faces
    /// without turning the top-down combat view into a full isometric diagonal.
    ///
    /// It lives HERE, with the rest of the world→screen contract, rather than in the client config: geometry that
    /// has to be built facing the eye (see <see cref="terrainBillboardBasisInto"/>) is compiled in the terrain worker,
    /// which cannot reach the UI config. `CAMERA.worldYaw` re-exports this so there is still exactly one value.
    /// </summary>
    public const double TERRAIN_CAMERA_WORLD_YAW = ConfigIndex.WORLD_PRESENTATION.cameraYaw;

    /// <summary>Ground-depth foreshortening (`sin(elevation)`).</summary>
    public static readonly double TERRAIN_VIEW_GROUND_SCALE = Math.sin(TERRAIN_CAMERA_ELEVATION);

    /// <summary>Upright-height foreshortening (`cos(elevation)`).</summary>
    public static readonly double TERRAIN_VIEW_HEIGHT_SCALE = Math.cos(TERRAIN_CAMERA_ELEVATION);

    /// <summary>Screen-space projection of a world point under the oblique camera (zoom/offset excluded).</summary>
    public static double terrainScreenX(double x, double _y, double _z)
    {
        return x;
    }

    public static double terrainScreenY(double _x, double y, double z)
    {
        return z * TERRAIN_VIEW_GROUND_SCALE - y * TERRAIN_VIEW_HEIGHT_SCALE;
    }

    /// <summary>
    /// Screen-right and screen-up axes expressed in COMPILE space, i.e. before the scene root applies the world
    /// yaw. A quad spanned by these two vectors is exactly perpendicular to the view direction once the root
    /// rotation is applied, so it presents the same disc at any azimuth.
    ///
    /// Soft volumetric cues (mist wisps) must be built on this basis. Authored as a plain vertical XY card they
    /// stood as a WALL in the world: `depthWrite:false` hid the fact only while nothing intersected them, but a
    /// card standing inside a Chasm or beside a bridge deck is clipped by the geometry it passes through, and the
    /// clip turns a soft cloud into a hard-edged grey slab — the "grey smoke" reported under bridges. A card that
    /// faces the eye can never intersect the world edge-on and therefore can never grow that silhouette.
    /// </summary>
    public static void terrainBillboardBasisInto(P3 right, P3 up, double yaw = TERRAIN_CAMERA_WORLD_YAW)
    {
        double cos = Math.cos(yaw);
        double sin = Math.sin(yaw);
        // The root rotates by `yaw` about Y, so the compile-space axes are the inverse rotation of the screen axes:
        // screen right is world (1,0,0) and screen up is world (0, H, -G) under the oblique basis.
        right.x = cos;
        right.y = 0;
        right.z = sin;
        up.x = TERRAIN_VIEW_GROUND_SCALE * sin;
        up.y = TERRAIN_VIEW_HEIGHT_SCALE;
        up.z = -TERRAIN_VIEW_GROUND_SCALE * cos;
    }

    /// <summary>
    /// Fill `m` with the normalized oblique orthographic projection. Larger view-depth is nearer (smaller NDC z).
    /// `depthMin/depthMax` bound the visible projected depth window (the near/far planes).
    /// </summary>
    /// <remarks>
    /// PORT NOTE: the original fills a three.js <c>Matrix4</c> through <c>m.set(n11, n12, …, n44)</c> (row-major
    /// arguments). This port fills <paramref name="m"/> (length 16) in <c>Matrix4.elements</c> order, i.e.
    /// COLUMN-MAJOR: <c>m[column * 4 + row]</c>, so <c>m[12..14]</c> hold the translation column.
    /// <c>Matrix4.elements</c> is a plain JS array, so the entries stay <c>double</c>.
    /// </remarks>
    public static double[] configureOrthographicProjection(
        double[] m,
        double width,
        double height,
        double zoom,
        double offsetX,
        double offsetY,
        double depthMin,
        double depthMax)
    {
        double range = Math.max(1e-3, depthMax - depthMin);
        // Row 1.
        m[0] = (2 * zoom) / width;
        m[4] = 0;
        m[8] = 0;
        m[12] = (2 * offsetX) / width - 1;
        // Row 2.
        m[1] = 0;
        m[5] = (2 * zoom * TERRAIN_VIEW_HEIGHT_SCALE) / height;
        m[9] = (-2 * zoom * TERRAIN_VIEW_GROUND_SCALE) / height;
        m[13] = 1 - (2 * offsetY) / height;
        // Row 3.
        m[2] = 0;
        m[6] = (-2 * TERRAIN_VIEW_GROUND_SCALE) / range;
        m[10] = (-2 * TERRAIN_VIEW_HEIGHT_SCALE) / range;
        m[14] = (depthMax + depthMin) / range;
        // Row 4.
        m[3] = 0;
        m[7] = 0;
        m[11] = 0;
        m[15] = 1;
        return m;
    }
}
