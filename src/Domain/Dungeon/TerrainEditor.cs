// Port of packages/shared/src/domain/dungeon/editor.ts — keep in lockstep with the original.
// createTerrainArtifact, CreateTerrainArtifactOptions and the two document constants live in TerrainEditor.Create.cs.
//
// Porting notes:
// * TS `new Error(message)` is InvalidOperationException(message) with the identical message text; malformed JSON TEXT
//   (JSON.parse's SyntaxError) surfaces as FormatException from the *Text entry points.
// * TerrainArtifactJson / TerrainEditorDocument (the JSON shapes) are System.Text.Json.Nodes.JsonObject: the writers
//   produce exactly the keys JSON.stringify would emit (undefined omitted, integers without a fraction, other numbers
//   in Number::toString form) and the readers accept anything JSON.parse would have produced.
// * `Set<number>` whose iteration order is observable (returned as `[...set]`) is JsSet; pure membership sets are
//   HashSet. Typed-array stores go through Js.U8/Js.I8, and `layer[i] ?? 0` reads through the bounds-checked helpers.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Grid;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Domain.TerrainTerrace;
using static Fluitown.Domain.WorldDecoration;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/// <summary>`TerrainBrushShape = 'circle' | 'square' | 'diamond'`.</summary>
public static class TerrainBrushShape
{
    public const string Circle = "circle";
    public const string Square = "square";
    public const string Diamond = "diamond";
}

/// <summary>C# names for the inline `'all' | 'walkable' | 'blocked'` union of the elevation/smooth edits.</summary>
public static class TerrainEditAffect
{
    public const string All = "all";
    public const string Walkable = "walkable";
}

public sealed class TerrainBrush
{
    /// <summary>Tile footprint diameter. `1` edits only the target cell.</summary>
    public double size = 1;
    /// <summary>A <see cref="TerrainBrushShape"/> value; null means circle.</summary>
    public string? shape;
    /// <summary>Blend elevation/material values toward the target instead of replacing them outright.</summary>
    public bool? soft;
    /// <summary>Multiplier for soft edits, 0..1. Defaults to 1.</summary>
    public double? strength;
}

public sealed class TerrainBrushCell
{
    public int tx;
    public int ty;
    public int index;
    public double weight;
}

public sealed class TerrainRect
{
    public int tx;
    public int ty;
    public int tw;
    public int th;

    public TerrainRect() { }

    public TerrainRect(int tx, int ty, int tw, int th)
    {
        this.tx = tx;
        this.ty = ty;
        this.tw = tw;
        this.th = th;
    }
}

public sealed class TerrainDecorationBrushEdit
{
    public int tx;
    public int ty;
    /// <summary>A TerrainDecorationKind value.</summary>
    public string kind = TerrainDecorationKind.Tree;
    /// <summary>Selected visual theme. Omitted legacy callers preserve biome/cell inheritance.</summary>
    public string? themeKey;
    public TerrainBrush? brush;
}

public sealed class TerrainDecorationEraseEdit
{
    public int tx;
    public int ty;
    public TerrainBrush? brush;
}

/// <summary>
/// One editor gesture's complete terrain intent. `height` is semantic: ordinary terrain uses `0..12`, while
/// Chasm uses its visible negative datum `-8..-5`. The serializer continues to store Chasm's unsigned depth
/// magnitude, so old artifacts remain byte-compatible. `themeKey` is applied to the same footprint as tile
/// and height, making one pointer gesture the complete visual + physical terrain intent.
/// </summary>
public sealed class TerrainPaintBrushEdit
{
    public int tx;
    public int ty;
    /// <summary>A TileType value.</summary>
    public int tile;
    public double height;
    public string themeKey = "";
    public TerrainBrush? brush;
}

/// <summary>Flood-fill counterpart to <see cref="TerrainPaintBrushEdit"/>; the source region is selected by TileType.</summary>
public sealed class TerrainPaintFillEdit
{
    public int tx;
    public int ty;
    /// <summary>A TileType value.</summary>
    public int tile;
    public double height;
    public string themeKey = "";
}

/// <summary>
/// Place one complete depth-tile structure (Cleft or suspension-bridge Underpass) with a single click. These
/// tiles are structures, not materials: the edit owns their shoulders/banks and approach datums as well.
/// </summary>
public sealed class TerrainDepthStructureBrushEdit
{
    public int tx;
    public int ty;
    /// <summary>A <see cref="TerrainDepthStructureKind"/> value.</summary>
    public string kind = TerrainDepthStructureKind.Cleft;
    /// <summary>Omit to read the axis from the terrain around the target cell (<see cref="TerrainPassageAxis"/>).</summary>
    public string? passageAxis;
    /// <summary>Underpass deck length in cells; clamped into the shared span band.</summary>
    public double? span;
    public string? themeKey;
}

public class TerrainEditResult
{
    public List<int> changed = new();
}

public sealed class TerrainDepthStructureEditResult : TerrainEditResult
{
    /// <summary>The structure that was written. Absent when the placement was rejected.</summary>
    public TerrainDepthStructurePlan? plan;
    /// <summary>Author-facing reason nothing was written.</summary>
    public string? rejection;
}

/// <summary>Re-theme terrain cells and their authored dressing without changing physical layout or object identity.</summary>
public sealed class TerrainThemeBrushEdit
{
    public int tx;
    public int ty;
    public string themeKey = "";
    public TerrainBrush? brush;
}

public sealed class TerrainMagicSelectionOptions
{
    /// <summary>Match the authored height as well as the physical tile and resolved theme. Defaults to false.</summary>
    public bool? matchElevation;
}

public sealed class TerrainSmoothEdit
{
    public int tx;
    public int ty;
    public TerrainBrush? brush;
    public double? passes;
    /// <summary>A <see cref="TerrainEditAffect"/> value; null means walkable.</summary>
    public string? affect;
    public double? maxLevel;
    /// <summary>
    /// Smallest tread the smoothed terrain may have, in tiles — see `TERRAIN_TERRACE_QUANTUM`. Defaults to
    /// a 2×2; raise it when an author wants broad shelves instead of steps.
    /// </summary>
    public double? quantum;
}

public sealed class TerrainStamp
{
    public int width;
    public int height;
    public byte[] baseTiles = Array.Empty<byte>();
    public sbyte[]? elevation;
    public byte[]? surface;
    public byte[]? variant;
    public List<string>? themePalette;
    public byte[]? themeIndex;
    /// <summary>Sparse selection mask. Omitted means every stamp cell is active.</summary>
    public byte[]? mask;
    /// <summary>Coordinates are relative to the stamp origin.</summary>
    public List<TerrainDecorationPlacement>? decorations;
}

public sealed class TerrainEditorHeightRange
{
    public int min;
    public int max;
}

public static partial class TerrainEditor
{
    // ── Small JS-semantics helpers ────────────────────────────────────────────────────────────────────

    /// <summary>`layer?.[i] ?? 0` on a typed array.</summary>
    private static int at(sbyte[]? layer, int index) =>
        layer != null && (uint)index < (uint)layer.Length ? layer[index] : 0;

    /// <summary>`layer?.[i] ?? 0` on a typed array.</summary>
    private static int at(byte[]? layer, int index) =>
        layer != null && (uint)index < (uint)layer.Length ? layer[index] : 0;

    /// <summary>ECMAScript WhiteSpace ∪ LineTerminator — what String.prototype.trim strips.</summary>
    private static bool isJsWhiteSpace(char c) =>
        c == (char)0x09 || c == (char)0x0b || c == (char)0x0c || c == (char)0x20 || c == (char)0xa0 || c == (char)0xfeff ||
        c == (char)0x0a || c == (char)0x0d || c == (char)0x2028 || c == (char)0x2029 ||
        CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>`value.trim().length === 0`.</summary>
    private static bool isBlank(string value)
    {
        foreach (char c in value)
            if (!isJsWhiteSpace(c)) return false;
        return true;
    }

    // ── Clones ────────────────────────────────────────────────────────────────────────────────────────

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

    // `{ ...door, tiles: door.tiles.map((t) => ({ ...t })) }` — every DungeonDoor field.
    private static DungeonDoor cloneDoor(DungeonDoor door)
    {
        return new DungeonDoor
        {
            id = door.id,
            x = door.x,
            y = door.y,
            orientation = door.orientation,
            tiles = door.tiles.map(t => new DoorTile(t.tx, t.ty)),
            roomA = door.roomA,
            roomB = door.roomB,
            locked = door.locked,
            keyId = door.keyId,
        };
    }

    private static List<TerrainMarker>? cloneMarkers(IReadOnlyList<TerrainMarker>? markers) =>
        markers?.map(marker => marker.Clone());

    private static List<TerrainDecorationPlacement>? cloneDecorations(IReadOnlyList<TerrainDecorationPlacement>? decorations) =>
        decorations?.map(decoration => decoration.Clone());

    private static List<DungeonRoom>? cloneRooms(IReadOnlyList<DungeonRoom>? rooms) => rooms?.map(cloneRoom);

    private static List<DungeonDoor>? cloneDoors(IReadOnlyList<DungeonDoor>? doors) => doors?.map(cloneDoor);

    public static TerrainArtifact cloneTerrainArtifact(TerrainArtifact artifact)
    {
        // `{ ...artifact, <lanes below replaced by copies> }` — floorUsage stays shared, exactly like the spread.
        var clone = artifact.Clone();
        clone.baseTiles = artifact.baseTiles.slice();
        clone.elevation = artifact.elevation?.slice();
        clone.surface = artifact.surface?.slice();
        clone.variant = artifact.variant?.slice();
        clone.themePalette = artifact.themePalette != null ? new List<string>(artifact.themePalette) : null;
        clone.themeIndex = artifact.themeIndex?.slice();
        clone.markers = cloneMarkers(artifact.markers);
        clone.decorations = cloneDecorations(artifact.decorations);
        clone.rooms = cloneRooms(artifact.rooms);
        clone.doors = cloneDoors(artifact.doors);
        return clone;
    }

    // ── JSON: reading helpers (JsonNode stands in for `unknown`) ──────────────────────────────────────

    /// <summary>`typeof value === 'object' && value !== null` — arrays are records too.</summary>
    private static bool isRecord(JsonNode? value) => value is JsonObject || value is JsonArray;

    /// <summary>
    /// `record[field]` with the undefined/null distinction JS keeps: false when the key is absent (undefined), true with
    /// a null node for an explicit JSON null. An array record has none of the fields the reader asks for.
    /// </summary>
    private static bool field(JsonNode? record, string name, out JsonNode? value)
    {
        if (record is JsonObject obj && obj.TryGetPropertyValue(name, out value)) return true;
        value = null;
        return false;
    }

    private static JsonNode? fieldOrNull(JsonNode? record, string name) => field(record, name, out var value) ? value : null;

    /// <summary>`typeof value === 'number'`, yielding the number JSON.parse would have produced.</summary>
    private static bool jsonNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue json || json.GetValueKind() != JsonValueKind.Number) return false;
        if (json.TryGetValue(out double parsed))
        {
            value = parsed;
            return true;
        }
        // In-memory values of other numeric types, and literals beyond the double range (JSON.parse gives ±Infinity).
        value = double.Parse(json.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>`typeof value === 'string'`.</summary>
    private static bool jsonString(JsonNode? node, out string value)
    {
        value = "";
        if (node is not JsonValue json || json.GetValueKind() != JsonValueKind.String) return false;
        value = json.GetValue<string>();
        return true;
    }

    /// <summary>`Number.isInteger(value)`.</summary>
    private static bool jsonInteger(JsonNode? node, out double value) => jsonNumber(node, out value) && Number.isInteger(value);

    private static double? maybeNumber(JsonNode? value) =>
        jsonNumber(value, out double number) && Number.isFinite(number) ? number : null;

    private static double requiredInt(JsonNode record, string name)
    {
        if (!jsonInteger(fieldOrNull(record, name), out double value))
            throw new InvalidOperationException($"Terrain artifact field \"{name}\" must be an integer.");
        return value;
    }

    private static double? optionalNumber(JsonNode record, string name) => maybeNumber(fieldOrNull(record, name));

    /// <summary>
    /// <see cref="optionalNumber"/> for the fields the C# artifact types as int (style, tier, room ids). An authored
    /// integer passes unchanged; a fractional number (kept verbatim by TS) is truncated.
    /// </summary>
    private static int? optionalInt(JsonNode record, string name)
    {
        double? value = optionalNumber(record, name);
        return value == null ? null : Js.ToInt32(value.Value);
    }

    private static string? optionalString(JsonNode record, string name) =>
        jsonString(fieldOrNull(record, name), out string value) ? value : null;

    private static byte[]? byteArrayFromUnknown(bool present, JsonNode? value, string name, double? expectedLength = null)
    {
        if (!present) return null;
        if (value is not JsonArray array)
            throw new InvalidOperationException($"Terrain artifact field \"{name}\" must be a number array.");
        if (expectedLength != null && array.Count != expectedLength.Value)
            throw new InvalidOperationException(
                $"Terrain artifact field \"{name}\" length must equal {Js.Str(expectedLength.Value)}.");
        var @out = new byte[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            if (!jsonInteger(array[i], out double n) || n < 0 || n > 255)
                throw new InvalidOperationException(
                    $"Terrain artifact field \"{name}\" contains an invalid byte at index {Js.Str(i)}.");
            @out[i] = (byte)n;
        }
        return @out;
    }

    /// <summary>
    /// Read a persisted elevation layer against the selected tile's storage semantics. Chasm is the deliberate
    /// exception: the editor presents a negative shaft height but serializes its positive depth magnitude up to
    /// CHASM_MAX_DEPTH. Treating the whole array as ordinary ground rejected the world's deepest valid
    /// Chasms and made the generator silently retain its default document.
    /// </summary>
    private static sbyte[]? elevationLayerFromUnknown(
        bool present,
        JsonNode? value,
        string name,
        double? expectedLength = null,
        byte[]? baseTiles = null)
    {
        if (!present) return null;
        if (value is not JsonArray array)
            throw new InvalidOperationException($"Terrain artifact field \"{name}\" must be a number array.");
        if (expectedLength != null && array.Count != expectedLength.Value)
            throw new InvalidOperationException(
                $"Terrain artifact field \"{name}\" length must equal {Js.Str(expectedLength.Value)}.");
        var @out = new sbyte[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            int? tile = baseTiles != null && i < baseTiles.Length ? baseTiles[i] : null;
            int min, max;
            if (tile == TileType.Chasm)
            {
                min = CHASM_MIN_DEPTH;
                max = CHASM_MAX_DEPTH;
            }
            else if (tile == null)
            {
                min = TERRAIN_MIN_ELEVATION;
                max = TERRAIN_MAX_ELEVATION;
            }
            else
            {
                var range = terrainEditorHeightRangeForTile(tile.Value);
                min = range.min;
                max = range.max;
            }
            if (!jsonInteger(array[i], out double n) || n < min || n > max)
                throw new InvalidOperationException(
                    $"Terrain artifact field \"{name}\" contains an out-of-domain stored level at index {Js.Str(i)}.");
            @out[i] = (sbyte)n;
        }
        return @out;
    }

    private static List<string>? stringArrayFromUnknown(bool present, JsonNode? value, string name)
    {
        if (!present) return null;
        if (value is not JsonArray array)
            throw new InvalidOperationException($"Terrain artifact field \"{name}\" must be a string array.");
        var @out = new List<string>(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            if (!jsonString(array[index], out string entry))
                throw new InvalidOperationException(
                    $"Terrain artifact field \"{name}\" contains a non-string at index {Js.Str(index)}.");
            @out.Add(entry);
        }
        return @out;
    }

    /// <summary>`entry.themeKey !== undefined && (typeof entry.themeKey !== 'string' || !entry.themeKey.trim())`.</summary>
    private static bool invalidThemeKey(JsonNode entry, out string? themeKey)
    {
        themeKey = null;
        if (!field(entry, "themeKey", out var node)) return false;
        if (!jsonString(node, out string key) || isBlank(key)) return true;
        themeKey = key;
        return false;
    }

    private static List<TerrainDecorationPlacement>? decorationsFromUnknown(bool present, JsonNode? value)
    {
        if (!present) return null;
        if (value is not JsonArray array)
            throw new InvalidOperationException("Terrain artifact field \"decorations\" must be an array.");
        var @out = new List<TerrainDecorationPlacement>(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            var entry = array[index];
            if (!isRecord(entry))
                throw new InvalidOperationException($"Terrain decoration at index {Js.Str(index)} must be an object.");
            string? kind = jsonString(fieldOrNull(entry, "kind"), out string kindValue) ? kindValue : null;
            if (!isTerrainDecorationKind(kind))
                throw new InvalidOperationException($"Terrain decoration at index {Js.Str(index)} has an invalid kind.");
            if (!jsonInteger(fieldOrNull(entry, "tx"), out double tx) || !jsonInteger(fieldOrNull(entry, "ty"), out double ty))
                throw new InvalidOperationException(
                    $"Terrain decoration at index {Js.Str(index)} must have integer coordinates.");
            if (!jsonInteger(fieldOrNull(entry, "seed"), out double seed))
                throw new InvalidOperationException($"Terrain decoration at index {Js.Str(index)} must have an integer seed.");
            if (invalidThemeKey(entry!, out string? themeKey))
                throw new InvalidOperationException($"Terrain decoration at index {Js.Str(index)} has an invalid theme.");
            @out.Add(new TerrainDecorationPlacement
            {
                kind = kind!,
                tx = Js.ToInt32(tx),
                ty = Js.ToInt32(ty),
                seed = seed,
                themeKey = themeKey,
            });
        }
        return @out;
    }

    // `{ ...(m as TerrainMarker) }`, `cloneRoom(r as DungeonRoom)`, `cloneDoor(d as DungeonDoor)`: TS copies these
    // unvalidated. The C# records are typed, so each known field is read when it is a number (or string/boolean) of the
    // right kind and left at its default otherwise.
    private static int intField(JsonNode? record, string name) =>
        jsonNumber(fieldOrNull(record, name), out double value) ? Js.ToInt32(value) : 0;

    private static int? optionalIntField(JsonNode? record, string name) =>
        jsonNumber(fieldOrNull(record, name), out double value) ? Js.ToInt32(value) : null;

    private static double doubleField(JsonNode? record, string name) =>
        jsonNumber(fieldOrNull(record, name), out double value) ? value : 0;

    private static double? optionalDoubleField(JsonNode? record, string name) =>
        jsonNumber(fieldOrNull(record, name), out double value) ? value : null;

    private static List<T> mapJsonArray<T>(JsonArray array, Func<JsonNode?, T> read)
    {
        var list = new List<T>(array.Count);
        foreach (var node in array) list.Add(read(node));
        return list;
    }

    private static TerrainMarker markerFromUnknown(JsonNode? node)
    {
        return new TerrainMarker
        {
            type = intField(node, "type"),
            tx = intField(node, "tx"),
            ty = intField(node, "ty"),
            axis = optionalIntField(node, "axis"),
            length = optionalDoubleField(node, "length"),
            id = jsonString(fieldOrNull(node, "id"), out string id) ? id : null,
            roomId = optionalIntField(node, "roomId"),
        };
    }

    private static DungeonRoom roomFromUnknown(JsonNode? node)
    {
        var rect = fieldOrNull(node, "rect");
        var doorIds = new List<int>();
        if (fieldOrNull(node, "doorIds") is JsonArray ids)
            foreach (var id in ids) doorIds.Add(jsonNumber(id, out double value) ? Js.ToInt32(value) : 0);
        return new DungeonRoom
        {
            id = intField(node, "id"),
            type = intField(node, "type"),
            rect = new TileRect(intField(rect, "tx"), intField(rect, "ty"), intField(rect, "tw"), intField(rect, "th")),
            cx = doubleField(node, "cx"),
            cy = doubleField(node, "cy"),
            doorIds = doorIds,
            threat = doubleField(node, "threat"),
            encounter = optionalDoubleField(node, "encounter"),
        };
    }

    private static DungeonDoor doorFromUnknown(JsonNode? node)
    {
        var tiles = new List<DoorTile>();
        if (fieldOrNull(node, "tiles") is JsonArray cells)
            foreach (var cell in cells) tiles.Add(new DoorTile(intField(cell, "tx"), intField(cell, "ty")));
        var locked = fieldOrNull(node, "locked");
        return new DungeonDoor
        {
            id = intField(node, "id"),
            x = doubleField(node, "x"),
            y = doubleField(node, "y"),
            orientation = intField(node, "orientation"),
            tiles = tiles,
            roomA = intField(node, "roomA"),
            roomB = intField(node, "roomB"),
            locked = locked is JsonValue lockedValue && lockedValue.GetValueKind() == JsonValueKind.True,
            keyId = intField(node, "keyId"),
        };
    }

    // ── JSON: writing helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A JS number as JSON.stringify writes it: non-finite → null, integers without a fraction (and -0 as 0), every
    /// other value in its Number::toString form.
    /// </summary>
    private static JsonNode? jsonNumberNode(double value)
    {
        if (!double.IsFinite(value)) return null;
        if (value == Math.floor(value) && Math.abs(value) < 9007199254740992.0) return JsonValue.Create((long)value);
        using var parsed = JsonDocument.Parse(Js.Str(value));
        return JsonValue.Create(parsed.RootElement.Clone());
    }

    private static void putNumber(JsonObject target, string name, double? value)
    {
        if (value != null) target[name] = jsonNumberNode(value.Value);
    }

    private static void putInt(JsonObject target, string name, int? value)
    {
        if (value != null) target[name] = JsonValue.Create(value.Value);
    }

    private static void putString(JsonObject target, string name, string? value)
    {
        if (value != null) target[name] = JsonValue.Create(value);
    }

    private static JsonArray jsonByteArray(byte[] layer)
    {
        var array = new JsonArray();
        foreach (byte value in layer) array.Add(JsonValue.Create((int)value));
        return array;
    }

    private static JsonArray jsonSignedArray(sbyte[] layer)
    {
        var array = new JsonArray();
        foreach (sbyte value in layer) array.Add(JsonValue.Create((int)value));
        return array;
    }

    private static JsonObject markerToJson(TerrainMarker marker)
    {
        var json = new JsonObject();
        putInt(json, "type", marker.type);
        putInt(json, "tx", marker.tx);
        putInt(json, "ty", marker.ty);
        putString(json, "id", marker.id);
        putInt(json, "axis", marker.axis);
        putNumber(json, "length", marker.length);
        putInt(json, "roomId", marker.roomId);
        return json;
    }

    private static JsonObject decorationToJson(TerrainDecorationPlacement decoration)
    {
        var json = new JsonObject();
        putString(json, "kind", decoration.kind);
        putInt(json, "tx", decoration.tx);
        putInt(json, "ty", decoration.ty);
        putNumber(json, "seed", decoration.seed);
        putString(json, "themeKey", decoration.themeKey);
        putInt(json, "growthStage", decoration.growthStage);
        return json;
    }

    private static JsonObject roomToJson(DungeonRoom room)
    {
        var json = new JsonObject();
        putInt(json, "id", room.id);
        putInt(json, "type", room.type);
        var rect = new JsonObject();
        putInt(rect, "tx", room.rect.tx);
        putInt(rect, "ty", room.rect.ty);
        putInt(rect, "tw", room.rect.tw);
        putInt(rect, "th", room.rect.th);
        json["rect"] = rect;
        putNumber(json, "cx", room.cx);
        putNumber(json, "cy", room.cy);
        var doorIds = new JsonArray();
        foreach (int id in room.doorIds) doorIds.Add(JsonValue.Create(id));
        json["doorIds"] = doorIds;
        putNumber(json, "threat", room.threat);
        putNumber(json, "encounter", room.encounter);
        return json;
    }

    private static JsonObject doorToJson(DungeonDoor door)
    {
        var json = new JsonObject();
        putInt(json, "id", door.id);
        putNumber(json, "x", door.x);
        putNumber(json, "y", door.y);
        putInt(json, "orientation", door.orientation);
        var tiles = new JsonArray();
        foreach (var tile in door.tiles)
        {
            var cell = new JsonObject();
            putInt(cell, "tx", tile.tx);
            putInt(cell, "ty", tile.ty);
            tiles.Add(cell);
        }
        json["tiles"] = tiles;
        putInt(json, "roomA", door.roomA);
        putInt(json, "roomB", door.roomB);
        json["locked"] = JsonValue.Create(door.locked);
        putInt(json, "keyId", door.keyId);
        return json;
    }

    private static JsonArray jsonList<T>(IReadOnlyList<T> items, Func<T, JsonObject> write)
    {
        var array = new JsonArray();
        for (int i = 0; i < items.Count; i++) array.Add(write(items[i]));
        return array;
    }

    // ── JSON: the public format ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// `TerrainArtifactJson` — the artifact with every typed layer as a plain number array, keys in the TS order and
    /// undefined fields omitted (floorUsage is not part of the format).
    /// </summary>
    public static JsonObject terrainArtifactToJson(TerrainArtifact artifact)
    {
        var json = new JsonObject();
        putInt(json, "schemaVersion", artifact.schemaVersion);
        putInt(json, "width", artifact.width);
        putInt(json, "height", artifact.height);
        putNumber(json, "tileSize", artifact.tileSize);
        putNumber(json, "originX", artifact.originX);
        putNumber(json, "originY", artifact.originY);
        putNumber(json, "index", artifact.index);
        putNumber(json, "seed", artifact.seed);
        putInt(json, "style", artifact.style);
        putString(json, "biomeKey", artifact.biomeKey);
        putInt(json, "tier", artifact.tier);
        json["baseTiles"] = jsonByteArray(artifact.baseTiles);
        if (artifact.elevation != null) json["elevation"] = jsonSignedArray(artifact.elevation);
        if (artifact.surface != null) json["surface"] = jsonByteArray(artifact.surface);
        if (artifact.variant != null) json["variant"] = jsonByteArray(artifact.variant);
        if (artifact.themePalette != null)
        {
            var palette = new JsonArray();
            foreach (string key in artifact.themePalette) palette.Add(JsonValue.Create(key));
            json["themePalette"] = palette;
        }
        if (artifact.themeIndex != null) json["themeIndex"] = jsonByteArray(artifact.themeIndex);
        putString(json, "tilesetId", artifact.tilesetId);
        if (artifact.markers != null) json["markers"] = jsonList(artifact.markers, markerToJson);
        if (artifact.decorations != null) json["decorations"] = jsonList(artifact.decorations, decorationToJson);
        if (artifact.rooms != null) json["rooms"] = jsonList(artifact.rooms, roomToJson);
        if (artifact.doors != null) json["doors"] = jsonList(artifact.doors, doorToJson);
        putInt(json, "startRoomId", artifact.startRoomId);
        putInt(json, "bossRoomId", artifact.bossRoomId);
        return json;
    }

    /// <summary>Accepts an editor document (`{ kind: 'fluitown.terrain-artifact', artifact }`) or a bare artifact.</summary>
    public static TerrainArtifact terrainArtifactFromJson(JsonNode? input)
    {
        var source =
            isRecord(input) &&
            jsonString(fieldOrNull(input, "kind"), out string kind) && kind == TERRAIN_EDITOR_DOCUMENT_KIND &&
            isRecord(fieldOrNull(input, "artifact"))
                ? fieldOrNull(input, "artifact")
                : input;
        if (!isRecord(source)) throw new InvalidOperationException("Terrain artifact JSON must be an object.");
        double width = requiredInt(source!, "width");
        double height = requiredInt(source!, "height");
        double count = width * height;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Terrain artifact dimensions must be positive.");
        var baseTiles = byteArrayFromUnknown(field(source, "baseTiles", out var baseTilesNode), baseTilesNode, "baseTiles", count);
        if (baseTiles == null) throw new InvalidOperationException("Terrain artifact field \"baseTiles\" is required.");
        // Object-literal order of the TS reader, so the first failing field reports first.
        int schemaVersion = Js.ToInt32(requiredInt(source!, "schemaVersion"));
        double? tileSize = optionalNumber(source!, "tileSize");
        double? originX = optionalNumber(source!, "originX");
        double? originY = optionalNumber(source!, "originY");
        double? index = optionalNumber(source!, "index");
        double? seed = optionalNumber(source!, "seed");
        int? style = optionalInt(source!, "style");
        string? biomeKey = optionalString(source!, "biomeKey");
        int? tier = optionalInt(source!, "tier");
        var elevation = elevationLayerFromUnknown(field(source, "elevation", out var elevationNode), elevationNode, "elevation", count, baseTiles);
        var surface = byteArrayFromUnknown(field(source, "surface", out var surfaceNode), surfaceNode, "surface", count);
        var variant = byteArrayFromUnknown(field(source, "variant", out var variantNode), variantNode, "variant", count);
        var themePalette = stringArrayFromUnknown(field(source, "themePalette", out var paletteNode), paletteNode, "themePalette");
        var themeIndex = byteArrayFromUnknown(field(source, "themeIndex", out var themeIndexNode), themeIndexNode, "themeIndex", count);
        string? tilesetId = optionalString(source!, "tilesetId");
        var markers = fieldOrNull(source, "markers") is JsonArray markerArray
            ? mapJsonArray(markerArray, markerFromUnknown)
            : null;
        var decorations = decorationsFromUnknown(field(source, "decorations", out var decorationsNode), decorationsNode);
        var rooms = fieldOrNull(source, "rooms") is JsonArray roomArray ? mapJsonArray(roomArray, roomFromUnknown) : null;
        var doors = fieldOrNull(source, "doors") is JsonArray doorArray ? mapJsonArray(doorArray, doorFromUnknown) : null;
        return new TerrainArtifact
        {
            schemaVersion = schemaVersion,
            width = (int)width,
            height = (int)height,
            tileSize = tileSize,
            originX = originX,
            originY = originY,
            index = index,
            seed = seed,
            style = style,
            biomeKey = biomeKey,
            tier = tier,
            baseTiles = baseTiles,
            elevation = elevation,
            surface = surface,
            variant = variant,
            themePalette = themePalette,
            themeIndex = themeIndex,
            tilesetId = tilesetId,
            markers = markers,
            decorations = decorations,
            rooms = rooms,
            doors = doors,
            startRoomId = optionalInt(source!, "startRoomId"),
            bossRoomId = optionalInt(source!, "bossRoomId"),
        };
    }

    /// <summary>
    /// `TerrainEditorDocument`: `{ kind, version, ...extra, artifact }`. The `extra` record of the TS signature is the
    /// three optional parameters (null = omitted).
    /// </summary>
    public static JsonObject terrainArtifactToEditorDocument(
        TerrainArtifact artifact,
        string? name = null,
        string? notes = null,
        string? exportedAt = null)
    {
        var document = new JsonObject
        {
            ["kind"] = TERRAIN_EDITOR_DOCUMENT_KIND,
            ["version"] = TERRAIN_EDITOR_DOCUMENT_VERSION,
        };
        putString(document, "name", name);
        putString(document, "notes", notes);
        putString(document, "exportedAt", exportedAt);
        document["artifact"] = terrainArtifactToJson(artifact);
        return document;
    }

    public static TerrainArtifact terrainArtifactFromEditorDocument(JsonNode? input)
    {
        return terrainArtifactFromJson(input);
    }

    // ── JSON text entry points (C# conveniences over JSON.stringify / JSON.parse) ─────────────────────

    private static readonly JsonDocumentOptions DOCUMENT_PARSE_OPTIONS = new() { MaxDepth = 256 };

    /// <summary>
    /// `JSON.stringify(value, null, pretty ? 2 : undefined)` over a JsonNode tree, byte for byte: two-space indentation
    /// with "\n", `"key": value`, empty containers as `[]`/`{}`, numbers in Number::toString form (non-finite → null) and
    /// only quotes, backslashes, C0 controls and lone surrogates escaped. (System.Text.Json's writer differs in the
    /// newline, in escaping C1 controls and in number formatting; this keeps exported files identical to the web tool's.)
    /// </summary>
    public static string stringifyTerrainEditorJson(JsonNode? node, bool pretty = true)
    {
        var sb = new StringBuilder();
        writeJsonText(sb, node, pretty, "");
        return sb.ToString();
    }

    private static void writeJsonText(StringBuilder sb, JsonNode? node, bool pretty, string indent)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
            {
                if (obj.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                string inner = pretty ? indent + "  " : indent;
                sb.Append('{');
                bool first = true;
                foreach (var entry in obj)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (pretty) sb.Append('\n').Append(inner);
                    writeJsonString(sb, entry.Key);
                    sb.Append(pretty ? ": " : ":");
                    writeJsonText(sb, entry.Value, pretty, inner);
                }
                if (pretty) sb.Append('\n').Append(indent);
                sb.Append('}');
                return;
            }
            case JsonArray array:
            {
                if (array.Count == 0)
                {
                    sb.Append("[]");
                    return;
                }
                string inner = pretty ? indent + "  " : indent;
                sb.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (pretty) sb.Append('\n').Append(inner);
                    writeJsonText(sb, array[i], pretty, inner);
                }
                if (pretty) sb.Append('\n').Append(indent);
                sb.Append(']');
                return;
            }
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        writeJsonString(sb, value.GetValue<string>());
                        return;
                    case JsonValueKind.True:
                        sb.Append("true");
                        return;
                    case JsonValueKind.False:
                        sb.Append("false");
                        return;
                    case JsonValueKind.Number:
                        if (value.TryGetValue(out int integer))
                        {
                            // Fast path for the byte layers (and `-0`, which Int32 parsing already reads as 0).
                            sb.Append(integer.ToString(CultureInfo.InvariantCulture));
                            return;
                        }
                        jsonNumber(value, out double number);
                        sb.Append(double.IsFinite(number) ? Js.Str(number) : "null");
                        return;
                    default:
                        sb.Append("null");
                        return;
                }
        }
    }

    /// <summary>ECMAScript QuoteJSONString (well-formed JSON.stringify).</summary>
    private static void writeJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"':
                    sb.Append('\\').Append('"');
                    break;
                case '\\':
                    sb.Append('\\').Append('\\');
                    break;
                case '\b':
                    sb.Append('\\').Append('b');
                    break;
                case '\f':
                    sb.Append('\\').Append('f');
                    break;
                case '\n':
                    sb.Append('\\').Append('n');
                    break;
                case '\r':
                    sb.Append('\\').Append('r');
                    break;
                case '\t':
                    sb.Append('\\').Append('t');
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append('\\').Append('u').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        sb.Append(c).Append(value[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        sb.Append('\\').Append('u').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// `JSON.stringify(terrainArtifactToEditorDocument(artifact, { name, notes, exportedAt }), null, pretty ? 2 : undefined)`
    /// — the file the web tool exports (its Export JSON button writes the pretty form with `name` and `exportedAt`).
    /// </summary>
    public static string terrainEditorDocumentToJsonText(
        TerrainArtifact artifact,
        string? name = null,
        string? notes = null,
        bool pretty = true,
        string? exportedAt = null)
    {
        return stringifyTerrainEditorJson(terrainArtifactToEditorDocument(artifact, name, notes, exportedAt), pretty);
    }

    /// <summary>`JSON.parse`, with its SyntaxError surfacing as <see cref="FormatException"/>.</summary>
    public static JsonNode? parseTerrainEditorJsonText(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: DOCUMENT_PARSE_OPTIONS);
        }
        catch (JsonException error)
        {
            throw new FormatException($"Terrain artifact JSON is not valid JSON: {error.Message}", error);
        }
    }

    /// <summary>As <c>terrainArtifactFromEditorDocumentText()</c>, also returning the document `name` (null for a bare artifact).</summary>
    public static TerrainArtifact terrainArtifactFromEditorDocumentText(string json, out string? name)
    {
        return terrainArtifactFromEditorDocumentText(json, out name, out _);
    }

    /// <summary>As <c>terrainArtifactFromEditorDocumentText()</c>, also returning the document `name` and `notes`.</summary>
    public static TerrainArtifact terrainArtifactFromEditorDocumentText(string json, out string? name, out string? notes)
    {
        var input = parseTerrainEditorJsonText(json);
        var artifact = terrainArtifactFromEditorDocument(input);
        bool isDocument =
            isRecord(input) &&
            jsonString(fieldOrNull(input, "kind"), out string kind) && kind == TERRAIN_EDITOR_DOCUMENT_KIND &&
            isRecord(fieldOrNull(input, "artifact"));
        name = isDocument && jsonString(fieldOrNull(input, "name"), out string documentName) ? documentName : null;
        notes = isDocument && jsonString(fieldOrNull(input, "notes"), out string documentNotes) ? documentNotes : null;
        return artifact;
    }

    // ── Layout drafting and layer materialisation ─────────────────────────────────────────────────────

    public static DungeonLayout draftDungeonLayoutFromTerrainArtifact(TerrainArtifact artifact)
    {
        double tileSize = artifact.tileSize ?? TILE_SIZE;
        return new DungeonLayout
        {
            index = artifact.index ?? 0,
            seed = artifact.seed ?? 0,
            style = artifact.style ?? 0,
            biomeKey = artifact.biomeKey ?? "mountain",
            tier = artifact.tier ?? 1,
            tileSize = tileSize,
            width = artifact.width,
            height = artifact.height,
            originX = artifact.originX ?? -(artifact.width * tileSize) / 2,
            originY = artifact.originY ?? -(artifact.height * tileSize) / 2,
            tiles = artifact.baseTiles.slice(),
            elevation = artifact.elevation?.slice(),
            terrain = new DungeonTerrainLayers
            {
                schemaVersion = artifact.schemaVersion,
                tilesetId = artifact.tilesetId,
                surface = artifact.surface?.slice(),
                variant = artifact.variant?.slice(),
                themePalette = artifact.themePalette != null ? new List<string>(artifact.themePalette) : null,
                themeIndex = artifact.themeIndex?.slice(),
                markers = cloneMarkers(artifact.markers),
                decorations = cloneDecorations(artifact.decorations),
            },
            rooms = cloneRooms(artifact.rooms) ?? new List<DungeonRoom>(),
            doors = cloneDoors(artifact.doors) ?? new List<DungeonDoor>(),
            startRoomId = artifact.startRoomId ?? -1,
            bossRoomId = artifact.bossRoomId ?? artifact.startRoomId ?? -1,
        };
    }

    public static TerrainArtifact ensureTerrainEditorLayers(TerrainArtifact artifact)
    {
        int count = artifact.width * artifact.height;
        if (artifact.elevation == null || artifact.elevation.Length != count)
            artifact.elevation = new sbyte[count];
        if (artifact.surface == null || artifact.surface.Length != count)
            artifact.surface = new byte[count];
        if (artifact.variant == null || artifact.variant.Length != count)
            artifact.variant = new byte[count];
        artifact.schemaVersion = TERRAIN_ARTIFACT_SCHEMA_VERSION;
        return artifact;
    }

    // ── Themes ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lazily materializes the authored theme layer; old artifacts remain compact until a theme is painted.</summary>
    public static byte[] ensureTerrainThemeLayer(TerrainArtifact artifact)
    {
        int count = artifact.width * artifact.height;
        if (artifact.themeIndex == null || artifact.themeIndex.Length != count)
        {
            artifact.themeIndex = new byte[count];
            Array.Fill(artifact.themeIndex, (byte)TERRAIN_THEME_INHERIT);
        }
        artifact.themePalette ??= new List<string>();
        return artifact.themeIndex;
    }

    /// <summary>Returns (and, if needed, allocates) the byte index for a stable registry theme key.</summary>
    public static int terrainThemePaletteIndex(TerrainArtifact artifact, string themeKey)
    {
        if (isBlank(themeKey)) throw new InvalidOperationException("Terrain theme key must not be blank.");
        artifact.themePalette ??= new List<string>();
        int existing = artifact.themePalette.IndexOf(themeKey);
        if (existing >= 0) return existing;
        if (artifact.themePalette.Count >= TERRAIN_THEME_PALETTE_LIMIT)
            throw new InvalidOperationException(
                $"Terrain theme palette is limited to {Js.Str(TERRAIN_THEME_PALETTE_LIMIT)} entries.");
        artifact.themePalette.push(themeKey);
        return artifact.themePalette.Count - 1;
    }

    /// <summary>Resolves a cell's explicit theme, falling back to the artifact base biome for legacy/unpainted cells.</summary>
    public static string terrainThemeKeyAt(TerrainArtifact artifact, int index)
    {
        int paletteIndex = artifact.themeIndex != null && (uint)index < (uint)artifact.themeIndex.Length
            ? artifact.themeIndex[index]
            : TERRAIN_THEME_INHERIT;
        if (paletteIndex == TERRAIN_THEME_INHERIT) return artifact.biomeKey ?? "mountain";
        var palette = artifact.themePalette;
        return (palette != null && paletteIndex < palette.Count ? palette[paletteIndex] : null)
            ?? artifact.biomeKey ?? "mountain";
    }

    private static bool setTerrainThemeAt(TerrainArtifact artifact, int index, int paletteIndex)
    {
        var themeIndex = ensureTerrainThemeLayer(artifact);
        if (themeIndex[index] == paletteIndex) return false;
        themeIndex[index] = Js.U8(paletteIndex);
        return true;
    }

    // ── Brushes ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>`Required&lt;TerrainBrush&gt;`. Size stays a double so a NaN size yields no cells, as in TS.</summary>
    private readonly struct NormalizedBrush
    {
        public readonly double size;
        public readonly string shape;
        public readonly bool soft;
        public readonly double strength;

        public NormalizedBrush(double size, string shape, bool soft, double strength)
        {
            this.size = size;
            this.shape = shape;
            this.soft = soft;
            this.strength = strength;
        }
    }

    private static NormalizedBrush normalizedBrush(TerrainBrush? brush)
    {
        return new NormalizedBrush(
            Math.max(1, Math.floor(brush?.size ?? 1)),
            brush?.shape ?? TerrainBrushShape.Circle,
            brush?.soft ?? false,
            Math.max(0, Math.min(1, brush?.strength ?? 1)));
    }

    public static List<TerrainBrushCell> terrainBrushCells(int width, int height, int cx, int cy, TerrainBrush? brush = null)
    {
        var b = normalizedBrush(brush);
        double half = (b.size - 1) / 2;
        double reachValue = Math.ceil(half);
        var @out = new List<TerrainBrushCell>();
        if (double.IsNaN(reachValue)) return @out; // `dy <= NaN` never holds
        int reach = (int)reachValue;
        for (int dy = -reach; dy <= reach; dy++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                int tx = cx + dx;
                int ty = cy + dy;
                if (!inBounds(width, height, tx, ty)) continue;
                double ax = Math.abs(dx);
                double ay = Math.abs(dy);
                bool inside;
                double metric;
                if (b.shape == TerrainBrushShape.Square)
                {
                    metric = Math.max(ax, ay);
                    inside = metric <= half + 0.001;
                }
                else if (b.shape == TerrainBrushShape.Diamond)
                {
                    metric = ax + ay;
                    inside = metric <= half + 0.001;
                }
                else
                {
                    metric = Math.hypot(dx, dy);
                    inside = metric <= half + 0.001;
                }
                if (!inside) continue;
                double falloff = half <= 0 ? 1 : Math.max(0, 1 - metric / Math.max(half, 0.001));
                @out.push(new TerrainBrushCell
                {
                    tx = tx,
                    ty = ty,
                    index = tileIndex(width, tx, ty),
                    weight = b.soft ? Math.max(0.05, falloff) * b.strength : 1,
                });
            }
        }
        return @out;
    }

    private static int decorationSeed(TerrainArtifact artifact, int tx, int ty, string kind)
    {
        int seed = Js.ToInt32(artifact.seed ?? 0);
        seed = Math.imul(seed ^ Math.imul(tx + 1, 0x45d9f3b), 0x119de1f3);
        seed = Math.imul(seed ^ Math.imul(ty + 1, 0x27d4eb2d), 0x3449f245);
        for (int i = 0; i < kind.Length; i++) seed = Math.imul(seed ^ kind[i], 0x01000193);
        return seed;
    }

    private static void sortDecorations(List<TerrainDecorationPlacement> decorations)
    {
        // `a.ty - b.ty || a.tx - b.tx || a.kind.localeCompare(b.kind)` — kinds are lowercase ASCII, where
        // localeCompare and ordinal comparison agree.
        decorations.sort((a, b) =>
            a.ty != b.ty ? a.ty - b.ty : a.tx != b.tx ? a.tx - b.tx : string.CompareOrdinal(a.kind, b.kind));
    }

    private static bool removeDecorationAt(TerrainArtifact artifact, int tx, int ty)
    {
        var decorations = artifact.decorations;
        if (decorations == null || decorations.Count == 0) return false;
        int index = decorations.findIndex(decoration => decoration.tx == tx && decoration.ty == ty);
        if (index < 0) return false;
        decorations.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Drop authored decorations whose cell no longer offers the ground they need.
    ///
    /// Terrain is painted after dressing at least as often as before it, so carving a ravine or flooding a meadow
    /// has to take its props with it — otherwise a whole grove keeps standing in the void it was cut out from.
    /// Every tile-mutating edit calls this once over its own changed set, which keeps the sweep linear in the
    /// authored decoration count instead of quadratic in a large fill.
    /// </summary>
    private static void pruneDecorationsForTiles(TerrainArtifact artifact, IEnumerable<int> changed)
    {
        var decorations = artifact.decorations;
        if (decorations == null || decorations.Count == 0) return;
        var affected = changed as HashSet<int> ?? new HashSet<int>(changed);
        if (affected.Count == 0) return;
        artifact.decorations = decorations.filter(decoration =>
        {
            int index = tileIndex(artifact.width, decoration.tx, decoration.ty);
            if (!affected.Contains(index)) return true;
            return terrainTileAcceptsDecoration(decoration.kind, at(artifact.baseTiles, index));
        });
    }

    private static void pushChanged(List<int> changed, HashSet<int> seen, int index)
    {
        if (!seen.Add(index)) return;
        changed.push(index);
    }

    /// <summary>
    /// Paint one stable decoration per affected cell, replacing an existing authored decoration there.
    ///
    /// A brush may sweep freely across a ravine or a river: cells that cannot carry the selected kind are simply
    /// skipped, so an author paints a tree line along a cliff edge in one stroke without seeding the void.
    /// </summary>
    public static TerrainEditResult applyTerrainDecorationBrush(TerrainArtifact artifact, TerrainDecorationBrushEdit edit)
    {
        if (Js.Truthy(edit.themeKey)) terrainThemePaletteIndex(artifact, edit.themeKey!);
        var decorations = artifact.decorations ??= new List<TerrainDecorationPlacement>();
        var changed = new List<int>();
        var seen = new HashSet<int>();
        foreach (var cell in terrainBrushCells(artifact.width, artifact.height, edit.tx, edit.ty, edit.brush))
        {
            if (!terrainTileAcceptsDecoration(edit.kind, artifact.baseTiles[cell.index])) continue;
            int existingIndex = decorations.findIndex(decoration => decoration.tx == cell.tx && decoration.ty == cell.ty);
            int seed = decorationSeed(artifact, cell.tx, cell.ty, edit.kind);
            var existing = existingIndex >= 0 ? decorations[existingIndex] : null;
            if (existing?.kind == edit.kind && existing.seed == seed && existing.themeKey == edit.themeKey) continue;
            var next = new TerrainDecorationPlacement
            {
                kind = edit.kind,
                tx = cell.tx,
                ty = cell.ty,
                seed = seed,
                themeKey = Js.Truthy(edit.themeKey) ? edit.themeKey : null,
            };
            if (existingIndex >= 0) decorations[existingIndex] = next;
            else decorations.push(next);
            pushChanged(changed, seen, cell.index);
        }
        sortDecorations(decorations);
        return new TerrainEditResult { changed = changed };
    }

    /// <summary>Remove authored decorations from the affected brush footprint without changing terrain.</summary>
    public static TerrainEditResult eraseTerrainDecorationBrush(TerrainArtifact artifact, TerrainDecorationEraseEdit edit)
    {
        var decorations = artifact.decorations;
        if (decorations == null || decorations.Count == 0) return new TerrainEditResult();
        var cells = terrainBrushCells(artifact.width, artifact.height, edit.tx, edit.ty, edit.brush);
        var selected = new HashSet<int>();
        foreach (var cell in cells) selected.Add(cell.index);
        var changed = new JsSet<int>();
        artifact.decorations = decorations.filter(decoration =>
        {
            int index = tileIndex(artifact.width, decoration.tx, decoration.ty);
            if (!selected.Contains(index)) return true;
            changed.add(index);
            return false;
        });
        return new TerrainEditResult { changed = changed.ToList() };
    }

    /// <summary>
    /// Clamp an authored level into a SIGNED range. The byte clamp beside it cannot serve here: it pins its
    /// floor at zero, which silently turned every sub-zero datum into ground the moment water was allowed to
    /// sit below it.
    /// </summary>
    private static int clampLevel(double value, double min, double max)
    {
        if (!Number.isFinite(value)) return (int)Math.max(min, Math.min(max, 0));
        return (int)Math.max(min, Math.min(max, Math.round(value)));
    }

    /// <summary>
    /// Semantic height range exposed by terrain-authoring UIs for a selected tile type.
    ///
    /// Three ranges, because three tile kinds genuinely occupy different parts of the vertical domain:
    /// Chasm carves the negative half, Water spans the WHOLE domain (a summit tarn and a flooded canyon floor
    /// are the same tile), and everything that stands on the ground occupies the signed −25..+25 band. Any authoring UI
    /// asks this function instead of stating bounds of its own — that is the entire reason it is exported.
    /// </summary>
    public static TerrainEditorHeightRange terrainEditorHeightRangeForTile(int tile)
    {
        if (tile == TileType.Chasm) return new TerrainEditorHeightRange { min = -CHASM_MAX_DEPTH, max = -CHASM_MIN_DEPTH };
        // Water has its own floor, and it is NOT the domain floor: a canyon may be cut deeper than any water
        // table so a river arriving at its rim always has somewhere to fall.
        if (tile == TileType.Water)
            return new TerrainEditorHeightRange { min = WATER_MIN_STORED_LEVEL, max = WATER_MAX_STORED_LEVEL };
        return new TerrainEditorHeightRange { min = TERRAIN_MIN_GROUND_ELEVATION, max = TERRAIN_MAX_ELEVATION };
    }

    /// <summary>Convert a serialized elevation level into the signed value an editor presents to its author.</summary>
    public static int terrainEditorHeightFromStored(int tile, double storedElevation)
    {
        if (tile == TileType.Chasm) return -(int)chasmDepthFromStored(storedElevation);
        var range = terrainEditorHeightRangeForTile(tile);
        return clampLevel(storedElevation, range.min, range.max);
    }

    /// <summary>Convert an editor's semantic height back into the shared stored terrain level.</summary>
    public static int terrainStoredElevationFromEditorHeight(int tile, double editorHeight)
    {
        if (tile == TileType.Chasm)
        {
            if (!Number.isFinite(editorHeight)) return CHASM_DEFAULT_DEPTH;
            return (int)Math.max(CHASM_MIN_DEPTH, Math.min(CHASM_MAX_DEPTH, Math.abs(Math.round(editorHeight))));
        }
        var range = terrainEditorHeightRangeForTile(tile);
        return clampLevel(editorHeight, range.min, range.max);
    }

    private static bool canAffect(TerrainArtifact artifact, int index, string affect)
    {
        if (affect == TerrainEditAffect.All) return true;
        bool walk = isWalkable(at(artifact.baseTiles, index));
        return affect == TerrainEditAffect.Walkable ? walk : !walk;
    }

    /// <summary>
    /// Paint physical tile type and semantic height as one edit. New tile cells take the requested height
    /// immediately; a soft brush only feathers the height of cells that already had the selected tile type.
    /// This keeps every freshly painted Chasm byte valid (`5..8`) even at a brush's low-weight fringe.
    /// </summary>
    public static TerrainEditResult applyTerrainPaintBrush(TerrainArtifact artifact, TerrainPaintBrushEdit edit)
    {
        ensureTerrainEditorLayers(artifact);
        var changed = new List<int>();
        var seen = new HashSet<int>();
        var elevation = artifact.elevation!;
        int targetStored = terrainStoredElevationFromEditorHeight(edit.tile, edit.height);
        var cells = terrainBrushCells(artifact.width, artifact.height, edit.tx, edit.ty, edit.brush);
        if (cells.Count == 0) return new TerrainEditResult { changed = changed };
        int themePaletteIndex = terrainThemePaletteIndex(artifact, edit.themeKey);

        foreach (var cell in cells)
        {
            int previousTile = artifact.baseTiles[cell.index];
            bool any = false;
            if (previousTile != edit.tile)
            {
                artifact.baseTiles[cell.index] = Js.U8(edit.tile);
                artifact.surface![cell.index] = TerrainSurface.Auto;
                artifact.variant![cell.index] = 0;
                any = true;
            }

            double currentStored =
                previousTile == edit.tile
                    ? terrainStoredElevationFromEditorHeight(
                        edit.tile,
                        terrainEditorHeightFromStored(edit.tile, at(elevation, cell.index)))
                    : targetStored;
            double blended =
                previousTile != edit.tile || cell.weight >= 1
                    ? targetStored
                    : currentStored + (targetStored - currentStored) * cell.weight;
            int nextStored = terrainStoredElevationFromEditorHeight(edit.tile, blended);
            if (elevation[cell.index] != nextStored)
            {
                elevation[cell.index] = Js.I8(nextStored);
                any = true;
            }
            if (setTerrainThemeAt(artifact, cell.index, themePaletteIndex)) any = true;
            if (any) pushChanged(changed, seen, cell.index);
        }
        pruneDecorationsForTiles(artifact, seen);
        return new TerrainEditResult { changed = changed };
    }

    /// <summary>
    /// Place one complete depth-tile structure — a Cleft or a suspension-bridge Underpass — in a single gesture.
    ///
    /// Neither tile is paintable cell by cell: both carry a multi-cell topology contract, and a lone cell of either
    /// one is an authoring error the renderer can only show as a stray wall block or as nothing at all. The
    /// placement is planned and proven by planTerrainDepthStructure against the shared profile predicates
    /// before anything is written, so a successful edit is valid terrain by construction and a rejected one leaves
    /// the artifact untouched with an author-facing reason.
    /// </summary>
    public static TerrainDepthStructureEditResult applyTerrainDepthStructureBrush(
        TerrainArtifact artifact,
        TerrainDepthStructureBrushEdit edit)
    {
        ensureTerrainEditorLayers(artifact);
        var planned = TerrainDepthStructures.planTerrainDepthStructure(
            artifact.baseTiles,
            artifact.elevation!,
            artifact.width,
            artifact.height,
            edit.kind,
            edit.tx,
            edit.ty,
            new TerrainDepthStructureOptions { passageAxis = edit.passageAxis, span = edit.span });
        if (planned.plan == null)
            return new TerrainDepthStructureEditResult { changed = new List<int>(), rejection = planned.rejection!.reason };

        int themePaletteIndex = Js.Truthy(edit.themeKey) ? terrainThemePaletteIndex(artifact, edit.themeKey!) : -1;
        var changed = new List<int>();
        var seen = new HashSet<int>();
        foreach (var cell in planned.plan.cells)
        {
            bool any = false;
            if (artifact.baseTiles[cell.index] != cell.tile)
            {
                artifact.baseTiles[cell.index] = Js.U8(cell.tile);
                artifact.surface![cell.index] = TerrainSurface.Auto;
                artifact.variant![cell.index] = 0;
                any = true;
            }
            if (artifact.elevation![cell.index] != cell.elevation)
            {
                artifact.elevation![cell.index] = Js.I8(cell.elevation);
                any = true;
            }
            if (themePaletteIndex >= 0 && setTerrainThemeAt(artifact, cell.index, themePaletteIndex))
                any = true;
            if (any) pushChanged(changed, seen, cell.index);
        }
        pruneDecorationsForTiles(artifact, seen);
        return new TerrainDepthStructureEditResult { changed = changed, plan = planned.plan };
    }

    /// <summary>
    /// Paint the complete visual theme: dense terrain materials plus authored decoration dressing.
    /// Physical tiles, elevation, surface/variant values, object kinds, positions, seeds, and collision stay intact.
    /// </summary>
    public static TerrainEditResult applyTerrainThemeBrush(TerrainArtifact artifact, TerrainThemeBrushEdit edit)
    {
        int paletteIndex = terrainThemePaletteIndex(artifact, edit.themeKey);
        var changed = new List<int>();
        var seen = new HashSet<int>();
        var cells = terrainBrushCells(artifact.width, artifact.height, edit.tx, edit.ty, edit.brush);
        var affected = new HashSet<int>();
        foreach (var cell in cells) affected.Add(cell.index);
        foreach (var cell in cells)
        {
            if (setTerrainThemeAt(artifact, cell.index, paletteIndex))
                pushChanged(changed, seen, cell.index);
        }
        for (int index = 0; index < (artifact.decorations?.Count ?? 0); index++)
        {
            var decoration = artifact.decorations![index];
            int cellIndex = tileIndex(artifact.width, decoration.tx, decoration.ty);
            if (!affected.Contains(cellIndex) || decoration.themeKey == edit.themeKey) continue;
            var rethemed = decoration.Clone();
            rethemed.themeKey = edit.themeKey;
            artifact.decorations![index] = rethemed;
            pushChanged(changed, seen, cellIndex);
        }
        return new TerrainEditResult { changed = changed };
    }

    /// <summary>
    /// Photoshop-style contiguous selection for the terrain domain. A cell belongs to the same visual island when
    /// its physical tile and resolved theme match the seed; height can optionally become part of the tolerance.
    /// </summary>
    public static List<int> terrainMagicSelection(
        TerrainArtifact artifact,
        int tx,
        int ty,
        TerrainMagicSelectionOptions? options = null)
    {
        options ??= new TerrainMagicSelectionOptions();
        if (!inBounds(artifact.width, artifact.height, tx, ty)) return new List<int>();
        int start = tileIndex(artifact.width, tx, ty);
        int targetTile = artifact.baseTiles[start];
        string targetTheme = terrainThemeKeyAt(artifact, start);
        int targetElevation = at(artifact.elevation, start);
        var seen = new byte[artifact.width * artifact.height];
        var selected = new List<int>();
        var stack = new List<int> { tx, ty };
        seen[start] = 1;
        while (stack.Count > 0)
        {
            int cy = stack.pop();
            int cx = stack.pop();
            int index = tileIndex(artifact.width, cx, cy);
            selected.push(index);
            for (int n = 0; n < 4; n++)
            {
                int nx = n == 0 ? cx + 1 : n == 1 ? cx - 1 : cx;
                int ny = n == 2 ? cy + 1 : n == 3 ? cy - 1 : cy;
                if (!inBounds(artifact.width, artifact.height, nx, ny)) continue;
                int next = tileIndex(artifact.width, nx, ny);
                if (seen[next] != 0) continue;
                seen[next] = 1;
                if (artifact.baseTiles[next] != targetTile) continue;
                if (terrainThemeKeyAt(artifact, next) != targetTheme) continue;
                if (options.matchElevation == true && at(artifact.elevation, next) != targetElevation) continue;
                stack.push(nx, ny);
            }
        }
        return selected;
    }

    // ── Terrace smoothing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Blend amount the smooth tool gives one footprint cell.
    ///
    /// Unlike the value brushes, smoothing honours Strength for HARD brushes too: "how far do I pull this terrace
    /// toward its surroundings" is the only dial this tool has, and a hard brush that ignored it would leave the
    /// slider dead. Soft footprint weights already carry the strength, so they pass through untouched.
    /// </summary>
    private static double smoothBlendWeight(TerrainBrushCell cell, NormalizedBrush brush)
    {
        return brush.soft ? cell.weight : cell.weight * brush.strength;
    }

    /// <summary>
    /// The terrace blocks one smooth gesture will edit, in the order it will edit them.
    ///
    /// Exported because an authoring cursor must be able to draw *exactly* what the tool is about to change: the
    /// smooth tool edits whole `TERRAIN_TERRACE_QUANTUM` blocks, so its reach is not the raw brush footprint,
    /// and a preview that redrew the circle would lie about it.
    /// </summary>
    public static List<TerrainTerraceBlock> terrainSmoothBlocks(TerrainArtifact artifact, TerrainSmoothEdit edit)
    {
        string affect = edit.affect ?? TerrainEditAffect.Walkable;
        var brush = normalizedBrush(edit.brush);
        var footprint = terrainBrushCells(artifact.width, artifact.height, edit.tx, edit.ty, edit.brush)
            .map(cell => new TerraceFootprintCell { tx = cell.tx, ty = cell.ty, weight = smoothBlendWeight(cell, brush) });
        return terrainTerraceBlocks(artifact.width, artifact.height, footprint, normalizedTerraceQuantum(edit.quantum))
            .filter(block => block.cells.some(index => canAffect(artifact, index, affect)));
    }

    /// <summary>Every cell one smooth gesture may write — the flat form of <see cref="terrainSmoothBlocks"/>, for cursors.</summary>
    public static List<int> terrainSmoothCells(TerrainArtifact artifact, TerrainSmoothEdit edit)
    {
        string affect = edit.affect ?? TerrainEditAffect.Walkable;
        var @out = new List<int>();
        foreach (var block in terrainSmoothBlocks(artifact, edit))
            foreach (int index in block.cells)
                if (canAffect(artifact, index, affect)) @out.push(index);
        return @out;
    }

    private sealed class TerraceBlockField
    {
        public int across;
        public int down;
        /// <summary>A Float32Array: every stored mean is rounded to single precision, exactly like the original.</summary>
        public float[] mean = Array.Empty<float>();
        /// <summary>0 = not read yet, 1 = mean is valid, 2 = the block holds nothing this gesture may touch.</summary>
        public byte[] state = Array.Empty<byte>();
    }

    /// <summary>Lazily filled lattice-resolution view of the elevation field for one smoothing pass.</summary>
    private static TerraceBlockField createTerraceBlockField(int width, int height, int quantum)
    {
        int across = (int)Math.max(1, Math.floor((double)width / quantum));
        int down = (int)Math.max(1, Math.floor((double)height / quantum));
        return new TerraceBlockField
        {
            across = across,
            down = down,
            mean = new float[across * down],
            state = new byte[across * down],
        };
    }

    /// <summary>
    /// Mean level of one lattice block, over the cells the gesture is allowed to touch.
    ///
    /// The whole operator reads and writes at BLOCK resolution: the terrace lattice is the field smoothing works on,
    /// and tiles only exist below it. Cells the gesture may not touch are not read either — averaging a chasm floor
    /// or a water table into walkable ground would be a different rule wearing this one's name. Filled lazily and
    /// kept for the pass, because neighbouring blocks share almost all of their kernel.
    /// </summary>
    private static double? terraceBlockMean(
        TerrainArtifact artifact,
        sbyte[] before,
        TerraceBlockField field,
        int bix,
        int biy,
        int quantum,
        string affect)
    {
        int key = biy * field.across + bix;
        int state = field.state[key];
        if (state == 1) return field.mean[key];
        if (state == 2) return null;
        var spanX = terraceBlockSpan(bix * quantum, artifact.width, quantum);
        var spanY = terraceBlockSpan(biy * quantum, artifact.height, quantum);
        double sum = 0;
        int count = 0;
        for (int ty = spanY.origin; ty < spanY.origin + spanY.extent; ty++)
        {
            for (int tx = spanX.origin; tx < spanX.origin + spanX.extent; tx++)
            {
                int index = tileIndex(artifact.width, tx, ty);
                if (!canAffect(artifact, index, affect)) continue;
                sum += at(before, index);
                count++;
            }
        }
        if (count == 0)
        {
            field.state[key] = 2;
            return null;
        }
        field.mean[key] = (float)(sum / count);
        field.state[key] = 1;
        return field.mean[key];
    }

    /// <summary>
    /// How far a block looks for the slope it should follow, in blocks.
    ///
    /// Tied to the brush, because a mean filter cannot spread a slope further than its own kernel: a linear ramp is
    /// a fixed point of averaging, so a fixed narrow kernel converges after two strokes to a short, steep flight of
    /// steps and then never widens it however long the author keeps brushing. Scaling the reach with the brush is
    /// what makes the tool's two jobs one tool — a small brush cleans up local dither, a broad brush lays out a
    /// long walkable ramp — and it matches what the cursor already promises about the gesture's scale.
    /// </summary>
    private static double terraceKernelReach(double brushSize, int quantum)
    {
        return Math.max(1, Math.min(16, Math.round(brushSize / (quantum * 4))));
    }

    /// <summary>
    /// Height the surroundings want a whole block to stand at.
    ///
    /// A tent over the block neighbourhood measured in blocks: the block itself counts fully, the ring around it
    /// half, the next ring a third. Wide enough to be a statement about the *slope* rather than about three cells of
    /// dither, and centred enough that a plateau's interior samples only its own level and therefore never drifts.
    /// </summary>
    private static double? terraceNeighbourhoodLevel(
        TerrainArtifact artifact,
        sbyte[] before,
        TerraceBlockField field,
        TerrainTerraceBlock block,
        int quantum,
        string affect,
        int reach)
    {
        // Lattice origins are whole multiples of the quantum, so these divisions are exact.
        int bix = block.tx / quantum;
        int biy = block.ty / quantum;
        double sum = 0;
        double weight = 0;
        for (int dy = -reach; dy <= reach; dy++)
        {
            int ny = biy + dy;
            if (ny < 0 || ny >= field.down) continue;
            for (int dx = -reach; dx <= reach; dx++)
            {
                int nx = bix + dx;
                if (nx < 0 || nx >= field.across) continue;
                double? mean = terraceBlockMean(artifact, before, field, nx, ny, quantum, affect);
                if (mean == null) continue;
                double w = 1.0 / (1 + Math.max(Math.abs(dx), Math.abs(dy)));
                sum += mean.Value * w;
                weight += w;
            }
        }
        return weight > 0 ? sum / weight : null;
    }

    /// <summary>Repair rounds after smoothing — a bound, not a target: a mixed block settles in one or two moves.</summary>
    private const int TERRACE_REPAIR_ROUNDS = 3;

    /// <summary>
    /// Restore the terrace quantum for cells the block pass could not keep whole: a block clipped by the map
    /// border, or one that also holds water/rock the gesture must leave alone. Each offender takes the supported
    /// level closest to what smoothing wanted, and falls back to the height it had before the gesture when no
    /// single move can support it — a smooth stroke may leave terrain unchanged, never more broken than it found it.
    /// </summary>
    private static void repairTerraceSupport(
        TerrainArtifact artifact,
        sbyte[] initial,
        IReadOnlyList<int> cells,
        int quantum,
        double maxLevel)
    {
        var elevation = artifact.elevation!;
        for (int round = 0; round < TERRACE_REPAIR_ROUNDS; round++)
        {
            bool dirty = false;
            foreach (int index in cells)
            {
                int tx = index % artifact.width;
                int ty = (int)Math.floor((double)index / artifact.width);
                if (hasTerraceSupportAt(elevation, artifact.width, artifact.height, tx, ty, quantum))
                    continue;
                var range = terrainEditorHeightRangeForTile(artifact.baseTiles[index]);
                double min = range.min;
                double max = Math.min(range.max, maxLevel);
                int wanted = at(elevation, index);
                int next = clampLevel(at(initial, index), min, max);
                double bestDistance = double.PositiveInfinity;
                foreach (int level in terraceSupportLevelsAt(elevation, artifact.width, artifact.height, tx, ty, quantum))
                {
                    if (level < min || level > max) continue;
                    double distance = Math.abs(level - wanted);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        next = level;
                    }
                }
                if (next == wanted) continue;
                elevation[index] = Js.I8(next);
                dirty = true;
            }
            if (!dirty) break;
        }
    }

    /// <summary>
    /// Smooth the ground the brush covers — as terraces, not as a per-cell average.
    ///
    /// The old operator averaged each cell's 3×3 neighbourhood and rounded, which is the textbook way to produce a
    /// dither of single-tile steps: neighbouring cells round to different levels, and a slope ends up as noise
    /// nobody can walk or author. This one works in `TERRAIN_TERRACE_QUANTUM` blocks instead. Every block the
    /// footprint touches is read as a unit, given ONE level from terraceNeighbourhoodLevel, and written as a
    /// unit — so no gesture can create a tread narrower than the quantum in the map interior, and the tool builds
    /// flights of steps and walkable ramps rather than staircase gravel. repairTerraceSupport covers the two
    /// cases a block cannot own outright (the map border, and blocks shared with terrain the gesture must not
    /// touch).
    ///
    /// A block flattens even at low Strength — that is the quantum, not the blend. Strength decides how far the
    /// block's common level travels toward its surroundings; the quantum decides that the block has one.
    /// </summary>
    public static TerrainEditResult applyTerrainElevationSmooth(TerrainArtifact artifact, TerrainSmoothEdit edit)
    {
        ensureTerrainEditorLayers(artifact);
        var elevation = artifact.elevation!;
        int passes = (int)Math.max(1, Math.floor(edit.passes ?? 1));
        string affect = edit.affect ?? TerrainEditAffect.Walkable;
        double maxLevel = Math.max(0, edit.maxLevel ?? Elevation.MAX_ELEVATION);
        int quantum = normalizedTerraceQuantum(edit.quantum);
        // Only read while at least one block exists, i.e. for a finite brush size.
        int reach = (int)terraceKernelReach(normalizedBrush(edit.brush).size, quantum);
        var blocks = terrainSmoothBlocks(artifact, edit);
        var initial = elevation.slice();
        var changed = new List<int>();
        var seen = new HashSet<int>();

        for (int pass = 0; pass < passes; pass++)
        {
            var before = elevation.slice();
            var field = createTerraceBlockField(artifact.width, artifact.height, quantum);
            foreach (var block in blocks)
            {
                var editable = block.cells.filter(index => canAffect(artifact, index, affect));
                if (editable.Count == 0) continue;
                double? target = terraceNeighbourhoodLevel(artifact, before, field, block, quantum, affect, reach);
                if (target == null) continue;
                double sum = 0;
                foreach (int index in editable) sum += at(before, index);
                double current = sum / editable.Count;
                double blended = current + (target.Value - current) * block.weight;
                foreach (int index in editable)
                {
                    var range = terrainEditorHeightRangeForTile(artifact.baseTiles[index]);
                    int next = clampLevel(blended, range.min, Math.min(range.max, maxLevel));
                    if (next == elevation[index]) continue;
                    elevation[index] = Js.I8(next);
                    pushChanged(changed, seen, index);
                }
            }
        }

        repairTerraceSupport(artifact, initial, changed, quantum, maxLevel);
        // Repair may have put a cell back exactly where it started; `changed` must mean changed.
        return new TerrainEditResult { changed = changed.filter(index => elevation[index] != initial[index]) };
    }

    // ── Fills ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fill one contiguous TileType region with tile and semantic height in the same transaction.</summary>
    public static TerrainEditResult floodFillTerrainPaint(TerrainArtifact artifact, TerrainPaintFillEdit edit)
    {
        ensureTerrainEditorLayers(artifact);
        if (!inBounds(artifact.width, artifact.height, edit.tx, edit.ty)) return new TerrainEditResult();
        int start = tileIndex(artifact.width, edit.tx, edit.ty);
        int sourceTile = artifact.baseTiles[start];
        int targetStored = terrainStoredElevationFromEditorHeight(edit.tile, edit.height);
        int themePaletteIndex = terrainThemePaletteIndex(artifact, edit.themeKey);
        var changed = new List<int>();
        var seen = new byte[artifact.baseTiles.Length];
        var stack = new List<int> { edit.tx, edit.ty };
        seen[start] = 1;
        while (stack.Count > 0)
        {
            int ty = stack.pop();
            int tx = stack.pop();
            int idx = tileIndex(artifact.width, tx, ty);
            bool any = false;
            if (artifact.baseTiles[idx] != edit.tile)
            {
                artifact.baseTiles[idx] = Js.U8(edit.tile);
                artifact.surface![idx] = TerrainSurface.Auto;
                artifact.variant![idx] = 0;
                any = true;
            }
            if (artifact.elevation![idx] != targetStored)
            {
                artifact.elevation![idx] = Js.I8(targetStored);
                any = true;
            }
            if (setTerrainThemeAt(artifact, idx, themePaletteIndex)) any = true;
            if (any) changed.push(idx);
            for (int n = 0; n < 4; n++)
            {
                int nx = n == 0 ? tx + 1 : n == 1 ? tx - 1 : tx;
                int ny = n == 2 ? ty + 1 : n == 3 ? ty - 1 : ty;
                if (!inBounds(artifact.width, artifact.height, nx, ny)) continue;
                int ni = tileIndex(artifact.width, nx, ny);
                if (seen[ni] != 0 || artifact.baseTiles[ni] != sourceTile) continue;
                seen[ni] = 1;
                stack.push(nx, ny);
            }
        }
        pruneDecorationsForTiles(artifact, changed);
        return new TerrainEditResult { changed = changed };
    }

    public static TerrainRect normalizeTerrainRect(TerrainRect rect, int width, int height)
    {
        int x0 = Math.max(0, Math.min(width, rect.tx));
        int y0 = Math.max(0, Math.min(height, rect.ty));
        int x1 = Math.max(0, Math.min(width, rect.tx + rect.tw));
        int y1 = Math.max(0, Math.min(height, rect.ty + rect.th));
        return new TerrainRect
        {
            tx = Math.min(x0, x1),
            ty = Math.min(y0, y1),
            tw = Math.abs(x1 - x0),
            th = Math.abs(y1 - y0),
        };
    }

    // ── Stamps, selection, clipboard ──────────────────────────────────────────────────────────────────

    public static TerrainStamp copyTerrainStamp(TerrainArtifact artifact, TerrainRect rect)
    {
        ensureTerrainEditorLayers(artifact);
        var r = normalizeTerrainRect(rect, artifact.width, artifact.height);
        int count = r.tw * r.th;
        byte[]? stampThemeIndex = null;
        if (artifact.themeIndex != null)
        {
            stampThemeIndex = new byte[count];
            Array.Fill(stampThemeIndex, (byte)TERRAIN_THEME_INHERIT);
        }
        var stamp = new TerrainStamp
        {
            width = r.tw,
            height = r.th,
            baseTiles = new byte[count],
            elevation = new sbyte[count],
            surface = new byte[count],
            variant = new byte[count],
            themePalette = artifact.themePalette != null ? new List<string>(artifact.themePalette) : null,
            themeIndex = stampThemeIndex,
            decorations = new List<TerrainDecorationPlacement>(),
        };
        for (int y = 0; y < r.th; y++)
        {
            for (int x = 0; x < r.tw; x++)
            {
                int src = tileIndex(artifact.width, r.tx + x, r.ty + y);
                int dst = tileIndex(r.tw, x, y);
                stamp.baseTiles[dst] = artifact.baseTiles[src];
                stamp.elevation[dst] = artifact.elevation![src];
                stamp.surface[dst] = artifact.surface![src];
                stamp.variant[dst] = artifact.variant![src];
                if (stamp.themeIndex != null && artifact.themeIndex != null)
                    stamp.themeIndex[dst] = Js.U8(at(artifact.themeIndex, src));
            }
        }
        stamp.decorations = (artifact.decorations ?? new List<TerrainDecorationPlacement>())
            .filter(decoration =>
                decoration.tx >= r.tx &&
                decoration.ty >= r.ty &&
                decoration.tx < r.tx + r.tw &&
                decoration.ty < r.ty + r.th)
            .map(decoration =>
            {
                var copy = decoration.Clone();
                copy.tx = decoration.tx - r.tx;
                copy.ty = decoration.ty - r.ty;
                return copy;
            });
        return stamp;
    }

    /// <summary>Copy an arbitrary, possibly non-rectangular selection into a masked, self-contained terrain stamp.</summary>
    public static TerrainStamp? copyTerrainSelectionStamp(TerrainArtifact artifact, IReadOnlyList<int> indices)
    {
        var selected = new JsSet<int>();
        foreach (int index in indices)
            if (index >= 0 && index < artifact.baseTiles.Length) selected.add(index);
        if (selected.size == 0) return null;
        int minX = artifact.width;
        int minY = artifact.height;
        int maxX = 0;
        int maxY = 0;
        foreach (int index in selected)
        {
            int x = index % artifact.width;
            int y = (int)Math.floor((double)index / artifact.width);
            minX = Math.min(minX, x);
            minY = Math.min(minY, y);
            maxX = Math.max(maxX, x);
            maxY = Math.max(maxY, y);
        }
        var stamp = copyTerrainStamp(artifact, new TerrainRect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        stamp.mask = new byte[stamp.width * stamp.height];
        foreach (int index in selected)
        {
            int x = (index % artifact.width) - minX;
            int y = (int)Math.floor((double)index / artifact.width) - minY;
            stamp.mask[tileIndex(stamp.width, x, y)] = 1;
        }
        var mask = stamp.mask;
        stamp.decorations = (stamp.decorations ?? new List<TerrainDecorationPlacement>())
            .filter(decoration => at(mask, tileIndex(stamp.width, decoration.tx, decoration.ty)) == 1);
        return stamp;
    }

    public static TerrainEditResult applyTerrainStamp(TerrainArtifact artifact, TerrainStamp stamp, int tx, int ty)
    {
        ensureTerrainEditorLayers(artifact);
        var changed = new List<int>();
        int stampCount = stamp.width * stamp.height;
        if (stampCount <= 0) return new TerrainEditResult { changed = changed };
        if (stamp.baseTiles.Length != stampCount)
            throw new InvalidOperationException($"Terrain stamp tile layer length must equal {Js.Str(stampCount)}.");
        foreach (var (name, length) in new (string, int?)[]
        {
            ("elevation", stamp.elevation?.Length),
            ("surface", stamp.surface?.Length),
            ("variant", stamp.variant?.Length),
            ("theme", stamp.themeIndex?.Length),
            ("mask", stamp.mask?.Length),
        })
        {
            if (length != null && length.Value != stampCount)
                throw new InvalidOperationException($"Terrain stamp {name} layer length must equal {Js.Str(stampCount)}.");
        }
        if (
            tx >= artifact.width ||
            ty >= artifact.height ||
            tx + stamp.width <= 0 ||
            ty + stamp.height <= 0)
            return new TerrainEditResult { changed = changed };
        if (stamp.themeIndex != null)
        {
            var palette = stamp.themePalette ?? new List<string>();
            if (palette.Count > TERRAIN_THEME_PALETTE_LIMIT || palette.some(key => isBlank(key)))
                throw new InvalidOperationException("Terrain stamp contains an invalid theme palette.");
            for (int i = 0; i < stamp.themeIndex.Length; i++)
            {
                int paletteIndex = stamp.themeIndex[i];
                if (paletteIndex != TERRAIN_THEME_INHERIT && paletteIndex >= palette.Count)
                    throw new InvalidOperationException(
                        $"Terrain stamp references missing theme palette entry {Js.Str(paletteIndex)}.");
            }
        }
        var seen = new HashSet<int>();
        var themeRemap = stamp.themeIndex != null
            ? (stamp.themePalette ?? new List<string>()).map(key => terrainThemePaletteIndex(artifact, key))
            : null;
        if (stamp.themeIndex != null) ensureTerrainThemeLayer(artifact);
        for (int y = 0; y < stamp.height; y++)
        {
            for (int x = 0; x < stamp.width; x++)
            {
                int dx = tx + x;
                int dy = ty + y;
                if (!inBounds(artifact.width, artifact.height, dx, dy)) continue;
                int src = tileIndex(stamp.width, x, y);
                if (stamp.mask != null && stamp.mask[src] == 0) continue;
                int dst = tileIndex(artifact.width, dx, dy);
                bool any = false;
                if (artifact.baseTiles[dst] != stamp.baseTiles[src])
                {
                    artifact.baseTiles[dst] = stamp.baseTiles[src];
                    any = true;
                }
                if (removeDecorationAt(artifact, dx, dy)) any = true;
                if (stamp.elevation != null && artifact.elevation![dst] != stamp.elevation[src])
                {
                    artifact.elevation![dst] = stamp.elevation[src];
                    any = true;
                }
                if (stamp.surface != null && artifact.surface![dst] != stamp.surface[src])
                {
                    artifact.surface![dst] = stamp.surface[src];
                    any = true;
                }
                if (stamp.variant != null && artifact.variant![dst] != stamp.variant[src])
                {
                    artifact.variant![dst] = stamp.variant[src];
                    any = true;
                }
                if (stamp.themeIndex != null)
                {
                    int sourceThemeIndex = stamp.themeIndex[src];
                    int? destinationThemeIndex =
                        sourceThemeIndex == TERRAIN_THEME_INHERIT
                            ? TERRAIN_THEME_INHERIT
                            : themeRemap != null && sourceThemeIndex < themeRemap.Count ? themeRemap[sourceThemeIndex] : null;
                    if (destinationThemeIndex == null)
                        throw new InvalidOperationException(
                            $"Terrain stamp references missing theme palette entry {Js.Str(sourceThemeIndex)}.");
                    if (setTerrainThemeAt(artifact, dst, destinationThemeIndex.Value)) any = true;
                }
                if (any) pushChanged(changed, seen, dst);
            }
        }
        foreach (var decoration in stamp.decorations ?? new List<TerrainDecorationPlacement>())
        {
            int dx = tx + decoration.tx;
            int dy = ty + decoration.ty;
            if (!inBounds(artifact.width, artifact.height, dx, dy)) continue;
            int destination = tileIndex(artifact.width, dx, dy);
            // A stamp carries its own terrain, but a masked cell keeps the ground it lands on. Re-asking the habitat
            // rule at the destination is what stops a copied grove from replanting itself over a ravine.
            if (!terrainTileAcceptsDecoration(decoration.kind, artifact.baseTiles[destination]))
                continue;
            removeDecorationAt(artifact, dx, dy);
            var placed = decoration.Clone();
            placed.tx = dx;
            placed.ty = dy;
            (artifact.decorations ??= new List<TerrainDecorationPlacement>()).push(placed);
            pushChanged(changed, seen, destination);
        }
        if (artifact.decorations != null) sortDecorations(artifact.decorations);
        return new TerrainEditResult { changed = changed };
    }

    /// <summary>Clear selected cells to neutral walkable terrain; used by cut/move after the stamp has been captured.</summary>
    public static TerrainEditResult clearTerrainSelection(TerrainArtifact artifact, IReadOnlyList<int> indices)
    {
        ensureTerrainEditorLayers(artifact);
        var changed = new List<int>();
        var seen = new HashSet<int>();
        var themeIndex = artifact.themeIndex;
        foreach (int index in indices)
        {
            if (index < 0 || index >= artifact.baseTiles.Length || seen.Contains(index)) continue;
            seen.Add(index);
            int tx = index % artifact.width;
            int ty = (int)Math.floor((double)index / artifact.width);
            bool any = false;
            if (artifact.baseTiles[index] != TileType.Floor)
            {
                artifact.baseTiles[index] = TileType.Floor;
                any = true;
            }
            if (artifact.elevation![index] != 0)
            {
                artifact.elevation![index] = 0;
                any = true;
            }
            if (artifact.surface![index] != TerrainSurface.Auto)
            {
                artifact.surface![index] = TerrainSurface.Auto;
                any = true;
            }
            if (artifact.variant![index] != 0)
            {
                artifact.variant![index] = 0;
                any = true;
            }
            if (themeIndex != null && at(themeIndex, index) != TERRAIN_THEME_INHERIT)
            {
                if ((uint)index < (uint)themeIndex.Length) themeIndex[index] = TERRAIN_THEME_INHERIT;
                any = true;
            }
            if (removeDecorationAt(artifact, tx, ty)) any = true;
            if (any) changed.push(index);
        }
        return new TerrainEditResult { changed = changed };
    }

    // ── Markers ───────────────────────────────────────────────────────────────────────────────────────
}
