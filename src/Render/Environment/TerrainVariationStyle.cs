// Port of packages/client/src/render/environment/terrainVariationStyle.ts — keep in lockstep with the original.

namespace Fluitown.Render;

// `export type TerrainWallSegmentVariant = 'quiet' | 'layered' | 'mineral' | 'fractured';` → string.

public sealed class TerrainWallSegmentProfile
{
    /// <summary>TerrainWallSegmentVariant: "quiet" | "layered" | "mineral" | "fractured".</summary>
    public readonly string kind;
    public readonly double bandDensity;
    public readonly double verticalDrift;
    public readonly double thickness;
    public readonly double mineralChance;
    public readonly double spanMin;
    public readonly double spanRange;
    public readonly double centreSpread;
    public readonly double tilt;
    public readonly double inkPresence;

    public TerrainWallSegmentProfile(
        string kind,
        double bandDensity,
        double verticalDrift,
        double thickness,
        double mineralChance,
        double spanMin,
        double spanRange,
        double centreSpread,
        double tilt,
        double inkPresence)
    {
        this.kind = kind;
        this.bandDensity = bandDensity;
        this.verticalDrift = verticalDrift;
        this.thickness = thickness;
        this.mineralChance = mineralChance;
        this.spanMin = spanMin;
        this.spanRange = spanRange;
        this.centreSpread = centreSpread;
        this.tilt = tilt;
        this.inkPresence = inkPresence;
    }
}

public static partial class TerrainVariationStyle
{
    /// <summary>`Readonly&lt;Record&lt;TerrainWallSegmentVariant, TerrainWallSegmentProfile&gt;&gt;` (frozen).</summary>
    public sealed class WallSegmentProfiles
    {
        public readonly TerrainWallSegmentProfile quiet = new(
            kind: "quiet",
            bandDensity: 0.55,
            verticalDrift: 0.035,
            thickness: 0.75,
            mineralChance: 0.08,
            spanMin: 0.2,
            spanRange: 0.22,
            centreSpread: 0.48,
            tilt: 0.018,
            inkPresence: 0.72);

        public readonly TerrainWallSegmentProfile layered = new(
            kind: "layered",
            bandDensity: 1.12,
            verticalDrift: 0.06,
            thickness: 1,
            mineralChance: 0.24,
            spanMin: 0.34,
            spanRange: 0.38,
            centreSpread: 0.56,
            tilt: 0.018,
            inkPresence: 1);

        public readonly TerrainWallSegmentProfile mineral = new(
            kind: "mineral",
            bandDensity: 0.8,
            verticalDrift: 0.08,
            thickness: 1.08,
            mineralChance: 0.62,
            spanMin: 0.3,
            spanRange: 0.42,
            centreSpread: 0.5,
            tilt: 0.035,
            inkPresence: 0.9);

        public readonly TerrainWallSegmentProfile fractured = new(
            kind: "fractured",
            bandDensity: 0.72,
            verticalDrift: 0.13,
            thickness: 0.72,
            mineralChance: 0.12,
            spanMin: 0.18,
            spanRange: 0.32,
            centreSpread: 0.62,
            tilt: 0.12,
            inkPresence: 0.84);
    }

    /// <summary>Four deterministic wall gestures. They alter the fragments already emitted by the cliff compiler, so
    /// variety adds neither geometry lanes nor draw calls and remains stable across worker/chunk boundaries.</summary>
    public static readonly WallSegmentProfiles TERRAIN_WALL_SEGMENT_PROFILES = new();

    public static TerrainWallSegmentProfile terrainWallSegmentVariantAt(double worldCellX, double worldCellY)
    {
        double sample = TerrainGeometryCompilerFields.cellHash(worldCellX * 43 + 0x1d, worldCellY * 47 - 0x35);
        if (sample < 0.28) return TERRAIN_WALL_SEGMENT_PROFILES.quiet;
        if (sample < 0.64) return TERRAIN_WALL_SEGMENT_PROFILES.layered;
        if (sample < 0.84) return TERRAIN_WALL_SEGMENT_PROFILES.mineral;
        return TERRAIN_WALL_SEGMENT_PROFILES.fractured;
    }
}
