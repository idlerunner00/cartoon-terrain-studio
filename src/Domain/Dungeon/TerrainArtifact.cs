// Port of packages/shared/src/domain/dungeon/terrainArtifact.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using System.Globalization;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Grid;
using static Fluitown.Domain.WorldDecoration;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

public sealed class TerrainArtifact
{
    public int schemaVersion;
    public int width;
    public int height;
    public double? tileSize;
    public double? originX;
    public double? originY;
    public double? index;
    public double? seed;
    /// <summary>A DungeonStyle value.</summary>
    public int? style;
    public string? biomeKey;
    public int? tier;
    public byte[] baseTiles = System.Array.Empty<byte>();
    public sbyte[]? elevation;
    public byte[]? surface;
    public byte[]? variant;
    public byte[]? floorUsage;
    public List<string>? themePalette;
    public byte[]? themeIndex;
    public string? tilesetId;
    public List<TerrainMarker>? markers;
    public List<TerrainDecorationPlacement>? decorations;
    public List<DungeonRoom>? rooms;
    public List<DungeonDoor>? doors;
    public int? startRoomId;
    public int? bossRoomId;

    /// <summary>Shallow copy — `{ ...artifact }`.</summary>
    public TerrainArtifact Clone() => (TerrainArtifact)MemberwiseClone();
}

/// <summary>
/// TS `type TerrainLayoutInput = DungeonLayout | TerrainArtifact`. Both arms convert implicitly, so callers pass
/// either a layout or an artifact exactly as in TypeScript. `isArtifact` is the TS `'baseTiles' in input` test.
/// </summary>
public sealed class TerrainLayoutInput
{
    public readonly DungeonLayout? layout;
    public readonly TerrainArtifact? artifact;

    private TerrainLayoutInput(DungeonLayout? layout, TerrainArtifact? artifact)
    {
        this.layout = layout;
        this.artifact = artifact;
    }

    public static implicit operator TerrainLayoutInput(TerrainArtifact artifact) => new(null, artifact);

    public int width => artifact != null ? artifact.width : layout!.width;
    public int height => artifact != null ? artifact.height : layout!.height;
    public sbyte[]? elevation => artifact != null ? artifact.elevation : layout!.elevation;
    public List<DungeonRoom>? rooms => artifact != null ? artifact.rooms : layout!.rooms;
    public int? startRoomId => artifact != null ? artifact.startRoomId : layout!.startRoomId;
    public int? bossRoomId => artifact != null ? artifact.bossRoomId : layout!.bossRoomId;
}

public static class TerrainValidationCode
{
    public const string SchemaVersion = "schema_version";
    public const string Dimensions = "dimensions";
    public const string LayerSize = "layer_size";
    public const string ThemePalette = "theme_palette";
    public const string ThemeIndex = "theme_index";
    public const string TileKind = "tile_kind";
    public const string MarkerBounds = "marker_bounds";
    public const string DecorationBounds = "decoration_bounds";
    public const string DecorationKind = "decoration_kind";
    public const string DecorationTheme = "decoration_theme";
    public const string DecorationSurface = "decoration_surface";
    public const string NoWalkable = "no_walkable";
    public const string WalkableDisconnected = "walkable_disconnected";
    public const string StartUnreachable = "start_unreachable";
    public const string BossUnreachable = "boss_unreachable";
    public const string HeightStep = "height_step";
    public const string WaterStoredElevation = "water_stored_elevation";
    public const string ChasmStoredDepth = "chasm_stored_depth";
    public const string BridgeStray = "bridge_stray";
    public const string BridgeSpan = "bridge_span";
    public const string BridgeWidth = "bridge_width";
    public const string CleftStructure = "cleft_structure";
    public const string UnderpassStructure = "underpass_structure";
    public const string OverheadBeam = "overhead_beam";
    public const string MinimumFootprint = "minimum_footprint";
    public const string SeamPort = "seam_port";
    public const string SeamMismatch = "seam_mismatch";
    public const string SeamHeightStep = "seam_height_step";
}

// `TerrainValidationSeverity` = 'error' | 'warning' → string; `TerrainValidationDiagnosticSeverity` adds 'off'.

public sealed class TerrainValidationIssue
{
    /// <summary>A TerrainValidationCode value.</summary>
    public string code = "";
    /// <summary>'error' | 'warning'.</summary>
    public string severity = "error";
    public string message = "";
    /// <summary>'tiles' | 'elevation' | 'surface' | 'variant' | 'floorUsage' | 'theme' | 'markers' | 'decorations' | 'seam'.</summary>
    public string? layer;
    public int? tx;
    public int? ty;
    public int? index;
    public int? roomId;
    /// <summary>`Record&lt;string, number | string | boolean&gt;` in insertion order; numbers are boxed doubles.</summary>
    public JsMap<string, object?>? meta;
}

public sealed class TerrainValidationResult
{
    public bool ok;
    public List<TerrainValidationIssue> issues = new();
}

public sealed class TerrainAnchor
{
    public int tx;
    public int ty;

    public TerrainAnchor(int tx, int ty)
    {
        this.tx = tx;
        this.ty = ty;
    }
}

public sealed class TerrainSeamCell
{
    /// <summary>A TileType value.</summary>
    public int tile;
    public double? elevation;
}

public sealed class TerrainSeamValidation
{
    public IReadOnlyList<TerrainSeamCell>? north;
    public IReadOnlyList<TerrainSeamCell>? south;
    public IReadOnlyList<TerrainSeamCell>? west;
    public IReadOnlyList<TerrainSeamCell>? east;
    /// <summary>Require every local edge to contain at least one walkable crossing/port. Useful for chunk artifacts.</summary>
    public bool? requireWalkablePort;
}

/// <summary>Not sealed: <see cref="TerrainFinalizeOptions"/> extends it, as in TS.</summary>
public class TerrainValidationOptions
{
    public bool? requireConnected;
    public bool? requireElevation;
    public bool? requireStartBossReachable;
    public double? maxClimb;
    /// <summary>Severity for extra walkable islands outside the primary climbable component. Runtime generators keep errors.</summary>
    public string? walkableDisconnectedSeverity;
    public int? minimumFootprint;
    public int? minimumFootprintBorder;
    /// <summary>Severity for local-footprint readability diagnostics. This is intentionally softer in editor workflows.</summary>
    public string? minimumFootprintSeverity;
    public TerrainAnchor? start;
    public TerrainAnchor? boss;
    public TerrainSeamValidation? seams;
    /// <summary>Endless chunks may hold one side of a bridge span inside the seam-stub margin; the neighbour owns the rest.</summary>
    public bool? allowPartialSeamBridgeSpans;
    /// <summary>Minimum bridge deck thickness in tiles. The standard terrain contract uses 2.</summary>
    public int? minimumBridgeWidth;
    public int? maxIssuesPerRule;
}

public sealed class TerrainCompileResult
{
    public DungeonLayout? layout;
    public TerrainValidationResult validation;
}

public sealed class TerrainFinalizeOptions : TerrainValidationOptions
{
    /// <summary>Included in thrown validation errors so generated-world failures point at the owning factory.</summary>
    public string? context;
}

public static class TerrainArtifactModule
{
    public static readonly TerrainValidationOptions TERRAIN_EDITOR_VALIDATION_OPTIONS = new()
    {
        requireElevation = true,
        requireStartBossReachable = true,
        minimumBridgeWidth = 2,
        minimumFootprint = 5,
        walkableDisconnectedSeverity = "warning",
        minimumFootprintSeverity = "warning",
        maxIssuesPerRule = 64,
    };

    private const int OVERHEAD_BEAM_MIN_LENGTH = 2;
    private const int OVERHEAD_BEAM_MAX_LENGTH = 4;
    private const int SEAM_STUB_MARGIN = 9;

    private static readonly (int dx, int dy)[] FOUR_DIRS = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    private static readonly (int dx, int dy)[] FORWARD_DIRS = { (1, 0), (0, 1) };

    // ── C# port helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an issue `meta` record in insertion order. Numeric values are boxed as double so every meta number
    /// is a JS number regardless of the C# integer type it was read from.
    /// </summary>
    private static JsMap<string, object?> Meta(params (string key, object? value)[] entries)
    {
        var map = new JsMap<string, object?>();
        foreach (var (key, value) in entries)
        {
            map.set(key, value switch
            {
                int i => (double)i,
                sbyte s => (double)s,
                byte b => (double)b,
                _ => value,
            });
        }
        return map;
    }

    /// <summary>`s.trim().length === 0` with ECMAScript's WhiteSpace + LineTerminator set (differs from char.IsWhiteSpace at U+0085 and U+FEFF).</summary>
    private static bool IsJsBlank(string s)
    {
        foreach (char c in s)
        {
            bool jsSpace =
                c == (char)0x09 || c == (char)0x0B || c == (char)0x0C || c == (char)0xFEFF ||
                c == (char)0x0A || c == (char)0x0D || c == (char)0x2028 || c == (char)0x2029 ||
                CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
            if (!jsSpace) return false;
        }
        return true;
    }

    /// <summary>`${layer[i]}` for a signed layer whose length may not match the grid (JS prints "undefined").</summary>
    private static string ElevationStr(sbyte[] layer, int i) => (uint)i < (uint)layer.Length ? Js.Str(layer[i]) : "undefined";

    // ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool isNearSeamStub(int width, int height, int tx, int ty)
    {
        return (
            tx <= SEAM_STUB_MARGIN ||
            ty <= SEAM_STUB_MARGIN ||
            tx >= width - 1 - SEAM_STUB_MARGIN ||
            ty >= height - 1 - SEAM_STUB_MARGIN);
    }

    private static bool bridgeComponentTouchesSeamStub(byte[] tiles, int width, int height, int tx, int ty)
    {
        int start = tileIndex(width, tx, ty);
        if (tiles[start] != TileType.Bridge) return false;
        var seen = new byte[tiles.Length];
        var stack = new List<int> { tx, ty };
        seen[start] = 1;
        while (stack.Count > 0)
        {
            int y = stack.pop();
            int x = stack.pop();
            if (isNearSeamStub(width, height, x, y)) return true;
            for (int k = 0; k < 4; k++)
            {
                // [x - 1, y], [x + 1, y], [x, y - 1], [x, y + 1]
                int nx = k == 0 ? x - 1 : k == 1 ? x + 1 : x;
                int ny = k == 2 ? y - 1 : k == 3 ? y + 1 : y;
                if (!inBounds(width, height, nx, ny)) continue;
                int ni = tileIndex(width, nx, ny);
                if (seen[ni] != 0 || tiles[ni] != TileType.Bridge) continue;
                seen[ni] = 1;
                stack.push(nx, ny);
            }
        }
        return false;
    }

    private static bool isArtifact(TerrainLayoutInput input) => input.artifact != null;

    private static byte[] tilesOf(TerrainLayoutInput input) => isArtifact(input) ? input.artifact!.baseTiles : input.layout!.tiles;

    private static DungeonTerrainLayers? terrainLayersOf(TerrainLayoutInput input)
    {
        if (!isArtifact(input)) return input.layout!.terrain;
        var artifact = input.artifact!;
        return new DungeonTerrainLayers
        {
            schemaVersion = artifact.schemaVersion,
            tilesetId = artifact.tilesetId,
            surface = artifact.surface,
            variant = artifact.variant,
            floorUsage = artifact.floorUsage,
            themePalette = artifact.themePalette,
            themeIndex = artifact.themeIndex,
            markers = artifact.markers,
            decorations = artifact.decorations,
        };
    }

    private static sbyte[]? elevationOf(TerrainLayoutInput input) => input.elevation;

    private static IReadOnlyList<DungeonRoom> roomsOf(TerrainLayoutInput input) =>
        (IReadOnlyList<DungeonRoom>?)input.rooms ?? System.Array.Empty<DungeonRoom>();

    private static int startRoomIdOf(TerrainLayoutInput input) => input.startRoomId ?? -1;

    private static int bossRoomIdOf(TerrainLayoutInput input) => input.bossRoomId ?? -1;

    private static IReadOnlyList<TerrainMarker> markersOf(TerrainLayoutInput input)
    {
        return isArtifact(input)
            ? (IReadOnlyList<TerrainMarker>?)input.artifact!.markers ?? System.Array.Empty<TerrainMarker>()
            : (IReadOnlyList<TerrainMarker>?)input.layout!.terrain?.markers ?? System.Array.Empty<TerrainMarker>();
    }

    /// <summary>`issues.push({ code, severity: extra?.severity ?? 'error', message, ...extra })` with `extra` as named arguments.</summary>
    private static void issue(
        List<TerrainValidationIssue> issues,
        string code,
        string message,
        string? severity = null,
        string? layer = null,
        int? tx = null,
        int? ty = null,
        int? index = null,
        int? roomId = null,
        JsMap<string, object?>? meta = null)
    {
        issues.push(new TerrainValidationIssue
        {
            code = code,
            severity = severity ?? "error",
            message = message,
            layer = layer,
            tx = tx,
            ty = ty,
            index = index,
            roomId = roomId,
            meta = meta,
        });
    }

    private static string? diagnosticSeverity(string? severity, string fallback)
    {
        if (severity == "off") return null;
        return severity ?? fallback;
    }

    private static bool validTileValue(int tile)
    {
        return (
            tile == TileType.Solid ||
            tile == TileType.Floor ||
            tile == TileType.Water ||
            tile == TileType.Bridge ||
            tile == TileType.Chasm ||
            tile == TileType.Cleft ||
            tile == TileType.Underpass);
    }

    private static void validateDepthTileStructures(
        List<TerrainValidationIssue> issues,
        byte[] tiles,
        sbyte[]? elevation,
        int width,
        int height,
        int cap)
    {
        int emittedClefts = 0;
        for (int index = 0; index < tiles.Length && emittedClefts < cap; index++)
        {
            if (tiles[index] != TileType.Cleft) continue;
            int tx = index % width;
            int ty = (int)Math.floor((double)index / width);
            var profile = elevation != null
                ? TerrainModel.terrainCleftProfileAt(tiles, elevation, width, height, tx, ty)
                : null;
            if (profile != null) continue;
            issue(
                issues,
                TerrainValidationCode.CleftStructure,
                "Cleft needs two same-level walkable sight approaches, two Solid wall shoulders and at least 2.8 levels of wall mass.",
                layer: elevation != null ? "tiles" : "elevation",
                index: index,
                tx: tx,
                ty: ty,
                meta: Meta(("hasElevation", elevation != null)));
            emittedClefts++;
        }

        int emittedUnderpasses = 0;
        for (int index = 0; index < tiles.Length && emittedUnderpasses < cap; index++)
        {
            if (tiles[index] != TileType.Underpass) continue;
            int tx = index % width;
            int ty = (int)Math.floor((double)index / width);
            var profile = elevation != null
                ? TerrainModel.terrainUnderpassProfileAt(tiles, elevation, width, height, tx, ty)
                : null;
            if (profile != null) continue;
            issue(
                issues,
                TerrainValidationCode.UnderpassStructure,
                "Underpass must span 2–10 same-level cells with open approaches, two rooted 2x2 Solid mountain banks at least +2.8 high, and a supported deck at least +4 above the passage.",
                layer: elevation != null ? "tiles" : "elevation",
                index: index,
                tx: tx,
                ty: ty,
                meta: Meta(("hasElevation", elevation != null)));
            emittedUnderpasses++;
        }
    }

    private static int layerAt(byte[]? layer, int width, int tx, int ty)
    {
        if (layer == null) return 0;
        int i = ty * width + tx;
        return (uint)i < (uint)layer.Length ? layer[i] : 0;
    }

    private static TerrainAnchor markerCell(TerrainMarker marker, int offset)
    {
        return marker.axis == TerrainMarkerAxis.Vertical
            ? new TerrainAnchor(marker.tx, marker.ty + offset)
            : new TerrainAnchor(marker.tx + offset, marker.ty);
    }

    private static void validateOverheadBeamMarker(
        List<TerrainValidationIssue> issues,
        TerrainMarker marker,
        byte[] tiles,
        sbyte[]? elevation,
        int width,
        int height)
    {
        int? axis = marker.axis;
        if (axis != TerrainMarkerAxis.Horizontal && axis != TerrainMarkerAxis.Vertical)
        {
            issue(issues, TerrainValidationCode.OverheadBeam, "Overhead beam marker needs a valid axis.",
                layer: "markers",
                tx: marker.tx,
                ty: marker.ty,
                meta: Meta(("axis", axis ?? -1), ("type", marker.type)));
            return;
        }

        double? length = marker.length;
        if (
            length == null ||
            !Number.isInteger(length.Value) ||
            length < OVERHEAD_BEAM_MIN_LENGTH ||
            length > OVERHEAD_BEAM_MAX_LENGTH)
        {
            issue(
                issues,
                TerrainValidationCode.OverheadBeam,
                "Overhead beam length must stay within the standard kit range.",
                layer: "markers",
                tx: marker.tx,
                ty: marker.ty,
                meta: Meta(
                    ("length", length ?? -1),
                    ("minLength", OVERHEAD_BEAM_MIN_LENGTH),
                    ("maxLength", OVERHEAD_BEAM_MAX_LENGTH)));
            return;
        }

        // `baseLevel`/`level` are nullable so an elevation layer shorter than the grid reproduces JS `undefined`
        // (`undefined < 0` is false, `undefined !== undefined` is false).
        double? baseLevel = -1;
        for (int offset = 0; offset < length; offset++)
        {
            var cell = markerCell(marker, offset);
            if (!inBounds(width, height, cell.tx, cell.ty))
            {
                issue(issues, TerrainValidationCode.OverheadBeam, "Overhead beam span leaves the grid.",
                    layer: "markers",
                    tx: cell.tx,
                    ty: cell.ty,
                    meta: Meta(("offset", offset), ("length", length.Value)));
                continue;
            }
            int idx = tileIndex(width, cell.tx, cell.ty);
            if (tiles[idx] != TileType.Floor)
            {
                issue(
                    issues,
                    TerrainValidationCode.OverheadBeam,
                    "Overhead beam must sit above ordinary walkable floor.",
                    layer: "markers",
                    tx: cell.tx,
                    ty: cell.ty,
                    index: idx,
                    meta: Meta(("tile", tiles[idx]), ("offset", offset)));
            }
            if (elevation == null) continue;
            double? level = (uint)idx < (uint)elevation.Length ? (double?)elevation[idx] : null;
            if (baseLevel < 0)
            {
                baseLevel = level;
            }
            else if (level != baseLevel)
            {
                issue(
                    issues,
                    TerrainValidationCode.OverheadBeam,
                    "Overhead beam span must stay on one elevation level.",
                    layer: "markers",
                    tx: cell.tx,
                    ty: cell.ty,
                    index: idx,
                    meta: Meta(("expected", baseLevel), ("actual", level), ("offset", offset)));
            }
        }
    }

    private static TerrainAnchor? firstWalkable(byte[] tiles, int width, int height)
    {
        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                if (isWalkable(tiles[tileIndex(width, tx, ty)])) return new TerrainAnchor(tx, ty);
            }
        }
        return null;
    }

    private static TerrainAnchor? markerAnchor(TerrainLayoutInput input, int type)
    {
        var marker = markersOf(input).find(m => m.type == type);
        return marker != null ? new TerrainAnchor(marker.tx, marker.ty) : null;
    }

    private static TerrainAnchor? roomAnchor(TerrainLayoutInput input, int roomId)
    {
        var room = roomsOf(input).find(r => r.id == roomId);
        if (room == null) return null;
        return new TerrainAnchor(
            (int)Math.floor(room.rect.tx + (double)room.rect.tw / 2),
            (int)Math.floor(room.rect.ty + (double)room.rect.th / 2));
    }

    private static TerrainAnchor? startAnchor(TerrainLayoutInput input, TerrainValidationOptions options)
    {
        return (
            options.start ??
            markerAnchor(input, TerrainMarkerType.Start) ??
            roomAnchor(input, startRoomIdOf(input)));
    }

    private static TerrainAnchor? bossAnchor(TerrainLayoutInput input, TerrainValidationOptions options)
    {
        return (
            options.boss ??
            markerAnchor(input, TerrainMarkerType.Boss) ??
            roomAnchor(input, bossRoomIdOf(input)));
    }

    private static byte[] computeClimbableReach(
        byte[] tiles,
        sbyte[]? elevation,
        int width,
        int height,
        TerrainAnchor seed,
        double maxClimb,
        MaterializedTerrain? sharedTerrain = null)
    {
        var reached = new byte[width * height];
        if (!inBounds(width, height, seed.tx, seed.ty)) return reached;
        int seedIndex = tileIndex(width, seed.tx, seed.ty);
        if (!isWalkable(tiles[seedIndex])) return reached;

        var terrain =
            sharedTerrain ?? TerrainModel.materializeTerrainGrid(tiles, width, height, elevation, new TerrainModelOptions { maxStep = maxClimb });
        var queue = new int[width * height];
        int head = 0;
        int tail = 0;
        reached[seedIndex] = 1;
        queue[tail++] = seedIndex;
        while (head < tail)
        {
            int idx = queue[head++];
            int tx = idx % width;
            int ty = (int)Math.floor((double)idx / width);
            foreach (var (dx, dy) in FOUR_DIRS)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (!inBounds(width, height, nx, ny)) continue;
                int ni = tileIndex(width, nx, ny);
                if (reached[ni] != 0 || !isWalkable(tiles[ni])) continue;
                var fromCell = terrain.cells[idx];
                var toCell = terrain.cells[ni];
                if (fromCell == null || toCell == null || !TerrainModel.terrainMovementRuleFor(fromCell, toCell, maxClimb).passable)
                    continue;
                reached[ni] = 1;
                queue[tail++] = ni;
            }
        }
        return reached;
    }

    private static void validateBridgeComponents(
        List<TerrainValidationIssue> issues,
        byte[] tiles,
        int width,
        int height,
        int cap,
        bool allowPartialSeamBridgeSpans)
    {
        var seen = new byte[tiles.Length];
        var stack = new List<int>();
        int emittedStray = 0;
        int emittedSpan = 0;

        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int start = tileIndex(width, tx, ty);
                if (seen[start] != 0 || tiles[start] != TileType.Bridge) continue;
                seen[start] = 1;
                stack.push(tx, ty);
                bool touchesWater = false;
                bool touchesChasm = false;
                bool touchesEdge = false;
                var landAnchors = new JsSet<int>();
                var first = new TerrainAnchor(tx, ty);

                while (stack.Count > 0)
                {
                    int y = stack.pop();
                    int x = stack.pop();
                    touchesEdge = touchesEdge || x == 0 || y == 0 || x == width - 1 || y == height - 1;
                    for (int k = 0; k < 4; k++)
                    {
                        // [x - 1, y], [x + 1, y], [x, y - 1], [x, y + 1]
                        int nx = k == 0 ? x - 1 : k == 1 ? x + 1 : x;
                        int ny = k == 2 ? y - 1 : k == 3 ? y + 1 : y;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        int tile = tiles[ni];
                        if (tile == TileType.Water)
                        {
                            touchesWater = true;
                        }
                        else if (tile == TileType.Chasm)
                        {
                            touchesChasm = true;
                        }
                        else if (tile != TileType.Bridge && isWalkable(tile))
                        {
                            landAnchors.add(ni);
                        }
                        else if (tile == TileType.Bridge && seen[ni] == 0)
                        {
                            seen[ni] = 1;
                            stack.push(nx, ny);
                        }
                    }
                }

                int landAnchorGroups = TerrainBridge.countTerrainBridgeLandAnchorGroups(landAnchors, width, height);

                // A neighbouring chunk can continue only a deck that physically reaches this chunk's edge. The old
                // near-seam allowance accepted any one-bank bridge within nine cells of an edge, including the visibly
                // short interior span this validation is meant to reject.
                bool partialSeamStub = allowPartialSeamBridgeSpans && touchesEdge && landAnchorGroups >= 1;
                bool touchesSpan = touchesWater || touchesChasm;
                if (!touchesSpan && !partialSeamStub && emittedStray < cap)
                {
                    issue(
                        issues,
                        TerrainValidationCode.BridgeStray,
                        "Bridge deck does not touch Water or Chasm.",
                        layer: "tiles",
                        tx: first.tx,
                        ty: first.ty,
                        meta: Meta(("anchors", landAnchors.size), ("anchorGroups", landAnchorGroups)));
                    emittedStray++;
                }
                bool partialSeamSpan =
                    allowPartialSeamBridgeSpans && touchesSpan && landAnchorGroups >= 1 && touchesEdge;
                if (touchesSpan && landAnchorGroups < 2 && !partialSeamSpan)
                {
                    if (emittedSpan >= cap) continue;
                    issue(
                        issues,
                        TerrainValidationCode.BridgeSpan,
                        "Bridge component must span Water or Chasm and connect at least two walkable land anchors.",
                        layer: "tiles",
                        tx: first.tx,
                        ty: first.ty,
                        meta: Meta(
                            ("anchors", landAnchors.size),
                            ("anchorGroups", landAnchorGroups),
                            ("touchesWater", touchesWater),
                            ("touchesChasm", touchesChasm)));
                    emittedSpan++;
                }
            }
        }
    }

    private sealed class SeamEdgeDef
    {
        public string name = "";
        public IReadOnlyList<TerrainSeamCell>? cells;
        public int count;
        public Func<int, int> tx = _ => 0;
        public Func<int, int> ty = _ => 0;
    }

    private static void validateSeams(
        List<TerrainValidationIssue> issues,
        byte[] tiles,
        sbyte[]? elevation,
        int width,
        int height,
        TerrainSeamValidation seams,
        double maxClimb,
        int cap)
    {
        var edgeDefs = new[]
        {
            new SeamEdgeDef { name = "north", cells = seams.north, count = width, tx = k => k, ty = _ => 0 },
            new SeamEdgeDef { name = "south", cells = seams.south, count = width, tx = k => k, ty = _ => height - 1 },
            new SeamEdgeDef { name = "west", cells = seams.west, count = height, tx = _ => 0, ty = k => k },
            new SeamEdgeDef { name = "east", cells = seams.east, count = height, tx = _ => width - 1, ty = k => k },
        };

        foreach (var edge in edgeDefs)
        {
            bool hasPort = false;
            for (int k = 0; k < edge.count; k++)
            {
                int tx = edge.tx(k);
                int ty = edge.ty(k);
                if (isWalkable(tiles[tileIndex(width, tx, ty)]))
                {
                    hasPort = true;
                    break;
                }
            }
            if (seams.requireWalkablePort == true && !hasPort)
            {
                issue(
                    issues,
                    TerrainValidationCode.SeamPort,
                    $"Chunk edge {edge.name} has no walkable seam port.",
                    layer: "seam",
                    meta: Meta(("edge", edge.name)));
            }

            if (edge.cells == null) continue;
            if (edge.cells.Count != edge.count)
            {
                issue(
                    issues,
                    TerrainValidationCode.LayerSize,
                    $"Seam {edge.name} length does not match the edge.",
                    layer: "seam",
                    meta: Meta(("edge", edge.name), ("expected", edge.count), ("actual", edge.cells.Count)));
                continue;
            }

            int emitted = 0;
            for (int k = 0; k < edge.count && emitted < cap; k++)
            {
                int tx = edge.tx(k);
                int ty = edge.ty(k);
                int localIndex = tileIndex(width, tx, ty);
                int localTile = tiles[localIndex];
                var remote = edge.cells[k];
                bool localWalk = isWalkable(localTile);
                bool remoteWalk = isWalkable(remote.tile);
                if (localWalk != remoteWalk)
                {
                    issue(
                        issues,
                        TerrainValidationCode.SeamMismatch,
                        $"Walkability mismatch across {edge.name} seam.",
                        layer: "seam",
                        tx: tx,
                        ty: ty,
                        meta: Meta(("edge", edge.name), ("remoteTile", remote.tile)));
                    emitted++;
                    continue;
                }
                if (!localWalk || remote.elevation == null) continue;
                double localLevel =
                    elevation != null && (uint)localIndex < (uint)elevation.Length ? elevation[localIndex] : 0;
                var connection = TerrainRules.terrainConnectionFromCells(
                    localTile,
                    remote.tile,
                    localLevel,
                    remote.elevation.Value,
                    new TerrainConnectionOptions { maxClimb = maxClimb });
                if (connection.kind == "cliff")
                {
                    issue(
                        issues,
                        TerrainValidationCode.SeamHeightStep,
                        $"Height step across {edge.name} seam exceeds max climb.",
                        layer: "seam",
                        tx: tx,
                        ty: ty,
                        meta: Meta(
                            ("edge", edge.name),
                            ("from", localLevel),
                            ("to", remote.elevation.Value),
                            ("maxClimb", maxClimb)));
                    emitted++;
                }
            }
        }
    }

    public static TerrainValidationResult validateTerrainLayout(
        TerrainLayoutInput input,
        TerrainValidationOptions? options = null)
    {
        options ??= new TerrainValidationOptions();
        var issues = new List<TerrainValidationIssue>();
        int width = input.width;
        int height = input.height;
        var tiles = tilesOf(input);
        var elevation = elevationOf(input);
        var terrain = terrainLayersOf(input);
        int count = width * height;
        int cap = Math.max(1, options.maxIssuesPerRule ?? 24);
        double maxClimb = options.maxClimb ?? TerrainRules.TERRAIN_STANDARD_MAX_CLIMB;
        // The height-step scan, the climbable-reach BFS and the seam-component walk all read movement rules from the
        // SAME materialization inputs — materialize once (lazily) and share it, instead of paying the full O(cells)
        // cell/edge derivation up to three times per validated layout.
        MaterializedTerrain? movementTerrain = null;
        MaterializedTerrain movementTerrainShared() =>
            movementTerrain ??= TerrainModel.materializeTerrainGrid(tiles, width, height, elevation, new TerrainModelOptions
            {
                maxStep = maxClimb,
            });

        if (isArtifact(input) && input.artifact!.schemaVersion != TERRAIN_ARTIFACT_SCHEMA_VERSION)
        {
            issue(
                issues,
                TerrainValidationCode.SchemaVersion,
                "Unsupported terrain artifact schema version.",
                meta: Meta(("expected", TERRAIN_ARTIFACT_SCHEMA_VERSION), ("actual", input.artifact!.schemaVersion)));
        }
        if (terrain != null && terrain.schemaVersion != TERRAIN_ARTIFACT_SCHEMA_VERSION)
        {
            issue(
                issues,
                TerrainValidationCode.SchemaVersion,
                "Unsupported terrain layer schema version.",
                meta: Meta(("expected", TERRAIN_ARTIFACT_SCHEMA_VERSION), ("actual", terrain.schemaVersion)));
        }
        // Width/height are C# ints, so the Number.isInteger half of this check can no longer fail; kept for traceability.
        if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0)
        {
            issue(
                issues,
                TerrainValidationCode.Dimensions,
                "Terrain dimensions must be positive integers.",
                meta: Meta(("width", width), ("height", height)));
            return new TerrainValidationResult { ok = false, issues = issues };
        }
        if (tiles.Length != count)
        {
            issue(issues, TerrainValidationCode.LayerSize, "Tile layer length must equal width * height.",
                layer: "tiles",
                meta: Meta(("expected", count), ("actual", tiles.Length)));
            return new TerrainValidationResult { ok = false, issues = issues };
        }
        if (elevation != null && elevation.Length != count)
        {
            issue(
                issues,
                TerrainValidationCode.LayerSize,
                "Elevation layer length must equal width * height.",
                layer: "elevation",
                meta: Meta(("expected", count), ("actual", elevation.Length)));
        }
        else if (elevation == null && options.requireElevation == true)
        {
            issue(
                issues,
                TerrainValidationCode.LayerSize,
                "Elevation layer is required for this terrain contract.",
                layer: "elevation");
        }
        if (terrain?.surface != null && terrain.surface.Length != count)
        {
            issue(
                issues,
                TerrainValidationCode.LayerSize,
                "Surface layer length must equal width * height.",
                layer: "surface",
                meta: Meta(("expected", count), ("actual", terrain.surface.Length)));
        }
        if (terrain?.variant != null && terrain.variant.Length != count)
        {
            issue(
                issues,
                TerrainValidationCode.LayerSize,
                "Variant layer length must equal width * height.",
                layer: "variant",
                meta: Meta(("expected", count), ("actual", terrain.variant.Length)));
        }
        if (terrain?.floorUsage != null && terrain.floorUsage.Length != count)
        {
            issue(
                issues,
                TerrainValidationCode.LayerSize,
                "Floor-usage layer length must equal width * height.",
                layer: "floorUsage",
                meta: Meta(("expected", count), ("actual", terrain.floorUsage.Length)));
        }
        if (terrain?.themeIndex != null && terrain.themeIndex.Length != count)
        {
            issue(
                issues,
                TerrainValidationCode.LayerSize,
                "Theme layer length must equal width * height.",
                layer: "theme",
                meta: Meta(("expected", count), ("actual", terrain.themeIndex.Length)));
        }
        IReadOnlyList<string> themePalette = (IReadOnlyList<string>?)terrain?.themePalette ?? System.Array.Empty<string>();
        if (themePalette.Count > TERRAIN_THEME_PALETTE_LIMIT)
        {
            issue(
                issues,
                TerrainValidationCode.ThemePalette,
                $"Theme palette cannot contain more than {Js.Str(TERRAIN_THEME_PALETTE_LIMIT)} entries.",
                layer: "theme",
                meta: Meta(("actual", themePalette.Count)));
        }
        var seenThemes = new HashSet<string>();
        for (int paletteIndex = 0; paletteIndex < themePalette.Count; paletteIndex++)
        {
            string themeKey = themePalette[paletteIndex];
            if (IsJsBlank(themeKey) || seenThemes.Contains(themeKey))
            {
                issue(
                    issues,
                    TerrainValidationCode.ThemePalette,
                    IsJsBlank(themeKey)
                        ? "Theme palette keys must not be blank."
                        : $"Theme palette key \"{themeKey}\" is duplicated.",
                    layer: "theme",
                    index: paletteIndex,
                    meta: Meta(("themeKey", themeKey)));
            }
            seenThemes.Add(themeKey);
        }
        if (terrain?.themeIndex != null && terrain.themeIndex.Length == count)
        {
            int badThemeIndices = 0;
            for (int i = 0; i < terrain.themeIndex.Length; i++)
            {
                int paletteIndex = terrain.themeIndex[i];
                if (paletteIndex == TERRAIN_THEME_INHERIT || paletteIndex < themePalette.Count) continue;
                if (badThemeIndices < cap)
                {
                    issue(
                        issues,
                        TerrainValidationCode.ThemeIndex,
                        "Theme cell references a missing palette entry.",
                        layer: "theme",
                        index: i,
                        tx: i % width,
                        ty: (int)Math.floor((double)i / width),
                        meta: Meta(("paletteIndex", paletteIndex), ("paletteSize", themePalette.Count)));
                }
                badThemeIndices++;
            }
        }

        int badTiles = 0;
        for (int i = 0; i < tiles.Length; i++)
        {
            if (validTileValue(tiles[i])) continue;
            if (badTiles < cap)
            {
                issue(issues, TerrainValidationCode.TileKind, "Unknown TileType value.",
                    layer: "tiles",
                    index: i,
                    tx: i % width,
                    ty: (int)Math.floor((double)i / width),
                    meta: Meta(("value", tiles[i])));
            }
            badTiles++;
        }

        foreach (var marker in markersOf(input))
        {
            if (!inBounds(width, height, marker.tx, marker.ty))
            {
                issue(issues, TerrainValidationCode.MarkerBounds, "Terrain marker is outside the grid.",
                    layer: "markers",
                    tx: marker.tx,
                    ty: marker.ty,
                    roomId: marker.roomId,
                    meta: Meta(("type", marker.type)));
                continue;
            }
            if (marker.type == TerrainMarkerType.OverheadBeam)
            {
                validateOverheadBeamMarker(issues, marker, tiles, elevation, width, height);
            }
        }

        foreach (var decoration in (IReadOnlyList<TerrainDecorationPlacement>?)terrain?.decorations ?? System.Array.Empty<TerrainDecorationPlacement>())
        {
            if (!isTerrainDecorationKind(decoration.kind))
            {
                issue(issues, TerrainValidationCode.DecorationKind, "Unknown terrain decoration kind.",
                    layer: "decorations",
                    tx: decoration.tx,
                    ty: decoration.ty,
                    meta: Meta(("kind", decoration.kind ?? "undefined")));
                continue;
            }
            if (!inBounds(width, height, decoration.tx, decoration.ty))
            {
                issue(
                    issues,
                    TerrainValidationCode.DecorationBounds,
                    "Terrain decoration is outside the grid.",
                    layer: "decorations",
                    tx: decoration.tx,
                    ty: decoration.ty,
                    meta: Meta(("kind", decoration.kind)));
            }
            else
            {
                int index = tileIndex(width, decoration.tx, decoration.ty);
                if (!terrainTileAcceptsDecoration(decoration.kind, tiles[index]))
                {
                    issue(
                        issues,
                        TerrainValidationCode.DecorationSurface,
                        "Terrain decoration stands on ground its kind cannot grow on.",
                        layer: "decorations",
                        tx: decoration.tx,
                        ty: decoration.ty,
                        index: index,
                        meta: Meta(("kind", decoration.kind), ("tile", tiles[index])));
                }
            }
            if (decoration.themeKey != null && IsJsBlank(decoration.themeKey))
            {
                issue(issues, TerrainValidationCode.DecorationTheme, "Terrain decoration theme is empty.",
                    layer: "decorations",
                    tx: decoration.tx,
                    ty: decoration.ty);
            }
        }

        if (badTiles > 0) return new TerrainValidationResult { ok = false, issues = issues };

        validateDepthTileStructures(issues, tiles, elevation, width, height, cap);

        validateBridgeComponents(
            issues,
            tiles,
            width,
            height,
            cap,
            options.allowPartialSeamBridgeSpans ?? false);
        int bridgeWidth = options.minimumBridgeWidth ?? 2;
        if (bridgeWidth > 1)
        {
            int emittedBridgeWidth = 0;
            var seamBridgeComponents = new JsMap<int, bool>();
            foreach (var bridgeIssue in TerrainBridge.findBridgeWidthIssues(
                tiles,
                width,
                height,
                bridgeWidth,
                Number.POSITIVE_INFINITY))
            {
                if (
                    options.allowPartialSeamBridgeSpans == true &&
                    (isNearSeamStub(width, height, bridgeIssue.tx, bridgeIssue.ty) ||
                        (seamBridgeComponents.TryGetValue(bridgeIssue.component, out bool knownSeam)
                            ? knownSeam
                            : bridgeComponentTouchesSeamStub(tiles, width, height, bridgeIssue.tx, bridgeIssue.ty))))
                {
                    seamBridgeComponents.set(bridgeIssue.component, true);
                    continue;
                }
                seamBridgeComponents.set(bridgeIssue.component, false);
                if (emittedBridgeWidth >= cap) break;
                issue(
                    issues,
                    TerrainValidationCode.BridgeWidth,
                    "Bridge deck is thinner than the minimum width.",
                    layer: "tiles",
                    tx: bridgeIssue.tx,
                    ty: bridgeIssue.ty,
                    index: bridgeIssue.index,
                    meta: Meta(
                        ("component", bridgeIssue.component),
                        ("axis", bridgeIssue.axis),
                        ("horizontalNeighbours", bridgeIssue.horizontalNeighbours),
                        ("verticalNeighbours", bridgeIssue.verticalNeighbours),
                        ("minimumBridgeWidth", bridgeWidth)));
                emittedBridgeWidth++;
            }
        }

        if (elevation != null)
        {
            // Water tiles store their authored terrace datum. The shared model applies the constant surface inset;
            // values outside the standard terrain ladder would create a phantom basin or an implausible sky sheet.
            int emittedWater = 0;
            for (int i = 0; i < count && emittedWater < cap; i++)
            {
                int tile = tiles[i];
                if (tile != TileType.Water) continue;
                double level = (uint)i < (uint)elevation.Length ? elevation[i] : 0;
                if (level >= TerrainModel.WATER_MIN_STORED_LEVEL && level <= TerrainModel.WATER_MAX_STORED_LEVEL) continue;
                issue(
                    issues,
                    TerrainValidationCode.WaterStoredElevation,
                    $"Water tile must store a terrace datum from {Js.Str(TerrainModel.WATER_MIN_STORED_LEVEL)} through {Js.Str(TerrainModel.WATER_MAX_STORED_LEVEL)}.",
                    layer: "elevation",
                    index: i,
                    tx: i % width,
                    ty: (int)Math.floor((double)i / width),
                    meta: Meta(
                        ("tile", tile),
                        ("level", level),
                        ("minLevel", (double)TerrainModel.WATER_MIN_STORED_LEVEL),
                        ("maxLevel", (double)TerrainModel.WATER_MAX_STORED_LEVEL)));
                emittedWater++;
            }

            int emittedChasm = 0;
            for (int i = 0; i < count && emittedChasm < cap; i++)
            {
                if (tiles[i] != TileType.Chasm) continue;
                double depth = (uint)i < (uint)elevation.Length ? elevation[i] : 0;
                if (depth >= TerrainModel.CHASM_MIN_DEPTH && depth <= TerrainModel.CHASM_MAX_DEPTH) continue;
                issue(
                    issues,
                    TerrainValidationCode.ChasmStoredDepth,
                    "Chasm tile must store a depth from 5 through 8.",
                    layer: "elevation",
                    index: i,
                    tx: i % width,
                    ty: (int)Math.floor((double)i / width),
                    meta: Meta(
                        ("tile", TileType.Chasm),
                        ("depth", depth),
                        ("minDepth", (double)TerrainModel.CHASM_MIN_DEPTH),
                        ("maxDepth", (double)TerrainModel.CHASM_MAX_DEPTH)));
                emittedChasm++;
            }

            int emitted = 0;
            var terrain2 = movementTerrainShared();
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int idx = tileIndex(width, tx, ty);
                    if (!isWalkable(tiles[idx])) continue;
                    foreach (var (dx, dy) in FORWARD_DIRS)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        if (!isWalkable(tiles[ni])) continue;
                        var fromCell = terrain2.cells[idx];
                        var toCell = terrain2.cells[ni];
                        if (fromCell == null || toCell == null || TerrainModel.terrainMovementRuleFor(fromCell, toCell, maxClimb).passable)
                            continue;
                        double fromLevel = TerrainModel.walkHeightForTerrainCell(fromCell) ?? fromCell.surfaceZ;
                        double toLevel = TerrainModel.walkHeightForTerrainCell(toCell) ?? toCell.surfaceZ;
                        double diff = Math.abs(toLevel - fromLevel);
                        if (emitted < cap)
                        {
                            issue(
                                issues,
                                TerrainValidationCode.HeightStep,
                                $"Walkable height edge {Js.Str(tiles[idx])}@{ElevationStr(elevation, idx)} → {Js.Str(tiles[ni])}@{ElevationStr(elevation, ni)} exceeds max climb.",
                                layer: "elevation",
                                tx: tx,
                                ty: ty,
                                meta: Meta(
                                    ("nx", nx),
                                    ("ny", ny),
                                    ("fromTile", tiles[idx]),
                                    ("toTile", tiles[ni]),
                                    ("from", (uint)idx < (uint)elevation.Length ? (double?)elevation[idx] : null),
                                    ("to", (uint)ni < (uint)elevation.Length ? (double?)elevation[ni] : null),
                                    ("diff", diff),
                                    ("maxClimb", maxClimb)));
                        }
                        emitted++;
                    }
                }
            }
        }

        bool requireConnected = options.requireConnected ?? true;
        string? disconnectedSeverity = diagnosticSeverity(options.walkableDisconnectedSeverity, "error");
        var first = startAnchor(input, options) ?? firstWalkable(tiles, width, height);
        if (first == null)
        {
            issue(issues, TerrainValidationCode.NoWalkable, "Terrain has no walkable tiles.",
                layer: "tiles");
        }
        else
        {
            var reach = computeClimbableReach(
                tiles,
                elevation,
                width,
                height,
                first,
                maxClimb,
                movementTerrainShared());
            if (requireConnected && !string.IsNullOrEmpty(disconnectedSeverity))
            {
                int emitted = 0;
                if (options.allowPartialSeamBridgeSpans == true)
                {
                    var terrain3 = movementTerrainShared();
                    var @checked = new byte[tiles.Length];
                    var stack = new List<int>();
                    var component = new List<int>();
                    for (int i = 0; i < tiles.Length; i++)
                    {
                        if (@checked[i] != 0 || !isWalkable(tiles[i]) || reach[i] != 0) continue;
                        @checked[i] = 1;
                        stack.push(i);
                        component.Clear();
                        bool touchesSeamStub = false;
                        while (stack.Count > 0)
                        {
                            int idx = stack.pop();
                            component.push(idx);
                            int tx = idx % width;
                            int ty = (int)Math.floor((double)idx / width);
                            touchesSeamStub = touchesSeamStub || isNearSeamStub(width, height, tx, ty);
                            foreach (var (dx, dy) in FOUR_DIRS)
                            {
                                int nx = tx + dx;
                                int ny = ty + dy;
                                if (!inBounds(width, height, nx, ny)) continue;
                                int ni = tileIndex(width, nx, ny);
                                if (@checked[ni] != 0 || reach[ni] != 0 || !isWalkable(tiles[ni])) continue;
                                var fromCell = terrain3.cells[idx];
                                var toCell = terrain3.cells[ni];
                                if (
                                    fromCell == null ||
                                    toCell == null ||
                                    !TerrainModel.terrainMovementRuleFor(fromCell, toCell, maxClimb).passable)
                                    continue;
                                @checked[ni] = 1;
                                stack.push(ni);
                            }
                        }
                        if (touchesSeamStub) continue;
                        foreach (int idx in component)
                        {
                            if (emitted >= cap) break;
                            issue(
                                issues,
                                TerrainValidationCode.WalkableDisconnected,
                                "Walkable tile is not in the climbable component.",
                                severity: disconnectedSeverity,
                                layer: "tiles",
                                index: idx,
                                tx: idx % width,
                                ty: (int)Math.floor((double)idx / width));
                            emitted++;
                        }
                        if (emitted >= cap) break;
                    }
                }
                else
                {
                    for (int i = 0; i < tiles.Length; i++)
                    {
                        if (!isWalkable(tiles[i]) || reach[i] != 0) continue;
                        if (emitted < cap)
                        {
                            issue(
                                issues,
                                TerrainValidationCode.WalkableDisconnected,
                                "Walkable tile is not in the climbable component.",
                                severity: disconnectedSeverity,
                                layer: "tiles",
                                index: i,
                                tx: i % width,
                                ty: (int)Math.floor((double)i / width));
                        }
                        emitted++;
                    }
                }
            }

            bool requireStartBoss =
                options.requireStartBossReachable ??
                (bossAnchor(input, options) != null && startAnchor(input, options) != null);
            if (requireStartBoss)
            {
                var start = startAnchor(input, options);
                if (start != null)
                {
                    int si = inBounds(width, height, start.tx, start.ty)
                        ? tileIndex(width, start.tx, start.ty)
                        : -1;
                    if (si < 0 || reach[si] == 0)
                    {
                        issue(
                            issues,
                            TerrainValidationCode.StartUnreachable,
                            "Start anchor is not on reachable walkable terrain.",
                            layer: "markers",
                            tx: start.tx,
                            ty: start.ty);
                    }
                }
                var boss = bossAnchor(input, options);
                if (boss != null)
                {
                    int bi = inBounds(width, height, boss.tx, boss.ty)
                        ? tileIndex(width, boss.tx, boss.ty)
                        : -1;
                    if (bi < 0 || reach[bi] == 0)
                    {
                        issue(
                            issues,
                            TerrainValidationCode.BossUnreachable,
                            "Boss anchor is not reachable from the start.",
                            layer: "markers",
                            tx: boss.tx,
                            ty: boss.ty);
                    }
                }
            }
        }

        int footprint = options.minimumFootprint ?? 0;
        string? footprintSeverity = diagnosticSeverity(options.minimumFootprintSeverity, "error");
        if (footprint >= 3 && !string.IsNullOrEmpty(footprintSeverity))
        {
            int emitted = 0;
            for (int ty = 0; ty < height && emitted < cap; ty++)
            {
                for (int tx = 0; tx < width && emitted < cap; tx++)
                {
                    int idx = tileIndex(width, tx, ty);
                    if (!isWalkable(tiles[idx])) continue;
                    if (
                        TerrainKit.hasMinimumWalkableFootprintAt(tiles, width, height, tx, ty, new WalkableFootprintOptions
                        {
                            size = footprint,
                            minBorder = options.minimumFootprintBorder ?? 0,
                            includeBridge = true,
                        }))
                        continue;
                    issue(
                        issues,
                        TerrainValidationCode.MinimumFootprint,
                        "Walkable tile lacks the required local footprint.",
                        severity: footprintSeverity,
                        layer: "tiles",
                        tx: tx,
                        ty: ty,
                        meta: Meta(("footprint", footprint)));
                    emitted++;
                }
            }
        }

        if (options.seams != null)
            validateSeams(issues, tiles, elevation, width, height, options.seams, maxClimb, cap);

        return new TerrainValidationResult { ok = issues.every(i => i.severity != "error"), issues = issues };
    }

    private static int? roomMarkerType(DungeonRoom room)
    {
        if (room.type == DungeonRoomType.Start) return TerrainMarkerType.Start;
        if (room.type == DungeonRoomType.Boss) return TerrainMarkerType.Boss;
        if (room.type == DungeonRoomType.Objective) return TerrainMarkerType.Objective;
        return null;
    }

    private static TerrainMarker? roomMarker(DungeonLayout layout, DungeonRoom room)
    {
        int? type = roomMarkerType(room);
        if (type == null) return null;
        return new TerrainMarker
        {
            type = type.Value,
            tx = (int)Math.floor(room.rect.tx + (double)room.rect.tw / 2),
            ty = (int)Math.floor(room.rect.ty + (double)room.rect.th / 2),
            roomId = room.id,
            id =
                type == TerrainMarkerType.Start
                    ? "start"
                    : type == TerrainMarkerType.Boss
                        ? "boss"
                        : $"objective:{Js.Str(room.id)}",
        };
    }

    private static TerrainMarker? doorMarker(DungeonLayout layout, DungeonDoor door)
    {
        if (door.tiles.Count > 0)
        {
            int sx = 0;
            int sy = 0;
            foreach (var t in door.tiles)
            {
                sx += t.tx;
                sy += t.ty;
            }
            return new TerrainMarker
            {
                type = TerrainMarkerType.Door,
                tx = (int)Math.round((double)sx / door.tiles.Count),
                ty = (int)Math.round((double)sy / door.tiles.Count),
                id = $"door:{Js.Str(door.id)}",
                roomId = door.roomA >= 0 ? door.roomA : door.roomB,
            };
        }

        return new TerrainMarker
        {
            type = TerrainMarkerType.Door,
            tx = (int)Math.floor((door.x - layout.originX) / layout.tileSize),
            ty = (int)Math.floor((door.y - layout.originY) / layout.tileSize),
            id = $"door:{Js.Str(door.id)}",
            roomId = door.roomA >= 0 ? door.roomA : door.roomB,
        };
    }

    private static string markerKey(TerrainMarker marker)
    {
        return $"{Js.Str(marker.type)}:{Js.Str(marker.tx)}:{Js.Str(marker.ty)}:{Js.Str(marker.axis ?? -1)}:{Js.Str(marker.length ?? -1)}:{Js.Str(marker.roomId ?? -1)}:{marker.id ?? ""}";
    }

    private static List<TerrainMarker> standardMarkersForLayout(DungeonLayout layout)
    {
        var markers = new List<TerrainMarker>();
        var seen = new HashSet<string>();
        void push(TerrainMarker? marker)
        {
            if (marker == null) return;
            string key = markerKey(marker);
            if (seen.Contains(key)) return;
            seen.Add(key);
            markers.push(marker);
        }

        foreach (var marker in (IReadOnlyList<TerrainMarker>?)layout.terrain?.markers ?? System.Array.Empty<TerrainMarker>()) push(marker.Clone());
        foreach (var room in layout.rooms) push(roomMarker(layout, room));
        foreach (var door in layout.doors) push(doorMarker(layout, door));
        return markers;
    }

    private static List<DungeonRoom> markerRooms(
        TerrainArtifact artifact,
        double tileSize,
        double originX,
        double originY)
    {
        var rooms = new List<DungeonRoom>();
        foreach (var marker in (IReadOnlyList<TerrainMarker>?)artifact.markers ?? System.Array.Empty<TerrainMarker>())
        {
            if (marker.type != TerrainMarkerType.Start && marker.type != TerrainMarkerType.Boss) continue;
            int id = rooms.Count;
            rooms.push(new DungeonRoom
            {
                id = id,
                type = marker.type == TerrainMarkerType.Start ? DungeonRoomType.Start : DungeonRoomType.Boss,
                rect = new TileRect(marker.tx, marker.ty, 1, 1),
                cx = originX + (marker.tx + 0.5) * tileSize,
                cy = originY + (marker.ty + 0.5) * tileSize,
                doorIds = new List<int>(),
                threat = marker.type == TerrainMarkerType.Boss ? 1 : 0,
            });
        }
        return rooms;
    }

    private static DungeonTerrainLayers? terrainLayersFromArtifact(TerrainArtifact artifact)
    {
        // The compiler calls this only with its private `cloneArtifactForCompile` result. Transfer those already
        // isolated lanes into the returned layout instead of cloning every typed array/object collection a second
        // time; caller-owned source artifacts remain completely detached while streamed generation avoids needless
        // short-lived allocations immediately before publication.
        return new DungeonTerrainLayers
        {
            schemaVersion = artifact.schemaVersion,
            tilesetId = artifact.tilesetId,
            surface = artifact.surface,
            variant = artifact.variant,
            floorUsage = artifact.floorUsage,
            themePalette = artifact.themePalette,
            themeIndex = artifact.themeIndex,
            markers = artifact.markers,
            decorations = artifact.decorations,
        };
    }

    private static JsSet<int> protectedMarkerCells(TerrainArtifact artifact)
    {
        var cells = new JsSet<int>();
        foreach (var marker in (IReadOnlyList<TerrainMarker>?)artifact.markers ?? System.Array.Empty<TerrainMarker>())
        {
            if (!inBounds(artifact.width, artifact.height, marker.tx, marker.ty)) continue;
            if (
                marker.type == TerrainMarkerType.OverheadBeam &&
                (marker.axis == TerrainMarkerAxis.Horizontal ||
                    marker.axis == TerrainMarkerAxis.Vertical) &&
                marker.length != null && Number.isInteger(marker.length.Value))
            {
                for (int offset = 0; offset < Math.max(1, marker.length ?? 1); offset++)
                {
                    var cell = markerCell(marker, offset);
                    if (inBounds(artifact.width, artifact.height, cell.tx, cell.ty))
                    {
                        cells.add(tileIndex(artifact.width, cell.tx, cell.ty));
                    }
                }
                continue;
            }
            cells.add(tileIndex(artifact.width, marker.tx, marker.ty));
        }
        return cells;
    }

    public static TerrainCompileResult compileTerrainArtifactToDungeonLayout(
        TerrainArtifact artifact,
        TerrainValidationOptions? options = null)
    {
        options ??= new TerrainValidationOptions();
        var prepared = TerrainArtifactClone.cloneTerrainArtifactForCompile(artifact);
        TerrainRules.normalizeTerrainStoredElevationInPlace(
            prepared.baseTiles,
            prepared.elevation,
            prepared.width,
            prepared.height);
        var protectedCells = protectedMarkerCells(prepared);
        var materialized = TerrainKit.materializeUnclimbableWalkableEdges(
            prepared.baseTiles,
            prepared.elevation,
            prepared.width,
            prepared.height,
            new MaterializeUnclimbableEdgesOptions
            {
                maxClimb = options.maxClimb ?? TerrainRules.TERRAIN_STANDARD_MAX_CLIMB,
                protect = (_, __, index) => protectedCells.has(index),
            });
        foreach (int idx in materialized)
        {
            // Typed-array stores outside the array are silently dropped in JS; the layer length is only
            // validated afterwards, so guard the store.
            if (prepared.surface != null && (uint)idx < (uint)prepared.surface.Length) prepared.surface[idx] = TerrainSurface.Auto;
            if (prepared.variant != null && (uint)idx < (uint)prepared.variant.Length) prepared.variant[idx] = 0;
        }
        var validation = validateTerrainLayout(prepared, options);
        if (!validation.ok) return new TerrainCompileResult { validation = validation };

        double tileSize = prepared.tileSize ?? Grid.TILE_SIZE;
        int width = prepared.width;
        int height = prepared.height;
        double originX = prepared.originX ?? -(width * tileSize) / 2;
        double originY = prepared.originY ?? -(height * tileSize) / 2;
        var rooms = prepared.rooms ?? markerRooms(prepared, tileSize, originX, originY);
        int startRoom =
            prepared.startRoomId ?? rooms.find(r => r.type == DungeonRoomType.Start)?.id ?? -1;
        int bossRoom =
            prepared.bossRoomId ?? rooms.find(r => r.type == DungeonRoomType.Boss)?.id ?? startRoom;

        return new TerrainCompileResult
        {
            validation = validation,
            layout = new DungeonLayout
            {
                index = prepared.index ?? 0,
                seed = prepared.seed ?? 0,
                style = prepared.style ?? 0,
                biomeKey = prepared.biomeKey ?? "terrain",
                tier = prepared.tier ?? 1,
                tileSize = tileSize,
                width = width,
                height = height,
                originX = originX,
                originY = originY,
                tiles = prepared.baseTiles,
                elevation = prepared.elevation,
                terrain = terrainLayersFromArtifact(prepared),
                rooms = rooms,
                doors = prepared.doors ?? new List<DungeonDoor>(),
                startRoomId = startRoom,
                bossRoomId = bossRoom,
            },
        };
    }

    public static TerrainArtifact terrainArtifactFromDungeonLayout(DungeonLayout layout)
    {
        return new TerrainArtifact
        {
            schemaVersion = layout.terrain?.schemaVersion ?? TERRAIN_ARTIFACT_SCHEMA_VERSION,
            width = layout.width,
            height = layout.height,
            tileSize = layout.tileSize,
            originX = layout.originX,
            originY = layout.originY,
            index = layout.index,
            seed = layout.seed,
            style = layout.style,
            biomeKey = layout.biomeKey,
            tier = layout.tier,
            baseTiles = layout.tiles,
            elevation = layout.elevation,
            surface = layout.terrain?.surface,
            variant = layout.terrain?.variant,
            floorUsage = layout.terrain?.floorUsage,
            themePalette = layout.terrain?.themePalette != null ? new List<string>(layout.terrain.themePalette) : null,
            themeIndex = layout.terrain?.themeIndex,
            tilesetId = layout.terrain?.tilesetId,
            markers = standardMarkersForLayout(layout),
            decorations = layout.terrain?.decorations?.map(decoration => decoration.Clone()),
            rooms = layout.rooms,
            doors = layout.doors,
            startRoomId = layout.startRoomId,
            bossRoomId = layout.bossRoomId,
        };
    }

    public static string summarizeTerrainValidation(TerrainValidationResult result, int maxIssues = 6)
    {
        if (result.issues.Count == 0) return "no terrain validation issues";
        return result.issues
            .slice(0, maxIssues)
            .map(i =>
            {
                string at = i.tx != null && i.ty != null ? $" at {Js.Str(i.tx.Value)},{Js.Str(i.ty.Value)}" : "";
                return $"{i.code}{at}: {i.message}";
            })
            .join("; ");
    }

    /// <summary>`const { context, ...validationOptions } = options` — every validation field except `context`.</summary>
    private static TerrainValidationOptions validationOptionsOf(TerrainFinalizeOptions options)
    {
        return new TerrainValidationOptions
        {
            requireConnected = options.requireConnected,
            requireElevation = options.requireElevation,
            requireStartBossReachable = options.requireStartBossReachable,
            maxClimb = options.maxClimb,
            walkableDisconnectedSeverity = options.walkableDisconnectedSeverity,
            minimumFootprint = options.minimumFootprint,
            minimumFootprintBorder = options.minimumFootprintBorder,
            minimumFootprintSeverity = options.minimumFootprintSeverity,
            start = options.start,
            boss = options.boss,
            seams = options.seams,
            allowPartialSeamBridgeSpans = options.allowPartialSeamBridgeSpans,
            minimumBridgeWidth = options.minimumBridgeWidth,
            maxIssuesPerRule = options.maxIssuesPerRule,
        };
    }

    /// <summary>
    /// Mandatory finalization gate for generated/authored world layouts. Generators may paint tiles with their
    /// local algorithms, but exported runtime layouts leave through this compiler so the world always carries the
    /// standard terrain schema, marker layer and shared validation rules. Throws (TS: `throw new Error`) when the
    /// layout fails validation; the Endless generator catches that and emits its safe fallback chunk.
    /// </summary>
    public static DungeonLayout finalizeTerrainLayout(DungeonLayout layout, TerrainFinalizeOptions? options = null)
    {
        options ??= new TerrainFinalizeOptions();
        string? context = options.context;
        var validationOptions = validationOptionsOf(options);
        var compiled = compileTerrainArtifactToDungeonLayout(
            terrainArtifactFromDungeonLayout(layout),
            validationOptions);
        if (compiled.layout == null)
        {
            throw new InvalidOperationException(
                $"{context ?? "finalizeTerrainLayout"}: {summarizeTerrainValidation(compiled.validation)}");
        }
        return compiled.layout;
    }

    /// <summary>Returns a TerrainSurfaceId.</summary>
    public static int terrainSurfaceAt(DungeonLayout layout, int tx, int ty)
    {
        if (!inBounds(layout.width, layout.height, tx, ty)) return TerrainSurface.Stone;
        int idx = tileIndex(layout.width, tx, ty);
        var surface = layout.terrain?.surface;
        int? @explicit = surface != null && (uint)idx < (uint)surface.Length ? (int?)surface[idx] : null;
        if (@explicit != null && @explicit != TerrainSurface.Auto) return @explicit.Value;
        return defaultSurfaceForTile(layout.tiles[idx]);
    }

    /// <summary>Returns a TerrainVariantId.</summary>
    public static int terrainVariantAt(DungeonLayout layout, int tx, int ty)
    {
        if (!inBounds(layout.width, layout.height, tx, ty)) return 0;
        return layerAt(layout.terrain?.variant, layout.width, tx, ty);
    }
}
