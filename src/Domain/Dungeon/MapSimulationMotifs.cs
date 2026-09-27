// Port of packages/shared/src/domain/dungeon/mapSimulationMotifs.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * **Landform motifs** — the macro bones a simulated world is built on.
 *
 * Terracing a noise field produces a *landscape*; it does not produce a *place you remember*. What makes one
 * world unmistakably not another is structure at map scale: a caldera ring, a spiral climbing to a summit, a
 * city grid cut into rock, a lattice of canyons, a field of mesas. That structure cannot come out of noise at
 * any amplitude — it has to be stated.
 *
 * So a motif is stated here, as data plus one stamp, and the composer applies at most one per map directly
 * after the relief pass. Everything downstream — hydrology, rifts, crossings, roads, ecology —
 * then reacts to those bones, which is what keeps a motif from reading as a decal laid over an unrelated map.
 *
 * **Legality by construction is not required of a stamp.** Motifs are deliberately allowed to be bold with
 * elevation: the composer legalizes afterwards with the same chain that legalizes its own terraces (a
 * walkable edge that would need more than a one-level climb becomes rock, orphans dissolve). A stamp only
 * has to respect the canvas guards — the map rim and the heart's plaza — and to leave the world *connected
 * enough* that the measured retry does not have to throw its work away.
 */

/// <summary>The working world a motif writes into, plus the guards it must respect.</summary>
public sealed class MotifCanvas
{
    public int width;
    public int height;
    public byte[] tiles = Array.Empty<byte>();
    public sbyte[] elevation = Array.Empty<sbyte>();
    public double maxLevel;
    public double seed;
    /// <summary>The composed centre of the world — most motifs organize themselves around it.</summary>
    public int heartTx;
    public int heartTy;
    /// <summary>How wild the roll asked this world to be, 0..1. Motifs read it for amplitude, never for identity.</summary>
    public double chaos;
    /// <summary>False for the map rim and the heart's plaza: the two places composition may never touch.</summary>
    public Func<int, int, bool> canWrite = (tx, ty) => true;
}

/// <summary>How a motif organizes the map. One implementation per form, shared by every entry that asks for it.</summary>
public static class MapMotifForm
{
    /// <summary>A ring massif around a sunken interior — the interior usually ends up flooded.</summary>
    public const string Caldera = "caldera";
    /// <summary>Concentric ring walls with staggered gates: a fortress read from above.</summary>
    public const string Rings = "rings";
    /// <summary>One continuous ramp winding from the rim to a central summit.</summary>
    public const string Spiral = "spiral";
    /// <summary>Orthogonal blocks and avenues cut into rock — structure that is obviously built, not grown.</summary>
    public const string Grid = "grid";
    /// <summary>Isolated flat-topped towers standing out of a low plain.</summary>
    public const string Mesas = "mesas";
    /// <summary>The map broken into a handful of plates, each at its own level, seams torn open.</summary>
    public const string Plates = "plates";
    /// <summary>A lattice of long straight canyons crossing the world.</summary>
    public const string Lattice = "lattice";
    /// <summary>Overlapping circular basins of varying depth.</summary>
    public const string Craters = "craters";
    /// <summary>The whole map as one giant staircase along a rolled axis.</summary>
    public const string Stair = "stair";
    /// <summary>A braided maze occupying one quarter of the world.</summary>
    public const string Warren = "warren";
}

public sealed class MapLandformMotif
{
    public string key = "";
    public string name = "";
    /// <summary>Shown while the layer builds — what this world *is*.</summary>
    public string summary = "";
    /// <summary>A <see cref="MapMotifForm"/> value.</summary>
    public string form = "";
    /// <summary>0..1 — how much of the map the motif claims.</summary>
    public double extent;
    /// <summary>Elevation levels between the motif's own steps.</summary>
    public double step;
    /// <summary>Roll weight against the other motifs.</summary>
    public double weight;
}

public static class MapSimulationMotifs
{
    /// <summary>
    /// The catalog. Ordering is stable and part of the deterministic roll, so appending a motif never re-rolls an
    /// existing seed into a different world — it only makes new worlds reachable.
    /// </summary>
    public static readonly IReadOnlyList<MapLandformMotif> MAP_LANDFORM_MOTIFS = Array.AsReadOnly(new[]
    {
        new MapLandformMotif
        {
            key = "sunken_caldera",
            name = "Sunken Caldera",
            summary = "A ring range stands around a drowned heart",
            form = MapMotifForm.Caldera,
            extent = 0.74,
            step = 3,
            weight = 1,
        },
        new MapLandformMotif
        {
            key = "ringwall_citadel",
            name = "Ringwall Citadel",
            summary = "Concentric ramparts climb toward the centre, each gate offset from the last",
            form = MapMotifForm.Rings,
            extent = 0.8,
            step = 2,
            weight = 0.9,
        },
        new MapLandformMotif
        {
            key = "spiral_ascent",
            name = "Spiral Ascent",
            summary = "One road coils from the rim to a single summit",
            form = MapMotifForm.Spiral,
            extent = 0.86,
            step = 1,
            weight = 0.75,
        },
        new MapLandformMotif
        {
            key = "cut_grid",
            name = "Cut Grid",
            summary = "Avenues and blocks quarried into the bedrock at right angles",
            form = MapMotifForm.Grid,
            extent = 0.68,
            step = 2,
            weight = 0.85,
        },
        new MapLandformMotif
        {
            key = "mesa_stand",
            name = "Mesa Stand",
            summary = "Flat-topped towers stand out of the low country",
            form = MapMotifForm.Mesas,
            extent = 0.7,
            step = 4,
            weight = 1,
        },
        new MapLandformMotif
        {
            key = "broken_plates",
            name = "Broken Plates",
            summary = "The ground has come apart into slabs, each resting at its own height",
            form = MapMotifForm.Plates,
            extent = 0.95,
            step = 2,
            weight = 1,
        },
        new MapLandformMotif
        {
            key = "canyon_lattice",
            name = "Canyon Lattice",
            summary = "Straight ravines cross the world in both directions",
            form = MapMotifForm.Lattice,
            extent = 0.9,
            step = 2,
            weight = 0.9,
        },
        new MapLandformMotif
        {
            key = "crater_field",
            name = "Crater Field",
            summary = "Overlapping basins pock the whole plain",
            form = MapMotifForm.Craters,
            extent = 0.85,
            step = 2,
            weight = 0.9,
        },
        new MapLandformMotif
        {
            key = "giant_stair",
            name = "Giant Stair",
            summary = "The land climbs across the map in enormous treads",
            form = MapMotifForm.Stair,
            extent = 1,
            step = 2,
            weight = 0.8,
        },
        new MapLandformMotif
        {
            key = "warren_quarter",
            name = "Warren Quarter",
            summary = "A braided warren has grown over one quarter of the map",
            form = MapMotifForm.Warren,
            extent = 0.5,
            step = 1,
            weight = 0.8,
        },
    });

    /// <summary>Apply one motif. Returns false when the site was unusable and the composer should keep its own landform.</summary>
    public static bool stampLandformMotif(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        switch (motif.form)
        {
            case MapMotifForm.Caldera:
                return stampCaldera(canvas, motif, rng);
            case MapMotifForm.Rings:
                return stampRings(canvas, motif, rng);
            case MapMotifForm.Spiral:
                return stampSpiral(canvas, motif, rng);
            case MapMotifForm.Grid:
                return stampGrid(canvas, motif, rng);
            case MapMotifForm.Mesas:
                return stampMesas(canvas, motif, rng);
            case MapMotifForm.Plates:
                return stampPlates(canvas, motif, rng);
            case MapMotifForm.Lattice:
                return stampLattice(canvas, motif, rng);
            case MapMotifForm.Craters:
                return stampCraters(canvas, motif, rng);
            case MapMotifForm.Stair:
                return stampStair(canvas, motif, rng);
            case MapMotifForm.Warren:
                return stampWarren(canvas, motif, rng);
            default:
                return false;
        }
    }

    // -----------------------------------------------------------------------------------------------------
    // Shared helpers

    private static double clampLevel(double value, double maxLevel)
    {
        double level = Math.round(value);
        return level < 0 ? 0 : level > maxLevel ? maxLevel : level;
    }

    /// <summary>Write one walkable cell at a level. Blocked cells are left to the composer's own wall pass.</summary>
    private static void setGround(MotifCanvas canvas, int tx, int ty, double level)
    {
        if (!canvas.canWrite(tx, ty)) return;
        int index = tileIndex(canvas.width, tx, ty);
        if (canvas.tiles[index] == TileType.Water) return;
        canvas.tiles[index] = TileType.Floor;
        canvas.elevation[index] = Js.I8(clampLevel(level, canvas.maxLevel));
    }

    private static void setRock(MotifCanvas canvas, int tx, int ty, double level)
    {
        if (!canvas.canWrite(tx, ty)) return;
        int index = tileIndex(canvas.width, tx, ty);
        canvas.tiles[index] = TileType.Solid;
        canvas.elevation[index] = Js.I8(clampLevel(level, canvas.maxLevel));
    }

    /// <summary>Organic wobble on a radius, so no motif ring reads as a drawn circle.</summary>
    private static double wobbleAt(MotifCanvas canvas, int tx, int ty, double amount)
    {
        return (valueNoise(Js.ToUint32(canvas.seed), tx, ty, 13) - 0.5) * 2 * amount;
    }

    /// <summary>
    /// The motif's working radius: how far from the heart it reaches before the ordinary landform resumes.
    ///
    /// Measured against the map's DIAGONAL, not its short axis. A motif sized off the short axis vanishes on a
    /// wide crop — a 92x34 map would hand a full-extent motif a radius of seventeen cells and the world would come
    /// back looking untouched, which is exactly what happened the first time this was measured.
    /// </summary>
    private static double motifRadius(MotifCanvas canvas, MapLandformMotif motif)
    {
        return Math.max(6, Math.hypot(canvas.width, canvas.height) * 0.5 * motif.extent);
    }

    // -----------------------------------------------------------------------------------------------------
    // Stamps

    /// <summary>A ring range around a sunken floor, breached once so the interior is reachable on foot.</summary>
    private static bool stampCaldera(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        double radius = motifRadius(canvas, motif);
        double inner = radius * rng.range(0.42, 0.62);
        double crest = radius * rng.range(0.72, 0.88);
        const double floorLevel = 1;
        double rimLevel = Math.min(canvas.maxLevel - 2, 2 + motif.step * rng.range(1.4, 2.6));
        double breach = rng.range(0, Math.PI * 2);
        double breachWidth = rng.range(0.24, 0.5);

        for (int ty = 0; ty < canvas.height; ty++)
        {
            for (int tx = 0; tx < canvas.width; tx++)
            {
                int dx = tx - canvas.heartTx;
                int dy = ty - canvas.heartTy;
                double distance = Math.hypot(dx, dy) + wobbleAt(canvas, tx, ty, radius * 0.09);
                if (distance > radius) continue;
                double angle = Math.atan2(dy, dx);
                double delta = Math.abs(((angle - breach + Math.PI * 3) % (Math.PI * 2)) - Math.PI);
                delta = Math.PI - delta;
                bool inBreach = delta < breachWidth;
                if (distance < inner)
                {
                    setGround(canvas, tx, ty, floorLevel);
                }
                else if (distance < crest)
                {
                    // The inner wall of the caldera: a climb the composer will turn into a real face, except at the breach
                    // where a ramp keeps the heart reachable on foot.
                    double t = (distance - inner) / Math.max(1, crest - inner);
                    if (inBreach) setGround(canvas, tx, ty, floorLevel + t * (rimLevel - floorLevel));
                    else setRock(canvas, tx, ty, floorLevel + t * (rimLevel - floorLevel));
                }
                else
                {
                    double t = 1 - (distance - crest) / Math.max(1, radius - crest);
                    setGround(canvas, tx, ty, floorLevel + t * (rimLevel - floorLevel) * 0.55);
                }
            }
        }
        return true;
    }

    /// <summary>Concentric ramparts, each with one gate, each gate turned from the last so the approach is a spiral walk.</summary>
    private static bool stampRings(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        double radius = motifRadius(canvas, motif);
        double count = rng.@int(3, 5);
        double gateWidth = rng.range(0.18, 0.34);
        double turn = rng.range(1.6, 2.6);
        double start = rng.range(0, Math.PI * 2);

        for (int ring = 0; ring < count; ring++)
        {
            double at = radius * (1 - ring / (count + 0.4));
            double thickness = Math.max(1.4, radius * 0.055);
            double level = 1 + (count - ring) * motif.step * 0.7;
            double gate = start + ring * turn;
            for (int ty = 0; ty < canvas.height; ty++)
            {
                for (int tx = 0; tx < canvas.width; tx++)
                {
                    int dx = tx - canvas.heartTx;
                    int dy = ty - canvas.heartTy;
                    double distance = Math.hypot(dx, dy) + wobbleAt(canvas, tx, ty, radius * 0.05);
                    if (Math.abs(distance - at) > thickness) continue;
                    double angle = Math.atan2(dy, dx);
                    double delta = Math.abs(((angle - gate + Math.PI * 3) % (Math.PI * 2)) - Math.PI);
                    delta = Math.PI - delta;
                    if (delta < gateWidth) setGround(canvas, tx, ty, level);
                    else setRock(canvas, tx, ty, level + motif.step);
                }
            }
        }
        // The courts between the ramparts step down outward, so the whole fortress reads as one climb.
        for (int ty = 0; ty < canvas.height; ty++)
        {
            for (int tx = 0; tx < canvas.width; tx++)
            {
                double distance = Math.hypot(tx - canvas.heartTx, ty - canvas.heartTy);
                if (distance > radius) continue;
                int index = tileIndex(canvas.width, tx, ty);
                if (canvas.tiles[index] != TileType.Floor) continue;
                double band = Math.floor((1 - distance / radius) * count);
                setGround(canvas, tx, ty, 1 + band * motif.step * 0.7);
            }
        }
        return true;
    }

    /// <summary>One ramp coiling out from a summit — the whole map becomes a single continuous climb.</summary>
    private static bool stampSpiral(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        double radius = motifRadius(canvas, motif);
        // Fewer, wider turns. A tight spiral drawn at cell resolution is a thread, and the terrain contract's own
        // cliff materialization eats a thread — the ramp has to be a road before it can survive being one.
        double turns = rng.range(1.6, 2.6);
        double laneWidth = Math.max(3.2, radius * rng.range(0.11, 0.17));
        double direction = rng.@bool() ? 1 : -1;
        double phase = rng.range(0, Math.PI * 2);
        double summit = Math.min(canvas.maxLevel - 4, 2 + Math.round(turns * motif.step * 1.2));
        // Share of the radius the cone occupies; beyond it the mountain simply stands in open country.
        double cone = rng.range(0.5, 0.68);

        for (int ty = 0; ty < canvas.height; ty++)
        {
            for (int tx = 0; tx < canvas.width; tx++)
            {
                int dx = tx - canvas.heartTx;
                int dy = ty - canvas.heartTy;
                double distance = Math.hypot(dx, dy);
                if (distance > radius) continue;
                double angle = Math.atan2(dy, dx) * direction;
                // Distance along the spiral, in turns, from the summit outward.
                double t = distance / radius;
                double spiralAngle = (phase + t * turns * Math.PI * 2) % (Math.PI * 2);
                double offset = Math.abs(((angle - spiralAngle + Math.PI * 3) % (Math.PI * 2)) - Math.PI);
                offset = Math.PI - offset;
                double lane = offset * distance;
                double level = summit * (1 - t);
                if (t > cone)
                {
                    // Outside the cone the world is ordinary ground. A spiral that turns the entire disc into rock is a
                    // sculpture, not a map: the mountain has to stand *in* somewhere you can walk.
                    double skirt = summit * (1 - cone) * (1 - (t - cone) / Math.max(1e-3, 1 - cone));
                    setGround(canvas, tx, ty, Math.max(1, skirt));
                }
                else if (lane < laneWidth || distance < radius * 0.1)
                {
                    setGround(canvas, tx, ty, level);
                }
                else
                {
                    setRock(canvas, tx, ty, level + motif.step);
                }
            }
        }
        return true;
    }

    /// <summary>Avenues and quarried blocks at right angles — structure that is obviously built.</summary>
    private static bool stampGrid(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        double radius = motifRadius(canvas, motif);
        double block = Math.max(
            5,
            Math.round(Math.min(canvas.width, canvas.height) * rng.range(0.09, 0.16)));
        double avenue = Math.max(2, Math.round(block * rng.range(0.28, 0.45)));
        double originX = canvas.heartTx - Math.round(radius);
        double originY = canvas.heartTy - Math.round(radius);

        for (int ty = 0; ty < canvas.height; ty++)
        {
            for (int tx = 0; tx < canvas.width; tx++)
            {
                if (Math.hypot(tx - canvas.heartTx, ty - canvas.heartTy) > radius) continue;
                double gx = (((tx - originX) % (block + avenue)) + block + avenue) % (block + avenue);
                double gy = (((ty - originY) % (block + avenue)) + block + avenue) % (block + avenue);
                bool onAvenue = gx < avenue || gy < avenue;
                // Each block gets its own quarried depth, so the grid reads as excavated rather than printed.
                double cellX = Math.floor((tx - originX) / (block + avenue));
                double cellY = Math.floor((ty - originY) / (block + avenue));
                double blockLevel = 1 + Math.floor(latticeHash(canvas.seed, cellX, cellY) * (motif.step + 2));
                if (onAvenue) setGround(canvas, tx, ty, 1);
                else setRock(canvas, tx, ty, blockLevel + motif.step);
            }
        }
        return true;
    }

    /// <summary>Flat-topped towers over a low plain, each reachable only by looking at it.</summary>
    private static bool stampMesas(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        double radius = motifRadius(canvas, motif);
        double count = rng.@int(5, 12);
        const double plain = 1;
        for (int ty = 0; ty < canvas.height; ty++)
        {
            for (int tx = 0; tx < canvas.width; tx++)
            {
                if (Math.hypot(tx - canvas.heartTx, ty - canvas.heartTy) > radius) continue;
                setGround(canvas, tx, ty, plain);
            }
        }
        for (int i = 0; i < count; i++)
        {
            double angle = rng.range(0, Math.PI * 2);
            double at = radius * rng.range(0.18, 0.94);
            int cx = (int)Math.round(canvas.heartTx + Math.cos(angle) * at);
            int cy = (int)Math.round(canvas.heartTy + Math.sin(angle) * at);
            double mesaRadius = Math.max(2.5, radius * rng.range(0.07, 0.19));
            double top = Math.min(canvas.maxLevel - 2, plain + motif.step * rng.range(0.8, 1.9));
            int r = (int)Math.ceil(mesaRadius) + 1;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int tx = cx + dx;
                    int ty = cy + dy;
                    if (!inBounds(canvas.width, canvas.height, tx, ty)) continue;
                    double distance = Math.hypot(dx, dy) + wobbleAt(canvas, tx, ty, mesaRadius * 0.22);
                    if (distance > mesaRadius) continue;
                    // A flat crown of walkable ground, ringed by the mesa's own cliff.
                    if (distance < mesaRadius * 0.68) setGround(canvas, tx, ty, top);
                    else setRock(canvas, tx, ty, top);
                }
            }
        }
        return true;
    }

    private struct PlateSite
    {
        public double x;
        public double y;
        public double level;
    }

    /// <summary>The map broken into slabs at different heights, seams torn open into rifts.</summary>
    private static bool stampPlates(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        double count = rng.@int(4, 8);
        var sites = new List<PlateSite>();
        for (int i = 0; i < count; i++)
        {
            double x = rng.range(0, canvas.width);
            double y = rng.range(0, canvas.height);
            double level = 1 + Math.round(rng.range(0, motif.step * 2.4));
            sites.push(new PlateSite { x = x, y = y, level = level });
        }
        double warpScale = Math.max(8, Math.min(canvas.width, canvas.height) * 0.2);
        for (int ty = 0; ty < canvas.height; ty++)
        {
            for (int tx = 0; tx < canvas.width; tx++)
            {
                double wx = tx + (valueNoise(canvas.seed, tx, ty, warpScale) - 0.5) * 12;
                double wy =
                    tx == 0 ? ty : ty + (valueNoise(Js.ToInt32(canvas.seed) ^ 0x2f6e, tx, ty, warpScale) - 0.5) * 12;
                int best = 0;
                double bestDistance = double.PositiveInfinity;
                double secondDistance = double.PositiveInfinity;
                for (int i = 0; i < sites.Count; i++)
                {
                    PlateSite site = sites[i];
                    double distance = Math.hypot(wx - site.x, wy - site.y);
                    if (distance < bestDistance)
                    {
                        secondDistance = bestDistance;
                        bestDistance = distance;
                        best = i;
                    }
                    else if (distance < secondDistance)
                    {
                        secondDistance = distance;
                    }
                }
                // Right on a plate boundary the ground has pulled apart.
                double seam = secondDistance - bestDistance;
                if (seam < 1.35)
                {
                    if (canvas.canWrite(tx, ty))
                    {
                        int index = tileIndex(canvas.width, tx, ty);
                        canvas.tiles[index] = TileType.Chasm;
                    }
                    continue;
                }
                setGround(canvas, tx, ty, sites[best].level);
            }
        }
        return true;
    }

    /// <summary>Long straight ravines crossing the world in both directions.</summary>
    private static bool stampLattice(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        double pitch = Math.max(
            9,
            Math.round(Math.min(canvas.width, canvas.height) * rng.range(0.16, 0.26)));
        double gorge = Math.max(2, Math.round(pitch * rng.range(0.16, 0.3)));
        double offsetX = rng.@int(0, pitch);
        double offsetY = rng.@int(0, pitch);
        double shear = rng.range(-0.25, 0.25);

        // A lattice with no crossings is a set of islands, and the composer would dissolve all but one of them.
        // Every few pitches each ravine is therefore interrupted by a land causeway, so the tables stay one world.
        double crossingPitch = pitch * rng.@int(2, 3);
        double crossingWidth = Math.max(3, Math.round(gorge * 1.8));
        double crossingOffsetX = rng.@int(0, crossingPitch);
        double crossingOffsetY = rng.@int(0, crossingPitch);

        for (int ty = 0; ty < canvas.height; ty++)
        {
            for (int tx = 0; tx < canvas.width; tx++)
            {
                if (!canvas.canWrite(tx, ty)) continue;
                double sx = tx + ty * shear + wobbleAt(canvas, tx, ty, pitch * 0.12);
                double sy = ty - tx * shear + wobbleAt(canvas, ty, tx, pitch * 0.12);
                double gx = (((sx - offsetX) % pitch) + pitch) % pitch;
                double gy = (((sy - offsetY) % pitch) + pitch) % pitch;
                int index = tileIndex(canvas.width, tx, ty);
                // A north-south ravine is bridged where the EAST-WEST crossing band runs, and vice versa.
                double crossX = (((sy - crossingOffsetY) % crossingPitch) + crossingPitch) % crossingPitch;
                double crossY = (((sx - crossingOffsetX) % crossingPitch) + crossingPitch) % crossingPitch;
                bool cutX = gx < gorge && crossX >= crossingWidth;
                bool cutY = gy < gorge && crossY >= crossingWidth;
                if (cutX || cutY)
                {
                    canvas.tiles[index] = TileType.Chasm;
                }
                else
                {
                    // The plateaus between the ravines sit a step above them, so each is its own table.
                    setGround(canvas, tx, ty, 1 + motif.step);
                }
            }
        }
        return true;
    }

    /// <summary>Overlapping circular basins, each at its own depth.</summary>
    private static bool stampCraters(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        double plain = 2 + motif.step;
        for (int ty = 0; ty < canvas.height; ty++)
        {
            for (int tx = 0; tx < canvas.width; tx++)
            {
                setGround(canvas, tx, ty, plain);
            }
        }
        double count = rng.@int(6, 16);
        for (int i = 0; i < count; i++)
        {
            double cx = rng.range(0, canvas.width);
            double cy = rng.range(0, canvas.height);
            double craterRadius = Math.max(3, Math.min(canvas.width, canvas.height) * rng.range(0.07, 0.2));
            double depth = Math.max(1, Math.round(rng.range(1, motif.step + 1.6)));
            int r = (int)Math.ceil(craterRadius) + 2;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int tx = (int)Math.round(cx + dx);
                    int ty = (int)Math.round(cy + dy);
                    if (!inBounds(canvas.width, canvas.height, tx, ty)) continue;
                    double distance = Math.hypot(dx, dy) + wobbleAt(canvas, tx, ty, craterRadius * 0.18);
                    if (distance > craterRadius) continue;
                    // A bowl: deepest in the middle, with a raised lip that the composer turns into a rim of rock.
                    double t = distance / craterRadius;
                    if (t > 0.86) setRock(canvas, tx, ty, plain + 1);
                    else setGround(canvas, tx, ty, plain - depth * (1 - t));
                }
            }
        }
        return true;
    }

    /// <summary>The whole world as one giant staircase along a rolled axis.</summary>
    private static bool stampStair(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        double angle = rng.range(0, Math.PI * 2);
        double dx = Math.cos(angle);
        double dy = Math.sin(angle);
        double span = Math.abs(dx) * canvas.width + Math.abs(dy) * canvas.height;
        double treads = rng.@int(4, 8);
        double tread = span / treads;
        double originX = canvas.width * 0.5;
        double originY = canvas.height * 0.5;
        double top = Math.min(canvas.maxLevel - 3, 1 + treads * motif.step);

        for (int ty = 0; ty < canvas.height; ty++)
        {
            for (int tx = 0; tx < canvas.width; tx++)
            {
                double along =
                    (tx - originX) * dx +
                    (ty - originY) * dy +
                    span * 0.5 +
                    wobbleAt(canvas, tx, ty, tread * 0.22);
                double index = Math.floor(along / tread);
                double level = 1 + Math.max(0, Math.min(treads, index)) * motif.step;
                setGround(canvas, tx, ty, Math.min(top, level));
            }
        }
        return true;
    }

    /// <summary>A braided warren over one quarter of the map — the one motif that is a maze rather than a landform.</summary>
    private static bool stampWarren(MotifCanvas canvas, MapLandformMotif motif, Rng rng)
    {
        double quadrantX = rng.@bool() ? 0 : 1;
        double quadrantY = rng.@bool() ? 0 : 1;
        int x0 = (int)Math.round(quadrantX * canvas.width * (1 - motif.extent));
        int y0 = (int)Math.round(quadrantY * canvas.height * (1 - motif.extent));
        int x1 = (int)Math.min(canvas.width, x0 + Math.round(canvas.width * motif.extent));
        int y1 = (int)Math.min(canvas.height, y0 + Math.round(canvas.height * motif.extent));
        int cell = (int)Math.max(
            3,
            Math.round(Math.min(canvas.width, canvas.height) * rng.range(0.05, 0.09)));
        const double level = 1;

        for (int ty = y0; ty < y1; ty++)
        {
            for (int tx = x0; tx < x1; tx++)
            {
                if (!canvas.canWrite(tx, ty)) continue;
                int gx = (int)Math.floor((double)(tx - x0) / cell);
                int gy = (int)Math.floor((double)(ty - y0) / cell);
                bool onGridX = (tx - x0) % cell == 0;
                bool onGridY = (ty - y0) % cell == 0;
                if (!onGridX && !onGridY)
                {
                    setGround(canvas, tx, ty, level);
                    continue;
                }
                // A braided maze: every wall segment has an independent chance of simply not being there, which is what
                // makes the warren loop instead of dead-ending everywhere.
                bool braid = latticeHash(Js.ToInt32(canvas.seed) ^ 0x51ab, gx, gy) < 0.32 + canvas.chaos * 0.2;
                if (braid) setGround(canvas, tx, ty, level);
                else setRock(canvas, tx, ty, level + motif.step);
            }
        }
        return true;
    }
}
