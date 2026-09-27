// Port of packages/shared/src/domain/dungeon/terrainFloorComposition.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Domain.TerrainSubstrate;
using Math = Fluitown.Runtime.JsMath;
// `export type TerrainFloorCompositionSample = TerrainCompositionSample` (structural alias; C# has no exported
// aliases, so consumers use TerrainCompositionSample directly).
using TerrainFloorCompositionSample = Fluitown.Domain.TerrainCompositionSample;

namespace Fluitown.Domain;

/// <summary>
/// Continuous semantic floor fields derived from real circulation plus terrain ecology. Values are 0..1 and
/// indexed like `terrain.cells`; `direction` stores the local route tangent in radians.
/// </summary>
public sealed class TerrainFloorCompositionPlan
{
    public float[] route = Array.Empty<float>();
    public float[] routeCore = Array.Empty<float>();
    public float[] dirt = Array.Empty<float>();
    public float[] meadow = Array.Empty<float>();
    public float[] grass = Array.Empty<float>();
    /// <summary>
    /// Continuous wildflower habitat, 0 bare .. 1 flower-patch core. This is floor composition rather than a
    /// decoration list: paths, meadow ecology, moisture and the same absolute-world fields that grow turf all
    /// participate before the render plan ever reaches a geometry compiler.
    /// </summary>
    public float[] flowers = Array.Empty<float>();
    /// <summary>Continuous damp/sediment body. It is consumed as vertex pigment in the existing floor material.</summary>
    public float[] wetness = Array.Empty<float>();
    public float[] direction = Array.Empty<float>();
}

/// <summary>
/// The three field samplers the floor plan reads (injected so this module stays independent from the full
/// terrain render-plan module). `biomeKey` is `string | undefined` → `string?`; the optional `biomeKey` of
/// `ecologyAt` is passed explicitly (null for undefined).
/// </summary>
public sealed class TerrainFloorCompositionSamplers
{
    /// <summary>(worldCellX, worldCellY, biomeKey, out) → out.</summary>
    public Func<double, double, string?, TerrainCompositionSample, TerrainCompositionSample> compositionAt = null!;
    /// <summary>(worldCellX, worldCellY, biomeKey?) → 0..1.</summary>
    public Func<double, double, string?, double> ecologyAt = null!;
    /// <summary>(worldCellX, worldCellY, biomeKey, scratch) → 0..1.</summary>
    public Func<double, double, string?, TerrainCompositionSample, double> focusPathWearAt = null!;
}

public static class TerrainFloorComposition
{
    public static TerrainFloorCompositionPlan emptyTerrainFloorCompositionPlan() => new TerrainFloorCompositionPlan
    {
        route = new float[0],
        routeCore = new float[0],
        dirt = new float[0],
        meadow = new float[0],
        grass = new float[0],
        flowers = new float[0],
        wetness = new float[0],
        direction = new float[0],
    };

    private static float[] floorLayer(float[]? reuse, int count) =>
        reuse != null && reuse.Length == count ? reuse : new float[count];

    private sealed class TerrainFloorNeighbourSample
    {
        public readonly int dx;
        public readonly int dy;
        public readonly double distance;
        public readonly double inverseDistanceSquared;

        public TerrainFloorNeighbourSample(int dx, int dy, double distance, double inverseDistanceSquared)
        {
            this.dx = dx;
            this.dy = dy;
            this.distance = distance;
            this.inverseDistanceSquared = inverseDistanceSquared;
        }
    }

    /// <summary>
    /// Route shoulder and tangent consume the same fixed 5x5 neighbourhood. Preserve its exact row-major order,
    /// but derive offsets and Euclidean distances once instead of rebuilding them for every streamed floor cell.
    /// (Immutable after construction; shared across threads.)
    /// </summary>
    private static readonly TerrainFloorNeighbourSample[] TERRAIN_FLOOR_NEIGHBOURS = buildTerrainFloorNeighbours();

    private static TerrainFloorNeighbourSample[] buildTerrainFloorNeighbours()
    {
        var samples = new List<TerrainFloorNeighbourSample>();
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                samples.push(new TerrainFloorNeighbourSample(
                    dx,
                    dy,
                    Math.hypot(dx, dy),
                    dx == 0 && dy == 0 ? 0 : 1.0 / Math.max(1, dx * dx + dy * dy)));
            }
        }
        return samples.ToArray();
    }

    private static double clamp(double value, double min, double max) => Math.min(max, Math.max(min, value));

    private static double smoothstep(double lo, double hi, double value)
    {
        double t = clamp((value - lo) / Math.max(1e-6, hi - lo), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Build one coherent floor ecology around the actual authored/generated circulation network.
    ///
    /// Porting note: every Float32Array read is widened to `double` before it enters arithmetic (`(double)x[i]`),
    /// because C# would otherwise evaluate `1 - routeCore[i]` in single precision where JS uses double.
    /// </summary>
    public static TerrainFloorCompositionPlan createTerrainFloorCompositionPlan(
        MaterializedTerrain terrain,
        IReadOnlyList<double> moisture,
        string? biomeKey,
        double originCellX,
        double originCellY,
        TerrainFloorCompositionPlan? reuse,
        TerrainFloorCompositionSamplers samplers)
    {
        int count = terrain.cells.Length;
        float[] route = floorLayer(reuse?.route, count);
        float[] routeCore = floorLayer(reuse?.routeCore, count);
        float[] dirt = floorLayer(reuse?.dirt, count);
        float[] meadow = floorLayer(reuse?.meadow, count);
        float[] grass = floorLayer(reuse?.grass, count);
        float[] flowers = floorLayer(reuse?.flowers, count);
        float[] wetness = floorLayer(reuse?.wetness, count);
        float[] direction = floorLayer(reuse?.direction, count);
        byte[]? semanticUsage = terrain.floorUsage;
        byte[]? semanticSurface = terrain.surface;
        // What this ground is MADE OF is registry data, sampled per cell as a large-region field — see
        // terrainSubstrate. It replaces three parallel chains of biome-key string comparisons that
        // each answered a different part of the same question, could disagree with one another, and gave every
        // world exactly one ground community for its entire infinite extent.
        TerrainSubstrateRecipe substrate = terrainSubstrateFor(biomeKey);
        var compositionScratch = new TerrainFloorCompositionSample
        {
            grove = 0,
            clearing = 0,
            landmark = 0,
            focus = 0,
            quiet = 0,
            direction = 0,
            patchId = 0,
            patchRoll = 0,
        };

        for (int index = 0; index < count; index++)
        {
            TerrainCell cell = terrain.cells[index];
            bool materializedFloor = terrainCellCarriesChasmFloor(cell);
            if ((cell.type != TileType.Floor || !cell.walkable) && !materializedFloor)
            {
                routeCore[index] =
                    route[index] =
                    dirt[index] =
                    meadow[index] =
                    grass[index] =
                    flowers[index] =
                    wetness[index] =
                        0;
                direction[index] = 0;
                continue;
            }
            // `routeCore` is the ROAD, and only the road: the authored artery where one is published, the ambient
            // desire-line field where none is. It stays exclusive because everything downstream treats this value as
            // "a road is here" — it grows a two-cell geodesic shoulder, it drives the route tangent, and it removes
            // vegetation outright. Feeding the broad ambient wear into it was measured at mean 107 → 226 ms and p95
            // 152 → 633 ms per bake, because it turned a tenth of the world into road and made every downstream
            // route consumer do a tenth of the world's worth of work. The ambient field belongs in the ground's
            // COLOUR, not in its circulation network — see `ambientWear` in the composition loop below.
            routeCore[index] = (float)(materializedFloor
                ? 0
                : semanticUsage != null
                    // `(semanticUsage[index] ?? 0) / 255`
                    ? ((uint)index < (uint)semanticUsage.Length ? semanticUsage[index] : 0) / 255.0
                    : samplers.focusPathWearAt(
                        originCellX + cell.x + 0.5,
                        originCellY + cell.y + 0.5,
                        biomeKey,
                        compositionScratch));
        }

        // PORT NOTE (allocation): the per-field ecology keys depend on the biome only; TS builds the template strings
        // per cell. The sampler hashes the key's characters (terrainEcologyBiomeSalt), so one string each is identical.
        string soilKey = $"{biomeKey ?? ""}:soil";
        string routeCompactionKey = $"{biomeKey ?? ""}:route-compaction";
        string turfColonyKey = $"{biomeKey ?? ""}:turf-colony";
        string flowerPatchKey = $"{biomeKey ?? ""}:flower-patch";
        string flowerPatchEdgeKey = $"{biomeKey ?? ""}:flower-patch-edge";
        string flowerFieldKey = $"{biomeKey ?? ""}:flower-field";
        string flowerSingleKey = $"{biomeKey ?? ""}:flower-single";
        // A two-cell geodesic shoulder keeps paths readable without bleeding across walls, cliffs or water.
        for (int index = 0; index < count; index++)
        {
            TerrainCell cell = terrain.cells[index];
            if ((cell.type != TileType.Floor || !cell.walkable) && !terrainCellCarriesChasmFloor(cell))
                continue;
            double expanded = routeCore[index];
            double xx = 0;
            double yy = 0;
            double xy = 0;
            double total = 0;
            foreach (TerrainFloorNeighbourSample sample in TERRAIN_FLOOR_NEIGHBOURS)
            {
                int nx = cell.x + sample.dx;
                int ny = cell.y + sample.dy;
                if (nx < 0 || ny < 0 || nx >= terrain.width || ny >= terrain.height) continue;
                int neighbourIndex = ny * terrain.width + nx;
                if (sample.distance <= 2.05)
                {
                    TerrainCell neighbour = terrain.cells[neighbourIndex];
                    if (
                        neighbour.type == TileType.Floor &&
                        neighbour.walkable &&
                        Math.abs(neighbour.surfaceZ - cell.surfaceZ) <= 0.2)
                    {
                        double falloff = sample.distance < 0.5 ? 1 : sample.distance < 1.2 ? 0.72 : 0.38;
                        expanded = Math.max(expanded, (double)routeCore[neighbour.id] * falloff);
                    }
                }
                if (sample.inverseDistanceSquared > 0)
                {
                    double weight = (double)routeCore[neighbourIndex] * sample.inverseDistanceSquared;
                    xx += weight * sample.dx * sample.dx;
                    yy += weight * sample.dy * sample.dy;
                    xy += weight * sample.dx * sample.dy;
                    total += weight;
                }
            }
            route[index] = (float)expanded;

            double worldX = originCellX + cell.x + 0.5;
            double worldY = originCellY + cell.y + 0.5;
            TerrainFloorCompositionSample composition = samplers.compositionAt(worldX, worldY, biomeKey, compositionScratch);
            /*
             * AMBIENT TRODDEN GROUND — the term that gives open country its patchwork, and it costs nothing.
             *
             * A floor is not uniform between its roads: it has trodden hollows, desire lines and bare compacted
             * ground, and the contrast between those and the closed turf beside them is what makes both read as
             * *patches* rather than as one flat tone. This is that field, broad and absolutely world-positioned, so a
             * patch keeps its shape across every streaming seam.
             *
             * It was previously reachable ONLY through `routeCore`, and only on maps publishing no `floorUsage`
             * layer — which in practice meant only the authored editor map. Every procedurally generated chunk
             * publishes a usage layer, took the semantic branch, and therefore never evaluated this field at all.
             * That is the whole reason the generated world's ground was one mid-tone with no visible paths and no
             * visible grass patches while the editor's blank sheet had both.
             *
             * Read off the composition sample already taken on the line above rather than through
             * `samplers.focusPathWearAt`, which recomputes the identical sample: that shortcut was measured at 3x the
             * whole bake (p50 194 → 637 ms) for a value that was already in hand.
             */
            double ambientWear = composition.routeWear ?? 0;
            double ecology = samplers.ecologyAt(worldX, worldY, biomeKey);
            double shelter = 0;
            TerrainContact north = cell.contacts.n!;
            TerrainContact east = cell.contacts.e!;
            TerrainContact south = cell.contacts.s!;
            TerrainContact west = cell.contacts.w!;
            shelter += north.solid ? 0.08 : north.type == TileType.Water ? 0.12 : 0;
            shelter += east.solid ? 0.08 : east.type == TileType.Water ? 0.12 : 0;
            shelter += south.solid ? 0.08 : south.type == TileType.Water ? 0.12 : 0;
            shelter += west.solid ? 0.08 : west.type == TileType.Water ? 0.12 : 0;
            // `moisture[index] ?? 0`
            double moistureWeight = clamp((uint)index < (uint)moisture.Count ? moisture[index] : 0, 0, 1);
            double focus = composition.focus;
            double quiet = composition.quiet;
            int waterContact =
                (north.type == TileType.Water ? 1 : 0) +
                (east.type == TileType.Water ? 1 : 0) +
                (south.type == TileType.Water ? 1 : 0) +
                (west.type == TileType.Water ? 1 : 0);
            // Moisture is already a terrain-neighbourhood field. Folding direct bank contact and one broad absolute-
            // world ecology sample into it gives shore sediment a soft body instead of four cell-shaped wet squares.
            // Reuse the ecology/composition samples already paid for above. A dedicated wet-noise octave looked
            // similar but added another value-noise traversal per floor cell in every streamed bake.
            double wetMacro = ecology * 0.62 + composition.clearing * 0.38;
            wetness[index] = (float)clamp(
                smoothstep(
                    0.2,
                    0.82,
                    moistureWeight * 0.7 + Math.min(1, waterContact) * 0.3 + (wetMacro - 0.5) * 0.12),
                0,
                1);
            double habitat =
                ecology * 0.4 +
                composition.grove * 0.28 +
                moistureWeight * 0.18 +
                shelter +
                quiet * 0.08 -
                focus * 0.15;
            // A broad ecological transition produces readable meadow biomes rather than isolated lucky cells. The
            // underlying habitat remains low-frequency/coherent, while the route field removes vegetation geodesically.
            // `semanticSurface?.[index] ?? TerrainSurface.Auto`
            int surface =
                semanticSurface != null && (uint)index < (uint)semanticSurface.Length
                    ? semanticSurface[index]
                    : TerrainSurface.Auto;
            bool statedGrass = surface == TerrainSurface.Grass;
            bool statedSoil = surface == TerrainSurface.Floor;
            bool statedSand = surface == TerrainSurface.Sand;
            bool statedStone = surface == TerrainSurface.Stone;
            bool statedMetal = surface == TerrainSurface.Metal;
            double meadowWeight = smoothstep(0.18, 0.64, habitat) * (1 - expanded * 0.94);
            double bareField =
                samplers.ecologyAt(worldX + 19.7, worldY - 13.1, soilKey) * 0.58 +
                composition.clearing * 0.2 +
                focus * 0.22 +
                (double)wetness[index] * 0.14 +
                (1 - moistureWeight) * 0.14;
            double compactionDetail = samplers.ecologyAt(
                worldX * 1.72 - 11.3,
                worldY * 1.72 + 7.9,
                routeCompactionKey);
            // The ground community this cell stands on. One sample answers all four ecology gains, so a scree flat and
            // the sward beside it can never disagree about which of them the cell belongs to.
            TerrainSubstrateFacies facies = terrainSubstrateFaciesAt(substrate, worldX, worldY);
            double meadowValue = meadowWeight * facies.meadow;
            // `surface` is the terrain generator's authored material statement. It used to stop at the chunk object:
            // the bake discarded it, then tried to reconstruct meadow/soil from a second stochastic field. That made
            // the generator preview show broad sward and dirt regions while the streamed game rendered one brown mat.
            // Ecology still shapes the edge and density inside a material region, but it may no longer contradict the
            // region itself. Bilinear sampling plus the turf mottle softens these cell statements into organic patches.
            if (statedGrass) meadowValue = Math.max(meadowValue, 0.68 * (1 - expanded * 0.92));
            else if (statedSoil) meadowValue *= 0.48;
            else if (statedSand || statedStone || statedMetal) meadowValue *= 0.14;
            meadow[index] = (float)clamp(meadowValue, 0, 1);
            double dirtValue = Math.max(
                expanded * 0.38 + (double)routeCore[index] * (0.24 + compactionDetail * 0.32),
                smoothstep(0.64, 0.84, bareField) * 0.62,
                (double)wetness[index] * (0.18 + Math.min(1, waterContact) * 0.28),
                smoothstep(0.12, 0.72, ambientWear) * (0.44 + compactionDetail * 0.24));
            // Authored soil/scree is a material bias, not a second route stencil. Keeping these floors below the
            // strong-wear range leaves the biome's neutral ground visible between sward and the real circulation
            // network; routeCore/ambient wear can still lift genuinely trodden earth above it.
            if (statedSoil) dirtValue = Math.max(dirtValue, 0.38);
            else if (statedSand) dirtValue = Math.max(dirtValue, 0.36);
            else if (statedStone) dirtValue = Math.max(dirtValue, 0.22);
            else if (statedMetal) dirtValue = Math.max(dirtValue, 0.3);
            else if (statedGrass) dirtValue *= 0.4;
            dirt[index] = (float)clamp(dirtValue * (1 - (double)meadow[index] * 0.44) * facies.bare, 0, 1);
            // Grass is a hierarchy of broad, continuous colonies inside the meadow body. A second, much slower field
            // decides which parts of a suitable meadow become closed turf. Geometry and pigment consume this exact
            // value, so every visible tuft grows from a matching green sward and colony edges cross chunk seams.
            //
            // THREE INPUTS, ONE SAY EACH. The retired form multiplied five gates, and the route penalty appeared in
            // three of them at once: `meadowWeight` already carries `(1 - expanded · 0.94)`, and grass then applied
            // `(1 - expanded · 0.96)` SQUARED on top, under a smoothstep of a smoothstep. The product collapsed —
            // measured on open highland floor, the median cell scored 0.000 and only 7 % cleared the geometry's
            // presence floor, so the world had no meadows at all, only stragglers near water. Compounding independent-
            // looking penalties that share a cause is how a field quietly empties out.
            double colonyMacro = samplers.ecologyAt(
                worldX * 0.56 + 37.1,
                worldY * 0.56 - 21.7,
                turfColonyKey);
            double colonyHabitat =
                ecology * 0.32 +
                colonyMacro * 0.3 +
                composition.grove * 0.2 +
                moistureWeight * 0.12 +
                quiet * 0.1 -
                focus * 0.18;
            // Membership: which parts of the world are turf at all. Broad, so a colony is an AREA and not a spot —
            // but never the whole map: a world with no clearings has no meadows either, only a green floor.
            double colonyMembership = smoothstep(0.32, 0.6, colonyHabitat);
            double grassValue =
                // Body: how closed that turf gets. `meadowWeight` is the one carrier of the route penalty.
                smoothstep(0.05, 0.4, meadowWeight) *
                colonyMembership *
                // The compacted core is bare, exactly. One purpose-built factor that is 1 on open ground and 0 on the
                // trail beats relying on a stack of penalties to happen to reach zero — which is what the retired
                // squared route term did, and it took every ordinary meadow down with it.
                (1 - (double)routeCore[index]) *
                // Trodden ground thins the turf, and this is what makes the patchwork READ. A dark wear patch beside
                // an unchanged green sward is only a tint; a dark patch where the turf has actually given way is a
                // place. The coefficient is deliberately partial — full suppression would carve the ambient field
                // into the meadow as hard as a road does, and the field is broad enough that the world would lose its
                // closed turf altogether (which is the failure mode the retired squared route penalty produced).
                (1 - smoothstep(0.24, 0.86, ambientWear) * 0.72) *
                // Focus: a landmark's approach stays clear so wayfinding survives.
                (1 - focus * 0.7) *
                (1 - (double)wetness[index] * Math.min(0.55, waterContact * 0.22)) *
                facies.grass;
            if (statedGrass)
                grassValue = Math.max(
                    grassValue,
                    0.58 *
                        (1 - (double)routeCore[index]) *
                        (1 - smoothstep(0.24, 0.86, ambientWear) * 0.45) *
                        (1 - focus * 0.55) *
                        (1 - (double)wetness[index] * Math.min(0.5, waterContact * 0.2)));
            else if (statedSoil) grassValue *= 0.28;
            else if (statedSand || statedStone) grassValue *= 0.08;
            else if (statedMetal) grassValue = 0;
            grass[index] = (float)clamp(grassValue, 0, 1);
            // Flowers do NOT inherit every grass colony. Three independent, much rarer absolute-world masks create a
            // readable hierarchy instead: pin-prick single sites, compact colonies and exceptional broad fields. Every
            // mask has a true zero outside its support, so most otherwise healthy turf remains deliberately flowerless.
            // The fields are still continuous and chunk-stable; this changes composition, not post-bake prop scatter.
            double patchMacro = samplers.ecologyAt(
                worldX * 0.38 - 31.7,
                worldY * 0.38 + 18.9,
                flowerPatchKey);
            double patchEdge = samplers.ecologyAt(
                worldX * 0.86 + 12.3,
                worldY * 0.86 - 44.1,
                flowerPatchEdgeKey);
            double fieldMacro = samplers.ecologyAt(
                worldX * 0.16 + 83.1,
                worldY * 0.16 - 57.4,
                flowerFieldKey);
            double singleSite = samplers.ecologyAt(
                worldX * 1.41 - 7.3,
                worldY * 1.41 + 29.7,
                flowerSingleKey);
            /*
             * ## The three flower gates, read off their own fields
             *
             * The hierarchy below — pin-prick singles, compact colonies, exceptional broad fields — is right, and it was
             * unreachable: every one of the three gates was set at or above the maximum its input can attain, so all
             * three masks were clipped and the world had effectively no flowers. Measured over 49 chunks (27 231 cells):
             *
             * | mask   | old gate              | its input: p50 → max | cells over the old lower edge |
             * | ------ | --------------------- | -------------------- | ----------------------------- |
             * | patch  | smoothstep(0.64,0.78) | 0.474 → **0.759**    | 6.3 %                         |
             * | field  | smoothstep(0.72,0.86) | 0.439 → **0.822**    | 1.8 %                         |
             * | single | smoothstep(0.9,0.975) | 0.514 → 0.967        | 0.4 %                         |
             *
             * Both upper edges sat past the field's own ceiling (0.78 > 0.759, 0.86 > 0.822), so `patchMask` and
             * `fieldMask` could never reach 1 at all — `fieldMask` peaked at 0.165 and was zero on 99.8 % of cells. The
             * composed result was zero on 93.4 % of Floor, and the multiplication downstream took the rest.
             *
             * The gates are now percentiles of the measured inputs, which keeps the intended hierarchy AND lets each
             * mask saturate where its field is genuinely high: patches active on ~20 % of cells (p80→p95), fields on
             * ~5 % (p95→p99.5), singles on ~10 % (p90→p98). This is the same failure the tree line and the screes bound
             * had: a threshold in absolute units, chosen without looking at the distribution it decides against.
             */
            double patchMask = smoothstep(
                0.6,
                0.7,
                patchMacro * 0.72 + ecology * 0.1 + composition.quiet * 0.1 + patchEdge * 0.08);
            // A flower field is a destination-scale exception, not the default endpoint of a strong patch. Its own
            // slower field can occasionally override a flowerless or patch region with a broad high-density body.
            double fieldMask = smoothstep(
                0.7,
                0.79,
                fieldMacro * 0.82 + composition.clearing * 0.1 + moistureWeight * 0.08);
            // Singles are tiny islands with no baseline. They never turn an entire grass patch into low-density bloom.
            double singleMask = smoothstep(0.89, 0.96, singleSite) * (1 - patchMask) * (1 - fieldMask);
            double moistureSuitability = clamp(0.78 + moistureWeight * 0.42 - (double)wetness[index] * 0.36, 0, 1);
            double flowerRegion = Math.max(
                singleMask * 0.12,
                patchMask * (0.26 + patchEdge * 0.2),
                fieldMask * (0.58 + patchEdge * 0.14),
                // Every stated sward carries a very sparse wildflower seed bank. The rare masks above still decide the
                // actual colonies/fields; this low floor only guarantees that a generated grass patch can never arrive
                // as a flowerless green polygon. Root survival remains deterministic in the geometry emitter.
                statedGrass ? 0.022 : 0);
            flowers[index] = (float)clamp(
                (double)meadow[index] *
                    flowerRegion *
                    (0.38 + (double)grass[index] * 0.62) *
                    moistureSuitability *
                    // The core is already removed from meadow, but keeping this explicit makes the no-flowers-on-path
                    // contract robust if the meadow weighting changes later.
                    (1 - (double)routeCore[index]) *
                    // A forced grass patch is a real sward even when it happens to rescue a block whose underlying facies
                    // is slate/sand. Retaining that facies' near-zero bloom gain made the guaranteed patch flowerless.
                    (statedGrass ? Math.max(0.58, facies.flowers) : facies.flowers),
                0,
                1);
            direction[index] = (float)(total > 0.05 ? 0.5 * Math.atan2(2 * xy, xx - yy) : composition.direction);
        }

        return new TerrainFloorCompositionPlan
        {
            route = route,
            routeCore = routeCore,
            dirt = dirt,
            meadow = meadow,
            grass = grass,
            flowers = flowers,
            wetness = wetness,
            direction = direction,
        };
    }
}
