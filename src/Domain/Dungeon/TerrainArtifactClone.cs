// Port of packages/shared/src/domain/dungeon/terrainArtifactClone.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;

namespace Fluitown.Domain;

public static class TerrainArtifactClone
{
    // `{ ...room, rect: { ...room.rect }, doorIds: [...room.doorIds] }` — every DungeonRoom field, spelled out.
    private static DungeonRoom cloneRoom(DungeonRoom room)
    {
        return new DungeonRoom
        {
            id = room.id,
            type = room.type,
            rect = room.rect.Clone(),
            cx = room.cx,
            cy = room.cy,
            doorIds = new List<int>(room.doorIds),
            threat = room.threat,
            encounter = room.encounter,
        };
    }

    // `{ ...door, tiles: door.tiles.map((tile) => ({ tx: tile.tx, ty: tile.ty })) }` — every DungeonDoor field.
    private static DungeonDoor cloneDoor(DungeonDoor door)
    {
        return new DungeonDoor
        {
            id = door.id,
            x = door.x,
            y = door.y,
            orientation = door.orientation,
            tiles = door.tiles.map(tile => new DoorTile(tile.tx, tile.ty)),
            roomA = door.roomA,
            roomB = door.roomB,
            locked = door.locked,
            keyId = door.keyId,
        };
    }

    public static TerrainArtifact cloneTerrainArtifactForCompile(TerrainArtifact artifact)
    {
        // `{ ...artifact, <lanes below replaced by copies> }`
        var clone = artifact.Clone();
        clone.baseTiles = artifact.baseTiles.slice();
        clone.elevation = artifact.elevation?.slice();
        clone.surface = artifact.surface?.slice();
        clone.variant = artifact.variant?.slice();
        clone.floorUsage = artifact.floorUsage?.slice();
        clone.themePalette = artifact.themePalette != null ? new List<string>(artifact.themePalette) : null;
        clone.themeIndex = artifact.themeIndex?.slice();
        clone.markers = artifact.markers?.map(marker => marker.Clone());
        clone.decorations = artifact.decorations?.map(decoration => decoration.Clone());
        clone.rooms = artifact.rooms?.map(room => cloneRoom(room));
        clone.doors = artifact.doors?.map(door => cloneDoor(door));
        return clone;
    }
}
