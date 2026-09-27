// Port of packages/client/src/render/environment/terrainMaterialCompiler.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Render.Palette;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// `Omit&lt;TerrainTileset, 'surfaceForTile'&gt;` — the structured-clone-safe tileset the terrain worker receives
/// (the main thread strips `surfaceForTile` before posting). <see cref="TerrainTileset"/> derives from it and adds
/// the resolver function, so every TerrainTileset is also a TerrainMaterialTileset (as with TS structural typing).
/// The fields are documented on the TerrainTileset interface in terrainTileset.ts.
/// </summary>
public class TerrainMaterialTileset
{
    /// <summary>Stable material-set id. Runtime layouts may reference this through DungeonTerrainLayers.tilesetId.</summary>
    public string id;
    /// <summary>TilesetKind.</summary>
    public string construction;
    /// <summary>Optional place-specific material language retained by the pure terrain-material resolver.</summary>
    public string? materialDialect;
    public TerrainPalette terrain;
    public ElevationPalette elevation;
    public FloodPalette flood;
    public BridgePalette bridge;
    public TerrainTilesetChasm chasm;
    public DecalSpec decal;
    public int peakTint;

    /// <summary>`const { surfaceForTile, ...tileset } = source` — a plain material tileset sharing the same palettes.</summary>
    public TerrainMaterialTileset ToMaterialTileset()
    {
        return new TerrainMaterialTileset
        {
            id = id,
            construction = construction,
            materialDialect = materialDialect,
            terrain = terrain,
            elevation = elevation,
            flood = flood,
            bridge = bridge,
            chasm = chasm,
            decal = decal,
            peakTint = peakTint,
        };
    }
}

public static partial class TerrainMaterialCompiler
{
    /// <summary>
    /// Pure terrain-material derivation shared by the render thread and the terrain-plan worker.
    /// Keeping this function free of renderer state is the quality contract for off-thread planning: a prepared
    /// tile carries the exact same paper base, biome tint, wall-height response and water/bridge palette as a
    /// synchronous bake.
    /// </summary>
    public static TerrainMaterial deriveRunTerrainMaterial(
        TerrainCell cell,
        double moisture,
        TerrainMaterialTileset tileset,
        WorldStyle style,
        bool quietFloor = false)
    {
        bool citadel = tileset.materialDialect == "aegis-citadel";
        if (cell.type == TileType.Chasm)
        {
            var @base = TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS.chasm;
            var chasm = tileset.chasm;
            if (citadel)
            {
                var citadelChasm = @base.Clone();
                citadelChasm.id = $"chasm:{toString16(chasm.deep)}";
                citadelChasm.top = chasm.floor;
                citadelChasm.topLight = chasm.rim;
                citadelChasm.topDark = chasm.deep;
                citadelChasm.side = chasm.face;
                citadelChasm.edgeLight = chasm.rim;
                citadelChasm.edgeDark = chasm.deep;
                citadelChasm.detail = chasm.face;
                return citadelChasm;
            }
            var material = @base.Clone();
            material.id = $"chasm:{toString16(chasm.deep)}";
            material.top = mix(@base.top, chasm.floor, 0.88);
            material.topLight = mix(@base.topLight, chasm.rim, 0.18);
            material.topDark = mix(@base.topDark, chasm.deep, 0.86);
            material.side = mix(@base.side, chasm.face, 0.82);
            material.edgeLight = mix(@base.edgeLight, chasm.rim, 0.56);
            material.edgeDark = mix(@base.edgeDark, chasm.deep, 0.9);
            material.detail = mix(@base.detail, chasm.face, 0.38);
            return material;
        }
        if (cell.type == TileType.Water)
        {
            // ONE home for the water-tile rule (see waterTerrainMaterial). This worker-leaf module is the copy
            // the BAKE actually runs, and it had drifted to its own weight set (0.30/0.34/0.38 against 0.50/0.55/0.60
            // in the render-thread twin) — which is why two attempts to take the biome-blind UI cyan out of the water
            // palette did not change the baked frame.
            FloodPalette flood;
            if (citadel)
            {
                flood = tileset.flood.Clone();
                flood.derived = false;
            }
            else
            {
                flood = tileset.flood;
            }
            return TerrainRenderPlanModule.waterTerrainMaterial(flood, style.waterTint);
        }
        if (cell.type == TileType.Bridge)
        {
            var wood = TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS.bridgeWood;
            var bridge = tileset.bridge;
            if (citadel)
            {
                var citadelBridge = wood.Clone();
                citadelBridge.top = bridge.body;
                citadelBridge.topLight = bridge.lip;
                citadelBridge.topDark = bridge.bodyShadow;
                citadelBridge.side = bridge.bodyShadow;
                citadelBridge.edgeLight = bridge.railLit;
                citadelBridge.edgeDark = bridge.edge;
                citadelBridge.detail = bridge.grain;
                return citadelBridge;
            }
            double bt(double weight) => clamp(weight * style.bridgeTint, 0, 0.8);
            var deck = wood.Clone();
            deck.top = mix(wood.top, bridge.body, bt(0.36));
            deck.topLight = mix(wood.topLight, bridge.lip, bt(0.32));
            deck.topDark = mix(wood.topDark, bridge.bodyShadow, bt(0.36));
            deck.side = mix(wood.side, bridge.bodyShadow, bt(0.3));
            deck.edgeLight = mix(wood.edgeLight, bridge.railLit, bt(0.35));
            deck.edgeDark = mix(wood.edgeDark, bridge.edge, bt(0.4));
            deck.detail = mix(wood.detail, bridge.grain, bt(0.45));
            return deck;
        }
        if (cell.type == TileType.Solid)
        {
            var terrain = tileset.terrain;
            var wallChalk = TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS.wallChalk;
            int biomeTop = wallCapColorAt(
                terrain.wallFill,
                terrain.wall,
                cell.surfaceZ,
                terrain.minLevel);
            // High caps used to converge on the same chalk highlight, bleaching every upper country into one white
            // sheet. Keep the continuous lift restrained, retain more of the biome's authored fill and give alternate
            // three-level geological shelves a very small mineral-value shift. The bands are broad world masses, never
            // per-cell colour noise, and match the Endless generator's default terrace rhythm.
            double heightTone = clamp((cell.surfaceZ - 3.2) * 0.032, 0, 0.19);
            int standardTop = mix(
                wallChalk.top,
                wallChalk.topLight,
                heightTone);
            double shelfBand = Math.floor(Math.max(0, cell.surfaceZ - 2.4) / 3);
            if ((Js.ToInt32(shelfBand) & 1) == 1)
                standardTop = mix(standardTop, wallChalk.side, 0.055);
            int top = mix(standardTop, biomeTop, clamp(0.28 * style.wallTint, 0, 0.58));
            // `if (tileset.peakTint !== undefined)` — TerrainTileset.peakTint is a required number, so always taken.
            top = mix(top, tileset.peakTint, clamp((cell.surfaceZ - 3.4) * 0.14, 0, 0.32));
            int side = mix(terrain.wallFace, wallChalk.side, 0.55);
            var wall = wallChalk.Clone();
            wall.id = $"wall:{Js.Str(Math.round(cell.surfaceZ * 2) / 2)}";
            wall.top = top;
            wall.topLight = mix(wallChalk.topLight, terrain.wallLit, 0.24);
            wall.topDark = mix(top, side, 0.42);
            wall.side = side;
            wall.edgeLight = terrain.wallLit;
            wall.edgeDark = terrain.wallLine;
            wall.detail = terrain.wallDeep;
            wall.roughness = 0.76;
            return wall;
        }

        var floorBase = TerrainGeometryCompilerStyle.cartoonFloorBase(quietFloor ? 0 : moisture, quietFloor ? 0 : cell.elevation);
        var elevation = tileset.elevation;
        double storedLevel = quietFloor ? 0 : Math.round(cell.elevation);
        double level = Math.max(
            0,
            Math.min(elevation.topFill.Length - 1, storedLevel - elevation.minLevel));
        // `elevation.topFill[level] ?? base.top`: level is an integer in range unless cell.elevation is NaN.
        var terrace = double.IsNaN(level) ? floorBase.top : elevation.topFill[(int)level];
        if (citadel)
        {
            var citadelFloor = floorBase.Clone();
            citadelFloor.id = $"aegis-floor:{Js.Str(level)}";
            citadelFloor.top = terrace;
            citadelFloor.topLight = mix(terrace, elevation.lit, 0.2);
            citadelFloor.topDark = mix(terrace, elevation.cliffFace, 0.44);
            citadelFloor.side = elevation.cliffFace;
            citadelFloor.edgeLight = elevation.lit;
            citadelFloor.edgeDark = elevation.shadow;
            citadelFloor.detail = elevation.cliffDeep;
            return citadelFloor;
        }
        double ft(double weight) => clamp(weight * style.floorTint, 0, 0.72);
        var floor = floorBase.Clone();
        floor.id = $"{floorBase.id}:{Js.Str(level)}";
        floor.top = mix(floorBase.top, terrace, ft(0.5));
        floor.topLight = mix(floorBase.topLight, terrace, ft(0.3));
        floor.topDark = mix(floorBase.topDark, mix(terrace, elevation.cliffFace, 0.4), ft(0.42));
        floor.side = mix(floorBase.side, elevation.cliffFace, ft(0.36));
        floor.detail = mix(floorBase.detail, elevation.cliffDeep, ft(0.24));
        return floor;
    }

    /// <summary>
    /// One biome has a tiny discrete material vocabulary. Reusing immutable material records substantially shrinks
    /// both worker structured-clone traffic and the render plan's retained object graph.
    /// (The cache belongs to the returned resolver, not to the module: one resolver per bake, one thread.)
    /// </summary>
    public static Func<TerrainCell, double, TerrainMaterial> createRunTerrainMaterialResolver(
        TerrainMaterialTileset tileset,
        WorldStyle style,
        bool quietFloor = false)
    {
        var cache = new Dictionary<double, TerrainMaterial>();
        return (cell, moisture) =>
        {
            double key =
                cell.type == TileType.Chasm
                    ? 1
                    : cell.type == TileType.Water
                        ? 2
                        : cell.type == TileType.Bridge
                            ? 3
                            : cell.type == TileType.Solid
                                ? 100 + cell.surfaceZ
                                : // Floor moisture is a continuous ecology signal, never a material-family key. Keeping it in this
                                  // discrete cache key retained one redundant material record per wetness value and made it too easy
                                  // for a future tint to reconstruct complete hydrology squares.
                                  10_000 + (quietFloor ? 0 : cell.elevation * 2);
            if (!cache.TryGetValue(key, out var material))
            {
                material = deriveRunTerrainMaterial(cell, moisture, tileset, style, quietFloor);
                cache[key] = material;
            }
            return material;
        };
    }

    private static int wallCapColorAt(
        IReadOnlyList<int> fills,
        int fallback,
        double surfaceZ,
        double minLevel)
    {
        if (fills.Count == 0) return fallback;
        double index = Math.max(0, Math.min(fills.Count - 1, Math.floor(surfaceZ - 0.001) - minLevel));
        // `fills[index] ?? fallback`: index is an in-range integer unless surfaceZ is NaN.
        return double.IsNaN(index) ? fallback : fills[(int)index];
    }

    private static double clamp(double value, double min, double max)
    {
        return value < min ? min : value > max ? max : value;
    }

    /// <summary>`Number.prototype.toString(16)` of an integral colour.</summary>
    private static string toString16(int value)
    {
        long v = value;
        return v < 0 ? "-" + (-v).ToString("x") : v.ToString("x");
    }
}
