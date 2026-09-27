// Port of packages/client/src/render/environment/terrainGeometryCompilerTheme.ts — keep in lockstep with the original.
using System.Collections.Generic;

namespace Fluitown.Render;

public static partial class TerrainGeometryCompilerTheme
{
    /// <summary>
    /// CPU-geometry art constants. These values mirror the renderer's canonical theme constants; byte-parity
    /// tests against ThreeTerrainLayer make any future drift fail loudly without pulling the theme/domain barrels
    /// into the worker.
    /// </summary>
    private static class WORLD_INK
    {
        public const int ink = 0x050505;
        public const int yellow = 0xf2d22e;
        public const int cyan = 0x32b9c6;
        public const int red = 0xf05a49;
        public const int green = 0x42b96d;
        public const int violet = 0x8e61d1;
    }

    public static class TERRAIN_GEOMETRY_GRADE
    {
        public const double saturateBoost = 0.15;
        public const double contrastBoost = 0.08;
        public const double brightnessBoost = 1.01;
    }

    public static readonly IReadOnlyList<int> TERRAIN_GEOMETRY_RAINBOW_COLORS = new[]
    {
        0xff3f7a, 0xffe95b, 0x5cff72, 0x45c7ff, 0x8f5cff,
    };

    public static readonly IReadOnlyList<int> TERRAIN_GEOMETRY_CARNIVAL_COLORS = new[]
    {
        0xff4f96, 0xd02c86, 0x61e8c7, 0x8c4dd8, 0xffe36a,
    };

    /// <summary>A mutable object in the original (typed `WorldStyle`, not frozen); consumers must not write to it.</summary>
    public static readonly WorldStyle TERRAIN_GEOMETRY_DEFAULT_WORLD_STYLE = new WorldStyle
    {
        floorTint = 1,
        groundAccent = 1,
        wallTint = 1,
        waterTint = 1,
        bridgeTint = 1,
        floorGrain = 1,
        rockGrain = 1,
        strata = 1,
        driftA = 0xffd9a6,
        driftB = 0xa6c8ff,
        driftAmp = 0.12,
        waveSpeed = 1,
        foam = 1,
        glint = 1,
        windDirection = -0.38,
        windStrength = 0.8,
        windSpeed = 0.9,
        windGust = 0.65,
        sunLight = 1,
        hemiLight = 1,
        fillLight = 1,
        ambientLight = 1,
    };

    public static class TERRAIN_GEOMETRY_HUB_PALETTE
    {
        public const int patchMoss = 0x77c879;
        public const int patchGrass = 0xa7d86a;
        public const int treeMid = WORLD_INK.green;
        public const int propInk = WORLD_INK.ink;
        public const int fire = WORLD_INK.red;
        public static readonly IReadOnlyList<int> neon = new[] { WORLD_INK.cyan, WORLD_INK.yellow, WORLD_INK.violet };
    }
}
