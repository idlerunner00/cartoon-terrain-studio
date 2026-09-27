// Port of packages/shared/src/domain/dungeon/editor.ts (createTerrainArtifact) — keep in lockstep with the original.
// The rest of editor.ts lives in TerrainEditor.cs; this part is separate because the map composer
// (MapSimulation.cs) creates its canvas through it.
using System.Collections.Generic;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

public sealed class CreateTerrainArtifactOptions
{
    public int? width;
    public int? height;
    public double? tileSize;
    public double? originX;
    public double? originY;
    public double? seed;
    /// <summary>A DungeonStyle value.</summary>
    public int? style;
    public string? biomeKey;
    public int? tier;
    /// <summary>A TileType value.</summary>
    public int? fillTile;
    /// <summary>A TileType value.</summary>
    public int? borderTile;
    public int? elevationLevel;
    public bool? withDefaultMarkers;
}

public static partial class TerrainEditor
{
    public const string TERRAIN_EDITOR_DOCUMENT_KIND = "fluitown.terrain-artifact";
    public const int TERRAIN_EDITOR_DOCUMENT_VERSION = 1;

    public static TerrainArtifact createTerrainArtifact(CreateTerrainArtifactOptions? options = null)
    {
        options ??= new CreateTerrainArtifactOptions();
        int width = (int)Math.max(3, Math.floor(options.width ?? 64));
        int height = (int)Math.max(3, Math.floor(options.height ?? 48));
        double tileSize = options.tileSize ?? Grid.TILE_SIZE;
        int count = width * height;
        int fillTile = options.fillTile ?? TileType.Floor;
        int borderTile = options.borderTile ?? TileType.Solid;
        var baseTiles = new byte[count];
        System.Array.Fill(baseTiles, (byte)fillTile);
        var elevation = new sbyte[count];
        System.Array.Fill(elevation, (sbyte)Math.max(
            TerrainModel.TERRAIN_MIN_ELEVATION,
            Math.min(TerrainModel.TERRAIN_MAX_ELEVATION, options.elevationLevel ?? 0)));
        var surface = new byte[count];
        System.Array.Fill(surface, (byte)TerrainSurface.Auto);
        var variant = new byte[count];

        for (int tx = 0; tx < width; tx++)
        {
            baseTiles[Grid.tileIndex(width, tx, 0)] = (byte)borderTile;
            baseTiles[Grid.tileIndex(width, tx, height - 1)] = (byte)borderTile;
        }
        for (int ty = 0; ty < height; ty++)
        {
            baseTiles[Grid.tileIndex(width, 0, ty)] = (byte)borderTile;
            baseTiles[Grid.tileIndex(width, width - 1, ty)] = (byte)borderTile;
        }

        var artifact = new TerrainArtifact
        {
            schemaVersion = DungeonTypes.TERRAIN_ARTIFACT_SCHEMA_VERSION,
            width = width,
            height = height,
            tileSize = tileSize,
            originX = options.originX ?? -(width * tileSize) / 2,
            originY = options.originY ?? -(height * tileSize) / 2,
            index = 0,
            seed = options.seed ?? 0,
            style = options.style ?? 0,
            biomeKey = options.biomeKey ?? "mountain",
            tier = options.tier ?? 1,
            baseTiles = baseTiles,
            elevation = elevation,
            surface = surface,
            variant = variant,
            markers = new List<TerrainMarker>(),
            decorations = new List<TerrainDecorationPlacement>(),
        };

        if (options.withDefaultMarkers ?? true)
        {
            int startX = (int)Math.max(1, Math.floor(width * 0.25));
            int startY = (int)Math.max(1, Math.floor(height * 0.5));
            int bossX = (int)Math.min(width - 2, Math.max(1, Math.floor(width * 0.75)));
            int bossY = (int)Math.min(height - 2, Math.max(1, Math.floor(height * 0.5)));
            artifact.baseTiles[Grid.tileIndex(width, startX, startY)] = TileType.Floor;
            artifact.baseTiles[Grid.tileIndex(width, bossX, bossY)] = TileType.Floor;
            artifact.markers = new List<TerrainMarker>
            {
                new() { type = TerrainMarkerType.Start, tx = startX, ty = startY, id = "start" },
                new() { type = TerrainMarkerType.Boss, tx = bossX, ty = bossY, id = "boss" },
            };
        }

        return artifact;
    }
}
