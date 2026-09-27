// Fluitown extension — NOT a port of the original. The comic look's wildflowers: the record is only written while
// FluitownVegetation.Enabled (the Style drawer's "3D vegetation" switch); without it the ported flower geometry stays.
using static Fluitown.Render.FluitownVegetation;
using static Fluitown.Render.TerrainGeometryCompilerModule;

namespace Fluitown.Render;

/// <summary>
/// Wildflowers leave the bake as placements, like the trees, bushes and grass: the ported
/// <see cref="FloorFlowerGeometry"/> still decides where a flower grows, which species (daisy, buttercup, cornflower,
/// poppy, bellflower) and which pigments it has, and the Godot vegetation layer grows it as a real low-poly plant
/// (FlowerMesher): a round stem, folded leaves in the species' rhythm, a head of cupped petals around a domed centre.
/// The original draws them as flat ribbons and petal cards with ink outlines, authored for its one oblique camera; seen
/// from the Flui's height they read as paper cut-outs.
///
/// Record (<see cref="FluitownVegetation.Stride"/> floats, compile space): foot, stem height, head radius, the world
/// site (x in <c>Seed</c>, y in <c>Bloom</c>) from which <see cref="FloorFlowerGeometry.floorFlowerIndividualityAt"/>
/// derives the specimen's individuality again, head rotation, species in <c>Form</c>, lean direction in <c>Lean</c>,
/// lean share of the height in <c>Density</c>, and stem, deep, petal and centre pigments.
/// </summary>
public static class FluitownFlowers
{
    /// <summary>Set before the first bake (TerrainSceneRenderer.Initialize, comic look); bakes run on worker threads.</summary>
    public static volatile bool Enabled;

    /// <summary>Vegetation lane kind of a wildflower (kinds 4–7 are the water plants, FluitownSmallPlants).</summary>
    public const int KindFlower = 8;

    /// <summary>Records one flower (FloorFlowerGeometry.addFlower in the comic look). Returns false outside it.</summary>
    public static bool record(
        PropGeometryBuilder builder,
        double rootX,
        double y0,
        double rootZ,
        double height,
        double radius,
        double leanAngle,
        double lean,
        double rotation,
        int species,
        double siteX,
        double siteY,
        int stem,
        int petal,
        int deep,
        int centre)
    {
        if (!Enabled || !FluitownVegetation.Enabled || builder is not TileGeometryBuilder tile || !(height > 0)) return false;
        FloatBuf lane = tile.vegetation;
        lane.ensure(Stride);
        lane.push(KindFlower);
        lane.push(rootX);
        lane.push(y0);
        lane.push(rootZ);
        lane.push(height);
        lane.push(radius);
        lane.push(siteX);
        lane.push(rotation);
        lane.push(stem);
        lane.push(deep);
        lane.push(petal);
        lane.push(centre);
        lane.push(species);
        lane.push(siteY);
        lane.push(leanAngle);
        lane.push(height > 0 ? lean / height : 0);
        return true;
    }
}
