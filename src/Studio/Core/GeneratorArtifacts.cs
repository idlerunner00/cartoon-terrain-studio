// Port of editor-side, engine-free helpers of packages/client/src/generator.ts (normalizeCoreArtifact,
// artifactFromEndlessArea, centerOnLayout's fit-view formula, requestedMapSize / requestedSeedSpan, the pure part of
// loadSeed, MAP_PRESETS, CORE_TILES, DECORATION_OPTIONS) and of packages/client/src/generatorSimulationPanel.ts
// (GENERATOR_DEFAULT_THEME, GENERATOR_THEME_OPTIONS) — keep in lockstep with the originals.
using System;
using System.Collections.Generic;
using System.Text;
using Fluitown.Domain;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace TerrainStudio.Core;

/// <summary>One entry of the "Theme Brush" / simulation theme select.</summary>
public sealed class GeneratorThemeOption
{
    public string key = "";
    public string label = "";
}

public static class GeneratorArtifacts
{
    public const int BASE_MAP_WIDTH = 64;
    public const int BASE_MAP_HEIGHT = 48;
    public const int MIN_MAP_WIDTH = 16;
    public const int MIN_MAP_HEIGHT = 16;
    public const int MAX_MAP_WIDTH = 384;
    public const int MAX_MAP_HEIGHT = 256;
    public const int MAX_MAP_CELLS = MAX_MAP_WIDTH * MAX_MAP_HEIGHT;

    // ── Theme options (generatorSimulationPanel.ts) ───────────────────────────────────────────────────

    /// <summary>The map studio starts from the same visual identity as the live Flui world.</summary>
    public const string GENERATOR_DEFAULT_THEME = WorldIdentity.SHARED_WORLD_BIOME;

    /// <summary>
    /// TS `generatorThemeKeys()` (mapSimulation.ts), inlined so this file does not depend on the in-progress
    /// MapSimulation port: the ENDLESS_DRESSING_THEMES keys in insertion order minus GENERATOR_ONLY_DRESSING_THEMES.
    /// </summary>
    public static IReadOnlyList<string> generatorThemeKeys()
    {
        var keys = new List<string>();
        foreach (string key in EndlessDressing.ENDLESS_DRESSING_THEMES.keys())
        {
            if (!EndlessDressing.GENERATOR_ONLY_DRESSING_THEMES.has(key)) keys.Add(key);
        }
        return keys;
    }

    /// <summary>`key.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase())`.</summary>
    public static string runThemeLabel(string key)
    {
        string spaced = key.Replace('_', ' ');
        var sb = new StringBuilder(spaced.Length);
        for (int i = 0; i < spaced.Length; i++)
        {
            char c = spaced[i];
            bool word = isJsWordChar(c);
            bool boundary = word && (i == 0 || !isJsWordChar(spaced[i - 1]));
            // JS toUpperCase on the matched word character; \w is ASCII-only, so ASCII upper-casing is exact.
            sb.Append(boundary && c >= 'a' && c <= 'z' ? (char)(c - 32) : c);
        }
        return sb.ToString();
    }

    private static bool isJsWordChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    private static IReadOnlyList<GeneratorThemeOption>? generatorThemeOptions;

    /// <summary>
    /// The themes this generator offers, read from the registry: the legacy neutral mountain, every run theme with a
    /// dressing registry row (minus generator-only dressings), the Aegis citadel, then the generator-only biomes of
    /// the render theme registry (`Fluitown.Render.Theme.generatorOnlyBiomes()`).
    /// </summary>
    public static IReadOnlyList<GeneratorThemeOption> GENERATOR_THEME_OPTIONS =>
        generatorThemeOptions ??= buildGeneratorThemeOptions();

    private static IReadOnlyList<GeneratorThemeOption> buildGeneratorThemeOptions()
    {
        var options = new List<GeneratorThemeOption>
        {
            new() { key = "mountain", label = "Neutral Mountain (legacy)" },
        };
        foreach (string key in generatorThemeKeys()) options.Add(new GeneratorThemeOption { key = key, label = runThemeLabel(key) });
        options.Add(new GeneratorThemeOption { key = "raid_holdthefort", label = "Aegis · White Citadel" });
        foreach (var biome in Fluitown.Render.Theme.generatorOnlyBiomes())
        {
            options.Add(new GeneratorThemeOption { key = biome.key, label = $"Generator Exclusive · {biome.name}" });
        }
        return options;
    }

    // ── Artifact helpers (generator.ts) ───────────────────────────────────────────────────────────────

    private static bool isCoreTile(int tile) =>
        tile == TileType.Floor ||
        tile == TileType.Solid ||
        tile == TileType.Water ||
        tile == TileType.Bridge ||
        tile == TileType.Chasm ||
        tile == TileType.Cleft ||
        tile == TileType.Underpass;

    /// <summary>
    /// Mutates and returns the artifact so it only contains what the core editor authors: the seven core tiles,
    /// bounded core decorations, no markers / rooms / doors / tileset, zeroed surface and variant layers.
    /// </summary>
    public static TerrainArtifact normalizeCoreArtifact(TerrainArtifact artifact)
    {
        // Inlined `ensureTerrainEditorLayers(artifact)` (editor.ts; ported in TerrainEditor.cs) so this file compiles
        // without that port: (re)allocate elevation/surface/variant when missing or mis-sized, stamp the schema.
        int count = artifact.width * artifact.height;
        if (artifact.elevation == null || artifact.elevation.Length != count) artifact.elevation = new sbyte[count];
        if (artifact.surface == null || artifact.surface.Length != count) artifact.surface = new byte[count];
        if (artifact.variant == null || artifact.variant.Length != count) artifact.variant = new byte[count];
        artifact.schemaVersion = DungeonTypes.TERRAIN_ARTIFACT_SCHEMA_VERSION;

        if (string.IsNullOrEmpty(artifact.biomeKey)) artifact.biomeKey = "mountain";
        artifact.tilesetId = null;
        artifact.markers = new List<TerrainMarker>();
        // tx/ty are ints in the C# model, so TS `Number.isInteger(tx/ty)` always holds.
        artifact.decorations = (artifact.decorations ?? new List<TerrainDecorationPlacement>()).filter(
            decoration =>
                WorldDecoration.isTerrainDecorationKind(decoration.kind) &&
                decoration.tx >= 0 &&
                decoration.ty >= 0 &&
                decoration.tx < artifact.width &&
                decoration.ty < artifact.height);
        artifact.rooms = new List<DungeonRoom>();
        artifact.doors = new List<DungeonDoor>();
        artifact.startRoomId = -1;
        artifact.bossRoomId = -1;
        if (artifact.surface != null) Array.Fill(artifact.surface, (byte)0);
        if (artifact.variant != null) Array.Fill(artifact.variant, (byte)0);
        for (int i = 0; i < artifact.baseTiles.Length; i++)
        {
            if (isCoreTile(artifact.baseTiles[i])) continue;
            artifact.baseTiles[i] = TileType.Solid;
        }
        return artifact;
    }

    /// <summary>
    /// Stitches a `span × span` block of endless chunks centred on chunk (cx, cy) into one finite artifact
    /// (tiles + elevation only; the first chunk supplies tile size, origin, seed, style, biome and tier), then
    /// <see cref="normalizeCoreArtifact"/>.
    /// </summary>
    public static TerrainArtifact artifactFromEndlessArea(DungeonDescriptor descriptor, int cx, int cy, int span)
    {
        int radius = (int)Math.floor(span / 2.0);
        var first = Descriptor.generateEndlessChunk(descriptor, cx - radius, cy - radius);
        int width = first.width * span;
        int height = first.height * span;
        var artifact = TerrainEditor.createTerrainArtifact(new CreateTerrainArtifactOptions
        {
            width = width,
            height = height,
            tileSize = first.tileSize,
            originX = first.originX,
            originY = first.originY,
            seed = first.seed,
            style = first.style,
            biomeKey = first.biomeKey,
            tier = first.tier,
            fillTile = TileType.Solid,
            borderTile = TileType.Solid,
            withDefaultMarkers = false,
        });
        for (int sy = 0; sy < span; sy++)
        {
            for (int sx = 0; sx < span; sx++)
            {
                int chunkX = cx - radius + sx;
                int chunkY = cy - radius + sy;
                var layout = sx == 0 && sy == 0 ? first : Descriptor.generateEndlessChunk(descriptor, chunkX, chunkY);
                int ox = sx * first.width;
                int oy = sy * first.height;
                for (int ty = 0; ty < layout.height; ty++)
                {
                    for (int tx = 0; tx < layout.width; tx++)
                    {
                        int src = ty * layout.width + tx;
                        int dst = (oy + ty) * artifact.width + (ox + tx);
                        // `tileIndexLocal(width, …)` is unchecked in TS and typed-array stores past the end are dropped.
                        if ((uint)dst >= (uint)artifact.baseTiles.Length) continue;
                        artifact.baseTiles[dst] = layout.tiles[src];
                        artifact.elevation![dst] = layout.elevation != null && src < layout.elevation.Length
                            ? layout.elevation[src]
                            : (sbyte)0;
                    }
                }
            }
        }
        return normalizeCoreArtifact(artifact);
    }

    /// <summary>
    /// TS `requestedMapSize` without the DOM: floor + clamp each side (NaN/0 → the 64 × 48 base), then scale both
    /// down uniformly when the area exceeds <see cref="MAX_MAP_CELLS"/>.
    /// </summary>
    public static (int width, int height) clampMapSize(double requestedWidth, double requestedHeight)
    {
        double width = clamp(
            Math.floor(Js.Truthy(requestedWidth) ? requestedWidth : BASE_MAP_WIDTH),
            MIN_MAP_WIDTH,
            MAX_MAP_WIDTH);
        double height = clamp(
            Math.floor(Js.Truthy(requestedHeight) ? requestedHeight : BASE_MAP_HEIGHT),
            MIN_MAP_HEIGHT,
            MAX_MAP_HEIGHT);
        if (width * height > MAX_MAP_CELLS)
        {
            double scale = Math.sqrt(MAX_MAP_CELLS / (width * height));
            width = clamp(Math.floor(width * scale), MIN_MAP_WIDTH, MAX_MAP_WIDTH);
            height = clamp(Math.floor(height * scale), MIN_MAP_HEIGHT, MAX_MAP_HEIGHT);
        }
        return ((int)width, (int)height);
    }

    private static double clamp(double v, double lo, double hi) => Math.max(lo, Math.min(hi, v));
}
