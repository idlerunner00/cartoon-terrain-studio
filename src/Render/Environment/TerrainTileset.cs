// Port of packages/client/src/render/environment/terrainTileset.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using static Fluitown.Render.Palette;
using static Fluitown.Render.Theme;

namespace Fluitown.Render;

/// <summary>
/// A theme's complete terrain material set. The data fields live on the <see cref="TerrainMaterialTileset"/> base
/// (terrainMaterialCompiler.ts: `Omit&lt;TerrainTileset, 'surfaceForTile'&gt;`), so a TerrainTileset can be handed to
/// the material resolver as-is:
///
///  - `id` — stable material-set id. Runtime layouts may reference this through DungeonTerrainLayers.tilesetId.
///  - `construction` — TilesetKind.
///  - `materialDialect` — optional place-specific material language retained by the pure terrain-material resolver.
///  - `terrain`, `elevation`, `flood`, `bridge`, `chasm`, `decal`, `peakTint`.
/// </summary>
public sealed class TerrainTileset : TerrainMaterialTileset
{
    /// <summary>
    /// Resolve Auto/undefined material hints through the physical tile kind without changing collision.
    /// `(tile: TileType, surface?: TerrainSurfaceId) => TerrainSurfaceId`.
    /// </summary>
    public Func<int, int?, int> surfaceForTile;
}

/// <summary>The inline `chasm: { rim, face, deep, floor, mist }` record of <see cref="TerrainTileset"/>.</summary>
public sealed class TerrainTilesetChasm
{
    public int rim;
    public int face;
    public int deep;
    public int floor;
    public int mist;
}

/// <summary>`type TerrainTilesetFactory = (biome: Biome) =&gt; TerrainTileset`.</summary>
public delegate TerrainTileset TerrainTilesetFactory(Biome biome);

public static partial class TerrainTilesetModule
{
    // `const factories = new Map<string, TerrainTilesetFactory>()` — module-level mutable registry. Every browser
    // worker owns its own copy of the module (a registration made on one thread is invisible to the others), so
    // it is thread-local here (RENDER_AGENT_BRIEF "Thread safety"). Never iterated → Dictionary.
    [ThreadStatic] private static Dictionary<string, TerrainTilesetFactory>? _factories;
    internal static Dictionary<string, TerrainTilesetFactory> factories => _factories ??= new Dictionary<string, TerrainTilesetFactory>();

    public static TerrainTileset proceduralTerrainTileset(Biome biome)
    {
        var terrain = terrainOf(biome);
        var elevation = elevationOf(biome);
        var flood = floodColors(biome);
        bool citadel = biome.materialDialect == "aegis-citadel";
        return new TerrainTileset
        {
            id = $"procedural:{biome.key}",
            construction = tilesetOf(biome),
            materialDialect = biome.materialDialect,
            terrain = terrain,
            elevation = elevation,
            flood = flood,
            bridge = bridgeOf(terrain, elevation, flood, biome.timber),
            chasm = new TerrainTilesetChasm
            {
                // A restrained biome trace survives in the stone, but dry slate/umber anchors every ravine. In
                // particular, neither its shaft geology nor its mist inherits flood cyan: a chasm must never read as a
                // second water layer. The pale haze is the same atmospheric paper family as the rest of the world.
                rim = mix(elevation.cliffFace, terrain.wallLit, 0.3),
                face = citadel
                    ? mix(terrain.wallFace, terrain.wallDeep, 0.42)
                    : mix(terrain.wallFace, 0x756e5e, 0.42),
                deep = citadel
                    ? mix(terrain.wallDeep, elevation.cliffDeep, 0.56)
                    : mix(terrain.wallDeep, 0x484238, 0.56),
                floor = citadel
                    ? mix(elevation.cliffDeep, terrain.wallDeep, 0.36)
                    : mix(terrain.wallDeep, 0x6a6251, 0.54),
                mist = citadel
                    ? mix(biome.haze, terrain.wallLit, 0.24)
                    : mix(biome.haze, WORLD_INK.paperWarm, 0.3),
            },
            decal = decalOf(biome),
            peakTint =
                biome.key == "arena"
                    ? WORLD_INK.yellow
                    : citadel
                        ? terrain.wallLit
                        : mix(WORLD_INK.paper, biome.detail, 0.08),
            surfaceForTile = (tile, surface) =>
                surface != null && surface != TerrainSurface.Auto
                    ? surface.Value
                    : DungeonTypes.defaultSurfaceForTile(tile),
        };
    }

    public static TerrainTileset terrainTilesetForBiome(Biome biome, string? tilesetId = null)
    {
        TerrainTilesetFactory? factory = !string.IsNullOrEmpty(tilesetId)
            ? (factories.TryGetValue(tilesetId, out var registered) ? registered : null)
            : null;
        return factory != null ? factory(biome) : proceduralTerrainTileset(biome);
    }
}
