// Port of packages/shared/src/domain/dungeon/terrainWater.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Inside Fluitown.Domain a bare `Js` binds to the namespace Fluitown.Runtime (outer-namespace members are searched
// before compilation-unit usings); this namespace-level alias makes it the runtime class.

public class TerrainWaterTopologyOptions
{
    public double? minimumComponentCells;
    public double? minimumBridgeBodyCells;
    /// <summary>A finite stitched window cannot judge a water body clipped by its outer edge.</summary>
    public bool? ignoreBoundaryComponents;
}

public sealed class TerrainBridgeUseOptions : TerrainWaterTopologyOptions
{
    /// <summary>A crossing may be required by an external contract (for Endless: the shared chunk-seam port).</summary>
    public Func<int, int, bool>? requiredBridgeAt;
    /// <summary>A connected alternate route longer than this still makes the bridge a meaningful shortcut.</summary>
    public double? minimumUsefulDetour;
    /// <summary>Chasm-spanning decks use the same body-size gate, independently of hydrological bodies.</summary>
    public double? minimumChasmBodyCells;
}

/// <summary>
/// One hydrological body. Bridge cells are part of `indices`: every deck cell replaced a water candidate and
/// therefore still contributes to the size of the underlying surface without pretending it is visible water.
/// </summary>
public sealed class TerrainWaterBody
{
    public List<int> indices = new();
    public List<int> waterIndices = new();
    public List<int> bridgeIndices = new();
    public bool touchesBoundary;
    public int surfaceCells;
    public int waterCells;
    public int bridgeCells;
    public bool small;
    public bool bridgeUndersized;
    public bool valid;
}

public sealed class TerrainWaterTopology
{
    public int surfaceCells;
    public int waterCells;
    public int bridgeCells;
    public List<TerrainWaterBody> components = new();
    public int smallComponents;
    public int undersizedBridgeBodies;
    public double minimumComponentCells;
    public double minimumBridgeBodyCells;
    public int minimumObservedComponentCells;
    public int minimumObservedBridgeBodyCells;
    public byte[] issueMask = Array.Empty<byte>();
}

public sealed class EnforceTerrainWaterTopologyResult
{
    public int removedCells;
    public int removedComponents;
    public TerrainWaterTopology topology = new();
}

public sealed class TerrainBridgeUseComponent
{
    public List<int> indices = new();
    public bool touchesBoundary;
    public int waterContacts;
    public int chasmContacts;
    public int barrierContacts;
    /// <summary>Locally distinct landing banks, not the number of individual adjacent walkable cells.</summary>
    public int landContacts;
    public int waterBodyCells;
    public int chasmBodyCells;
    public int barrierBodyCells;
    /// <summary>TerrainBridgeSpan (Water/Chasm) or null.</summary>
    public int? span;
    public int alternatePathDistance;
    public bool necessary;
    public bool required;
    public bool ignored;
    public bool valid;
}

public sealed class TerrainBridgeUseTopology
{
    public List<TerrainBridgeUseComponent> components = new();
    public int redundantComponents;
    public int unanchoredComponents;
    public int dryComponents;
    public int undersizedWaterBodies;
    public int undersizedChasmBodies;
    public byte[] issueMask = Array.Empty<byte>();
}

public static class TerrainWater
{
    /// <summary>Small puddles read as generation noise rather than authored terrain at the production camera scale.</summary>
    public const int TERRAIN_WATER_MIN_COMPONENT_CELLS = 10;
    /// <summary>A bridge is a landmark crossing, reserved for a substantial body of water.</summary>
    public const int TERRAIN_BRIDGE_MIN_WATER_BODY_CELLS = 20;

    private static readonly (int dx, int dy)[] CARDINAL_DIRECTIONS = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    private static bool validDimensions(byte[] tiles, int width, int height) =>
        width > 0 && height > 0 && tiles.Length >= width * height;

    private static (int[] labels, List<int> sizes) bridgeBarrierBodies(
        byte[] tiles,
        byte[] bridgeSpans,
        int width,
        int height,
        int span)
    {
        int count = width * height;
        var labels = new int[count].fill(-1);
        var sizes = new List<int>();
        var queue = new int[count];
        for (int start = 0; start < count; start++)
        {
            int startTile = tiles[start];
            if (
                labels[start] >= 0 ||
                (startTile != span && !(startTile == TileType.Bridge && bridgeSpans[start] == span)))
                continue;
            int label = sizes.Count;
            int head = 0;
            int tail = 0;
            int size = 0;
            labels[start] = label;
            queue[tail++] = start;
            while (head < tail)
            {
                int index = queue[head++];
                size++;
                int tx = index % width;
                int ty = index / width; // Math.floor of a non-negative quotient
                foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int ni = ny * width + nx;
                    int tile = tiles[ni];
                    if (
                        labels[ni] >= 0 ||
                        (tile != span && !(tile == TileType.Bridge && bridgeSpans[ni] == span)))
                        continue;
                    labels[ni] = label;
                    queue[tail++] = ni;
                }
            }
            sizes.push(size);
        }
        return (labels, sizes);
    }

    /// <summary>Measure connected Water+Bridge bodies and enforce the two authored surface-size contracts.</summary>
    public static TerrainWaterTopology analyzeTerrainWaterTopology(
        byte[] tiles,
        int width,
        int height,
        TerrainWaterTopologyOptions? options = null)
    {
        options ??= new TerrainWaterTopologyOptions();
        double minimumComponentCells = Math.max(
            1,
            Math.floor(options.minimumComponentCells ?? TERRAIN_WATER_MIN_COMPONENT_CELLS));
        double minimumBridgeBodyCells = Math.max(
            minimumComponentCells,
            Math.floor(options.minimumBridgeBodyCells ?? TERRAIN_BRIDGE_MIN_WATER_BODY_CELLS));
        int count = Math.max(0, width * height);
        var issueMask = new byte[count];
        if (!validDimensions(tiles, width, height))
        {
            return new TerrainWaterTopology
            {
                surfaceCells = 0,
                waterCells = 0,
                bridgeCells = 0,
                components = new List<TerrainWaterBody>(),
                smallComponents = 0,
                undersizedBridgeBodies = 0,
                minimumComponentCells = minimumComponentCells,
                minimumBridgeBodyCells = minimumBridgeBodyCells,
                minimumObservedComponentCells = 0,
                minimumObservedBridgeBodyCells = 0,
                issueMask = issueMask,
            };
        }

        var seen = new byte[count];
        var bridgeSpans = TerrainBridgeSpanModule.classifyTerrainBridgeSpans(tiles, width, height);
        var queue = new int[count];
        var components = new List<TerrainWaterBody>();
        int surfaceCells = 0;
        int waterCells = 0;
        int bridgeCells = 0;
        int smallComponents = 0;
        int undersizedBridgeBodies = 0;
        double minimumObservedComponentCells = double.PositiveInfinity;
        double minimumObservedBridgeBodyCells = double.PositiveInfinity;

        for (int start = 0; start < count; start++)
        {
            if (
                seen[start] != 0 ||
                !TerrainModel.terrainTileCarriesWater(tiles[start], bridgeSpans[start]))
                continue;
            int head = 0;
            int tail = 0;
            bool touchesBoundary = false;
            var indices = new List<int>();
            var waterIndices = new List<int>();
            var bridgeIndices = new List<int>();
            seen[start] = 1;
            queue[tail++] = start;
            while (head < tail)
            {
                int index = queue[head++];
                int tx = index % width;
                int ty = index / width; // Math.floor of a non-negative quotient
                int tile = tiles[index];
                indices.push(index);
                if (tile == TileType.Water) waterIndices.push(index);
                else bridgeIndices.push(index);
                if (tx == 0 || ty == 0 || tx == width - 1 || ty == height - 1) touchesBoundary = true;
                foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int ni = ny * width + nx;
                    if (
                        seen[ni] != 0 ||
                        !TerrainModel.terrainTileCarriesWater(tiles[ni], bridgeSpans[ni]))
                        continue;
                    seen[ni] = 1;
                    queue[tail++] = ni;
                }
            }

            bool ignored = options.ignoreBoundaryComponents == true && touchesBoundary;
            bool small = indices.Count < minimumComponentCells;
            bool bridgeUndersized = bridgeIndices.Count > 0 && indices.Count < minimumBridgeBodyCells;
            bool valid = ignored || (!small && !bridgeUndersized);
            if (!ignored)
            {
                minimumObservedComponentCells = Math.min(minimumObservedComponentCells, indices.Count);
                if (bridgeIndices.Count > 0)
                    minimumObservedBridgeBodyCells = Math.min(minimumObservedBridgeBodyCells, indices.Count);
                if (small) smallComponents++;
                if (bridgeUndersized) undersizedBridgeBodies++;
                if (!valid) foreach (int index in indices) issueMask[index] = 1;
            }
            surfaceCells += indices.Count;
            waterCells += waterIndices.Count;
            bridgeCells += bridgeIndices.Count;
            components.push(new TerrainWaterBody
            {
                indices = indices,
                waterIndices = waterIndices,
                bridgeIndices = bridgeIndices,
                touchesBoundary = touchesBoundary,
                surfaceCells = indices.Count,
                waterCells = waterIndices.Count,
                bridgeCells = bridgeIndices.Count,
                small = small,
                bridgeUndersized = bridgeUndersized,
                valid = valid,
            });
        }

        return new TerrainWaterTopology
        {
            surfaceCells = surfaceCells,
            waterCells = waterCells,
            bridgeCells = bridgeCells,
            components = components,
            smallComponents = smallComponents,
            undersizedBridgeBodies = undersizedBridgeBodies,
            minimumComponentCells = minimumComponentCells,
            minimumBridgeBodyCells = minimumBridgeBodyCells,
            minimumObservedComponentCells = Number.isFinite(minimumObservedComponentCells)
                ? (int)minimumObservedComponentCells
                : 0,
            minimumObservedBridgeBodyCells = Number.isFinite(minimumObservedBridgeBodyCells)
                ? (int)minimumObservedBridgeBodyCells
                : 0,
            issueMask = issueMask,
        };
    }

    /// <summary>Restore every invalid closed surface to its exact terrain before water was painted.</summary>
    public static EnforceTerrainWaterTopologyResult enforceTerrainWaterTopology(
        byte[] tiles,
        byte[] fallbackTiles,
        int width,
        int height,
        TerrainWaterTopologyOptions? options = null)
    {
        options ??= new TerrainWaterTopologyOptions();
        if (!validDimensions(tiles, width, height) || fallbackTiles.Length < width * height)
        {
            return new EnforceTerrainWaterTopologyResult
            {
                removedCells = 0,
                removedComponents = 0,
                topology = analyzeTerrainWaterTopology(tiles, width, height, options),
            };
        }
        var topology = analyzeTerrainWaterTopology(tiles, width, height, options);
        var bridgeSpans = TerrainBridgeSpanModule.classifyTerrainBridgeSpans(tiles, width, height);
        int removedCells = 0;
        int removedComponents = 0;
        foreach (var component in topology.components)
        {
            if (component.valid) continue;
            removedComponents++;
            foreach (int index in component.indices)
            {
                if (!TerrainModel.terrainTileCarriesWater(tiles[index], bridgeSpans[index]))
                    continue;
                tiles[index] = fallbackTiles[index];
                removedCells++;
            }
        }
        return new EnforceTerrainWaterTopologyResult
        {
            removedCells = removedCells,
            removedComponents = removedComponents,
            topology = analyzeTerrainWaterTopology(tiles, width, height, options),
        };
    }

    /// <summary>
    /// Audit whether each bridge is physically anchored, spans a large-enough Water/Chasm body and is needed.
    /// A deck is useful when it is the only connection or replaces a materially long alternate route. This keeps
    /// bridges that turn peninsulas into navigation loops while still rejecting decorative pond decking.
    /// </summary>
    public static TerrainBridgeUseTopology analyzeTerrainBridgeUse(
        byte[] tiles,
        int width,
        int height,
        TerrainBridgeUseOptions? options = null)
    {
        options ??= new TerrainBridgeUseOptions();
        int count = Math.max(0, width * height);
        var issueMask = new byte[count];
        if (!validDimensions(tiles, width, height))
        {
            return new TerrainBridgeUseTopology
            {
                components = new List<TerrainBridgeUseComponent>(),
                redundantComponents = 0,
                unanchoredComponents = 0,
                dryComponents = 0,
                undersizedWaterBodies = 0,
                undersizedChasmBodies = 0,
                issueMask = issueMask,
            };
        }

        var bridgeSpans = TerrainBridgeSpanModule.classifyTerrainBridgeSpans(tiles, width, height);
        var water = analyzeTerrainWaterTopology(tiles, width, height, options);
        var waterBodyByCell = new int[count].fill(-1);
        for (int body = 0; body < water.components.Count; body++)
        {
            foreach (int index in water.components[body].indices) waterBodyByCell[index] = body;
        }
        var chasmBodies = bridgeBarrierBodies(tiles, bridgeSpans, width, height, TileType.Chasm);

        var seen = new byte[count];
        var blockedDeck = new byte[count];
        var contactMark = new byte[count];
        var distance = new int[count];
        var queue = new int[count];
        var components = new List<TerrainBridgeUseComponent>();
        int redundantComponents = 0;
        int unanchoredComponents = 0;
        int dryComponents = 0;
        int undersizedWaterBodies = 0;
        int undersizedChasmBodies = 0;
        double minimumBridgeBodyCells = Math.max(
            TERRAIN_WATER_MIN_COMPONENT_CELLS,
            Math.floor(options.minimumBridgeBodyCells ?? TERRAIN_BRIDGE_MIN_WATER_BODY_CELLS));
        double minimumUsefulDetour = Math.max(4, Math.floor(options.minimumUsefulDetour ?? 24));
        double minimumChasmBodyCells = Math.max(
            4,
            Math.floor(options.minimumChasmBodyCells ?? options.minimumBridgeBodyCells ?? 20));

        for (int start = 0; start < count; start++)
        {
            if (seen[start] != 0 || tiles[start] != TileType.Bridge) continue;
            var indices = new List<int>();
            var landContactIndices = new List<int>();
            bool touchesBoundary = false;
            bool required = false;
            int waterContacts = 0;
            int chasmContacts = 0;
            int head = 0;
            int tail = 0;
            seen[start] = 1;
            queue[tail++] = start;
            while (head < tail)
            {
                int index = queue[head++];
                int tx = index % width;
                int ty = index / width; // Math.floor of a non-negative quotient
                indices.push(index);
                blockedDeck[index] = 1;
                if (options.requiredBridgeAt?.Invoke(tx, ty) == true) required = true;
                if (tx == 0 || ty == 0 || tx == width - 1 || ty == height - 1) touchesBoundary = true;
                foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int ni = ny * width + nx;
                    int tile = tiles[ni];
                    if (tile == TileType.Water) waterContacts++;
                    if (tile == TileType.Chasm) chasmContacts++;
                    if (tile != TileType.Bridge && isWalkable(tile) && contactMark[ni] == 0)
                    {
                        contactMark[ni] = 1;
                        landContactIndices.push(ni);
                    }
                    if (seen[ni] != 0 || tile != TileType.Bridge) continue;
                    seen[ni] = 1;
                    queue[tail++] = ni;
                }
            }

            int landAnchorGroups = TerrainBridge.countTerrainBridgeLandAnchorGroups(landContactIndices, width, height);
            int waterBodyCells = 0;
            int chasmBodyCells = 0;
            bool hasWaterSpan = false;
            bool hasChasmSpan = false;
            foreach (int index in indices)
            {
                int encodedSpan = bridgeSpans[index];
                hasWaterSpan = hasWaterSpan || encodedSpan == TileType.Water;
                hasChasmSpan = hasChasmSpan || encodedSpan == TileType.Chasm;
                int waterBody = waterBodyByCell[index];
                if (waterBody >= 0)
                    waterBodyCells = Math.max(waterBodyCells, water.components[waterBody].surfaceCells);
                int chasmBody = chasmBodies.labels[index];
                if (chasmBody >= 0)
                    chasmBodyCells = Math.max(
                        chasmBodyCells,
                        chasmBody < chasmBodies.sizes.Count ? chasmBodies.sizes[chasmBody] : 0);
            }
            int? span = hasWaterSpan
                ? TileType.Water
                : hasChasmSpan
                    ? TileType.Chasm
                    : null;
            int barrierContacts = waterContacts + chasmContacts;
            int barrierBodyCells = Math.max(waterBodyCells, chasmBodyCells);
            bool ignored = options.ignoreBoundaryComponents == true && touchesBoundary;
            bool necessary = false;
            int alternatePathDistance = 0;
            if (landAnchorGroups >= 2)
            {
                distance.fill(-1);
                head = 0;
                tail = 0;
                distance[landContactIndices[0]] = 0;
                queue[tail++] = landContactIndices[0];
                while (head < tail)
                {
                    int index = queue[head++];
                    int tx = index % width;
                    int ty = index / width; // Math.floor of a non-negative quotient
                    foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int ni = ny * width + nx;
                        if (distance[ni] >= 0 || blockedDeck[ni] != 0 || !isWalkable(tiles[ni])) continue;
                        distance[ni] = distance[index] + 1;
                        queue[tail++] = ni;
                    }
                }
                bool unreachable = landContactIndices.some(index => distance[index] < 0);
                alternatePathDistance = landContactIndices.reduce(
                    (maximum, index) => Math.max(maximum, distance[index]),
                    0);
                necessary = unreachable || alternatePathDistance >= minimumUsefulDetour;
            }

            bool anchored = landAnchorGroups >= 2;
            bool supported = barrierContacts > 0 && span != null;
            bool waterLargeEnough = waterContacts > 0 && waterBodyCells >= minimumBridgeBodyCells;
            bool chasmLargeEnough = chasmContacts > 0 && chasmBodyCells >= minimumChasmBodyCells;
            bool largeEnough = waterLargeEnough || chasmLargeEnough;
            bool valid = ignored || (supported && largeEnough && anchored && (required || necessary));
            if (!ignored)
            {
                if (!necessary && !required && anchored) redundantComponents++;
                if (!anchored) unanchoredComponents++;
                if (!supported) dryComponents++;
                if (waterContacts > 0 && !waterLargeEnough) undersizedWaterBodies++;
                if (chasmContacts > 0 && !chasmLargeEnough) undersizedChasmBodies++;
                if (!valid) foreach (int index in indices) issueMask[index] = 1;
            }
            components.push(new TerrainBridgeUseComponent
            {
                indices = indices,
                touchesBoundary = touchesBoundary,
                waterContacts = waterContacts,
                chasmContacts = chasmContacts,
                barrierContacts = barrierContacts,
                landContacts = landAnchorGroups,
                waterBodyCells = waterBodyCells,
                chasmBodyCells = chasmBodyCells,
                barrierBodyCells = barrierBodyCells,
                span = span,
                alternatePathDistance = alternatePathDistance,
                necessary = necessary,
                required = required,
                ignored = ignored,
                valid = valid,
            });
            foreach (int index in indices) blockedDeck[index] = 0;
            foreach (int index in landContactIndices) contactMark[index] = 0;
        }

        return new TerrainBridgeUseTopology
        {
            components = components,
            redundantComponents = redundantComponents,
            unanchoredComponents = unanchoredComponents,
            dryComponents = dryComponents,
            undersizedWaterBodies = undersizedWaterBodies,
            undersizedChasmBodies = undersizedChasmBodies,
            issueMask = issueMask,
        };
    }
}
