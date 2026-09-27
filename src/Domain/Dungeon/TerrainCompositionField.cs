// Port of packages/shared/src/domain/dungeon/terrainCompositionField.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * World-stable ecological and authored-procedural composition fields.
 *
 * This module is deliberately independent from the render-plan builder: generation, bake-time pigment and
 * dressing all sample the same immutable field without pulling the several-thousand-line plan assembler into
 * their dependency graph.
 */

public sealed class TerrainCompositionSample
{
    public double grove;
    public double clearing;
    public double landmark;
    /// <summary>Compact hierarchy peak around the patch's landmark anchor.</summary>
    public double focus;
    /// <summary>Negative-space sector opposite the focus anchor; consumers keep it deliberately calm.</summary>
    public double quiet;
    public double direction;
    /// <summary>Stable identity and roll of the nearest global composition cell.</summary>
    public double patchId;
    public double patchRoll;
    /// <summary>Global ring anchor and the route/corridor leading through its composition cell.</summary>
    public double? landmarkX;
    public double? landmarkY;
    public double? routeWear;
}

public static class TerrainCompositionField
{
    private static double clamp(double value, double min, double max) => Math.max(min, Math.min(max, value));

    public static double hashTerrainCell(double id)
    {
        int hash = Math.imul(Js.ToInt32(id), 0x45d9f3b);
        hash = Math.imul(hash ^ (int)((uint)hash >> 16), 0x45d9f3b);
        return (uint)(hash ^ (int)((uint)hash >> 16)) / 4294967296.0;
    }

    /// <summary>Pre-hashed biome key for callers that sample an entire bake with one stable salt.</summary>
    public static int terrainEcologyBiomeSalt(string? biomeKey)
    {
        // `let hash = 0x811c9dc5` is a double until the first `^=`; `hash | 0` of the untouched value (empty
        // key) is its ToInt32, which is exactly this int bit pattern.
        int hash = unchecked((int)0x811c9dc5);
        string key = biomeKey ?? "default";
        for (int index = 0; index < key.Length; index++)
        {
            hash ^= key[index];
            hash = Math.imul(hash, 0x01000193);
        }
        return hash;
    }

    private static double terrainEcologyLatticeValue(double x, double y, double salt) =>
        hashTerrainCell(Math.imul(Js.ToInt32(x), 0x1f123bb5) ^ Math.imul(Js.ToInt32(y), 0x5f356495) ^ Js.ToInt32(salt));

    private static double terrainEcologyValueNoise(double x, double y, double scale, double salt)
    {
        double sx = x / scale;
        double sy = y / scale;
        double x0 = Math.floor(sx);
        double y0 = Math.floor(sy);
        double fx = sx - x0;
        double fy = sy - y0;
        double ux = fx * fx * (3 - 2 * fx);
        double uy = fy * fy * (3 - 2 * fy);
        double nw = terrainEcologyLatticeValue(x0, y0, salt);
        double ne = terrainEcologyLatticeValue(x0 + 1, y0, salt);
        double sw = terrainEcologyLatticeValue(x0, y0 + 1, salt);
        double se = terrainEcologyLatticeValue(x0 + 1, y0 + 1, salt);
        double north = nw + (ne - nw) * ux;
        double south = sw + (se - sw) * ux;
        return north + (south - north) * uy;
    }

    /// <summary>Allocation-free ecology sample for hot loops that already hold the biome salt.</summary>
    public static double terrainEcologyPatchWithSalt(double worldCellX, double worldCellY, double salt)
    {
        // Two broad, differently oriented octaves yield readable groves and clearings without cell-white-noise.
        // The mean stays near 0.5, so redistributing existing scatter budget into clusters does not raise average
        // density; the hard per-LOD instance caps remain the final performance guard.
        double broad = terrainEcologyValueNoise(worldCellX, worldCellY, 8.5, salt);
        double detail = terrainEcologyValueNoise(
            worldCellX + worldCellY * 0.31,
            worldCellY - worldCellX * 0.23,
            3.75,
            Js.ToInt32(salt) ^ 0x6c8e9cf5);
        return clamp(broad * 0.74 + detail * 0.26, 0, 1);
    }

    /// <summary>Continuous deterministic ecology field used to compose groves, grass pockets and prop-rich landmarks.</summary>
    public static double terrainEcologyPatchAt(double worldCellX, double worldCellY, string? biomeKey = null) =>
        terrainEcologyPatchWithSalt(worldCellX, worldCellY, terrainEcologyBiomeSalt(biomeKey));

    private static double compositionSmoothstep(double lo, double hi, double value)
    {
        double t = clamp((value - lo) / Math.max(1e-6, hi - lo), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// World-stable authored-procedural composition field.
    ///
    /// One jittered centre per broad lattice region forms an infinite deterministic Voronoi layout. Its core,
    /// clearing and outer landmark ring are then biased by the existing ecology field. Scatter consumers can use
    /// the same hierarchy without sharing mutable chunk state, so a grove or landmark cannot restart at a seam.
    /// Pass `out` in worker hot paths to avoid allocating one record per sampled terrain cell.
    /// </summary>
    public static TerrainCompositionSample terrainCompositionAt(
        double worldCellX,
        double worldCellY,
        string? biomeKey = null,
        TerrainCompositionSample? @out = null,
        double? ecologyOverride = null)
    {
        // Default parameter `{ grove: 0, …, patchRoll: 0 }` (landmarkX/landmarkY/routeWear absent).
        @out ??= new TerrainCompositionSample();
        int salt = terrainEcologyBiomeSalt(biomeKey) ^ 0x49d72b1f;
        const double scale = 13.5;
        double latticeX = Math.floor(worldCellX / scale);
        double latticeY = Math.floor(worldCellY / scale);
        double nearestDistanceSq = double.PositiveInfinity;
        double nearestHash = 0;
        double nearestLatticeX = 0;
        double nearestLatticeY = 0;
        double nearestCentreX = 0;
        double nearestCentreY = 0;
        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                double gx = latticeX + ox;
                double gy = latticeY + oy;
                double centreHash = terrainEcologyLatticeValue(gx, gy, salt);
                double secondHash = terrainEcologyLatticeValue(gx, gy, salt ^ 0x6c8e9cf5);
                double centreX = (gx + 0.18 + centreHash * 0.64) * scale;
                double centreY = (gy + 0.18 + secondHash * 0.64) * scale;
                double dx = worldCellX - centreX;
                double dy = worldCellY - centreY;
                double distanceSq = dx * dx + dy * dy;
                if (distanceSq < nearestDistanceSq)
                {
                    nearestDistanceSq = distanceSq;
                    nearestHash = (centreHash + secondHash * 0.61803398875) % 1;
                    nearestLatticeX = gx;
                    nearestLatticeY = gy;
                    nearestCentreX = centreX;
                    nearestCentreY = centreY;
                }
            }
        }
        double distance = Math.sqrt(nearestDistanceSq) / scale;
        double ecology =
            ecologyOverride ?? terrainEcologyPatchWithSalt(worldCellX, worldCellY, salt ^ 0x3187a5d3);
        double core = 1 - compositionSmoothstep(0.12, 0.68, distance);
        double clearing = compositionSmoothstep(0.57, 0.93, distance) * (0.82 - ecology * 0.26);
        double ring = 1 - clamp(Math.abs(distance - 0.52) / 0.3, 0, 1);
        @out.direction = nearestHash * Math.PI * 2;
        @out.patchId =
            (uint)(Math.imul(Js.ToInt32(nearestLatticeX), 0x1f123bb5) ^
                   Math.imul(Js.ToInt32(nearestLatticeY), 0x5f356495) ^
                   salt);
        @out.patchRoll = terrainEcologyLatticeValue(nearestLatticeX, nearestLatticeY, salt ^ 0x2c9277b5);
        double directionX = Math.cos(@out.direction);
        double directionY = Math.sin(@out.direction);
        @out.landmarkX = nearestCentreX + directionX * scale * 0.52;
        @out.landmarkY = nearestCentreY + directionY * scale * 0.52;
        double relativeX = worldCellX - nearestCentreX;
        double relativeY = worldCellY - nearestCentreY;
        double signedAlong = relativeX * directionX + relativeY * directionY;
        double across = Math.abs(relativeX * -directionY + relativeY * directionX);
        double along = Math.abs(signedAlong);
        @out.routeWear =
            (1 - compositionSmoothstep(0.55, 1.85, across)) *
            (1 - compositionSmoothstep(scale * 0.72, scale * 1.08, along));
        double landmarkDistance = Math.hypot(worldCellX - @out.landmarkX.Value, worldCellY - @out.landmarkY.Value);
        @out.focus = 1 - compositionSmoothstep(scale * 0.08, scale * 0.3, landmarkDistance);
        double quietSector =
            compositionSmoothstep(scale * 0.08, scale * 0.56, -signedAlong) *
            compositionSmoothstep(0.22, 0.82, distance);
        @out.quiet = clamp(
            quietSector * (0.64 + clearing * 0.36) * (1 - @out.routeWear.Value * 0.78),
            0,
            1);
        // All consumers share these two fields: the focus becomes one clean prop/landmark statement, its opposite
        // sector becomes breathing room, and the grove remains a framing mass rather than filling both equally.
        @out.grove = clamp(core * 0.68 + ecology * 0.32 - @out.focus * 0.2, 0, 1);
        @out.clearing = clamp(clearing + @out.focus * 0.34 + @out.quiet * 0.4, 0, 1);
        @out.landmark = clamp(ring * 0.64 + ecology * 0.18 + @out.focus * 0.32, 0, 1);
        return @out;
    }

    /// <summary>Deterministic path-wear mask shared by bake-time vertex colour and landmark selection.</summary>
    public static double terrainFocusPathWearAt(
        double worldCellX,
        double worldCellY,
        string? biomeKey = null,
        TerrainCompositionSample? scratch = null) =>
        terrainCompositionAt(worldCellX, worldCellY, biomeKey, scratch).routeWear ?? 0;
}
