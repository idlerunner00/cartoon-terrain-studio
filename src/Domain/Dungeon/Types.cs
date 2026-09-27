// Port of packages/shared/src/domain/dungeon/types.ts — keep in lockstep with the original.
using System.Collections.Generic;

namespace Fluitown.Domain;

// The carrier of every terrain height is one SIGNED byte per tile (`TerrainElevationLayer = Int8Array`):
// ground, walls and water span −25..+25 while Chasm stores a positive depth magnitude. In C# it is `sbyte[]`.

/// <summary>
/// A grid tile's nature — the collision truth. Read solidity through <see cref="DungeonTypes.blocksMovement"/> /
/// <see cref="DungeonTypes.blocksSight"/> so every tile kind is treated consistently.
/// </summary>
public static class TileType
{
    /// <summary>Rock / wall / mountain — blocks movement and projectiles/sight.</summary>
    public const int Solid = 0;
    /// <summary>Walkable floor. Transparent.</summary>
    public const int Floor = 1;
    /// <summary>River water — blocks movement; transparent to sight/projectiles.</summary>
    public const int Water = 2;
    /// <summary>A walkable crossing over water or a chasm (plank deck).</summary>
    public const int Bridge = 3;
    /// <summary>A deep open ravine — blocks movement but not sight. Stored elevation is depth 5..30.</summary>
    public const int Chasm = 4;
    /// <summary>A narrow split through a wall: bodies do not fit, sight and projectiles pass.</summary>
    public const int Cleft = 5;
    /// <summary>Walkable ground beneath a suspension deck anchored to high wall banks at both ends.</summary>
    public const int Underpass = 6;
}

/// <summary>Visual surface/material ids. `Auto` means "derive from TileType + biome".</summary>
public static class TerrainSurface
{
    public const int Auto = 0;
    public const int Floor = 1;
    public const int Stone = 2;
    public const int Water = 3;
    public const int Bridge = 4;
    public const int Grass = 5;
    public const int Sand = 6;
    public const int Metal = 7;
    public const int Chasm = 9;
    public const int Cleft = 10;
    public const int Underpass = 11;
}

/// <summary>Marker ids an editor/validator can use without affecting collision.</summary>
public static class TerrainMarkerType
{
    public const int Start = 0;
    public const int Boss = 1;
    public const int SeamPort = 4;
    public const int Door = 5;
    public const int Objective = 6;
    public const int OverheadBeam = 7;
    /// <summary>Data-driven semantic set piece in a generated Endless Country.</summary>
    public const int Landmark = 8;
}

public static class TerrainMarkerAxis
{
    public const int Horizontal = 0;
    public const int Vertical = 1;
}

public sealed class TerrainMarker
{
    public int type;
    public int tx;
    public int ty;
    /// <summary>Optional one-dimensional marker axis, used by overhead beams.</summary>
    public int? axis;
    /// <summary>Optional marker span in tiles, used by overhead beams.</summary>
    public double? length;
    /// <summary>Optional stable editor id.</summary>
    public string? id;
    /// <summary>Optional room/objective binding.</summary>
    public int? roomId;

    public TerrainMarker Clone() => (TerrainMarker)MemberwiseClone();
}

/// <summary>Optional visual/authoring layers parallel to <see cref="DungeonLayout.tiles"/>.</summary>
public sealed class DungeonTerrainLayers
{
    public int schemaVersion;
    /// <summary>Optional renderer tileset/material-set id. Null means the procedural biome tileset.</summary>
    public string? tilesetId;
    /// <summary>`width * height` visual material ids. Null means derive via TerrainSurface.Auto.</summary>
    public byte[]? surface;
    /// <summary>`width * height` visual variant ids, interpreted by the chosen tileset.</summary>
    public byte[]? variant;
    /// <summary>
    /// `width * height` semantic ground-use weights. Zero leaves the cell to natural habitat composition;
    /// 255 is the centre of a routed avenue/trail.
    /// </summary>
    public byte[]? floorUsage;
    /// <summary>Artifact-local theme keys referenced by <see cref="themeIndex"/>.</summary>
    public List<string>? themePalette;
    /// <summary>`width * height` indices into <see cref="themePalette"/>; TERRAIN_THEME_INHERIT inherits the layout biome.</summary>
    public byte[]? themeIndex;
    /// <summary>Sparse semantic editor markers. They do not affect collision by themselves.</summary>
    public List<TerrainMarker>? markers;
    /// <summary>Sparse, collision-neutral decorations painted explicitly by an author.</summary>
    public List<TerrainDecorationPlacement>? decorations;
}

/// <summary>What a room is for.</summary>
public static class DungeonRoomType
{
    public const int Start = 0;
    public const int Combat = 1;
    public const int Boss = 3;
    public const int Objective = 7;
}

/// <summary>How a dungeon is laid out along its forward axis.</summary>
public static class DungeonMode
{
    public const int Finite = 0;
    public const int Endless = 1;
}

/// <summary>How a dungeon is entered.</summary>
public static class DungeonAccess
{
    public const int Open = 0;
}

/// <summary>Which generation algorithm produced the layout (also a coarse visual style hint).</summary>
public static class DungeonStyle
{
    public const int Rooms = 0;
    public const int Caves = 1;
}

/// <summary>An axis-aligned rectangle in integer TILE coordinates.</summary>
public sealed class TileRect
{
    public int tx;
    public int ty;
    public int tw;
    public int th;

    public TileRect() { }

    public TileRect(int tx, int ty, int tw, int th)
    {
        this.tx = tx;
        this.ty = ty;
        this.tw = tw;
        this.th = th;
    }

    public TileRect Clone() => (TileRect)MemberwiseClone();
}

/// <summary>A room/chamber: a rectangle of floor with a type, a world-space centre and its boundary doors.</summary>
public sealed class DungeonRoom
{
    public int id;
    public int type;
    public TileRect rect = new();
    public double cx;
    public double cy;
    public List<int> doorIds = new();
    public double threat;
    public double? encounter;
}

public sealed class DoorTile
{
    public int tx;
    public int ty;

    public DoorTile(int tx, int ty)
    {
        this.tx = tx;
        this.ty = ty;
    }
}

/// <summary>A doorway: the boundary tiles of an opening between a room and a corridor/another room.</summary>
public sealed class DungeonDoor
{
    public int id;
    public double x;
    public double y;
    public int orientation;
    public List<DoorTile> tiles = new();
    public int roomA;
    public int roomB;
    public bool locked;
    public int keyId;
}

/// <summary>The complete, reproducible output of the generator for ONE chunk.</summary>
public sealed class DungeonLayout
{
    /// <summary>Chunk index (endless chunk key; exceeds int32, hence double).</summary>
    public double index;
    /// <summary>The resolved numeric seed actually used.</summary>
    public double seed;
    public int style;
    /// <summary>Biome key for client theming.</summary>
    public string biomeKey = "";
    public int tier;
    /// <summary>World units per tile.</summary>
    public double tileSize;
    public int width;
    public int height;
    /// <summary>World-space top-left corner of tile (0,0).</summary>
    public double originX;
    public double originY;
    /// <summary>`width * height` TileType cells, row-major.</summary>
    public byte[] tiles = System.Array.Empty<byte>();
    /// <summary>Optional `width * height` signed elevation levels, row-major, parallel to <see cref="tiles"/>.</summary>
    public sbyte[]? elevation;
    /// <summary>Optional visual/material layers, parallel to <see cref="tiles"/>.</summary>
    public DungeonTerrainLayers? terrain;
    public List<DungeonRoom> rooms = new();
    public List<DungeonDoor> doors = new();
    public int startRoomId;
    public int bossRoomId;
}

/// <summary>The instance-level descriptor: everything needed to regenerate a world's geometry from scratch.</summary>
public sealed class DungeonDescriptor
{
    public string seed = "";
    public string biomeKey = "";
    public int tier;
    public int mode;
    public int access;
    public int? style;
    public int? generationVersion;
    public bool? local;
}

public static class DungeonTypes
{
    /// <summary>Does this tile block a body's MOVEMENT (rock, river or chasm)?</summary>
    public static bool blocksMovement(int tile) =>
        tile == TileType.Solid || tile == TileType.Water || tile == TileType.Chasm || tile == TileType.Cleft;

    /// <summary>Does this tile block SIGHT / projectiles in the horizontal gameplay plane?</summary>
    public static bool blocksSight(int tile) => tile == TileType.Solid;

    /// <summary>Is this tile walkable ground a body may stand on?</summary>
    public static bool isWalkable(int tile) =>
        tile == TileType.Floor || tile == TileType.Bridge || tile == TileType.Underpass;

    /// <summary>Version of the authored terrain-layer contract carried by generated/editor artifacts.</summary>
    public const int TERRAIN_ARTIFACT_SCHEMA_VERSION = 1;

    /// <summary>Reserved cell value for inheriting the layout's base biome.</summary>
    public const int TERRAIN_THEME_INHERIT = 0xff;

    /// <summary>A byte layer has 255 usable palette entries because 0xff is reserved for inheritance.</summary>
    public const int TERRAIN_THEME_PALETTE_LIMIT = TERRAIN_THEME_INHERIT;

    public static int defaultSurfaceForTile(int tile)
    {
        switch (tile)
        {
            case TileType.Solid:
                return TerrainSurface.Stone;
            case TileType.Water:
                return TerrainSurface.Water;
            case TileType.Bridge:
                return TerrainSurface.Bridge;
            case TileType.Chasm:
                return TerrainSurface.Chasm;
            case TileType.Cleft:
                return TerrainSurface.Cleft;
            case TileType.Underpass:
                return TerrainSurface.Underpass;
            case TileType.Floor:
            default:
                return TerrainSurface.Floor;
        }
    }
}
