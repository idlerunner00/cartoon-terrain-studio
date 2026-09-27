// Fluitown extension — NOT a port of the original. The comic look's plants on cliff faces: the records are only written
// while Enabled (the Style drawer's "flowers" switch); without it the ported wall growth stays.
using System;
using static Fluitown.Render.FluitownVegetation;
using static Fluitown.Render.TerrainGeometryCompilerModule;

namespace Fluitown.Render;

/// <summary>
/// The plants the ported <see cref="TerrainWallGrowth"/> grows on a cliff face — a shrub pushing out of a crevice, a
/// climber, a fern fanning from a fissure — leave the bake as placements; the Godot vegetation layer grows them as real
/// plants (WallPlantMesher): a godot-flui shrub leaning out of the rock, ivy climbing from the wall's foot on branching
/// stems with lobed leaves, a fern of arching, pinnate fronds. The ported ones are a few ribbons and leaf cards drawn for
/// the original's oblique camera, which also left the east and west faces bare (edge-on to it); the Flui perspective sees
/// every face, so here every orientation may carry one.
///
/// Three records per plant: <see cref="KindWallPlant"/> at the plant's root on the wall, <see cref="KindWallTangent"/>, a
/// point a little along the wall from it, and <see cref="KindWallFoot"/>, the wall's foot below the root. The organic form
/// displaces them like the wall (TerrainOrganicForm.apply), so the Godot side knows where the bent wall runs, which way it
/// faces and — the mountain form leans walls linearly with height — where it stands at every height of a climber.
/// </summary>
public static class FluitownWallPlants
{
    /// <summary>Set before the first bake (TerrainSceneRenderer.Initialize, comic look); bakes run on worker threads.</summary>
    public static volatile bool Enabled;

    /// <summary>Vegetation lane kinds (4–7 water plants, 8 flowers).</summary>
    public const int KindWallPlant = 9, KindWallTangent = 10, KindWallFoot = 11;
    /// <summary><c>Form</c> of a wall plant.</summary>
    public const int FormShrub = 0, FormIvy = 1, FormFern = 2;
    /// <summary>Distance of the tangent point along the wall (compile px).</summary>
    public const double TangentPx = 12;

    /// <summary>Whether wall plants grow on every wall orientation (the Flui perspective sees them all).</summary>
    public static bool AllFaces => Enabled && FluitownVegetation.Enabled;

    /// <summary>
    /// Records one wall plant (TerrainWallGrowth.addTerrainWallGrowth in the comic look). <paramref name="nx"/>,
    /// <paramref name="nz"/> is the wall's outward normal, (<paramref name="tx"/>, <paramref name="tz"/>) its tangent.
    /// </summary>
    public static bool record(
        PropGeometryBuilder builder,
        int form,
        double rootX,
        double rootY,
        double rootZ,
        double nx,
        double nz,
        double tx,
        double tz,
        double wallHeight,
        double footY,
        double scale,
        double seed,
        double cover,
        TerrainWallGrowthProfile profile)
    {
        if (!AllFaces || builder is not TileGeometryBuilder tile) return false;
        FloatBuf lane = tile.vegetation;
        lane.ensure(Stride * 3);
        lane.push(KindWallPlant);
        lane.push(rootX);
        lane.push(rootY);
        lane.push(rootZ);
        lane.push(wallHeight);
        lane.push(scale);
        lane.push(seed - Math.Floor(seed));
        lane.push(Math.Atan2(nz, nx));
        lane.push(profile.leafMid);
        lane.push(profile.leafDeep);
        lane.push(profile.stem);
        lane.push(profile.accent);
        lane.push(form);
        lane.push(profile.leafLight);
        lane.push(rootY - footY);
        lane.push(cover);
        // The tangent companion: only its position is read.
        lane.push(KindWallTangent);
        lane.push(rootX + tx * TangentPx);
        lane.push(rootY);
        lane.push(rootZ + tz * TangentPx);
        for (int i = 4; i < Stride; i++) lane.push(0);
        // The foot companion: the wall's foot straight below the root.
        lane.push(KindWallFoot);
        lane.push(rootX);
        lane.push(footY);
        lane.push(rootZ);
        for (int i = 4; i < Stride; i++) lane.push(0);
        return true;
    }
}
