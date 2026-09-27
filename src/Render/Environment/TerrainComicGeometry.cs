// Fluitown extension — NOT a port of the original. 360° geometry for the comic look, so the terrain is closed when seen
// from every side (the 3D view).
namespace Fluitown.Render;

/// <summary>
/// The original builds its props for one fixed oblique camera: a post, trunk or branch gets only the side bands that
/// face that camera (the south half), a box only its lid and south face. The Flui perspective walks around them, and
/// from the north the half shells show their hollow insides. With <see cref="ClosedShells"/> the prop primitives
/// (<see cref="WorldPropPrimitives.propFrustum"/>, <see cref="WorldPropPrimitives.propLimb"/>,
/// <see cref="WorldPropPrimitives.propSpike"/>, <see cref="WorldPropPrimitives.propBox"/>) build every side.
/// </summary>
public static class TerrainComicGeometry
{
    /// <summary>Set before the first bake (TerrainSceneRenderer.Initialize: comic look only); bakes run on worker threads.</summary>
    public static volatile bool ClosedShells;
}
