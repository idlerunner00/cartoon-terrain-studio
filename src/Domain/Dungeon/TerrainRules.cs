// Port of packages/shared/src/domain/dungeon/terrainRules.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// `export { TERRAIN_HEIGHT_RULES, TERRAIN_PHYSICS, TERRAIN_STANDARD_WALL_BASE_RISE, TransitionKind }` re-exports
// terrainModel.ts symbols. C# has no re-exports, and re-declaring them here would make every file that
// `using static`s both modules ambiguous, so callers reference them on TerrainModel / TransitionKind directly.
//
// `TerrainWaterSurfaceLevel` = number → double; `TerrainVisualHeightOptions` = TerrainModelOptions (used directly);
// `TerrainConnectionKind` = string literal union → string ('same_level' | 'ramp' | 'cleft' | 'underpass' | 'bridge'
// | 'cliff' | 'water' | 'chasm' | 'blocked_tile' | 'out_of_bounds').

public sealed class TerrainTileRules
{
    public int tile;
    public bool walkable;
    /// <summary>Legal centre tile for an actor body. Footprint/radius clearance is checked by GridView collision.</summary>
    public bool standable;
    public bool movementBlocked;
    public bool sightBlocked;
    public bool solid;
    public bool water;
    public bool bridge;
    public bool chasm;
    public bool cleft;
    public bool underpass;
    public bool overhead;
}

/// <summary>Not sealed: <c>TerrainRules.TerrainContactBetweenOptions</c> models the TS intersection type.</summary>
public class TerrainConnectionOptions
{
    public double? maxClimb;
    /// <summary>Used only for stitched streamed seams where both sides are visible but the local heightfield ends.</summary>
    public bool? ignoreHeight;
}

public sealed class TerrainConnectionRule
{
    /// <summary>A TerrainConnectionKind value.</summary>
    public string kind = "";
    public int fromTile;
    public int toTile;
    public double fromLevel;
    public double toLevel;
    public double delta;
    public double maxClimb;
    public bool walkable;
    public bool blocksMovement;
    public bool blocksSight;
    /// <summary>A TransitionKind value.</summary>
    public string transition = "";
}

public sealed class StandardWallRiseOptions
{
    public double? baseRise;
    public double? extraRise;
    public double? maxElevation;
    public double? skylineWeight;
    public double? salt;
}

public static class TerrainRules
{
    public const double TERRAIN_STANDARD_MAX_CLIMB = TerrainModel.TERRAIN_PHYSICS.maxStep;
    public const double TERRAIN_STANDARD_WALL_EXTRA_RISE = 1;

    // Endless walls reserve more of their height in the physical shell than authored/finite terrain does. This
    // keeps their visible faces deep even when both the walkable plateau and the stored Solid cap have reached
    // the shared elevation ceiling; the generator correspondingly reserves less stored headroom below it.

    /// <summary>
    /// Ordinary Endless rock is only a little taller than authored terrain. The old 6.4-level shell made every
    /// single wall monumental before geology had even contributed, so a camera looked into a continuous stockade
    /// and most of the playable floor disappeared behind it. Height now belongs to the sparse ridge/pinnacle
    /// fields below: low shelves are the default and the old dramatic silhouette survives only at real summits.
    /// </summary>
    public const double TERRAIN_ENDLESS_WALL_BASE_RISE = 2.4;
    /// <summary>Broad mountain shoulders. They are intentionally modest; pinnacles carry the exceptional height.</summary>
    public const int TERRAIN_ENDLESS_MOUNTAIN_TIERS = 3;
    public const double TERRAIN_ENDLESS_MOUNTAIN_STEP = 2;
    /// <summary>Rare 2x2/3x3 summit blocks which preserve the former mountain scale without blanketing every wall mass.</summary>
    public const int TERRAIN_ENDLESS_PINNACLE_TIERS = 4;
    public const double TERRAIN_ENDLESS_PINNACLE_STEP = 5;
    private const double TERRAIN_ENDLESS_MOUNTAIN_APEX_RISE = 2;
    private const double TERRAIN_ENDLESS_CROWN_STEP = 1.8;
    /// <summary>Highest possible physical Endless wall rise, including odd-level geological snap and mountain crown.</summary>
    public const double TERRAIN_ENDLESS_WALL_MAX_RISE =
        TERRAIN_ENDLESS_WALL_BASE_RISE +
        1 +
        TERRAIN_ENDLESS_MOUNTAIN_TIERS * TERRAIN_ENDLESS_MOUNTAIN_STEP +
        TERRAIN_ENDLESS_PINNACLE_TIERS * TERRAIN_ENDLESS_PINNACLE_STEP +
        TERRAIN_ENDLESS_MOUNTAIN_APEX_RISE +
        2 * TERRAIN_ENDLESS_CROWN_STEP;

    private static double clamp01(double v) => v <= 0 ? 0 : v >= 1 ? 1 : v;

    /// <summary>Deterministic tile hash used by the standard terrain rules.</summary>
    public static double terrainHash(double tx, double ty, double salt = 0)
    {
        // The three Math.imul results are summed as doubles in JS and then `| 0`-wrapped; wrapping int32
        // addition gives the identical value modulo 2^32.
        int h = unchecked(
            Math.imul(Js.ToInt32(tx), 374761393) +
            Math.imul(Js.ToInt32(ty), 668265263) +
            Math.imul(Js.ToInt32(salt), 1442695041));
        h = Math.imul(h ^ (int)((uint)h >> 13), 1274126177);
        return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
    }

    private static double smoothTerrainValueAt(double tx, double ty, double cellSize, double salt)
    {
        double x = tx / cellSize;
        double y = ty / cellSize;
        double ix = Math.floor(x);
        double iy = Math.floor(y);
        double fx = x - ix;
        double fy = y - iy;
        double sx = fx * fx * (3 - 2 * fx);
        double sy = fy * fy * (3 - 2 * fy);
        double north =
            terrainHash(ix, iy, salt) + (terrainHash(ix + 1, iy, salt) - terrainHash(ix, iy, salt)) * sx;
        double south =
            terrainHash(ix, iy + 1, salt) +
            (terrainHash(ix + 1, iy + 1, salt) - terrainHash(ix, iy + 1, salt)) * sx;
        return north + (south - north) * sy;
    }

    /// <summary>
    /// World-stable, organically banded CHASM_MIN_DEPTH..CHASM_MAX_DEPTH waterfall/cloud-entry depth used by
    /// generators and editors.
    ///
    /// The two lattices are stated RELATIVE to the depth span, exactly like the ground relief's octaves are
    /// stated relative to the band count. When the domain deepened from 8 to 25, the span this field spends grew
    /// five-fold; on the old 13/5.5-tile lattices that would have turned a readable basin into a spike field,
    /// with neighbouring throat cells fifteen levels apart. Scaled with the span, a canyon still descends at the
    /// rate it always did — it simply keeps descending for longer.
    /// </summary>
    public static double standardChasmDepthAt(double tx, double ty)
    {
        double coarse = smoothTerrainValueAt(tx, ty, 68, 0x43a57);
        double local = smoothTerrainValueAt(tx + 3.75, ty - 5.25, 29, 0x19d31);
        double depth = Math.floor(
            (coarse * 0.7 + local * 0.3) * (TerrainModel.CHASM_MAX_DEPTH - TerrainModel.CHASM_MIN_DEPTH + 1));
        return TerrainModel.CHASM_MIN_DEPTH +
            Math.min((double)(TerrainModel.CHASM_MAX_DEPTH - TerrainModel.CHASM_MIN_DEPTH), depth);
    }

    /// <summary>
    /// Three-argument form: this is the shape of the `solidWallRiseAt` / `wallRiseAt` callbacks
    /// (`(tx, ty, storedElevation) => number`), so the method group converts to those delegates.
    /// </summary>
    public static double standardWallRiseAt(int tx, int ty, double elevation) => standardWallRiseAt(tx, ty, elevation, null);

    public static double standardWallRiseAt(int tx, int ty, double elevation, StandardWallRiseOptions? options)
    {
        double baseRise = options?.baseRise ?? TerrainModel.TERRAIN_STANDARD_WALL_BASE_RISE;
        double extraRise = options?.extraRise ?? TERRAIN_STANDARD_WALL_EXTRA_RISE;
        if (extraRise <= 0) return baseRise;
        double maxElevation = Math.max(1, options?.maxElevation ?? Elevation.MAX_ELEVATION);
        double highness = clamp01(elevation / maxElevation);
        double skyline = clamp01(options?.skylineWeight ?? (0.2 + highness * 0.5));
        if (skyline <= 0.04) return baseRise;
        double salt = options?.salt ?? 0x6d2b79f5;
        double h = terrainHash(Math.floor((double)tx / 4), Math.floor((double)ty / 4), salt);
        int extra = 0;
        if (h < skyline) extra++;
        if (h < skyline * 0.58 && highness > 0.22) extra++;
        if (h < skyline * 0.32 && highness > 0.48) extra++;
        return baseRise + Math.min(extraRise, extra);
    }

    /// <summary>World-stable crown band (0 | 1 | 2) shared by the stored Endless strata and their render-only summit lift.</summary>
    public static int endlessWallCrownBandAt(double tx, double ty)
    {
        double warp = Math.floor(smoothTerrainValueAt(71, ty - 37, 19, 0x4f1bbcdc) * 7);
        double phase = (((tx + warp) % 10) + 10) % 10;
        return phase <= 2 ? 0 : phase <= 5 ? 1 : 2;
    }

    /// <summary>
    /// Broad, world-stable mountain tier carried by blocked rock only.
    ///
    /// Walkable ground must keep the one-level neighbour contract, so it cannot contain the sheer 10..20-level
    /// silhouettes a mountain needs. Rock can. A warped ridge field therefore adds six decisive four-level crowns
    /// on top of the normal wall shell. Quantising the smooth field is intentional: a continuous lift would draw
    /// another shallow one-step contour on every cap, while these sparse boundaries read as escarpments and leave
    /// wide summit/shoulder bodies between them. Absolute coordinates make the result continuous across streamed
    /// chunk seams without storing another raster layer.
    /// </summary>
    public static int endlessWallMountainTierAt(double tx, double ty)
    {
        double massif = smoothTerrainValueAt(tx - 151, ty + 89, 118, 0x72e4a91d);
        double massifGateInput = clamp01((massif - 0.38) / 0.5);
        // A massif now decides whether a shoulder exists as well as how high it is. The former 0.48 floor made a
        // mountain tier active almost everywhere, which defeated the distinction between ordinary walls and peaks.
        double massifGate = massifGateInput * massifGateInput * (3 - 2 * massifGateInput);
        double warp = (massif - 0.5) * 46;
        double ridge = 1 - Math.abs(Math.sin((tx * 0.82 + ty * 0.47 + warp) / 22));
        double ridgeInput = clamp01((ridge - 0.34) / 0.66);
        double ridgeBody = ridgeInput * ridgeInput * (3 - 2 * ridgeInput);
        double strength = massifGate * ridgeBody;
        if (strength < 0.28) return 0;
        return (int)Math.min(
            TERRAIN_ENDLESS_MOUNTAIN_TIERS,
            1 + Math.floor(((strength - 0.28) / 0.72) * TERRAIN_ENDLESS_MOUNTAIN_TIERS));
    }

    /// <summary>
    /// Sparse, block-sized summit accent inside the upper ridge shoulders.
    ///
    /// The broad fields above remain smooth and geological. This independent coarse hash is deliberately local:
    /// only about one in ten eligible 2x2 blocks receives a pinnacle, and only the rarest roll reaches the old
    /// twenty-level crown. That is the requested visual hierarchy — many low walls, some coherent ridges, a few
    /// blocks tall enough to become navigation landmarks.
    /// </summary>
    public static int endlessWallPinnacleTierAt(double tx, double ty, int? mountainTier = null)
    {
        mountainTier ??= endlessWallMountainTierAt(tx, ty);
        if (mountainTier < 2) return 0;
        double blockX = Math.floor(tx / 2);
        double blockY = Math.floor(ty / 2);
        double roll = terrainHash(blockX, blockY, 0x1f83d9ab);
        if (roll < 0.885) return 0;
        if (roll >= 0.985) return 4;
        if (roll >= 0.96) return 3;
        if (roll >= 0.925) return 2;
        return 1;
    }

    /// <summary>World-stable wall shell used exclusively by streamed Endless terrain.</summary>
    public static double endlessWallRiseAt(int tx, int ty, double elevation)
    {
        double baseRise = standardWallRiseAt(tx, ty, elevation, new StandardWallRiseOptions
        {
            baseRise = TERRAIN_ENDLESS_WALL_BASE_RISE,
            extraRise = 0,
        });
        // Render wall caps on the same two-level geological ladder even when an exposed-minimum repair had to store
        // an odd base value. This removes the repeated one-level cap stripes visible in long walls without changing
        // collision or the authored signed elevation byte.
        double geologicalSnap = Math.ceil(elevation / 2) * 2 - elevation;
        int mountainTier = endlessWallMountainTierAt(tx, ty);
        int pinnacleTier = endlessWallPinnacleTierAt(tx, ty, mountainTier);
        // Only the rare upper third of ridge tiers receives the apex kick. Raising every tier merely scales the same
        // smooth staircase; a sparse two-level summit is what creates the deliberately disproportionate peak
        // requested by the camera-scale silhouette contract while broad shoulders remain readable around it. Tier
        // four is included so an ordinary camera region can actually encounter an apex rather than depending on the
        // theoretical maximum of the slow 118-tile massif field.
        double mountainRise =
            mountainTier * TERRAIN_ENDLESS_MOUNTAIN_STEP +
            (pinnacleTier >= 3 ? TERRAIN_ENDLESS_MOUNTAIN_APEX_RISE : 0);
        // Preserve all three geological roof families with meaningful silhouette separation. A 1.8-level step is
        // large enough to read as a deliberate shelf (and stay above the shallow-stripe threshold), while the low
        // 2.4-level shell still keeps the median far below the former ubiquitous 6.4-level wall base.
        double crownRise = endlessWallCrownBandAt(tx, ty) * TERRAIN_ENDLESS_CROWN_STEP;
        return (
            baseRise +
            geologicalSnap +
            mountainRise +
            pinnacleTier * TERRAIN_ENDLESS_PINNACLE_STEP +
            crownRise);
    }

    public static TerrainTileRules terrainTileRules(int tile)
    {
        bool walkable = isWalkable(tile);
        return new TerrainTileRules
        {
            tile = tile,
            walkable = walkable,
            standable = walkable,
            movementBlocked = blocksMovement(tile),
            sightBlocked = blocksSight(tile),
            solid = tile == TileType.Solid,
            water = tile == TileType.Water,
            bridge = tile == TileType.Bridge,
            chasm = tile == TileType.Chasm,
            cleft = tile == TileType.Cleft,
            underpass = tile == TileType.Underpass,
            overhead = tile == TileType.Underpass,
        };
    }

    public static double terrainStoredElevationAt(sbyte[]? elevation, int width, int height, int tx, int ty)
    {
        if (elevation == null || !inBounds(width, height, tx, ty)) return 0;
        int index = tileIndex(width, tx, ty);
        return (uint)index < (uint)elevation.Length ? elevation[index] : 0;
    }

    /// <summary>One memoized materialization variant of a tile grid (keyed by the exact input identities).</summary>
    private sealed class RulesMaterializationEntry
    {
        public sbyte[]? elevation;
        public int width;
        public int height;
        public double? maxStep;
        // `unknown` in TS; compared by identity. Delegate.Equals (same method + target) is the C# analogue of JS
        // function identity: a method group converted twice compares equal, two closures of one lambda do not.
        public Delegate? solidWallRiseAt;
        public Delegate? waterLevelAt;
        public MaterializedTerrain terrain;
    }

    /// <summary>
    /// Memo for <see cref="materializeTerrainForRules"/>: the tile-level rule helpers (`terrainConnectionBetween`,
    /// `terrainContactBetween`, `terrainWaterSurfaceLevelAt`, …) are convenience per-query wrappers, and callers
    /// naturally invoke them per tile/edge — which used to re-materialize the ENTIRE grid on every call.
    ///
    /// Contract: the tiles/elevation arrays passed to the rule helpers are treated as immutable snapshots — every
    /// shared-kernel routine that mutates such an array in place calls <see cref="invalidateTerrainRulesMaterialization"/>.
    /// Callers passing custom options must keep the SAME options values/callback identities across queries of one
    /// grid — a fresh per-call closure defeats the memo. Keyed weakly by the tiles array (reference identity, like
    /// a WeakMap), so caches die with their layouts. The lock is a C# addition: JS is single-threaded, while chunk
    /// generation and sampling here may run on worker threads. It only guards the memo; results are unaffected.
    /// </summary>
    private static readonly ConditionalWeakTable<byte[], List<RulesMaterializationEntry>> rulesMaterializationCache = new();
    private static readonly object rulesMaterializationLock = new();
    /// <summary>Distinct option-variants kept per tiles array (default rules + custom maxStep/wall-rise variants).</summary>
    private const int RULES_MATERIALIZATION_VARIANTS = 4;

    /// <summary>Drop every memoized materialization for a tiles array whose contents (or elevation) changed in place.</summary>
    public static void invalidateTerrainRulesMaterialization(byte[] tiles)
    {
        lock (rulesMaterializationLock) rulesMaterializationCache.Remove(tiles);
    }

    public static MaterializedTerrain materializeTerrainForRules(
        byte[] tiles,
        sbyte[]? elevation,
        int width,
        int height,
        TerrainModelOptions? options = null)
    {
        options ??= new TerrainModelOptions();
        var solidWallRiseAt = options.solidWallRiseAt ?? standardWallRiseAt;
        List<RulesMaterializationEntry>? entries;
        lock (rulesMaterializationLock)
        {
            if (rulesMaterializationCache.TryGetValue(tiles, out entries))
            {
                foreach (var entry in entries)
                {
                    if (
                        ReferenceEquals(entry.elevation, elevation) &&
                        entry.width == width &&
                        entry.height == height &&
                        entry.maxStep == options.maxStep &&
                        Equals(entry.solidWallRiseAt, solidWallRiseAt) &&
                        Equals(entry.waterLevelAt, options.waterLevelAt))
                    {
                        return entry.terrain;
                    }
                }
            }
        }
        var terrain = TerrainModel.materializeTerrainGrid(tiles, width, height, elevation, new TerrainModelOptions
        {
            maxStep = options.maxStep,
            solidWallRiseAt = solidWallRiseAt,
            waterLevelAt = options.waterLevelAt,
        });
        lock (rulesMaterializationLock)
        {
            if (!rulesMaterializationCache.TryGetValue(tiles, out entries))
            {
                entries = new List<RulesMaterializationEntry>();
                rulesMaterializationCache.AddOrUpdate(tiles, entries);
            }
            entries.unshift(new RulesMaterializationEntry
            {
                elevation = elevation,
                width = width,
                height = height,
                maxStep = options.maxStep,
                solidWallRiseAt = solidWallRiseAt,
                waterLevelAt = options.waterLevelAt,
                terrain = terrain,
            });
            if (entries.Count > RULES_MATERIALIZATION_VARIANTS)
                entries.RemoveRange(RULES_MATERIALIZATION_VARIANTS, entries.Count - RULES_MATERIALIZATION_VARIANTS);
        }
        return terrain;
    }

    public static double terrainCellSurfaceLevelAt(
        byte[] tiles,
        sbyte[]? elevation,
        int width,
        int height,
        int tx,
        int ty,
        Func<int, int, double, double>? wallRiseAt = null)
    {
        wallRiseAt ??= standardWallRiseAt;
        if (!inBounds(width, height, tx, ty)) return 0;
        int tile = getTile(tiles, width, height, tx, ty);
        double stored = terrainStoredElevationAt(elevation, width, height, tx, ty);
        if (tile == TileType.Solid) return stored + wallRiseAt(tx, ty, stored);
        if (tile == TileType.Chasm) return -TerrainModel.chasmDepthFromStored(stored);
        if (tile == TileType.Water) return TerrainModel.waterSurfaceLevelFromStored(stored);
        if (tile == TileType.Bridge) return stored + TerrainModel.BRIDGE_DECK_LIFT;
        return stored;
    }

    public static int normalizeTerrainStoredElevationInPlace(
        byte[] tiles,
        sbyte[]? elevation,
        int width,
        int height)
    {
        if (elevation == null) return 0;
        int changed = 0;
        int count = Math.min(Math.min(width * height, tiles.Length), elevation.Length);
        for (int index = 0; index < count; index++)
        {
            int tile = tiles[index];
            double current = elevation[index];
            double normalized =
                tile == TileType.Water
                    ? Math.max(TerrainModel.WATER_MIN_STORED_LEVEL, Math.min(TerrainModel.WATER_MAX_STORED_LEVEL, current))
                    : tile == TileType.Chasm
                        ? current >= TerrainModel.CHASM_MIN_DEPTH && current <= TerrainModel.CHASM_MAX_DEPTH
                            ? current
                            : standardChasmDepthAt(index % width, Math.floor((double)index / width))
                        : current;
            if (current == normalized) continue;
            elevation[index] = Js.I8(normalized);
            changed++;
        }
        if (changed > 0) invalidateTerrainRulesMaterialization(tiles);
        return changed;
    }

    private static string terrainConnectionKind(
        TerrainTileRules from,
        TerrainTileRules to,
        double delta,
        bool blocked,
        bool ignoreHeight)
    {
        if (from.chasm || to.chasm) return "chasm";
        if (from.water || to.water) return "water";
        if (from.cleft || to.cleft) return "cleft";
        if (!from.standable || !to.standable) return "blocked_tile";
        if (blocked) return "cliff";
        if (from.underpass || to.underpass) return "underpass";
        if (from.bridge || to.bridge) return "bridge";
        if (delta == 0 || ignoreHeight) return "same_level";
        return "ramp";
    }

    public static TerrainConnectionRule terrainConnectionFromCells(
        int fromTile,
        int toTile,
        double fromLevel,
        double toLevel,
        TerrainConnectionOptions? options = null)
    {
        options ??= new TerrainConnectionOptions();
        double maxClimb = options.maxClimb ?? TERRAIN_STANDARD_MAX_CLIMB;
        var from = terrainTileRules(fromTile);
        var to = terrainTileRules(toTile);
        double delta = toLevel - fromLevel;
        bool standable = from.standable && to.standable;
        bool heightBlocked = options.ignoreHeight != true && Math.abs(delta) > maxClimb;
        bool blocks = !standable || heightBlocked;
        string transition =
            blocks || !standable
                ? TransitionKind.Blocked
                : delta > 0
                    ? TransitionKind.StepUp
                    : delta < 0
                        ? TransitionKind.StepDown
                        : TransitionKind.Level;

        return new TerrainConnectionRule
        {
            kind = terrainConnectionKind(from, to, delta, blocks, options.ignoreHeight == true),
            fromTile = fromTile,
            toTile = toTile,
            fromLevel = fromLevel,
            toLevel = toLevel,
            delta = delta,
            maxClimb = maxClimb,
            walkable = standable,
            blocksMovement = blocks,
            blocksSight = from.sightBlocked || to.sightBlocked,
            transition = transition,
        };
    }

    private static TerrainConnectionRule connectionFromMaterializedCells(
        TerrainCell fromCell,
        TerrainCell toCell,
        TerrainConnectionOptions? options = null)
    {
        options ??= new TerrainConnectionOptions();
        double maxClimb = options.maxClimb ?? TERRAIN_STANDARD_MAX_CLIMB;
        if (options.ignoreHeight == true)
        {
            return terrainConnectionFromCells(
                fromCell.type,
                toCell.type,
                fromCell.elevation,
                toCell.elevation,
                options);
        }

        var movement = TerrainModel.terrainMovementRuleFor(fromCell, toCell, maxClimb);
        var from = terrainTileRules(fromCell.type);
        var to = terrainTileRules(toCell.type);
        double fromLevel = TerrainModel.walkHeightForTerrainCell(fromCell) ?? fromCell.surfaceZ;
        double toLevel = TerrainModel.walkHeightForTerrainCell(toCell) ?? toCell.surfaceZ;
        double delta = toLevel - fromLevel;

        return new TerrainConnectionRule
        {
            kind = terrainConnectionKind(from, to, delta, !movement.passable, false),
            fromTile = fromCell.type,
            toTile = toCell.type,
            fromLevel = fromLevel,
            toLevel = toLevel,
            delta = delta,
            maxClimb = maxClimb,
            walkable = from.standable && to.standable,
            blocksMovement = !movement.passable,
            blocksSight = from.sightBlocked || to.sightBlocked,
            transition = movement.passable
                ? delta > 0
                    ? TransitionKind.StepUp
                    : delta < 0
                        ? TransitionKind.StepDown
                        : TransitionKind.Level
                : TransitionKind.Blocked,
        };
    }

    public static TerrainConnectionRule terrainConnectionInMaterialized(
        MaterializedTerrain terrain,
        int fromTx,
        int fromTy,
        int toTx,
        int toTy,
        TerrainConnectionOptions? options = null)
    {
        options ??= new TerrainConnectionOptions();
        var tiles = terrain.tiles;
        var elevation = terrain.elevation;
        int width = terrain.width;
        int height = terrain.height;
        if (!inBounds(width, height, fromTx, fromTy) || !inBounds(width, height, toTx, toTy))
        {
            return new TerrainConnectionRule
            {
                kind = "out_of_bounds",
                fromTile = getTile(tiles, width, height, fromTx, fromTy),
                toTile = getTile(tiles, width, height, toTx, toTy),
                fromLevel = terrainStoredElevationAt(elevation, width, height, fromTx, fromTy),
                toLevel = terrainStoredElevationAt(elevation, width, height, toTx, toTy),
                delta = 0,
                maxClimb = options.maxClimb ?? TERRAIN_STANDARD_MAX_CLIMB,
                walkable = false,
                blocksMovement = true,
                blocksSight = true,
                transition = TransitionKind.Blocked,
            };
        }
        var fromCell = TerrainModel.terrainCellAt(terrain, fromTx, fromTy)!;
        var toCell = TerrainModel.terrainCellAt(terrain, toTx, toTy)!;
        return connectionFromMaterializedCells(fromCell, toCell, options);
    }

    public static bool terrainMoveBlockedInMaterialized(
        MaterializedTerrain terrain,
        int fromTx,
        int fromTy,
        int toTx,
        int toTy,
        TerrainConnectionOptions? options = null)
    {
        if (fromTx == toTx && fromTy == toTy)
        {
            return blocksMovement(getTile(terrain.tiles, terrain.width, terrain.height, toTx, toTy));
        }
        return terrainConnectionInMaterialized(terrain, fromTx, fromTy, toTx, toTy, options ?? new TerrainConnectionOptions())
            .blocksMovement;
    }
}
