// Port of packages/client/src/render/environment/terrainGeometryCompiler.ts — keep in lockstep with the original.
//
// PART C1 (TS lines 1–3549): module-level types, constants and helpers, `FloatBuf`, `TileGeometryBuilder`, the
// merge functions, polygon helpers, chasm-floor compilation, and `TerrainGeometryCompiler` with ALL instance
// fields, the constructor and the methods up to and including `buildTileDressing`. The remaining methods live in
// the partial-class files TerrainGeometryCompiler.{Dressing,Solid,Facades,Water,Details}.cs.
//
// PORT NOTES
// * `number | readonly number[]` builder channels are `NumberOrArray` (declared in WorldPropPrimitives.cs);
//   `typeof x === 'number'` is `x.isNumber`, `x[i] ?? y` is `optAt(x.array!, i) ?? y` (JS `undefined` past the
//   end of an array). Arrays of hex colours handed to the builder are `double[]` (like WATER_COLOR_SCRATCH).
// * `Float32Array` lanes are `float[]`, `Uint32Array` index lanes `uint[]`. The FloatBuf store is the only
//   float rounding (`(float)value`), exactly like the Float32Array assignment in the original.
// * `TerrainGeometryBufferPool.acquire(byteLength): ArrayBuffer` cannot hand out a typed view in C#; the pool
//   interface therefore exposes the two views the builder takes (`acquireFloat32`/`acquireUint32`, element
//   counts). `null` means "allocate".
// * Module-level mutable scratch (QUAD, SHADE4, CAP_*_SCRATCH, ORGANIC_*, SHORE_CONTACT_*, BUILDER, caches …) is
//   [ThreadStatic] with a lazy getter: in the browser every worker owns its own module copy, in Godot several
//   threads share one process.
// * The async cooperative compile variants are synchronous here (same structure; `await checkpoint()` becomes
//   a `Func<bool>` call — TRUE continues, FALSE cancels, exactly like the TS Promise<boolean>).
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Domain.TerrainRenderPlanModule;
using static Fluitown.Domain.TerrainVisualContour;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerTheme;
using static Fluitown.Render.TerrainGroundDetail;
using static Fluitown.Render.TerrainTileLattice;
using static Fluitown.Render.WorldPropPrimitives;
using static Fluitown.Render.TerrainProjection;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.TerrainFloorTurf;
using static Fluitown.Render.TerrainWallGrowth;
using static Fluitown.Render.TerrainSurfaceProfileModule;
using static Fluitown.Render.TerrainBakePigment;
// `terrainOrganicHeightAt` (terrainVisualGround.ts) is called qualified: TerrainVisualGround also exposes a
// public `clamp` that would make the Fields `clamp` ambiguous under `using static`.
using static Fluitown.Render.TerrainMaterialCompiler;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainChasmGeometry;
using static Fluitown.Render.TerrainWaterGeometryScratch;
using static Fluitown.Render.TerrainWaterfallCornerGeometry;
using static Fluitown.Render.TerrainLiquidChasmContour;
using static Fluitown.Render.TerrainGeometryCompilerModule;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class TerrainCompilerThemeVisual
{
    public string key = "";
    public Biome biome = null!;
    public TerrainMaterialTileset tileset = null!;
    public WorldStyle style = null!;
}

public sealed class TerrainBakeAudit
{
    public int cleftCells;
    public int underpassCells;
    public int underpassPlanks;
    public int underpassVisibleGaps;
    public int underpassCableSegments;
    public int underpassHangers;
    public int underpassAnchorPosts;
    public int bridgeCells;
    public int bridgeOverlapCells;
    public double bridgeMinimumClearance;
    public int bridgeStructuralCells;
    public int bridgeAbutments;
    public int bridgePiers;
    public int bridgeRailSegments;
    public int bridgeJoineryMarks;
    public int chasmCells;
    /// <summary>Number of Chasm cells that emitted their real floor cap at `cell.baseZ`.</summary>
    public int abyssSurfaceCells;
    public int chasmWallContinuations;
    public int chasmWallColourMismatches;
    public int chasmDepthLedges;
    public int chasmTalusClusters;
    public int nearAtmosphereCells;
    public int farAtmosphereCells;
    public int mistBanks;
    public double maximumMistAlpha;
    public double minimumMistHeight;
    public double maximumMistHeight;
    public int mistNonCoreAnchors;
    public double minimumVoidDrop;
    public double abyssSurfaceZMin;
    public double abyssSurfaceZMax;
}

public sealed class TerrainGeometryPayload
{
    public sealed class SurfaceLane
    {
        public float[] position = null!;
        public float[] normal = null!;
        public float[] color = null!;
        public float[] surface = null!;
        /// <summary>HDR geometry emission multiplier. Float32 is intentional: values above one survive into ACES.</summary>
        public float[] emissive = null!;
        /// <summary>
        /// **The ground channel** — four floats per vertex describing the surface a fragment stands on.
        ///
        /// `x` is the organic host-surface cover, 0 bare .. 1 closed: turf on floor caps and moss on natural rock
        /// faces. It is a MATERIAL channel, not a decoration: the fragment shader treats it as part of the host
        /// itself, exactly the way trail pigment is part of the cap colour. Floor blades and wall plants both root
        /// in the same continuous field carried here.
        ///
        /// `yzw` are per-KIND: floor cap → `y` wear 0..1, `zw` route tangent as a unit `(cos, sin)`; vertical face
        /// → `y` world-px drop BELOW the face's crest, `zw` unused (0).
        ///
        /// A wind-bearing decoration of any kind overloads `w` with the NEGATIVE presented linear footprint of
        /// its complete polygon. Animated decoration never consumes the floor-route tangent, so the sign is an
        /// unambiguous microgeometry-LOD marker and costs no fifth attribute/interpolator.
        /// </summary>
        public float[] ground = null!;
        public uint[] index = null!;
        public TerrainGeometryBounds bounds = null!;
    }

    public sealed class WaterLane
    {
        public float[] position = null!;
        public float[] normal = null!;
        public float[] color = null!;
        public float[] water = null!;
        /// <summary>The liquid sheet unfolded into its source surface plane; waterfall crests share the exact pool UV.</summary>
        public float[] fold = null!;
        /// <summary>Bake-side dark-bank/tree silhouette hint, mixed analytically by the existing water shader.</summary>
        public float[] reflection = null!;
        public uint[] index = null!;
        public TerrainGeometryBounds bounds = null!;
    }

    public sealed class MistLane
    {
        public float[] position = null!;
        public float[] color = null!;
        public float[] mist = null!;
        public uint[] index = null!;
        public TerrainGeometryBounds bounds = null!;
    }

    public sealed class OverlayLane
    {
        public float[] position = null!;
        public float[] color = null!;
        public uint[] index = null!;
        public TerrainGeometryBounds bounds = null!;
    }

    public sealed class ActorWallLane
    {
        public float[] position = null!;
        public uint[] index = null!;
        public TerrainGeometryBounds bounds = null!;
    }

    public SurfaceLane? surface;
    public WaterLane? water;
    public MistLane? mist;
    public OverlayLane? overlay;
    public ActorWallLane? actorWall;
    /// <summary>Fluitown comic look only (not in the original): plant placements drawn by the Godot vegetation layer.</summary>
    public TerrainVegetationLane? vegetation;
}

/// <summary>
/// One exact terrain tile split at the compiler's structural/dressing boundary.
///
/// `base` owns immutable ground, cliffs, water, bridges and waterfall topology. `dynamic` owns authored
/// dressing, vegetation and tree life. Drawing base before dynamic is the same order the monolithic compiler
/// emitted; concatenating the two payloads therefore reproduces the legacy payload byte-for-byte while allowing
/// a live edit to replace only the second half.
/// </summary>
public sealed class TerrainGeometryLayers
{
    public TerrainGeometryPayload @base = null!;
    public TerrainGeometryPayload dynamic = null!;
}

/// <summary>`{ cancelled: true } | { cancelled: false; geometry; audit }`.</summary>
public sealed class TerrainGeometryCompileResult
{
    public bool cancelled;
    public TerrainGeometryPayload? geometry;
    public TerrainBakeAudit? audit;
}

/// <summary>`{ cancelled: true } | { cancelled: false; layers; audit }`.</summary>
public sealed class TerrainGeometryLayerCompileResult
{
    public bool cancelled;
    public TerrainGeometryLayers? layers;
    public TerrainBakeAudit? audit;
}

/// <summary>
/// Exact-size storage supplied by the terrain worker. The builder writes only into buffers matching the
/// emitted typed view, so transfer byte counts remain honest while backtracking can reuse detached stores.
/// </summary>
/// <remarks>
/// PORT NOTE: TS `acquire(byteLength): ArrayBuffer` + a typed view over it. C# arrays cannot alias, so the pool
/// hands out the two typed views the builder uses; `length` is in ELEMENTS (byteLength / 4). Every acquired
/// array is fully overwritten by the caller, exactly as the TS `set` calls overwrite the reused ArrayBuffer.
/// </remarks>
public interface TerrainGeometryBufferPool
{
    float[] acquireFloat32(int length);
    uint[] acquireUint32(int length);
}

public sealed class TerrainGeometryBounds
{
    public double x;
    public double y;
    public double z;
    public double radius;
}

public static partial class TerrainGeometryCompilerModule
{
    // `export { terrainChasmFloorPointMask } from './terrainChasmFloorGeometry.js';` — re-exports are not
    // forwarded; callers use TerrainChasmFloorGeometry.terrainChasmFloorPointMask.

    /// <summary>`type TerrainTileset = TerrainMaterialTileset &amp; { readonly surfaceForTile?: unknown }` → TerrainMaterialTileset.</summary>
    internal sealed class PaintedThemeVisual
    {
        public TerrainMaterialTileset tileset = null!;
        public WorldStyle style = null!;
        public Func<TerrainCell, double, TerrainMaterial> materialForCell = null!;
    }

    /// <summary>Height (px of Y) of one elevation level — MUST equal the actor screen lift per level.</summary>
    internal const double ELEV = TERRAIN_ELEVATION_STEP_PX;
    /// <summary>How deep below the water SURFACE its basin floor sits (levels; mirrors the shared water model's basin).</summary>
    internal const double WATER_BASIN_DEPTH = 0.88;
    /// <summary>Same-datum cells share one seam; transitions consume the same exact model values on both sides.
    /// Inward taper of the opaque basin shell, in world pixels. It makes every isolated bank a closed tub.</summary>
    internal const double WATER_BASIN_SHELL_TAPER_MIN = 1.15;
    internal const double WATER_BASIN_SHELL_TAPER_MAX = 2.4;

    /* ── Light rig ───────────────────────────────────────────────────────────────────────────────────────────
     * Concept: ONE warm key ("sun") from the north-west-high — the direction the 2D art always implied (bright
     * north/west rims, shaded south faces, contact shadows falling south-east) — casting the only real shadows;
     * a HEMISPHERE fills sky-vs-ground ambience so caps read brighter than faces even out of the sun; a soft cool
     * FILL from the south lifts the (sun-less) south faces so height reads plastically instead of going flat-black;
     * a tiny AMBIENT floors the darkest pixel. Intensities are balanced so a flat, fully-lit cap comes out at
     * ~1.0× its authored albedo — the palette stays the art direction, the lights add form. */
    // The physical light-rig constants moved to the pure leaf module `terrainLightRig.ts` (WP1 of the
    // Render3D migration) so the volumetric actor scene's rig (`render3d/core/lightRig.ts`) reads the SAME
    // truth without pulling this renderer module. All uses below are unchanged.
    /// <summary>Minimum edge drop (levels) that earns a bevelled crest / a leaned side face.</summary>
    internal const double BEVEL_MIN_DROP = 0.26;
    /// <summary>Crest chamfer size (px): rock crisp, earth soft, banks gentle. The light does the highlighting.</summary>
    internal const double BEVEL_ROCK = 2.1;
    internal static readonly double BEVEL_EARTH = CARTOON_TERRAIN_STYLE.junctions.earthTerraceBevelHeightPx;
    internal const double BEVEL_BANK = 1.7;
    /// <summary>How dark a face's FOOT gets relative to its crest (per level of drop, clamped) — the grounding gradient
    /// that gives walls weight: crest bright, base sinking into contact darkness.</summary>
    internal const double FACE_BASE_DARKEN_PER_LEVEL = 0.065;
    internal const double FACE_BASE_DARKEN_MAX = 0.24;
    internal const double CHASM_FACE_DARKEN_MAX = 0.52;

    internal static double wallDepthShadeAt(
        double crestY,
        double sampleY,
        bool continuesIntoChasm,
        double continuationDatumY = 0)
    {
        double ordinarySampleY = continuesIntoChasm ? Math.max(sampleY, continuationDatumY) : sampleY;
        double ordinaryDepthLevels = Math.max(0, (crestY - ordinarySampleY) / ELEV);
        double ordinaryDarkening = Math.min(
            FACE_BASE_DARKEN_MAX,
            ordinaryDepthLevels * FACE_BASE_DARKEN_PER_LEVEL);
        if (!continuesIntoChasm || sampleY >= continuationDatumY) return 1 - ordinaryDarkening;
        // The modelled segment boundary is the exact continuity datum: the value here is byte-for-byte the ordinary wall result.
        // Only additional physical depth below it may add darkness, so the Chasm can never restart brighter.
        double datumDepthLevels = Math.max(0, (crestY - continuationDatumY) / ELEV);
        double datumDarkening = Math.min(
            FACE_BASE_DARKEN_MAX,
            datumDepthLevels * FACE_BASE_DARKEN_PER_LEVEL);
        double belowDatumLevels = (continuationDatumY - sampleY) / ELEV;
        return
            1 -
            Math.min(CHASM_FACE_DARKEN_MAX, datumDarkening + belowDatumLevels * FACE_BASE_DARKEN_PER_LEVEL);
    }

    /// <param name="material">A <see cref="TerrainEdgeMaterial"/> literal.</param>
    internal static double chasmWallPatternStrength(string material)
    {
        // The tiny delta is an encoded family bit consumed before the shader normalises both variants to the exact
        // 0.17 relief amplitude of an ordinary geological face.
        return material == "rock" ? 0.18 : 0.17;
    }

    /// <summary>Contact band on the LOW floor at a face's foot: max alpha and world-px width (scaled by drop).</summary>
    internal const double CONTACT_ALPHA_MAX = 0.18;
    internal const double CONTACT_WIDTH_MAX = 7;

    /// <summary>
    /// Floats per vertex in the surface ground channel — `(cover, wear|crestDrop, tangentX, tangentZ|facetSize)`.
    ///
    /// Exported because four independent places have to agree on it: the compiler that fills the buffer, the
    /// `aGround` attribute binding, the warm-up geometry stub, and anything reading a lane back out. A literal
    /// `4` in each of those is four chances to drift.
    /// </summary>
    public const int TERRAIN_GROUND_CHANNEL_STRIDE = 4;

    /// <summary>TS `interface TerrainGeometryCursor` (module-private; public here because the builder returns it).</summary>
    public sealed class TerrainGeometryCursor
    {
        public sealed class SurfaceCursor
        {
            public int position;
            public int normal;
            public int color;
            public int surface;
            public int emissive;
            public int ground;
            public int index;
        }

        public sealed class WaterCursor
        {
            public int position;
            public int normal;
            public int color;
            public int water;
            public int fold;
            public int reflection;
            public int index;
        }

        public sealed class MistCursor
        {
            public int position;
            public int color;
            public int mist;
            public int index;
        }

        public sealed class OverlayCursor
        {
            public int position;
            public int color;
            public int index;
        }

        public sealed class ActorWallCursor
        {
            public int position;
            public int index;
        }

        public SurfaceCursor surface = new();
        public WaterCursor water = new();
        public MistCursor mist = new();
        public OverlayCursor overlay = new();
        public ActorWallCursor actorWall = new();
        /// <summary>Fluitown: floats written to the vegetation records (see <see cref="FluitownVegetation"/>).</summary>
        public int vegetation;
    }

    /// <summary>Never written after construction (all zero), so one shared instance is safe.</summary>
    internal static readonly TerrainGeometryCursor EMPTY_GEOMETRY_CURSOR = new TerrainGeometryCursor();

    [ThreadStatic] private static double[]? _CAMERA_AWAY_CREST_COLOR_SCRATCH;
    internal static double[] CAMERA_AWAY_CREST_COLOR_SCRATCH => _CAMERA_AWAY_CREST_COLOR_SCRATCH ??= new double[] { 0, 0, 0, 0 };

    /// <summary>Grade the authored palette the way the (now bypassed) world colour-matrix did — saturation/contrast/
    /// brightness lift baked into the terrain albedo so the hybrid keeps the same rich, painted look.</summary>
    [ThreadStatic] private static Dictionary<int, int>? _gradeCache;
    private static Dictionary<int, int> gradeCache => _gradeCache ??= new Dictionary<int, int>();

    internal static int gradeColor(int hex)
    {
        if (gradeCache.TryGetValue(hex, out int cached)) return cached;
        double sat = 1 + TERRAIN_GEOMETRY_GRADE.saturateBoost;
        double con = 1 + TERRAIN_GEOMETRY_GRADE.contrastBoost;
        double bri = TERRAIN_GEOMETRY_GRADE.brightnessBoost;
        double r = (double)((hex >> 16) & 255) / 255;
        double g = (double)((hex >> 8) & 255) / 255;
        double b = (double)(hex & 255) / 255;
        double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        r = lum + (r - lum) * sat;
        g = lum + (g - lum) * sat;
        b = lum + (b - lum) * sat;
        r = (0.5 + (r - 0.5) * con) * bri;
        g = (0.5 + (g - 0.5) * con) * bri;
        b = (0.5 + (b - 0.5) * con) * bri;
        static double c(double v) => Math.max(0, Math.min(255, Math.round(v * 255)));
        int @out = (Js.ToInt32(c(r)) << 16) | (Js.ToInt32(c(g)) << 8) | Js.ToInt32(c(b));
        gradeCache[hex] = @out;
        return @out;
    }

    /// <summary>Byte-exact sRGB transfer used by Three r185's default linear working colour space. Keeping this tiny pure
    /// function here prevents the CPU-only worker from importing the Three module graph for one colour operation.</summary>
    internal static double srgbToLinear(double value)
    {
        return value < 0.04045
            ? value * 0.0773993808
            : Math.pow(value * 0.9478672986 + 0.0521327014, 2.4);
    }

    /// <summary>Graded-hex → linear-rgb triple, cached — the setHex sRGB→linear conversion per POLYGON was a measurable
    /// slice of the bake's mesh stage; the palette is a few dozen distinct hexes per biome, so this cache is tiny.</summary>
    /// <remarks>The returned array is the cached triple: callers must never write it (TS `readonly [n, n, n]`).
    /// PORT NOTE: TS keys the cache by the raw number; the result depends only on ToInt32(hex) (see gradeColor),
    /// so keying by ToInt32(hex) returns identical triples.</remarks>
    [ThreadStatic] private static Dictionary<int, double[]>? _linearCache;
    private static Dictionary<int, double[]> linearCache => _linearCache ??= new Dictionary<int, double[]>();

    internal static double[] linearRGB(int hex)
    {
        if (!linearCache.TryGetValue(hex, out double[]? @out))
        {
            int graded = gradeColor(hex);
            @out = new[]
            {
                srgbToLinear((double)((graded >> 16) & 255) / 255),
                srgbToLinear((double)((graded >> 8) & 255) / 255),
                srgbToLinear((double)(graded & 255) / 255),
            };
            linearCache[hex] = @out;
        }
        return @out;
    }

    internal static double[] linearRGB(double hex) => linearRGB(Js.ToInt32(hex));

    /// <summary>A theme drift pole (hex) → a brightness-neutral rgb MULTIPLIER around 1.0: only the colour's deviation
    /// from its own luminance survives, scaled by `amp` — hue variance, never brightness noise (the invariant the
    /// painterly drift always had). The default poles reproduce the original warm/cool constants.</summary>

    /// <summary>Surface pattern families resolved by the shader (see SURF_NOISE_GLSL); strength rides aSurf.y.
    /// TS `const SURF = TERRAIN_SURFACE_PATTERN;`.</summary>
    internal static class SURF
    {
        public const int floor = TERRAIN_SURFACE_PATTERN.floor;
        public const int rockCap = TERRAIN_SURFACE_PATTERN.rockCap;
        public const int earthFace = TERRAIN_SURFACE_PATTERN.earthFace;
        public const int rockFace = TERRAIN_SURFACE_PATTERN.rockFace;
        public const int bridge = TERRAIN_SURFACE_PATTERN.bridge;
        public const int basin = TERRAIN_SURFACE_PATTERN.basin;
        public const int waterBank = TERRAIN_SURFACE_PATTERN.waterBank;
        public const int chasmWall = TERRAIN_SURFACE_PATTERN.chasmWall;
        public const int chasmFloor = TERRAIN_SURFACE_PATTERN.chasmFloor;
    }

    /// <summary>JS `array[i]` for a read-only number array: `undefined` (null) outside the array, so `a[i] ?? x`
    /// ports as `optAt(a, i) ?? x`.</summary>
    internal static double? optAt(IReadOnlyList<double> a, int i) => (uint)i < (uint)a.Count ? a[i] : null;

    /// <summary>Growable Float32 scratch that survives across bakes: emit via <see cref="push"/>, snapshot via
    /// <c>slice</c>. Kills the per-bake churn of growing plain number[] accumulators and their per-element
    /// re-conversion.</summary>
    public sealed class FloatBuf
    {
        private float[] buf = new float[4096];
        public int n = 0;

        public void reset()
        {
            n = 0;
        }

        public void ensure(int extra)
        {
            if (n + extra <= buf.Length) return;
            int next = buf.Length * 2;
            while (next < n + extra) next *= 2;
            var grown = new float[next];
            Array.Copy(buf, grown, buf.Length);
            buf = grown;
        }

        public void push(double value)
        {
            ensure(1);
            buf[n++] = (float)value;
        }

        public void push2(double a, double b)
        {
            ensure(2);
            buf[n++] = (float)a;
            buf[n++] = (float)b;
        }

        public void push3(double a, double b, double c)
        {
            ensure(3);
            buf[n++] = (float)a;
            buf[n++] = (float)b;
            buf[n++] = (float)c;
        }

        public void push4(double a, double b, double c, double d)
        {
            ensure(4);
            buf[n++] = (float)a;
            buf[n++] = (float)b;
            buf[n++] = (float)c;
            buf[n++] = (float)d;
        }

        public float[] sliceRange(int start, int end, TerrainGeometryBufferPool? pool = null)
        {
            int length = Math.max(0, end - start);
            if (pool == null)
            {
                var copy = new float[length];
                Array.Copy(buf, start, copy, 0, length);
                return copy;
            }
            float[] @out = pool.acquireFloat32(length);
            Array.Copy(buf, start, @out, 0, length);
            return @out;
        }
    }

    /// <summary>Reused polygon scratch (the builder copies vertex data out synchronously, so ONE mutable quad/tri is safe).</summary>
    [ThreadStatic] private static P3[]? _QUAD;
    internal static P3[] QUAD => _QUAD ??= new[] { new P3(0, 0, 0), new P3(0, 0, 0), new P3(0, 0, 0), new P3(0, 0, 0) };

    internal static P3[] quadInto(
        P3[] @out,
        double x0,
        double y0,
        double z0,
        double x1,
        double y1,
        double z1,
        double x2,
        double y2,
        double z2,
        double x3,
        double y3,
        double z3)
    {
        @out[0].x = x0;
        @out[0].y = y0;
        @out[0].z = z0;
        @out[1].x = x1;
        @out[1].y = y1;
        @out[1].z = z1;
        @out[2].x = x2;
        @out[2].y = y2;
        @out[2].z = z2;
        @out[3].x = x3;
        @out[3].y = y3;
        @out[3].z = z3;
        return @out;
    }

    internal static P3[] quad(
        double x0,
        double y0,
        double z0,
        double x1,
        double y1,
        double z1,
        double x2,
        double y2,
        double z2,
        double x3,
        double y3,
        double z3)
    {
        return quadInto(QUAD, x0, y0, z0, x1, y1, z1, x2, y2, z2, x3, y3, z3);
    }

    /// <summary>Never written (TS `readonly number[]`).</summary>
    internal static readonly double[] UNIT_SHADE = { 1, 1, 1, 1 };
    /// <summary>Never written (TS `readonly number[]`).</summary>
    internal static readonly double[] UNIT_ZERO = { 0, 0, 0, 0 };
    /// <summary>Reused per-vertex shade/alpha scratch (consumed synchronously by the builder, like QUAD).</summary>
    [ThreadStatic] private static double[]? _SHADE4;
    internal static double[] SHADE4 => _SHADE4 ??= new double[] { 1, 1, 1, 1 };
    [ThreadStatic] private static double[]? _CAP_SHADE_SCRATCH;
    internal static double[] CAP_SHADE_SCRATCH => _CAP_SHADE_SCRATCH ??= filledArray(20, 1);
    /// <summary>Hex colours stored as doubles (JS numbers) so the array is a builder `hex` lane.</summary>
    [ThreadStatic] private static double[]? _CAP_COLOR_SCRATCH;
    internal static double[] CAP_COLOR_SCRATCH => _CAP_COLOR_SCRATCH ??= filledArray(20, 0xffffff);
    /// <summary>
    /// The ground channel per cap sample, `(cover, wear, tangentX, tangentZ)` × up to 20 lattice points — the
    /// grass patch plus the two parameters the fragment shader draws sub-tile wear structure from.
    /// </summary>
    [ThreadStatic] private static double[]? _CAP_GROUND_SCRATCH;
    internal static double[] CAP_GROUND_SCRATCH => _CAP_GROUND_SCRATCH ??= filledArray(20 * TERRAIN_GROUND_CHANNEL_STRIDE, 0);
    [ThreadStatic] private static double[]? _WIND4;
    internal static double[] WIND4 => _WIND4 ??= new double[] { 0, 0, 0, 0 };

    private static double[] filledArray(int length, double value)
    {
        var a = new double[length];
        for (int i = 0; i < length; i++) a[i] = value;
        return a;
    }

    internal static void setShade4(double a, double b, double c, double d)
    {
        double[] shade4 = SHADE4;
        shade4[0] = a;
        shade4[1] = b;
        shade4[2] = c;
        shade4[3] = d;
    }

    internal static void setWind4(double a, double b, double c, double d)
    {
        double[] wind4 = WIND4;
        wind4[0] = a;
        wind4[1] = b;
        wind4[2] = c;
        wind4[3] = d;
    }

    // The worker compiler mirrors the renderer-side seam contract exactly. Each worker owns its own module and
    // therefore its own allocation-free scratch record.
    [ThreadStatic] private static TerrainVisualContourCorners? _ORGANIC_CONTOUR_SCRATCH;
    internal static TerrainVisualContourCorners ORGANIC_CONTOUR_SCRATCH =>
        _ORGANIC_CONTOUR_SCRATCH ??= new TerrainVisualContourCorners { nw = 0, ne = 0, se = 0, sw = 0 };
    /// <summary>One 3x3 cap lattice, reused across every cell. Static CPU shaping pays no steady-state allocations.</summary>
    [ThreadStatic] private static double[]? _ORGANIC_HEIGHT_SCRATCH;
    internal static double[] ORGANIC_HEIGHT_SCRATCH => _ORGANIC_HEIGHT_SCRATCH ??= new double[9];
    /// <summary>The same lattice as POINTS — the welded patch is emitted once, so the cap needs one point set, not four.</summary>
    [ThreadStatic] private static P3[]? _ORGANIC_LATTICE_POINTS;
    internal static P3[] ORGANIC_LATTICE_POINTS => _ORGANIC_LATTICE_POINTS ??= createLatticePoints();
    /// <summary>Reference holder for the four outline corners of a welded lattice (winding is decided once per patch).</summary>
    [ThreadStatic] private static P3[]? _LATTICE_CORNER_SCRATCH;
    internal static P3[] LATTICE_CORNER_SCRATCH => _LATTICE_CORNER_SCRATCH ??= new[]
    {
        ORGANIC_LATTICE_POINTS[0],
        ORGANIC_LATTICE_POINTS[0],
        ORGANIC_LATTICE_POINTS[0],
        ORGANIC_LATTICE_POINTS[0],
    };
    [ThreadStatic] private static double[]? _ORGANIC_NORMAL_X_SCRATCH;
    internal static double[] ORGANIC_NORMAL_X_SCRATCH => _ORGANIC_NORMAL_X_SCRATCH ??= new double[9];
    [ThreadStatic] private static double[]? _ORGANIC_NORMAL_Z_SCRATCH;
    internal static double[] ORGANIC_NORMAL_Z_SCRATCH => _ORGANIC_NORMAL_Z_SCRATCH ??= new double[9];

    private static P3[] createLatticePoints()
    {
        var points = new P3[9];
        for (int i = 0; i < points.Length; i++) points[i] = new P3(0, 0, 0);
        return points;
    }

    /// <summary>One cap cut edge, as the crest emitter derives it (corner cuts already applied).</summary>
    internal sealed class TerrainCrestSegment
    {
        /// <summary>A <see cref="TerrainEdgeDirection"/> literal.</summary>
        public string dir = "";
        public double x0;
        public double z0;
        public double x1;
        public double z1;
        public double startInset;
        public double endInset;
    }

    /// <summary>
    /// The ONE organic bank field — a broad wander with a finer detail octave, in absolute world px.
    ///
    /// The tessellated ground dissolve reads it to decide how far inland the sand reaches, and the drawn waterline
    /// reads it to decide where the pen actually runs. They have to be the same field, or the illustration draws
    /// one bank and paints a different one.
    /// </summary>
    internal static double terrainShoreField(double worldX, double worldZ)
    {
        double detailWeight = CARTOON_TERRAIN_STYLE.waterShore.detailNoiseWeight;
        double broad = smoothCellNoise(
            worldX,
            worldZ,
            CARTOON_TERRAIN_STYLE.waterShore.noiseScalePx,
            1103);
        double detail = smoothCellNoise(
            worldX,
            worldZ,
            CARTOON_TERRAIN_STYLE.waterShore.detailNoiseScalePx,
            1877);
        return broad * (1 - detailWeight) + detail * detailWeight;
    }

    /// <summary>
    /// How far inland the bank dissolve reaches at a world point, in world px — the ONE bank width.
    ///
    /// The tessellated water surface fades to floor pigment over exactly this distance, and the drawn waterline is
    /// placed at a fixed fraction of it, so the pen follows the beach: where the sand runs wide the stroke steps
    /// further in, where the bank pinches the stroke hugs the water. Two marks, one measurement.
    /// </summary>
    internal static double terrainShoreReachAt(double worldX, double worldZ)
    {
        double field = terrainShoreField(worldX, worldZ);
        double eased = field * field * (3 - 2 * field);
        return
            CARTOON_TERRAIN_STYLE.waterShore.minimumBlendPx +
            eased *
            (CARTOON_TERRAIN_STYLE.waterShore.maximumBlendPx -
             CARTOON_TERRAIN_STYLE.waterShore.minimumBlendPx);
    }

    /// <summary>
    /// How far the bank has already pushed OUT over the liquid at a world point, in world px.
    ///
    /// <see cref="terrainShoreReachAt"/> says how wide the damp band is; this says where it STARTS. Without it the band
    /// always started at the tile contact, so a pool's outline was the lattice with a soft edge painted on it —
    /// long runs ruler-straight, turns square. Here the full-strength end of the band wanders out into the water
    /// on a broad landform octave plus a finer nibble, and a straight tile run resolves as spits and shallow bays.
    ///
    /// One-sided on purpose (see the authored constants): the bank may advance over the liquid, never retreat
    /// inland of the contact. The liquid paints the canonical Floor pigment itself, so an advance is the water
    /// cell drawing beach — while a retreat would strand water pigment against the Floor cap beside it and put the
    /// ruler-clean tile step straight back.
    ///
    /// A pure function of the absolute world point, like every other term of this bank: two water cells, two bake
    /// frames and the pen all compute the identical value at a shared position.
    /// </summary>
    internal static double terrainShoreMeanderAt(double worldX, double worldZ)
    {
        var style = CARTOON_TERRAIN_STYLE.waterShore;
        double broad = smoothCellNoise(worldX, worldZ, style.meanderScalePx, 3301);
        double detail = smoothCellNoise(worldX, worldZ, style.meanderDetailScalePx, 4409);
        double field = broad * (1 - style.meanderDetailWeight) + detail * style.meanderDetailWeight;
        double shaped = clamp((field - 0.5) * style.meanderContrast + 0.5, 0, 1);
        return shaped * shaped * (3 - 2 * shaped) * style.meanderReachPx;
    }

    /// <summary>
    /// Every Floor contact that can still reach one dissolve sample: its distance to the drawn bank, the reach
    /// authored there and which way it lies. A 3x3 liquid window with four edges each cannot exceed 36 entries;
    /// the buffers are module-level so the bake hot path never allocates.
    /// </summary>
    internal const int SHORE_CONTACT_LIMIT = 64;
    [ThreadStatic] private static double[]? _SHORE_CONTACT_DISTANCE;
    internal static double[] SHORE_CONTACT_DISTANCE => _SHORE_CONTACT_DISTANCE ??= new double[SHORE_CONTACT_LIMIT];
    [ThreadStatic] private static double[]? _SHORE_CONTACT_MEANDER;
    internal static double[] SHORE_CONTACT_MEANDER => _SHORE_CONTACT_MEANDER ??= new double[SHORE_CONTACT_LIMIT];
    [ThreadStatic] private static double[]? _SHORE_CONTACT_REACH;
    internal static double[] SHORE_CONTACT_REACH => _SHORE_CONTACT_REACH ??= new double[SHORE_CONTACT_LIMIT];
    [ThreadStatic] private static double[]? _SHORE_CONTACT_DIR_X;
    internal static double[] SHORE_CONTACT_DIR_X => _SHORE_CONTACT_DIR_X ??= new double[SHORE_CONTACT_LIMIT];
    [ThreadStatic] private static double[]? _SHORE_CONTACT_DIR_Z;
    internal static double[] SHORE_CONTACT_DIR_Z => _SHORE_CONTACT_DIR_Z ??= new double[SHORE_CONTACT_LIMIT];

    /// <summary>
    /// The ONE Floor/Water dissolve sample: how much canonical bank pigment a point of liquid carries, and which
    /// pigment that is. Output is `[groundBlend, canonicalFloorPigment]`.
    ///
    /// Both liquid emitters — the water cell itself and the seamless fill under a dry cell's rounded corner — kept
    /// their own copy of this rule, one stated in world px and one in grid units. That is precisely how a drawn
    /// bank and a painted bank drift apart, so there is one implementation now; the two callers differ only in how
    /// they reach their neighbourhood and their Floor pigment.
    ///
    /// Every term is a pure function of the absolute sample position: the window is anchored on the SAMPLE rather
    /// than on whichever cell happens to own it, so two neighbouring owners compute the identical value at the
    /// vertices they share, and a bake-frame seam cannot become a colour step.
    /// </summary>
    /// <param name="liquidAt">`(tileX, tileY) => TerrainCell | null`.</param>
    /// <param name="floorPigmentAt">`(floorCell, sampleX, sampleY) => hex`.</param>
    /// <param name="out">`[groundBlend, pigmentHex]` (the pigment is stored as a JS number).</param>
    internal static void terrainShoreDissolveAt(
        MaterializedTerrain terrain,
        double gridX,
        double gridY,
        double tileSize,
        double worldX,
        double worldZ,
        double waterY,
        int baseColor,
        Func<int, int, TerrainCell?> liquidAt,
        Func<TerrainCell, double, double, int> floorPigmentAt,
        double[] @out)
    {
        double[] contactDistance = SHORE_CONTACT_DISTANCE;
        double[] contactMeander = SHORE_CONTACT_MEANDER;
        double[] contactReach = SHORE_CONTACT_REACH;
        double[] contactDirX = SHORE_CONTACT_DIR_X;
        double[] contactDirZ = SHORE_CONTACT_DIR_Z;
        @out[0] = 0;
        @out[1] = baseColor;
        int pigment = baseColor;
        double pigmentWeight = 0;
        int contacts = 0;
        var style = CARTOON_TERRAIN_STYLE.waterShore;
        // The widest band this rule can produce: the full dissolve, plus the meander that moved its start out over
        // the liquid. Any contact still able to reach the sample belongs to a liquid cell within that many tiles.
        double maximumReach = style.maximumBlendPx + style.meanderReachPx;
        int span = (int)Math.max(1, Math.ceil(maximumReach / tileSize));
        int anchorX = (int)Math.floor(gridX);
        int anchorY = (int)Math.floor(gridY);
        for (int candidateY = anchorY - span; candidateY <= anchorY + span; candidateY++)
        {
            for (int candidateX = anchorX - span; candidateX <= anchorX + span; candidateX++)
            {
                if (liquidAt(candidateX, candidateY) == null) continue;
                foreach (TerrainDirection direction in TerrainDirections)
                {
                    TerrainCell? floorCell = terrainCellAt(
                        terrain,
                        candidateX + direction.dx,
                        candidateY + direction.dy);
                    if (
                        floorCell == null ||
                        floorCell.type != TileType.Floor ||
                        !floorCell.walkable ||
                        Math.abs(floorCell.surfaceZ * ELEV - waterY) > ELEV * style.maximumHeightDeltaLevels
                    )
                        continue;
                    double nearestGridX = gridX;
                    double nearestGridY = gridY;
                    if (direction.key == "n" || direction.key == "s")
                    {
                        nearestGridX = clamp(gridX, candidateX, candidateX + 1);
                        nearestGridY = candidateY + (direction.key == "s" ? 1 : 0);
                    }
                    else
                    {
                        nearestGridX = candidateX + (direction.key == "e" ? 1 : 0);
                        nearestGridY = clamp(gridY, candidateY, candidateY + 1);
                    }
                    double contact = Math.hypot(gridX - nearestGridX, gridY - nearestGridY) * tileSize;
                    if (contact > maximumReach) continue;
                    double nearestWorldX = worldX + (nearestGridX - gridX) * tileSize;
                    double nearestWorldZ = worldZ + (nearestGridY - gridY) * tileSize;
                    // Measure from the DRAWN bank rather than from the tile contact. The meander has already carried the
                    // band's full-strength end this far out over the liquid, and everything inside it is beach — which is
                    // the whole reason a straight tile run stops reading as the outline of the pool.
                    double meander = terrainShoreMeanderAt(nearestWorldX, nearestWorldZ);
                    double distance = contact - meander;
                    // The same bank width the drawn waterline rides — see terrainShoreReachAt.
                    double reach = terrainShoreReachAt(nearestWorldX, nearestWorldZ);
                    if (contacts < SHORE_CONTACT_LIMIT)
                    {
                        // Which WAY this bank lies from the sample, as a unit vector. Two banks meeting at an angle are one
                        // turn and want the corner softening below; two banks FACING each other across a brook are not a
                        // turn at all, and are what the meander has to be rationed against.
                        double bearing = Math.hypot(nearestGridX - gridX, nearestGridY - gridY);
                        if (!Js.Truthy(bearing)) bearing = 1;
                        contactDistance[contacts] = contact;
                        contactMeander[contacts] = meander;
                        contactReach[contacts] = reach;
                        contactDirX[contacts] = (nearestGridX - gridX) / bearing;
                        contactDirZ[contacts] = (nearestGridY - gridY) / bearing;
                        contacts++;
                    }
                    // The pigment average keeps its own per-contact weights: a colour blend has no crease to remove, and
                    // gathering it from the softened distance would smear a neighbouring material across the corner.
                    double normalized = clamp(distance / Math.max(0.001, reach), 0, 1);
                    double weight = 1 - normalized * normalized * (3 - 2 * normalized);
                    if (weight <= 0) continue;
                    int floorColor = floorPigmentAt(floorCell, nearestGridX, nearestGridY);
                    pigment =
                        pigmentWeight == 0
                            ? floorColor
                            : mix(pigment, floorColor, weight / (pigmentWeight + weight));
                    pigmentWeight += weight;
                }
            }
        }
        if (contacts > 0)
        {
            int nearest = 0;
            for (int index = 1; index < contacts; index++)
            {
                if (contactDistance[index] < contactDistance[nearest]) nearest = index;
            }
            double nearestDistance = contactDistance[nearest];
            double radius = style.bankCornerSoftenPx;
            // How far the OPPOSITE bank is — `maximumReach` when there isn't one in range, which is also the value a
            // bank has the instant it enters range, so the ration below is continuous as one appears.
            double opposing = maximumReach;
            // A SMOOTH minimum, not `Math.min`. Taking the plain nearer of two contacts creases the distance field
            // along their bisector, and a creased field's iso-contour turns through a right angle — the drawn square
            // corner every pool kept however far its straight runs wandered. The polynomial blend removes the crease
            // itself, so a corner becomes a turn with a radius: no corner classifier, no chamfer constant. Beyond the
            // radius it IS the plain minimum, so a straight run is untouched.
            double softening = 0;
            for (int index = 0; index < contacts; index++)
            {
                if (index == nearest) continue;
                double alignment =
                    contactDirX[index] * contactDirX[nearest] +
                    contactDirZ[index] * contactDirZ[nearest];
                // How much this bank FACES the nearest one, faded in rather than switched on: a hard "is it opposite"
                // test would make the ration below jump the moment a bank rotated past the threshold, and a jump in the
                // ration is a drawn line across the water.
                double facing = clamp((-0.2 - alignment) / 0.5, 0, 1);
                opposing = Math.min(
                    opposing,
                    maximumReach + (contactDistance[index] - maximumReach) * facing);
                double overlap = Math.max(radius - (contactDistance[index] - nearestDistance), 0);
                if (overlap <= 0) continue;
                // Only a real TURN is softened, and "turn" is a BAND, not a threshold. A bank arrives here as several
                // collinear tile segments, and near their junction the runner-up is barely further away than the winner
                // — soften against that and a dead straight bank grows a bulge at every 40 px. So the gate closes at
                // both ends: at alignment 1 the two contacts are the same bank continuing, at alignment -1 they face
                // each other across a channel. Only the perpendicular middle is a corner.
                double turn = clamp((alignment + 0.6) / 0.6, 0, 1) * clamp((0.85 - alignment) / 0.35, 0, 1);
                softening = Math.max(softening, (turn * overlap * overlap) / (4 * radius));
            }
            // Ration the meander against the water it is eating. A bank may walk its full authored amplitude out into
            // open water, but a one-cell brook is 40 px wide and two banks helping themselves to 13 px each would silt
            // it into damp sand — the pool would gain a coastline and the stream would disappear. The ramp is steep
            // (fourth power) because it has to be nearly nothing at half a tile and exactly everything the moment the
            // opposite bank leaves range, which is what makes it continuous: a bank entering range carries
            // `maximumReach`, the same value its absence does. No width classifier, no special case for streams.
            double openness = clamp(opposing / maximumReach, 0, 1);
            double pinch = openness * openness;
            double allowance = style.meanderReachPx * pinch * pinch;
            // The corner radius is rationed by the same measure, for the same reason: the tip of a one-cell peninsula
            // is a pair of turns, and rounding both at full radius would erase the peninsula rather than round it.
            double bankDistance =
                nearestDistance - Math.min(contactMeander[nearest], allowance) - softening * pinch;
            double normalized = clamp(bankDistance / Math.max(0.001, contactReach[nearest]), 0, 1);
            @out[0] = 1 - normalized * normalized * (3 - 2 * normalized);
        }
        @out[1] = pigment;
    }

    internal static bool terrainCellHasVisualContour(MaterializedTerrain terrain, TerrainCell cell)
    {
        TerrainVisualContourCorners contour = terrainVisualContourCorners(
            terrain,
            cell,
            cell.x,
            cell.y,
            ORGANIC_CONTOUR_SCRATCH);
        return contour.nw + contour.ne + contour.se + contour.sw > 0.001;
    }

    /// <summary>
    /// Topology-safe opt-in for real visual floor undulation at an arbitrary logical-grid sample.
    ///
    /// Interior samples belong to one cell, edge samples to two and corners to four. Every owning cell must be an
    /// ordinary walkable Floor at the same authoritative height. Waterlines, bridge ends, cliffs, underpasses and
    /// sampled-frame borders are therefore pinned exactly, while a tessellated cap may still rise at its centre.
    /// </summary>
    public static double terrainOrganicPointMask(
        MaterializedTerrain terrain,
        double sampleX,
        double sampleY,
        double surfaceZ)
    {
        bool onXEdge = Number.isInteger(sampleX);
        bool onYEdge = Number.isInteger(sampleY);
        double minX = Math.floor(sampleX) - (onXEdge ? 1 : 0);
        double maxX = Math.floor(sampleX);
        double minY = Math.floor(sampleY) - (onYEdge ? 1 : 0);
        double maxY = Math.floor(sampleY);
        for (double y = minY; y <= maxY; y++)
        {
            for (double x = minX; x <= maxX; x++)
            {
                // terrainCellAt(terrain, x, y): an out-of-range coordinate is simply absent.
                TerrainCell? candidate =
                    x >= 0 && y >= 0 && x < terrain.width && y < terrain.height
                        ? terrainCellAt(terrain, (int)x, (int)y)
                        : null;
                if (
                    candidate == null ||
                    candidate.type != TileType.Floor ||
                    !candidate.walkable ||
                    Math.abs(candidate.surfaceZ - surfaceZ) > 0.001 ||
                    // Contour caps remain on the authoritative flat datum. Their ordinary neighbours must pin the shared
                    // vertex too, otherwise the GPU lifts only one side and exposes a zoom-flickering crack.
                    terrainCellHasVisualContour(terrain, candidate)
                )
                    return 0;
            }
        }
        return 1;
    }

    /*
     * Accumulates one bake-tile's geometry into shared-material meshes: the LIT surface (caps, faces, bevels,
     * basins — receives/casts shadows), the LIT animated water surface, a dedicated soft atmospheric mist batch,
     * and the UNLIT TRANSLUCENT overlay (decals, AO/contact bands, ink lines). All positions are absolute world
     * coordinates; winding is normalized against the oblique projection by flipping INDEX order (never copying
     * vertices) so every emitted face is front-facing on screen.
     */

    /// <summary>TS `interface TerrainSurfaceRemapContext` (module-private; public because the builder API names it).</summary>
    public sealed class TerrainSurfaceRemapContext
    {
        public IReadOnlyList<P3> points = null!;
        public double normalX;
        public double normalY;
        public double normalZ;
    }

    /// <summary>TS `type TerrainSurfaceChannelRemap = (kind, strength, context) => readonly [number, number]`.</summary>
    public delegate (double kind, double strength) TerrainSurfaceChannelRemap(
        double kind,
        double strength,
        TerrainSurfaceRemapContext context);

    /// <summary>TS `(hex: number, alpha: number) => readonly [number, number]` (overlay channel remap).</summary>
    public delegate (int hex, double alpha) TerrainOverlayChannelRemap(int hex, double alpha);
}

/// <summary>
/// Accumulates one tile's vertices. Deliberately free of any Three.js import so a terrain worker can own one;
/// the render layer turns a filled builder into scene Groups through its transferable payload.
///
/// Flips per-polygon winding (never vertices) so every emitted face is front-facing on screen.
/// ONE module-level instance is reused across bakes (<see cref="reset"/>) — zero steady-state allocation growth.
/// </summary>
/// <remarks>
/// PORT NOTE: TypeScript satisfies the structural builder contracts of the helper modules (PropGeometryBuilder,
/// SuspensionSurfaceBuilder, TerrainGroundingOverlayBuilder, TerrainGroundingSurfaceBuilder, …) implicitly; C#
/// implements them explicitly at the end of this class, forwarding to the general methods.
/// </remarks>
public sealed partial class TileGeometryBuilder :
    PropGeometryBuilder,
    SuspensionSurfaceBuilder,
    TerrainGroundingOverlayBuilder,
    TerrainGroundingSurfaceBuilder
{
    public sealed class SurfaceLane
    {
        public readonly FloatBuf pos = new();
        public readonly FloatBuf nrm = new();
        public readonly FloatBuf col = new();
        public readonly FloatBuf surf = new();
        public readonly FloatBuf emissive = new();
        public readonly FloatBuf ground = new();
        public readonly List<int> idx = new();
    }

    public sealed class WaterLane
    {
        public readonly FloatBuf pos = new();
        public readonly FloatBuf nrm = new();
        public readonly FloatBuf col = new();
        public readonly FloatBuf attr = new();
        public readonly FloatBuf fold = new();
        public readonly FloatBuf reflection = new();
        public readonly List<int> idx = new();
    }

    public sealed class MistLane
    {
        public readonly FloatBuf pos = new();
        public readonly FloatBuf col = new();
        public readonly FloatBuf attr = new();
        public readonly List<int> idx = new();
    }

    public sealed class OverlayLane
    {
        public readonly FloatBuf pos = new();
        public readonly FloatBuf col = new();
        public readonly List<int> idx = new();
    }

    public sealed class ActorWallLane
    {
        public readonly FloatBuf pos = new();
        public readonly List<int> idx = new();
    }

    public int chasmFloorCells = 0;
    public int chasmDepthLedges = 0;
    public int chasmTalusClusters = 0;
    public int bridgeStructuralCells = 0;
    public int bridgeAbutments = 0;
    public int bridgePiers = 0;
    public int bridgeRailSegments = 0;
    public int bridgeJoineryMarks = 0;
    public int underpassPlanks = 0;
    public int underpassVisibleGaps = 0;
    public int underpassCableSegments = 0;
    public int underpassHangers = 0;
    public int underpassAnchorPosts = 0;
    /// <summary>Fluitown comic look: plant placement records (TileGeometryBuilder.Vegetation.cs).</summary>
    public readonly FloatBuf vegetation = new();
    public readonly SurfaceLane surface = new();
    public readonly WaterLane water = new();
    public readonly MistLane mist = new();
    public readonly OverlayLane overlay = new();
    /// <summary>The world's ONLY shadow caster: visible SOLID-wall copies plus low-poly dressing proxies (see
    /// <see cref="addShadowCaster"/>). The surface batch is `castShadow: false` — the whole world through the depth
    /// pass would cost more than everything else. Floors, bridges and water are never added.</summary>
    public readonly ActorWallLane actorWall = new();
    private TerrainWallPigmentProfile wallPigment = fallbackTerrainWallPigmentProfile();
    private TerrainWallGrowthProfile wallGrowth = TERRAIN_WALL_GROWTH_DISABLED;
    private TerrainWallGrowthProfile chasmWallGrowth = TERRAIN_WALL_GROWTH_DISABLED;
    private readonly double[] wallPigmentScratch = { 0, 0, 0 };
    private TerrainSurfaceChannelRemap? surfaceChannelRemap;
    private TerrainOverlayChannelRemap? overlayChannelRemap;

    /// <summary>
    /// Compile an established surface language into another material class without forking its geometry.
    /// Chasm terraces use this to keep the ordinary Floor compiler's exact caps, bevels, corner returns and ink,
    /// while publishing those vertices as dark deep-floor/deep-wall surfaces to the terrain shader.
    /// </summary>
    public void withTerrainChannelRemap(
        TerrainSurfaceChannelRemap surfaceRemap,
        TerrainOverlayChannelRemap overlayRemap,
        Action emit)
    {
        TerrainSurfaceChannelRemap? previousSurface = surfaceChannelRemap;
        TerrainOverlayChannelRemap? previousOverlay = overlayChannelRemap;
        surfaceChannelRemap = surfaceRemap;
        overlayChannelRemap = overlayRemap;
        try
        {
            emit();
        }
        finally
        {
            surfaceChannelRemap = previousSurface;
            overlayChannelRemap = previousOverlay;
        }
    }

    public void configureWallPigment(TerrainWallPigmentProfile profile)
    {
        wallPigment = profile;
    }

    public void configureWallGrowth(
        TerrainWallGrowthProfile profile,
        TerrainWallGrowthProfile? chasmProfile = null)
    {
        chasmProfile ??= profile;
        wallGrowth = profile;
        chasmWallGrowth = chasmProfile;
    }

    public void reset()
    {
        chasmFloorCells = 0;
        chasmDepthLedges = 0;
        chasmTalusClusters = 0;
        bridgeStructuralCells = 0;
        bridgeAbutments = 0;
        bridgePiers = 0;
        bridgeRailSegments = 0;
        bridgeJoineryMarks = 0;
        underpassPlanks = 0;
        underpassVisibleGaps = 0;
        underpassCableSegments = 0;
        underpassHangers = 0;
        underpassAnchorPosts = 0;
        vegetation.reset();
        surface.pos.reset();
        surface.nrm.reset();
        surface.col.reset();
        surface.surf.reset();
        surface.emissive.reset();
        surface.ground.reset();
        surface.idx.Clear();
        water.pos.reset();
        water.nrm.reset();
        water.col.reset();
        water.attr.reset();
        water.fold.reset();
        water.reflection.reset();
        water.idx.Clear();
        mist.pos.reset();
        mist.col.reset();
        mist.attr.reset();
        mist.idx.Clear();
        overlay.pos.reset();
        overlay.col.reset();
        overlay.idx.Clear();
        actorWall.pos.reset();
        actorWall.idx.Clear();
    }

    /// <summary>Fan-triangulate from a vertex that sees the whole polygon. Concave corner aprons use their square
    /// corner as root; the stored vertex and material-channel order is unchanged.</summary>
    private void fan(List<int> idx, int @base, int count, bool flip, int root = 0)
    {
        for (int i = 1; i < count - 1; i++)
        {
            int a = root + i, b = a + 1;
            if (a >= count) a -= count;
            if (b >= count) b -= count;
            if (flip)
            {
                idx.Add(@base + root);
                idx.Add(@base + b);
                idx.Add(@base + a);
            }
            else
            {
                idx.Add(@base + root);
                idx.Add(@base + a);
                idx.Add(@base + b);
            }
        }
    }

    private static void push3(List<int> idx, int a, int b, int c)
    {
        idx.Add(a);
        idx.Add(b);
        idx.Add(c);
    }

    private static void push6(List<int> idx, int a, int b, int c, int d, int e, int f)
    {
        idx.Add(a);
        idx.Add(b);
        idx.Add(c);
        idx.Add(d);
        idx.Add(e);
        idx.Add(f);
    }

    private static double orOne(double value) => Js.Truthy(value) ? value : 1;

    /// <summary>
    /// Push a polygon into the lit surface batch; a concave apron supplies its visible square corner as `fanRoot`.
    /// `shade` optionally darkens/lightens per VERTEX
    /// (multiplier on the linear colour) — the baked gradient that grounds wall faces (crest 1 → foot &lt;1).
    ///
    /// `ground` is the per-vertex ground channel (see TerrainGeometryPayload.surface.ground). As a
    /// NUMBER it is a bare organic-cover value for the whole polygon — the common case, and what the shader's
    /// fast path expects. As an ARRAY it carries the full `(cover, wear, tangentX, tangentZ)` quad per vertex,
    /// i.e. FOUR entries per point, which only the floor-cap emitters need. The face slot (`y` = drop below the
    /// crest) is derived here from the polygon's own extent, so no caller has to supply or understand it.
    /// </summary>
    /// <remarks>`null` for an optional channel is the TS `undefined` (the TS default applies: shade UNIT_SHADE,
    /// wind UNIT_ZERO, emissive 0, ground 0).</remarks>
    public void addSurface(
        IReadOnlyList<P3> points,
        NumberOrArray nx,
        NumberOrArray ny,
        NumberOrArray nz,
        NumberOrArray hex,
        NumberOrArray kind,
        NumberOrArray strength,
        IReadOnlyList<double>? shade = null,
        NumberOrArray? wind = null,
        bool actorWall = false,
        bool preserveEdgeOn = false,
        bool orbitBackside = false,
        NumberOrArray? emissive = null,
        NumberOrArray? ground = null,
        double? wallPigmentFootY = null,
        double? wallPigmentCrestY = null,
        double? wallGroundCrestY = null,
        int fanRoot = 0)
    {
        IReadOnlyList<double> shadeV = shade ?? UNIT_SHADE;
        NumberOrArray windV = wind ?? new NumberOrArray(UNIT_ZERO);
        NumberOrArray emissiveV = emissive ?? new NumberOrArray(0);
        NumberOrArray groundV = ground ?? new NumberOrArray(0);
        double faceNx = nx.isNumber ? nx.number : (optAt(nx.array!, 0) ?? 0);
        double faceNy = ny.isNumber ? ny.number : (optAt(ny.array!, 0) ?? 1);
        double faceNz = nz.isNumber ? nz.number : (optAt(nz.array!, 0) ?? 0);
        if (surfaceChannelRemap != null)
        {
            var remapContext = new TerrainSurfaceRemapContext
            {
                points = points,
                normalX = faceNx,
                normalY = faceNy,
                normalZ = faceNz,
            };
            if (kind.isNumber && strength.isNumber)
            {
                (double remappedKind, double remappedStrength) = surfaceChannelRemap(kind.number, strength.number, remapContext);
                kind = remappedKind;
                strength = remappedStrength;
            }
            else
            {
                var remappedKinds = new double[points.Count];
                var remappedStrengths = new double[points.Count];
                for (int index = 0; index < points.Count; index++)
                {
                    double sourceKind =
                        kind.isNumber ? kind.number : (optAt(kind.array!, index) ?? optAt(kind.array!, 0) ?? SURF.floor);
                    double sourceStrength =
                        strength.isNumber ? strength.number : (optAt(strength.array!, index) ?? optAt(strength.array!, 0) ?? 0);
                    (double k, double s) remapped = surfaceChannelRemap(sourceKind, sourceStrength, remapContext);
                    remappedKinds[index] = remapped.k;
                    remappedStrengths[index] = remapped.s;
                }
                kind = remappedKinds;
                strength = remappedStrengths;
            }
        }
        // A polygon is dropped only when it covers NOTHING on the presented screen. The yaw-free area alone used to
        // decide that, which discarded every wall standing in a constant-x plane — invisible before the world gained
        // its yaw, a real surface after it. `preserveEdgeOn` remains the call site's way to keep a genuinely
        // zero-area polygon in the batch.
        double area = screenArea(points);
        if (points.Count < 3) return;
        if (area == 0 && !preserveEdgeOn && presentedScreenArea(points) == 0) return;
        SurfaceLane b = surface;
        int @base = b.pos.n / 3;
        double[] baseRgb = linearRGB(hex.isNumber ? hex.number : (optAt(hex.array!, 0) ?? 0xffffff));
        double faceNormalLength = orOne(Math.hypot(faceNx, faceNy, faceNz));
        // Actor-occluding geological shells gain bounded rows for strata and height pigment. Their positions stay
        // exactly on the reviewed closed shell: independently bowing a lit facade away from actorWall reopens the
        // historical moving White-Cliff strip at narrow returns.
        P3? p0 = points.Count > 0 ? points[0] : null;
        P3? p1 = points.Count > 1 ? points[1] : null;
        P3? p3 = points.Count > 3 ? points[3] : null;
        double faceMinY = double.PositiveInfinity;
        double faceMaxY = double.NegativeInfinity;
        for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            P3 point = points[pointIndex];
            faceMinY = Math.min(faceMinY, point.y);
            faceMaxY = Math.max(faceMaxY, point.y);
        }
        double faceHeight = Math.max(0, faceMaxY - faceMinY);
        double pigmentFootY = wallPigmentFootY ?? faceMinY;
        double pigmentCrestY = wallPigmentCrestY ?? faceMaxY;
        double pigmentHeight = Math.max(0.001, pigmentCrestY - pigmentFootY);
        // **The crest datum for the ground channel's face slot.** A vertical face's own top edge is the one
        // landmark the fragment shader needs to draw an edge — an overhang contact under it and a fresh-break
        // lip below that — and it is knowable right here, from the polygon that is being emitted, for every one
        // of the 117 call sites at once. Publishing world PX below the crest rather than a normalised fraction
        // is deliberate: a lip is a physical width, so a one-step kerb and a four-step cliff must show the same
        // sliver, which a fraction of face height cannot express. Horizontal surfaces never read the slot.
        bool verticalFace = Math.abs(faceNy / faceNormalLength) < 0.5 && faceHeight > 0.001;
        bool groundIsQuad = !groundV.isNumber;
        double coverOf(int index) =>
            groundV.isNumber ? groundV.number : (optAt(groundV.array!, index * TERRAIN_GROUND_CHANNEL_STRIDE) ?? 0);
        double groundCrestY = wallGroundCrestY ?? faceMaxY;
        double crestDropAt(double y) => verticalFace ? Math.max(0, groundCrestY - y) : 0;
        double primaryKind = kind.isNumber ? kind.number : (optAt(kind.array!, 0) ?? SURF.floor);
        bool chasmGeologicalWall =
            primaryKind >= SURF.chasmWall - 0.25 && primaryKind < SURF.chasmWall + 0.25;
        bool geologicalWall =
            (actorWall || chasmGeologicalWall) &&
            points.Count == 4 &&
            Math.abs(faceNy / faceNormalLength) < 0.45 &&
            faceHeight > ELEV * 0.35;
        bool broadGeologicalWall =
            geologicalWall &&
            p0 != null &&
            p1 != null &&
            Math.hypot(p1.x - p0.x, p1.z - p0.z) >= ELEV * 1.5;
        bool subdividedSurface = actorWall || chasmGeologicalWall;
        double dropLevels = faceHeight / ELEV;
        int subdivisionsU =
            subdividedSurface &&
            points.Count == 4 &&
            p0 != null &&
            p1 != null &&
            Math.hypot(p1.x - p0.x, p1.z - p0.z) >= 2
                ? 2
                : 1;
        int subdivisionsV =
            broadGeologicalWall && dropLevels >= 2
                ? (int)Math.min(4, Math.max(3, Math.round(dropLevels)))
                : subdividedSurface &&
                    points.Count == 4 &&
                    p0 != null &&
                    p3 != null &&
                    Math.hypot(p3.x - p0.x, p3.z - p0.z) >= 2
                    ? 2
                    : 1;
        bool flip = polygonWindingFlip(points, faceNx, faceNy, faceNz, orbitBackside, area);
        if ((subdivisionsU > 1 || subdivisionsV > 1) && points.Count == 4)
        {
            P3 c0 = p0!;
            P3 c1 = p1!;
            P3 p2 = points[2];
            P3 c3 = p3!;
            ActorWallLane wall = this.actorWall;
            int wallBase = wall.pos.n / 3;
            for (int vIndex = 0; vIndex <= subdivisionsV; vIndex++)
            {
                double v = (double)vIndex / subdivisionsV;
                for (int uIndex = 0; uIndex <= subdivisionsU; uIndex++)
                {
                    double u = (double)uIndex / subdivisionsU;
                    double w0 = (1 - u) * (1 - v);
                    double w1 = u * (1 - v);
                    double w2 = u * v;
                    double w3 = (1 - u) * v;
                    double px = c0.x * w0 + c1.x * w1 + p2.x * w2 + c3.x * w3;
                    double py = c0.y * w0 + c1.y * w1 + p2.y * w2 + c3.y * w3;
                    double pz = c0.z * w0 + c1.z * w1 + p2.z * w2 + c3.z * w3;
                    double vertexShade =
                        (optAt(shadeV, 0) ?? 1) * w0 +
                        (optAt(shadeV, 1) ?? 1) * w1 +
                        (optAt(shadeV, 2) ?? 1) * w2 +
                        (optAt(shadeV, 3) ?? 1) * w3;
                    double vertexKind =
                        kind.isNumber
                            ? kind.number
                            : (optAt(kind.array!, 0) ?? SURF.floor) * w0 +
                              (optAt(kind.array!, 1) ?? optAt(kind.array!, 0) ?? SURF.floor) * w1 +
                              (optAt(kind.array!, 2) ?? optAt(kind.array!, 0) ?? SURF.floor) * w2 +
                              (optAt(kind.array!, 3) ?? optAt(kind.array!, 0) ?? SURF.floor) * w3;
                    double vertexStrength =
                        strength.isNumber
                            ? strength.number
                            : (optAt(strength.array!, 0) ?? 0) * w0 +
                              (optAt(strength.array!, 1) ?? optAt(strength.array!, 0) ?? 0) * w1 +
                              (optAt(strength.array!, 2) ?? optAt(strength.array!, 0) ?? 0) * w2 +
                              (optAt(strength.array!, 3) ?? optAt(strength.array!, 0) ?? 0) * w3;
                    double windWeight =
                        windV.isNumber
                            ? windV.number
                            : (optAt(windV.array!, 0) ?? 0) * w0 +
                              (optAt(windV.array!, 1) ?? optAt(windV.array!, 0) ?? 0) * w1 +
                              (optAt(windV.array!, 2) ?? optAt(windV.array!, 0) ?? 0) * w2 +
                              (optAt(windV.array!, 3) ?? optAt(windV.array!, 0) ?? 0) * w3;
                    double vertexEmissive =
                        emissiveV.isNumber
                            ? emissiveV.number
                            : (optAt(emissiveV.array!, 0) ?? 0) * w0 +
                              (optAt(emissiveV.array!, 1) ?? optAt(emissiveV.array!, 0) ?? 0) * w1 +
                              (optAt(emissiveV.array!, 2) ?? optAt(emissiveV.array!, 0) ?? 0) * w2 +
                              (optAt(emissiveV.array!, 3) ?? optAt(emissiveV.array!, 0) ?? 0) * w3;
                    double[] color0 = linearRGB(hex.isNumber ? hex.number : (optAt(hex.array!, 0) ?? 0xffffff));
                    double[] color1 = linearRGB(hex.isNumber ? hex.number : (optAt(hex.array!, 1) ?? optAt(hex.array!, 0) ?? 0xffffff));
                    double[] color2 = linearRGB(hex.isNumber ? hex.number : (optAt(hex.array!, 2) ?? optAt(hex.array!, 0) ?? 0xffffff));
                    double[] color3 = linearRGB(hex.isNumber ? hex.number : (optAt(hex.array!, 3) ?? optAt(hex.array!, 0) ?? 0xffffff));
                    double mixedR = color0[0] * w0 + color1[0] * w1 + color2[0] * w2 + color3[0] * w3;
                    double mixedG = color0[1] * w0 + color1[1] * w1 + color2[1] * w2 + color3[1] * w3;
                    double mixedB = color0[2] * w0 + color1[2] * w1 + color2[2] * w2 + color3[2] * w3;
                    if (geologicalWall)
                    {
                        terrainWallPigmentInto(
                            wallPigmentScratch,
                            mixedR,
                            mixedG,
                            mixedB,
                            px,
                            py,
                            pz,
                            clamp((py - pigmentFootY) / pigmentHeight, 0, 1),
                            1 - Math.abs(u - 0.5) * 2,
                            wallPigment);
                    }
                    else
                    {
                        wallPigmentScratch[0] = mixedR;
                        wallPigmentScratch[1] = mixedG;
                        wallPigmentScratch[2] = mixedB;
                    }
                    double mixedNx =
                        nx.isNumber
                            ? nx.number
                            : (optAt(nx.array!, 0) ?? faceNx) * w0 +
                              (optAt(nx.array!, 1) ?? faceNx) * w1 +
                              (optAt(nx.array!, 2) ?? faceNx) * w2 +
                              (optAt(nx.array!, 3) ?? faceNx) * w3;
                    double mixedNy =
                        ny.isNumber
                            ? ny.number
                            : (optAt(ny.array!, 0) ?? faceNy) * w0 +
                              (optAt(ny.array!, 1) ?? faceNy) * w1 +
                              (optAt(ny.array!, 2) ?? faceNy) * w2 +
                              (optAt(ny.array!, 3) ?? faceNy) * w3;
                    double mixedNz =
                        nz.isNumber
                            ? nz.number
                            : (optAt(nz.array!, 0) ?? faceNz) * w0 +
                              (optAt(nz.array!, 1) ?? faceNz) * w1 +
                              (optAt(nz.array!, 2) ?? faceNz) * w2 +
                              (optAt(nz.array!, 3) ?? faceNz) * w3;
                    double mixedNormalLength = orOne(Math.hypot(mixedNx, mixedNy, mixedNz));
                    b.pos.push3(px, py, pz);
                    b.nrm.push3(
                        mixedNx / mixedNormalLength,
                        mixedNy / mixedNormalLength,
                        mixedNz / mixedNormalLength);
                    b.col.push3(
                        wallPigmentScratch[0] * vertexShade,
                        wallPigmentScratch[1] * vertexShade,
                        wallPigmentScratch[2] * vertexShade);
                    b.surf.push2(vertexKind, windWeight > 0 ? -(windWeight + 0.001) : vertexStrength);
                    b.emissive.push(vertexEmissive);
                    // The cover slot is shared by horizontal turf and vertical moss. Rock faces sample one continuous
                    // world field at the ACTUAL subdivided vertex, so the patch cannot reveal a cell/chunk boundary and
                    // the plant emitter can root in the exact same coverage. `y` carries this vertex's own drop below
                    // the crest; the tangent slots belong to floor caps and stay zero on a face.
                    bool chasmWallVertex =
                        vertexKind >= SURF.chasmWall - 0.25 && vertexKind < SURF.chasmWall + 0.25;
                    b.ground.push4(
                        geologicalWall &&
                        ((vertexKind >= SURF.rockFace - 0.25 && vertexKind < SURF.rockFace + 0.25) ||
                         chasmWallVertex)
                            ? terrainWallMossCoverAt(
                                px,
                                py,
                                pz,
                                clamp((py - pigmentFootY) / pigmentHeight, 0, 1),
                                chasmWallVertex ? chasmWallGrowth : wallGrowth)
                            : 0,
                        crestDropAt(py),
                        0,
                        0);
                    if (actorWall) wall.pos.push3(px, py, pz);
                }
            }
            int rowStride = subdivisionsU + 1;
            for (int vIndex = 0; vIndex < subdivisionsV; vIndex++)
            {
                for (int uIndex = 0; uIndex < subdivisionsU; uIndex++)
                {
                    int a = @base + vIndex * rowStride + uIndex;
                    int bIndex = a + 1;
                    int d = a + rowStride;
                    int c = d + 1;
                    int wallA = wallBase + vIndex * rowStride + uIndex;
                    int wallB = wallA + 1;
                    int wallD = wallA + rowStride;
                    int wallC = wallD + 1;
                    if (flip)
                    {
                        push6(b.idx, a, c, bIndex, a, d, c);
                        if (actorWall) push6(wall.idx, wallA, wallC, wallB, wallA, wallD, wallC);
                    }
                    else
                    {
                        push6(b.idx, a, bIndex, c, a, c, d);
                        if (actorWall) push6(wall.idx, wallA, wallB, wallC, wallA, wallC, wallD);
                    }
                }
            }
            return;
        }
        // The wind channel used to describe only how FAR a vertex could bend. That cannot answer whether the
        // polygon itself is resolvable: a half-pixel blossom cap may travel several pixels and still flash between
        // zero and one covered sample. Publish the polygon's presented linear footprint in the otherwise unused
        // ground.w lane for every wind-bearing face. It is constant across the face, worker-transferable, and lets
        // the material perform object-space microgeometry LOD without sampling or blurring neighbouring pixels.
        bool faceHasWind;
        if (windV.isNumber) faceHasWind = windV.number > 0;
        else
        {
            faceHasWind = false;
            IReadOnlyList<double> windArray = windV.array!;
            for (int windIndex = 0; windIndex < windArray.Count; windIndex++)
            {
                if (windArray[windIndex] > 0)
                {
                    faceHasWind = true;
                    break;
                }
            }
        }
        double animatedFacetFootprint = faceHasWind
            ? Math.sqrt(Math.abs(presentedScreenArea(points)) * 0.5)
            : 0;
        for (int i = 0; i < points.Count; i++)
        {
            P3 p = points[i];
            double s = optAt(shadeV, i) ?? optAt(shadeV, 0) ?? 1;
            double[] rgb = hex.isNumber ? baseRgb : linearRGB(optAt(hex.array!, i) ?? optAt(hex.array!, 0) ?? 0xffffff);
            double vertexNx = nx.isNumber ? nx.number : (optAt(nx.array!, i) ?? faceNx);
            double vertexNy = ny.isNumber ? ny.number : (optAt(ny.array!, i) ?? faceNy);
            double vertexNz = nz.isNumber ? nz.number : (optAt(nz.array!, i) ?? faceNz);
            double vertexNormalLength = orOne(Math.hypot(vertexNx, vertexNy, vertexNz));
            b.pos.push3(p.x, p.y, p.z);
            b.nrm.push3(
                vertexNx / vertexNormalLength,
                vertexNy / vertexNormalLength,
                vertexNz / vertexNormalLength);
            b.col.push3(rgb[0] * s, rgb[1] * s, rgb[2] * s);
            // Wind lives in the sign of the existing surface-strength channel. Terrain uses positive pattern
            // strength; foliage stores -(bendPx + epsilon), avoiding a full extra vertex buffer on every polygon.
            double windWeight = windV.isNumber ? windV.number : (optAt(windV.array!, i) ?? 0);
            double surfaceStrength = strength.isNumber ? strength.number : (optAt(strength.array!, i) ?? 0);
            b.surf.push2(
                kind.isNumber ? kind.number : (optAt(kind.array!, i) ?? optAt(kind.array!, 0) ?? SURF.floor),
                windWeight > 0 ? -(windWeight + 0.001) : surfaceStrength);
            b.emissive.push(emissiveV.isNumber ? emissiveV.number : (optAt(emissiveV.array!, i) ?? 0));
            b.ground.push4(
                coverOf(i),
                groundIsQuad ? (optAt(groundV.array!, i * TERRAIN_GROUND_CHANNEL_STRIDE + 1) ?? 0) : crestDropAt(p.y),
                groundIsQuad ? (optAt(groundV.array!, i * TERRAIN_GROUND_CHANNEL_STRIDE + 2) ?? 0) : 0,
                animatedFacetFootprint > 0
                    ? -animatedFacetFootprint
                    : groundIsQuad
                        ? (optAt(groundV.array!, i * TERRAIN_GROUND_CHANNEL_STRIDE + 3) ?? 0)
                        : 0);
        }
        // The gameplay projection normalizes visible faces in screen space. Closed backside geometry used by the
        // 360-degree presentation orbit deliberately gets the opposite winding: it is culled at yaw zero and
        // becomes the front face after the world turns around. Its authored normal still owns lighting.
        fan(b.idx, @base, points.Count, flip, fanRoot);
        if (actorWall)
        {
            ActorWallLane wall = this.actorWall;
            int wallBase = wall.pos.n / 3;
            if (points.Count > 4)
            {
                double minX = double.PositiveInfinity;
                double maxX = double.NegativeInfinity;
                double minZ = double.PositiveInfinity;
                double maxZ = double.NegativeInfinity;
                double maxY = double.NegativeInfinity;
                for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                {
                    P3 p = points[pointIndex];
                    minX = Math.min(minX, p.x);
                    maxX = Math.max(maxX, p.x);
                    maxY = Math.max(maxY, p.y);
                    minZ = Math.min(minZ, p.z);
                    maxZ = Math.max(maxZ, p.z);
                }
                // Edge chips are a visual cap nick only. Keep the actor fan's closure centre on the true cap datum.
                wall.pos.push3((minX + maxX) * 0.5, maxY, (minZ + maxZ) * 0.5);
                for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                {
                    P3 p = points[pointIndex];
                    wall.pos.push3(p.x, p.y, p.z);
                }
                for (int index = 0; index < points.Count; index++)
                {
                    int a = wallBase + 1 + index;
                    int next = wallBase + 1 + ((index + 1) % points.Count);
                    if (flip) push3(wall.idx, wallBase, next, a);
                    else push3(wall.idx, wallBase, a, next);
                }
            }
            else
            {
                for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                {
                    P3 p = points[pointIndex];
                    wall.pos.push3(p.x, p.y, p.z);
                }
                fan(wall.idx, wallBase, points.Count, flip);
            }
        }
    }

    /// <summary>
    /// Push ONE welded `side × side` vertex lattice as a single indexed patch of the lit surface batch.
    ///
    /// The organic floor cap is a tessellated PATCH, not a bag of quads. Emitting each sub-quad through
    /// <see cref="addSurface"/> gave the 2×2 patch sixteen unshared vertices for nine distinct positions: every interior
    /// lattice point existed two or four times over, each copy re-deriving the identical colour/normal/turf from
    /// the identical scratch entry. Welding removes 44 % of the cap's vertices and every opportunity for those
    /// copies to ever disagree, at an identical triangle count, identical positions and identical draw call — the
    /// patch simply becomes one surface instead of four that happen to touch.
    ///
    /// Per-vertex channels are addressed by LATTICE INDEX (`row * side + column`, north-west first), matching the
    /// scratch layout the cap already fills. The winding decision is taken once from the patch outline, which is
    /// exactly what the per-quad path did (all sub-quads of a cap share one orientation).
    /// </summary>
    public void addSurfaceLattice(
        IReadOnlyList<P3> points,
        int side,
        IReadOnlyList<double> normalX,
        IReadOnlyList<double> normalZ,
        IReadOnlyList<double> hex,
        double kind,
        double strength,
        IReadOnlyList<double> shade,
        IReadOnlyList<double> ground)
    {
        if (surfaceChannelRemap != null)
            (kind, strength) = surfaceChannelRemap(kind, strength, new TerrainSurfaceRemapContext
            {
                points = points,
                normalX = 0,
                normalY = 1,
                normalZ = 0,
            });
        int count = side * side;
        if (side < 2 || points.Count < count) return;
        P3[] corners = LATTICE_CORNER_SCRATCH;
        corners[0] = points[0];
        corners[1] = points[side - 1];
        corners[2] = points[count - 1];
        corners[3] = points[count - side];
        // Cap lattices are horizontal, so they are always well-conditioned; they follow the shared rule anyway.
        double area = screenArea(corners);
        bool flipLattice = polygonWindingFlip(corners, 0, 1, 0, false, area);
        SurfaceLane b = surface;
        int @base = b.pos.n / 3;
        for (int index = 0; index < count; index++)
        {
            P3 point = points[index];
            double s = optAt(shade, index) ?? 1;
            double[] rgb = linearRGB(optAt(hex, index) ?? optAt(hex, 0) ?? 0xffffff);
            double nx = optAt(normalX, index) ?? 0;
            double nz = optAt(normalZ, index) ?? 0;
            double length = orOne(Math.hypot(nx, 1, nz));
            b.pos.push3(point.x, point.y, point.z);
            b.nrm.push3(nx / length, 1 / length, nz / length);
            b.col.push3(rgb[0] * s, rgb[1] * s, rgb[2] * s);
            b.surf.push2(kind, strength);
            b.emissive.push(0);
            // A cap is horizontal, so its ground channel is the floor reading throughout: cover, wear, tangent.
            b.ground.push4(
                optAt(ground, index * TERRAIN_GROUND_CHANNEL_STRIDE) ?? 0,
                optAt(ground, index * TERRAIN_GROUND_CHANNEL_STRIDE + 1) ?? 0,
                optAt(ground, index * TERRAIN_GROUND_CHANNEL_STRIDE + 2) ?? 0,
                optAt(ground, index * TERRAIN_GROUND_CHANNEL_STRIDE + 3) ?? 0);
        }
        bool flip = flipLattice;
        for (int row = 0; row + 1 < side; row++)
        {
            for (int column = 0; column + 1 < side; column++)
            {
                int nw = @base + row * side + column;
                int ne = nw + 1;
                int sw = nw + side;
                int se = sw + 1;
                if (flip) push6(b.idx, nw, se, ne, nw, sw, se);
                else push6(b.idx, nw, ne, se, nw, se, sw);
            }
        }
    }

    /// <summary>Push a horizontal water-surface polygon. Per-vertex shore/foam + depth ride aWater.xy. z carries the REAL
    /// positive cell size, so 40 px Hub/editor water and 78.125 px run water evaluate the same physical shoreline
    /// width. w carries packed bank topology. A Bridge never changes either channel: cover is physical occlusion,
    /// not a second liquid material. Neither value translates the world-continuous field.</summary>
    /// <remarks>`null` optional channels take the TS defaults (reflection 0, normal (0, 1, 0)).</remarks>
    public void addWater(
        IReadOnlyList<P3> points,
        NumberOrArray hex,
        IReadOnlyList<double> foam,
        NumberOrArray depth,
        double flowX,
        double flowZ,
        double cellSize,
        NumberOrArray? reflection = null,
        NumberOrArray? normalX = null,
        NumberOrArray? normalY = null,
        NumberOrArray? normalZ = null)
    {
        addWaterFace(
            points,
            normalX ?? new NumberOrArray(0),
            normalY ?? new NumberOrArray(1),
            normalZ ?? new NumberOrArray(0),
            hex,
            foam,
            depth,
            flowX,
            flowZ,
            false,
            false,
            reflection ?? new NumberOrArray(0),
            true,
            cellSize);
    }

    /// <summary>Waterfall sheets share the animated liquid batch but carry a real vertical normal. A negative depth marks
    /// the falling-water shader path; z stores sheet axis and w stores the crest world-height so the horizontal
    /// surface field can continue over the lip and advect down the complete curtain without a UV restart.</summary>
    /// <param name="viewerFacingSheet">
    /// This polygon is an UNBACKED sheet hanging in open air, so it has no outward side of its own and must
    /// always present itself to the eye. Winding then resolves against the view direction instead of against
    /// the supplied shading normals, which for a falling curtain are world-up at its crest and therefore carry
    /// no orientation at all once the sheet stands edge-on.
    /// </param>
    public void addWaterFace(
        IReadOnlyList<P3> points,
        NumberOrArray nx,
        NumberOrArray ny,
        NumberOrArray nz,
        NumberOrArray hex,
        IReadOnlyList<double> foam,
        NumberOrArray depth,
        NumberOrArray flowX,
        NumberOrArray flowZ,
        bool orbitBackside = false,
        bool preserveEdgeOn = false,
        NumberOrArray? reflection = null,
        bool cellLocalUv = false,
        double cellSize = 0,
        double reflectionFieldY = 0,
        double reflectionFieldZ = 0,
        NumberOrArray? foldX = null,
        NumberOrArray? foldZ = null,
        bool viewerFacingSheet = false)
    {
        NumberOrArray reflectionV = reflection ?? new NumberOrArray(0);
        // As in the surface batch: presence is decided by what the PRESENTED projection covers.
        double area = screenArea(points);
        if (points.Count < 3) return;
        if (area == 0 && !preserveEdgeOn && presentedScreenArea(points) == 0) return;
        WaterLane b = water;
        int @base = b.pos.n / 3;
        double faceNx = nx.isNumber ? nx.number : (optAt(nx.array!, 0) ?? 0);
        double faceNy = ny.isNumber ? ny.number : (optAt(ny.array!, 0) ?? 1);
        double faceNz = nz.isNumber ? nz.number : (optAt(nz.array!, 0) ?? 0);
        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minZ = double.PositiveInfinity;
        double maxZ = double.NegativeInfinity;
        if (cellLocalUv)
        {
            for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                P3 point = points[pointIndex];
                minX = Math.min(minX, point.x);
                maxX = Math.max(maxX, point.x);
                minZ = Math.min(minZ, point.z);
                maxZ = Math.max(maxZ, point.z);
            }
            // A contoured corner no longer contains the square extremum on both axes. Snap to the authoritative tile
            // lattice instead of normalizing against the shrunken polygon bounds, so independently baked neighbours
            // still agree bit-for-bit on their local coordinates.
            if (!(cellSize > 0)) throw new InvalidOperationException("cell-local water UVs require a positive cell size");
            minX = Math.floor((minX + 0.001) / cellSize) * cellSize;
            minZ = Math.floor((minZ + 0.001) / cellSize) * cellSize;
            maxX = minX + cellSize;
            maxZ = minZ + cellSize;
        }
        for (int i = 0; i < points.Count; i++)
        {
            P3 p = points[i];
            double[] rgb = linearRGB(hex.isNumber ? hex.number : (optAt(hex.array!, i) ?? optAt(hex.array!, 0) ?? 0));
            double r = rgb[0];
            double g = rgb[1];
            double bl = rgb[2];
            double vertexNx = nx.isNumber ? nx.number : (optAt(nx.array!, i) ?? faceNx);
            double vertexNy = ny.isNumber ? ny.number : (optAt(ny.array!, i) ?? faceNy);
            double vertexNz = nz.isNumber ? nz.number : (optAt(nz.array!, i) ?? faceNz);
            double len = orOne(Math.hypot(vertexNx, vertexNy, vertexNz));
            b.pos.push3(p.x, p.y, p.z);
            b.nrm.push3(vertexNx / len, vertexNy / len, vertexNz / len);
            b.col.push3(r, g, bl);
            b.attr.push4(
                optAt(foam, i) ?? 0,
                depth.isNumber ? depth.number : (optAt(depth.array!, i) ?? 0),
                flowX.isNumber ? flowX.number : (optAt(flowX.array!, i) ?? optAt(flowX.array!, 0) ?? 0),
                flowZ.isNumber ? flowZ.number : (optAt(flowZ.array!, i) ?? optAt(flowZ.array!, 0) ?? 0));
            b.fold.push2(
                foldX == null
                    ? p.x
                    : foldX.Value.isNumber
                        ? foldX.Value.number
                        : (optAt(foldX.Value.array!, i) ?? optAt(foldX.Value.array!, 0) ?? p.x),
                foldZ == null
                    ? p.z
                    : foldZ.Value.isNumber
                        ? foldZ.Value.number
                        : (optAt(foldZ.Value.array!, i) ?? optAt(foldZ.Value.array!, 0) ?? p.z));
            b.reflection.push3(
                reflectionV.isNumber ? reflectionV.number : (optAt(reflectionV.array!, i) ?? optAt(reflectionV.array!, 0) ?? 0),
                cellLocalUv ? clamp((p.x - minX) / Math.max(0.001, maxX - minX), 0, 1) : reflectionFieldY,
                cellLocalUv ? clamp((p.z - minZ) / Math.max(0.001, maxZ - minZ), 0, 1) : reflectionFieldZ);
        }
        // Curved waterfall and deformed shoreline quads are not guaranteed to remain convex after the production
        // yaw/projection. Winding the complete polygon once can therefore keep one fan triangle and cull its
        // neighbour, which is the tiny triangular hole repeatedly visible between waterfall strips and below
        // Bridge corners. Resolve every emitted triangle independently. Ordinary horizontal liquid retains its
        // physical face normal; an unbacked curtain uses the actual presented eye and presented projected area.
        P3[] triangle = WATER_CONTOUR_TRIANGLE;
        for (int index = 1; index < points.Count - 1; index++)
        {
            triangle[0] = points[0];
            triangle[1] = points[index];
            triangle[2] = points[index + 1];
            P3 triangleNormal = polygonNormalInto(triangle);
            // A rounded waterfall return begins at one shared crest vertex. Its first quad is therefore a triangle,
            // not a quad with a zero-area half; publishing that degenerate half left an undefined normal/winding at
            // precisely the Water/Chasm corner. The common triangulator owns this invariant for every liquid polygon.
            if (triangleNormal.x == 0 && triangleNormal.y == 0 && triangleNormal.z == 0) continue;
            double triangleArea = viewerFacingSheet
                ? presentedScreenAreaAtTransferPrecision(triangle)
                : screenArea(triangle);
            // An unbacked sheet triangle exactly edge-on to the immutable production camera covers no sample and has
            // no meaningful front face. Do not publish an index that FrontSide must discard as an ambiguous zero.
            if (viewerFacingSheet && triangleArea == 0) continue;
            // For an explicitly eye-facing sheet the presented raster area IS the ownership contract, including for
            // an almost edge-on terminal sliver. Sending that tiny area through the generic world-area fallback can
            // override its unambiguous screen sign and cull it; this was the final black/purple triangle in the
            // exhaustive Bridge(Water)/Bridge(Chasm) corner matrix. Ordinary physical faces still use their authored
            // normal and the near-edge-on world-space fallback.
            bool flip = viewerFacingSheet
                ? triangleArea > 0
                : polygonWindingFlip(
                    triangle,
                    faceNx,
                    faceNy,
                    faceNz,
                    orbitBackside,
                    triangleArea);
            if (flip) push3(b.idx, @base, @base + index + 1, @base + index);
            else push3(b.idx, @base, @base + index, @base + index + 1);
        }
    }

    /// <summary>Push a translucent decal polygon (uniform alpha).</summary>
    public void addOverlay(IReadOnlyList<P3> points, int hex, double alpha, double edgeOnNormalX = 0)
    {
        addOverlayShaded(points, hex, alpha, null, edgeOnNormalX);
    }

    /// <summary>Push a polygon into the shadow-caster batch ONLY — never drawn, purely to throw a sun shadow. Its
    /// material is `colorWrite: false`/`shadowSide: DoubleSide`, so a proxy needs no pigment and no hull.</summary>
    public void addShadowCaster(IReadOnlyList<P3> points)
    {
        if (points.Count < 3) return;
        int @base = actorWall.pos.n / 3;
        for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            P3 point = points[pointIndex];
            actorWall.pos.push3(point.x, point.y, point.z);
        }
        fan(actorWall.idx, @base, points.Count, false);
    }

    /// <summary>
    /// Allocation-free feathered contact AO for a grounded prop. One centre plus an irregular transparent ring
    /// replaces the old uniformly dark hex sticker; it remains part of the single overlay batch.
    /// </summary>
    public void addContactBlob(
        double cx,
        double cz,
        double y,
        double rx,
        double rz,
        int hex,
        double alpha)
    {
        if (alpha <= 0.004 || rx <= 0.1 || rz <= 0.1) return;
        const int segments = 8;
        OverlayLane b = overlay;
        int @base = b.pos.n / 3;
        double[] rgb = linearRGB(hex);
        double r = rgb[0];
        double g = rgb[1];
        double bl = rgb[2];
        double shadowZ = cz + 2;
        b.pos.push3(cx, y, shadowZ);
        b.col.push4(r, g, bl, Math.min(1, alpha));
        double seedX = Math.floor(cx * 0.125);
        double seedZ = Math.floor(cz * 0.125);
        for (int index = 0; index < segments; index++)
        {
            double angle = ((double)index / segments) * PROP_TAU;
            double irregular = 0.9 + cellHash(seedX + index * 17, seedZ - index * 29) * 0.2;
            b.pos.push3(
                cx + Math.cos(angle) * rx * irregular,
                y,
                shadowZ + Math.sin(angle) * rz * irregular);
            b.col.push4(r, g, bl, 0);
        }
        for (int index = 0; index < segments; index++)
        {
            int next = (index + 1) % segments;
            // Ground circles are clockwise in screen coordinates under the shear projection, hence flipped here.
            push3(b.idx, @base, @base + 1 + next, @base + 1 + index);
        }
    }

    /// <summary>Push a translucent decal polygon with optional per-vertex alpha (soft AO/contact gradients).</summary>
    public void addOverlayShaded(
        IReadOnlyList<P3> points,
        int hex,
        double alpha,
        IReadOnlyList<double>? alphas = null,
        double edgeOnNormalX = 0)
    {
        if (overlayChannelRemap != null)
        {
            double sourceAlpha = alpha;
            (int remappedHex, double remappedAlpha) = overlayChannelRemap(hex, sourceAlpha);
            hex = remappedHex;
            alpha = remappedAlpha;
            if (alphas != null)
            {
                double alphaScale = sourceAlpha > 0.0001 ? alpha / sourceAlpha : 0;
                var scaled = new double[alphas.Count];
                for (int index = 0; index < alphas.Count; index++) scaled[index] = alphas[index] * alphaScale;
                alphas = scaled;
            }
        }
        if ((alphas == null && alpha <= 0.004) || points.Count < 3) return;
        double area = screenArea(points);
        if (area == 0 && edgeOnNormalX == 0) return;
        OverlayLane b = overlay;
        int @base = b.pos.n / 3;
        double[] rgb = linearRGB(hex);
        double r = rgb[0];
        double g = rgb[1];
        double bl = rgb[2];
        for (int i = 0; i < points.Count; i++)
        {
            P3 p = points[i];
            b.pos.push3(p.x, p.y, p.z);
            b.col.push4(r, g, bl, Math.min(1, (alphas != null ? optAt(alphas, i) : null) ?? alpha));
        }
        bool flip = area == 0 ? polygonNormalDot(points, edgeOnNormalX, 0, 0) < 0 : area > 0;
        fan(b.idx, @base, points.Count, flip);
    }

    /// <summary>One upright, feathered updraft wisp. Generic Chasm atmosphere uses this orientation so its silhouette
    /// describes air moving through a shaft, never a horizontal fog disc that can be mistaken for muddy ground.</summary>
    public void addMistWisp(
        double cx,
        double cy,
        double cz,
        double radiusX,
        double radiusY,
        int hex,
        double alpha,
        double phase,
        double drift,
        double seed,
        double layer = 2,
        double rise = 0.58)
    {
        if (alpha <= 0.004 || radiusX <= 0.1 || radiusY <= 0.1) return;
        const int segments = 12;
        MistLane b = mist;
        int @base = b.pos.n / 3;
        double[] rgb = linearRGB(hex);
        double r = rgb[0];
        double g = rgb[1];
        double bl = rgb[2];
        void vertex(double x, double y, double z, double a)
        {
            b.pos.push3(x, y, z);
            b.col.push4(r, g, bl, clamp(a, 0, 1));
            b.attr.push4(phase, drift, Math.floor(clamp(layer, 0, 2)) + clamp(rise, 0.02, 0.98), seed);
        }
        // Build the disc on the eye-facing basis instead of as a vertical XY card. A vertical card is a WALL in
        // the world: it stands inside the Chasm it is meant to soften and passes through bridge decks and water,
        // and every one of those intersections clips the soft alpha into a hard grey silhouette — the "grey
        // smoke" seen under bridges and across chasm mouths. A disc perpendicular to the view direction cannot
        // intersect the world edge-on, so it can never produce that edge at any camera azimuth.
        P3 right = MIST_BILLBOARD_RIGHT;
        P3 up = MIST_BILLBOARD_UP;
        terrainBillboardBasisInto(right, up);
        vertex(cx, cy, cz, alpha);
        for (int ring = 0; ring < 2; ring++)
        {
            double scale = ring == 0 ? 0.54 : 1;
            double ringAlpha = ring == 0 ? alpha * 0.56 : 0;
            for (int i = 0; i < segments; i++)
            {
                double angle = ((double)i / segments) * Math.PI * 2;
                double irregular = 0.9 + cellHash(i * 61 + Math.floor(seed * 887), i * 43 + 29) * 0.2;
                double across = Math.cos(angle) * radiusX * scale * irregular;
                double along = Math.sin(angle) * radiusY * scale * irregular;
                vertex(
                    cx + right.x * across + up.x * along,
                    cy + right.y * across + up.y * along,
                    cz + right.z * across + up.z * along,
                    ringAlpha);
            }
        }
        int inner = @base + 1;
        int outer = inner + segments;
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            push3(b.idx, @base, inner + i, inner + next);
            push6(b.idx, inner + i, outer + i, outer + next, inner + i, outer + next, inner + next);
        }
    }

    /// <summary>A decal LINE lying in a horizontal plane at height `y` (most decals live on a cap/floor top).</summary>
    public void addOverlayLineFlat(
        double y,
        double ax,
        double az,
        double bx,
        double bz,
        double width,
        int hex,
        double alpha)
    {
        double dx = bx - ax;
        double dz = bz - az;
        double len = orOne(Math.hypot(dx, dz));
        double px = (-dz / len) * width * 0.5;
        double pz = (dx / len) * width * 0.5;
        addOverlay(
            quad(ax + px, y, az + pz, bx + px, y, bz + pz, bx - px, y, bz - pz, ax - px, y, az - pz),
            hex,
            alpha);
    }

    public TerrainGeometryCursor geometryCursor()
    {
        var cursor = new TerrainGeometryCursor();
        cursor.surface.position = surface.pos.n;
        cursor.surface.normal = surface.nrm.n;
        cursor.surface.color = surface.col.n;
        cursor.surface.surface = surface.surf.n;
        cursor.surface.emissive = surface.emissive.n;
        cursor.surface.ground = surface.ground.n;
        cursor.surface.index = surface.idx.Count;
        cursor.water.position = water.pos.n;
        cursor.water.normal = water.nrm.n;
        cursor.water.color = water.col.n;
        cursor.water.water = water.attr.n;
        cursor.water.fold = water.fold.n;
        cursor.water.reflection = water.reflection.n;
        cursor.water.index = water.idx.Count;
        cursor.mist.position = mist.pos.n;
        cursor.mist.color = mist.col.n;
        cursor.mist.mist = mist.attr.n;
        cursor.mist.index = mist.idx.Count;
        cursor.overlay.position = overlay.pos.n;
        cursor.overlay.color = overlay.col.n;
        cursor.overlay.index = overlay.idx.Count;
        cursor.actorWall.position = actorWall.pos.n;
        cursor.actorWall.index = actorWall.idx.Count;
        cursor.vegetation = vegetation.n;
        return cursor;
    }

    /// <summary>Snapshot the CPU-authored tile as transfer-only buffers. The terrain worker uses this path so the main
    /// thread receives no materialized-cell/render-plan object graph and performs no polygon generation.</summary>
    public TerrainGeometryPayload toTransferPayload(TerrainGeometryBufferPool? pool = null)
    {
        return toTransferPayloadRange(EMPTY_GEOMETRY_CURSOR, geometryCursor(), pool);
    }

    /// <summary>Snapshot one contiguous compiler layer and rebase its indices to its own local vertex stores.</summary>
    public TerrainGeometryPayload toTransferPayloadRange(
        TerrainGeometryCursor start,
        TerrainGeometryCursor end,
        TerrainGeometryBufferPool? pool = null)
    {
        float[]? surfacePosition =
            end.surface.index > start.surface.index
                ? surface.pos.sliceRange(start.surface.position, end.surface.position, pool)
                : null;
        float[]? waterPosition =
            end.water.index > start.water.index
                ? water.pos.sliceRange(start.water.position, end.water.position, pool)
                : null;
        float[]? mistPosition =
            end.mist.index > start.mist.index
                ? mist.pos.sliceRange(start.mist.position, end.mist.position, pool)
                : null;
        float[]? overlayPosition =
            end.overlay.index > start.overlay.index
                ? overlay.pos.sliceRange(start.overlay.position, end.overlay.position, pool)
                : null;
        float[]? actorWallPosition =
            end.actorWall.index > start.actorWall.index
                ? actorWall.pos.sliceRange(start.actorWall.position, end.actorWall.position, pool)
                : null;
        var payload = new TerrainGeometryPayload();
        // Property order (and so slice/acquire order from the pool) follows the TS object literal.
        if (surfacePosition != null)
        {
            payload.surface = new TerrainGeometryPayload.SurfaceLane
            {
                position = surfacePosition,
                normal = surface.nrm.sliceRange(start.surface.normal, end.surface.normal, pool),
                color = surface.col.sliceRange(start.surface.color, end.surface.color, pool),
                surface = surface.surf.sliceRange(
                    start.surface.surface,
                    end.surface.surface,
                    pool),
                emissive = surface.emissive.sliceRange(
                    start.surface.emissive,
                    end.surface.emissive,
                    pool),
                ground = surface.ground.sliceRange(
                    start.surface.ground,
                    end.surface.ground,
                    pool),
                index = uint32IndexRange(
                    pool,
                    surface.idx,
                    start.surface.index,
                    end.surface.index,
                    start.surface.position / 3),
                bounds = boundsOfPositions(surfacePosition),
            };
        }
        if (waterPosition != null)
        {
            payload.water = new TerrainGeometryPayload.WaterLane
            {
                position = waterPosition,
                normal = water.nrm.sliceRange(start.water.normal, end.water.normal, pool),
                color = water.col.sliceRange(start.water.color, end.water.color, pool),
                water = water.attr.sliceRange(start.water.water, end.water.water, pool),
                fold = water.fold.sliceRange(start.water.fold, end.water.fold, pool),
                reflection = water.reflection.sliceRange(
                    start.water.reflection,
                    end.water.reflection,
                    pool),
                index = uint32IndexRange(
                    pool,
                    water.idx,
                    start.water.index,
                    end.water.index,
                    start.water.position / 3),
                bounds = boundsOfPositions(waterPosition),
            };
        }
        if (mistPosition != null)
        {
            payload.mist = new TerrainGeometryPayload.MistLane
            {
                position = mistPosition,
                color = mist.col.sliceRange(start.mist.color, end.mist.color, pool),
                mist = mist.attr.sliceRange(start.mist.mist, end.mist.mist, pool),
                index = uint32IndexRange(
                    pool,
                    mist.idx,
                    start.mist.index,
                    end.mist.index,
                    start.mist.position / 3),
                bounds = boundsOfPositions(mistPosition),
            };
        }
        if (overlayPosition != null)
        {
            payload.overlay = new TerrainGeometryPayload.OverlayLane
            {
                position = overlayPosition,
                color = overlay.col.sliceRange(start.overlay.color, end.overlay.color, pool),
                index = uint32IndexRange(
                    pool,
                    overlay.idx,
                    start.overlay.index,
                    end.overlay.index,
                    start.overlay.position / 3),
                bounds = boundsOfPositions(overlayPosition),
            };
        }
        if (actorWallPosition != null)
        {
            payload.actorWall = new TerrainGeometryPayload.ActorWallLane
            {
                position = actorWallPosition,
                index = uint32IndexRange(
                    pool,
                    actorWall.idx,
                    start.actorWall.index,
                    end.actorWall.index,
                    start.actorWall.position / 3),
                bounds = boundsOfPositions(actorWallPosition),
            };
        }
        payload.vegetation = TerrainVegetationLane.Slice(vegetation, start.vegetation, end.vegetation);
        return payload;
    }

    // ── Structural builder contracts (TS satisfies them implicitly) ───────────────────────────────────────

    void PropSurfaceBuilder.addSurface(
        IReadOnlyList<P3> points,
        NumberOrArray nx,
        NumberOrArray ny,
        NumberOrArray nz,
        NumberOrArray hex,
        NumberOrArray kind,
        NumberOrArray strength,
        IReadOnlyList<double>? shade,
        NumberOrArray? wind,
        bool actorWall,
        bool preserveEdgeOn,
        bool orbitBackside,
        NumberOrArray? emissive)
    {
        addSurface(points, nx, ny, nz, hex, kind, strength, shade, wind, actorWall, preserveEdgeOn, orbitBackside, emissive);
    }

    void SuspensionSurfaceBuilder.addSurface(
        IReadOnlyList<SuspensionPoint> points,
        double nx,
        double ny,
        double nz,
        int hex,
        int kind,
        double strength,
        double[]? shade,
        double[]? wind,
        bool actorWall,
        bool preserveEdgeOn,
        bool orbitBackside)
    {
        addSurface(
            points,
            nx,
            ny,
            nz,
            hex,
            kind,
            strength,
            shade,
            wind == null ? (NumberOrArray?)null : new NumberOrArray(wind),
            actorWall,
            preserveEdgeOn,
            orbitBackside);
    }

    void SuspensionSurfaceBuilder.addOverlay(IReadOnlyList<SuspensionPoint> points, int hex, double alpha)
    {
        addOverlay(points, hex, alpha);
    }

    /// <summary>Per-thread exact-length copies for <see cref="groundingPoints"/>, one per point count (≤ 16).</summary>
    [ThreadStatic] private static P3[]?[]? _GROUNDING_POINTS_SCRATCH;

    private static P3[] newGroundingScratch(int count)
    {
        var points = new P3[count];
        for (int index = 0; index < count; index++) points[index] = new P3(0, 0, 0);
        return points;
    }

    private static P3[] groundingPoints(List<TerrainGroundingPoint> points)
    {
        // TS passes the very same objects; C# point classes are nominal, so the builder reads a value copy. The
        // builder copies vertex data out synchronously and never retains the points, so the copy is invisible —
        // and can live in per-thread scratch (one exact-length array per count: the builder reads `Count`).
        int count = points.Count;
        P3[] copy;
        if (count <= 16)
        {
            P3[]?[] scratch = _GROUNDING_POINTS_SCRATCH ??= new P3[]?[17];
            copy = scratch[count] ??= newGroundingScratch(count);
            for (int index = 0; index < count; index++)
            {
                P3 point = copy[index];
                point.x = points[index].x;
                point.y = points[index].y;
                point.z = points[index].z;
            }
            return copy;
        }
        copy = new P3[count];
        for (int index = 0; index < count; index++)
            copy[index] = new P3(points[index].x, points[index].y, points[index].z);
        return copy;
    }

    void TerrainGroundingOverlayBuilder.addOverlayShaded(
        List<TerrainGroundingPoint> points,
        int hex,
        double alpha,
        IReadOnlyList<double>? alphas,
        double edgeOnNormalX)
    {
        addOverlayShaded(groundingPoints(points), hex, alpha, alphas, edgeOnNormalX);
    }

    void TerrainGroundingSurfaceBuilder.addSurface(
        List<TerrainGroundingPoint> points,
        double nx,
        double ny,
        double nz,
        int hex,
        int kind,
        double strength,
        IReadOnlyList<double>? shade,
        double? wind,
        bool? actorWall,
        bool? preserveEdgeOn,
        bool? orbitBackside)
    {
        addSurface(
            groundingPoints(points),
            nx,
            ny,
            nz,
            hex,
            kind,
            strength,
            shade,
            wind == null ? (NumberOrArray?)null : new NumberOrArray(wind.Value),
            actorWall ?? false,
            preserveEdgeOn ?? false,
            orbitBackside ?? false);
    }
}

public static partial class TerrainGeometryCompilerModule
{
    internal static uint[] uint32IndexRange(
        TerrainGeometryBufferPool? pool,
        IReadOnlyList<int> values,
        int start,
        int end,
        int vertexOffset)
    {
        int count = Math.max(0, end - start);
        uint[] @out = pool != null
            ? pool.acquireUint32(count)
            : new uint[count];
        for (int index = 0; index < count; index++)
        {
            // `(values[start + index] ?? 0) - vertexOffset` stored into a Uint32Array (ToUint32 wraps negatives).
            int value = (uint)(start + index) < (uint)values.Count ? values[start + index] : 0;
            @out[index] = unchecked((uint)(value - vertexOffset));
        }
        return @out;
    }

    /// <summary>Conservative box-centred sphere computed where geometry is authored (the worker in Endless runs).</summary>
    internal static TerrainGeometryBounds boundsOfPositions(float[] position)
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        double maxZ = double.NegativeInfinity;
        for (int i = 0; i < position.Length; i += 3)
        {
            double px = position[i];
            double py = position[i + 1];
            double pz = position[i + 2];
            minX = Math.min(minX, px);
            minY = Math.min(minY, py);
            minZ = Math.min(minZ, pz);
            maxX = Math.max(maxX, px);
            maxY = Math.max(maxY, py);
            maxZ = Math.max(maxZ, pz);
        }
        double x = (minX + maxX) * 0.5;
        double y = (minY + maxY) * 0.5;
        double z = (minZ + maxZ) * 0.5;
        double radiusSq = 0;
        for (int i = 0; i < position.Length; i += 3)
        {
            double dx = position[i] - x;
            double dy = position[i + 1] - y;
            double dz = position[i + 2] - z;
            radiusSq = Math.max(radiusSq, dx * dx + dy * dy + dz * dz);
        }
        return new TerrainGeometryBounds { x = x, y = y, z = z, radius = Math.sqrt(radiusSq) };
    }

    /// <summary>Concatenate cached structural geometry with a freshly compiled dressing layer without changing ordering.</summary>
    public static TerrainGeometryPayload mergeTerrainGeometryLayers(
        TerrainGeometryLayers layers,
        TerrainGeometryBufferPool? pool = null)
    {
        TerrainGeometryPayload @base = layers.@base;
        TerrainGeometryPayload dynamic = layers.dynamic;
        TerrainGeometryPayload.SurfaceLane? surface = mergeSurfaceLane(@base.surface, dynamic.surface, pool);
        TerrainGeometryPayload.WaterLane? water = mergeWaterLane(@base.water, dynamic.water, pool);
        TerrainGeometryPayload.MistLane? mist = mergeMistLane(@base.mist, dynamic.mist, pool);
        TerrainGeometryPayload.OverlayLane? overlay = mergeOverlayLane(@base.overlay, dynamic.overlay, pool);
        TerrainGeometryPayload.ActorWallLane? actorWall = mergeActorWallLane(@base.actorWall, dynamic.actorWall, pool);
        return new TerrainGeometryPayload
        {
            surface = surface,
            water = water,
            mist = mist,
            overlay = overlay,
            actorWall = actorWall,
            vegetation = TerrainVegetationLane.Merge(@base.vegetation, dynamic.vegetation),
        };
    }

    internal static float[] concatenateFloat32(
        float[]? left,
        float[]? right,
        TerrainGeometryBufferPool? pool = null)
    {
        int length = (left?.Length ?? 0) + (right?.Length ?? 0);
        float[] @out = pool != null
            ? pool.acquireFloat32(length)
            : new float[length];
        if (left != null) Array.Copy(left, 0, @out, 0, left.Length);
        if (right != null) Array.Copy(right, 0, @out, left?.Length ?? 0, right.Length);
        return @out;
    }

    internal static uint[] concatenateIndices(
        uint[]? left,
        uint[]? right,
        double leftVertexCount,
        TerrainGeometryBufferPool? pool = null)
    {
        int length = (left?.Length ?? 0) + (right?.Length ?? 0);
        uint[] @out = pool != null
            ? pool.acquireUint32(length)
            : new uint[length];
        if (left != null) Array.Copy(left, 0, @out, 0, left.Length);
        if (right != null)
        {
            int offset = left?.Length ?? 0;
            for (int index = 0; index < right.Length; index++)
                @out[offset + index] = Js.ToUint32(right[index] + leftVertexCount);
        }
        return @out;
    }

    internal static TerrainGeometryPayload.SurfaceLane? mergeSurfaceLane(
        TerrainGeometryPayload.SurfaceLane? left,
        TerrainGeometryPayload.SurfaceLane? right,
        TerrainGeometryBufferPool? pool = null)
    {
        if (left == null && right == null) return null;
        float[] position = concatenateFloat32(left?.position, right?.position, pool);
        return new TerrainGeometryPayload.SurfaceLane
        {
            position = position,
            normal = concatenateFloat32(left?.normal, right?.normal, pool),
            color = concatenateFloat32(left?.color, right?.color, pool),
            surface = concatenateFloat32(left?.surface, right?.surface, pool),
            emissive = concatenateFloat32(left?.emissive, right?.emissive, pool),
            ground = concatenateFloat32(left?.ground, right?.ground, pool),
            index = concatenateIndices(left?.index, right?.index, (double)(left?.position.Length ?? 0) / 3, pool),
            bounds = boundsOfPositions(position),
        };
    }

    internal static TerrainGeometryPayload.WaterLane? mergeWaterLane(
        TerrainGeometryPayload.WaterLane? left,
        TerrainGeometryPayload.WaterLane? right,
        TerrainGeometryBufferPool? pool = null)
    {
        if (left == null && right == null) return null;
        float[] position = concatenateFloat32(left?.position, right?.position, pool);
        return new TerrainGeometryPayload.WaterLane
        {
            position = position,
            normal = concatenateFloat32(left?.normal, right?.normal, pool),
            color = concatenateFloat32(left?.color, right?.color, pool),
            water = concatenateFloat32(left?.water, right?.water, pool),
            fold = concatenateFloat32(left?.fold, right?.fold, pool),
            reflection = concatenateFloat32(left?.reflection, right?.reflection, pool),
            index = concatenateIndices(left?.index, right?.index, (double)(left?.position.Length ?? 0) / 3, pool),
            bounds = boundsOfPositions(position),
        };
    }

    internal static TerrainGeometryPayload.MistLane? mergeMistLane(
        TerrainGeometryPayload.MistLane? left,
        TerrainGeometryPayload.MistLane? right,
        TerrainGeometryBufferPool? pool = null)
    {
        if (left == null && right == null) return null;
        float[] position = concatenateFloat32(left?.position, right?.position, pool);
        return new TerrainGeometryPayload.MistLane
        {
            position = position,
            color = concatenateFloat32(left?.color, right?.color, pool),
            mist = concatenateFloat32(left?.mist, right?.mist, pool),
            index = concatenateIndices(left?.index, right?.index, (double)(left?.position.Length ?? 0) / 3, pool),
            bounds = boundsOfPositions(position),
        };
    }

    internal static TerrainGeometryPayload.OverlayLane? mergeOverlayLane(
        TerrainGeometryPayload.OverlayLane? left,
        TerrainGeometryPayload.OverlayLane? right,
        TerrainGeometryBufferPool? pool = null)
    {
        if (left == null && right == null) return null;
        float[] position = concatenateFloat32(left?.position, right?.position, pool);
        return new TerrainGeometryPayload.OverlayLane
        {
            position = position,
            color = concatenateFloat32(left?.color, right?.color, pool),
            index = concatenateIndices(left?.index, right?.index, (double)(left?.position.Length ?? 0) / 3, pool),
            bounds = boundsOfPositions(position),
        };
    }

    internal static TerrainGeometryPayload.ActorWallLane? mergeActorWallLane(
        TerrainGeometryPayload.ActorWallLane? left,
        TerrainGeometryPayload.ActorWallLane? right,
        TerrainGeometryBufferPool? pool = null)
    {
        if (left == null && right == null) return null;
        float[] position = concatenateFloat32(left?.position, right?.position, pool);
        return new TerrainGeometryPayload.ActorWallLane
        {
            position = position,
            index = concatenateIndices(left?.index, right?.index, (double)(left?.position.Length ?? 0) / 3, pool),
            bounds = boundsOfPositions(position),
        };
    }

    /// <summary>Eye-facing basis for mist discs; allocation-free because every wisp rewrites it.</summary>
    [ThreadStatic] private static P3? _MIST_BILLBOARD_RIGHT;
    internal static P3 MIST_BILLBOARD_RIGHT => _MIST_BILLBOARD_RIGHT ??= new P3(1, 0, 0);
    [ThreadStatic] private static P3? _MIST_BILLBOARD_UP;
    internal static P3 MIST_BILLBOARD_UP => _MIST_BILLBOARD_UP ??= new P3(0, 1, 0);

    // PORT NOTE (integration hazard): `setBiome` configures THIS builder's wall pigment/growth, and the
    // buildTransferable* methods bake into it. In TS both run in the same worker; with a per-thread BUILDER the
    // Godot side must call `setBiome` on the thread that later bakes (or re-run it there), otherwise the bake
    // thread's BUILDER still carries the fallback wall pigment.
    [ThreadStatic] private static TileGeometryBuilder? _BUILDER;
    internal static TileGeometryBuilder BUILDER => _BUILDER ??= new TileGeometryBuilder();

    /// <summary>Newell normal dotted with an authored face normal; robust when a polygon begins with duplicate vertices.
    /// Newell normal of a polygon, written into a shared scratch. Length = twice the polygon's world area.</summary>
    [ThreadStatic] private static P3? _POLYGON_NORMAL_SCRATCH;
    internal static P3 POLYGON_NORMAL_SCRATCH => _POLYGON_NORMAL_SCRATCH ??= new P3(0, 0, 0);

    internal static P3 polygonNormalInto(IReadOnlyList<P3> points)
    {
        double px = 0;
        double py = 0;
        double pz = 0;
        for (int index = 0; index < points.Count; index++)
        {
            P3 a = points[index];
            P3 b = points[(index + 1) % points.Count];
            px += (a.y - b.y) * (a.z + b.z);
            py += (a.z - b.z) * (a.x + b.x);
            pz += (a.x - b.x) * (a.y + b.y);
        }
        P3 scratch = POLYGON_NORMAL_SCRATCH;
        scratch.x = px;
        scratch.y = py;
        scratch.z = pz;
        return scratch;
    }

    internal static double polygonNormalDot(IReadOnlyList<P3> points, double nx, double ny, double nz)
    {
        P3 normal = polygonNormalInto(points);
        return normal.x * nx + normal.y * ny + normal.z * nz;
    }

    /// <summary>
    /// A projected area this small relative to the polygon's own world area carries no usable orientation.
    ///
    /// A north-south wall is mathematically edge-on in the yaw-free basis, so its projected area is zero and the
    /// SIGN of that area — which the winding rule below reads — is numerically meaningless. Exactly-zero areas were
    /// already handled; the damage came from faces that are only NEARLY edge-on, such as every east/west wall the
    /// compiler folds inward by a fraction of a pixel to seal its raster joint. Those produced a tiny non-zero
    /// area whose sign came from the fold rather than from the face, so the wall was wound inside-out and the
    /// `FrontSide` surface material culled it — an invisible face at gameplay yaw, and a hole once the presentation
    /// orbit gave it real screen area. Any face the yaw-free camera genuinely sees projects at a large fraction of
    /// its world area (a south wall and a flat cap both land near 0.66, a steep bevel near 0.3), while a folded
    /// east/west wall stays two orders of magnitude below that. The threshold sits in the empty gap between them,
    /// so no well-conditioned polygon changes its winding and every near-edge-on one is resolved in world space.
    /// </summary>
    internal const double POLYGON_EDGE_ON_PROJECTION = 0.1;

    /// <summary>
    /// Front-face winding for every batch (`surface`, `water`, cap lattice).
    ///
    /// Well-conditioned polygons keep the established screen-space contract, under which front-facing means a
    /// NEGATIVE projected area and `orbitBackside` deliberately inverts a shell so it is culled until the camera
    /// orbits onto it. Edge-on and near-edge-on polygons instead resolve against their own Newell normal, which is
    /// exact at every azimuth — that is the only way an east/west wall can be wound correctly at all, since its
    /// projected area holds no orientation to read.
    /// </summary>
    internal static bool polygonWindingFlip(
        IReadOnlyList<P3> points,
        double nx,
        double ny,
        double nz,
        bool orbitBackside,
        double projectedArea)
    {
        P3 normal = polygonNormalInto(points);
        double worldTwiceArea = Math.hypot(normal.x, normal.y, normal.z);
        if (Math.abs(projectedArea) < POLYGON_EDGE_ON_PROJECTION * worldTwiceArea)
        {
            double orientation = normal.x * nx + normal.y * ny + normal.z * nz;
            return orbitBackside ? orientation >= 0 : orientation < 0;
        }
        return orbitBackside ? projectedArea < 0 : projectedArea > 0;
    }

    /// <summary>Signed screen area under the oblique projection in SCREEN coordinates (y down): positive = clockwise on
    /// screen. NDC flips y, which NEGATES the sign — so a screen-CLOCKWISE polygon is NDC-clockwise, i.e. a GL
    /// BACK face. Front-facing (kept by the FrontSide materials) therefore means NEGATIVE screen area here.</summary>
    internal static double screenArea(IReadOnlyList<P3> points)
    {
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            P3 a = points[i];
            P3 b = points[(i + 1) % points.Count];
            double ax = terrainScreenX(a.x, a.y, a.z);
            double ay = terrainScreenY(a.x, a.y, a.z);
            double bx = terrainScreenX(b.x, b.y, b.z);
            double by = terrainScreenY(b.x, b.y, b.z);
            area += ax * by - bx * ay;
        }
        return area;
    }

    // The presented-area path now runs for every animated decoration polygon, so keep the fixed camera basis out
    // of that hot loop. The production yaw is immutable for one compiler module instance.
    internal static readonly double PRESENTED_SCREEN_COS = Math.cos(TERRAIN_CAMERA_WORLD_YAW);
    internal static readonly double PRESENTED_SCREEN_SIN = Math.sin(TERRAIN_CAMERA_WORLD_YAW);

    /// <summary>
    /// Twice the signed area a polygon actually covers on the PRESENTED screen, i.e. after the world yaw.
    ///
    /// <see cref="screenArea"/> answers in the yaw-free basis, where a polygon standing in a constant-x plane collapses
    /// to a line and measures exactly zero. That zero is what the emitters read as "nobody can see this", and it
    /// was true only while the camera looked straight down the x axis. Under TERRAIN_CAMERA_WORLD_YAW every
    /// such wall has real width, so PRESENCE has to be decided here — the shaft seal beside a crossed chasm was
    /// built correctly and then silently dropped by that stale test, leaving a slit through to the backdrop.
    ///
    /// Winding deliberately keeps asking <see cref="screenArea"/>: its contract, and the near-edge-on fallback in
    /// <see cref="polygonWindingFlip"/>, are calibrated against the yaw-free basis, and a yawed east/west wall lands
    /// close enough to that threshold that moving it would make the branch taken depend on rounding.
    /// </summary>
    internal static double presentedScreenArea(IReadOnlyList<P3> points)
    {
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            P3 a = points[i];
            P3 b = points[(i + 1) % points.Count];
            double ax = PRESENTED_SCREEN_COS * a.x + PRESENTED_SCREEN_SIN * a.z;
            double ay = terrainScreenY(a.x, a.y, -PRESENTED_SCREEN_SIN * a.x + PRESENTED_SCREEN_COS * a.z);
            double bx = PRESENTED_SCREEN_COS * b.x + PRESENTED_SCREEN_SIN * b.z;
            double by = terrainScreenY(b.x, b.y, -PRESENTED_SCREEN_SIN * b.x + PRESENTED_SCREEN_COS * b.z);
            area += ax * by - bx * ay;
        }
        return area;
    }

    /// <summary>
    /// Presented area after the vertex coordinates have the exact Float32 precision used by the transfer buffers.
    ///
    /// A terminal waterfall sweep can converge to a triangle whose double-precision projected area is only a few
    /// ten-thousandths of a square world unit. Rounding its vertices for the GPU can change that area's sign. If the
    /// CPU winds the double triangle, FrontSide then culls the transferred triangle even though both calculations
    /// were individually consistent. Eye-facing sheets use this transfer-precision version so authored winding and
    /// raster winding are decided from the same vertices.
    /// </summary>
    internal static double presentedScreenAreaAtTransferPrecision(IReadOnlyList<P3> points)
    {
        double area = 0;
        for (int index = 0; index < points.Count; index++)
        {
            P3 a = points[index];
            P3 b = points[(index + 1) % points.Count];
            double axValue = Math.fround(a.x);
            double ayValue = Math.fround(a.y);
            double azValue = Math.fround(a.z);
            double bxValue = Math.fround(b.x);
            double byValue = Math.fround(b.y);
            double bzValue = Math.fround(b.z);
            double ax = PRESENTED_SCREEN_COS * axValue + PRESENTED_SCREEN_SIN * azValue;
            double ay = terrainScreenY(
                axValue,
                ayValue,
                -PRESENTED_SCREEN_SIN * axValue + PRESENTED_SCREEN_COS * azValue);
            double bx = PRESENTED_SCREEN_COS * bxValue + PRESENTED_SCREEN_SIN * bzValue;
            double by = terrainScreenY(
                bxValue,
                byValue,
                -PRESENTED_SCREEN_SIN * bxValue + PRESENTED_SCREEN_COS * bzValue);
            area += ax * by - bx * ay;
        }
        return area;
    }

    internal sealed class ChasmFloorCompilation
    {
        public MaterializedTerrain terrain = null!;
        public TerrainRenderPlan plan = null!;
    }

    internal static double? shiftOptionalDatum(double? value, double offset)
    {
        return value == null ? null : value.Value + offset;
    }

    // PORT NOTE (allocation): the proxy terrain, its input planes and its material array are per-thread scratch,
    // materialized with `reuse` exactly like the worker's own `materializedScratch` (a fresh materialization
    // allocated every cell, contact, edge and face-segment list again per bake). The reuse path rewrites every
    // field of every cell, and a ChasmFloorCompilation never outlives the build that created it on this thread.
    [ThreadStatic] private static MaterializedTerrain? _CHASM_PROXY_TERRAIN_SCRATCH;
    [ThreadStatic] private static byte[]? _CHASM_PROXY_TILES_SCRATCH;
    [ThreadStatic] private static sbyte[]? _CHASM_PROXY_ELEVATION_SCRATCH;
    [ThreadStatic] private static TerrainMaterial[]? _CHASM_PROXY_MATERIALS_SCRATCH;

    /// <summary>
    /// The deep basin is rendered as a genuine Floor height field. Materialization happens at unsigned proxy
    /// levels so it can reuse the authoritative terrain model; every vertical datum is then translated to the
    /// negative Chasm band without changing a single edge relation, contour corner or bevel measurement.
    /// </summary>
    internal static ChasmFloorCompilation? materializeChasmFloorCompilation(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        bool anyChasmFloor = false;
        foreach (TerrainCell candidate in terrain.cells)
        {
            if (terrainCellCarriesChasmFloor(candidate))
            {
                anyChasmFloor = true;
                break;
            }
        }
        if (!anyChasmFloor) return null;

        int count = terrain.width * terrain.height;
        byte[] proxyTiles = _CHASM_PROXY_TILES_SCRATCH?.Length == count
            ? _CHASM_PROXY_TILES_SCRATCH
            : _CHASM_PROXY_TILES_SCRATCH = new byte[count];
        Array.Fill(proxyTiles, (byte)TileType.Floor);
        // Every entry is written below.
        sbyte[] proxyElevation = _CHASM_PROXY_ELEVATION_SCRATCH?.Length == count
            ? _CHASM_PROXY_ELEVATION_SCRATCH
            : _CHASM_PROXY_ELEVATION_SCRATCH = new sbyte[count];
        for (int index = 0; index < count; index++)
        {
            TerrainCell source = terrain.cells[index];
            double floorZ = terrainCellCarriesChasmFloor(source)
                ? (terrainChasmFloorZAt(terrain, source) ?? CHASM_ABYSS_SURFACE_Z)
                : CHASM_ABYSS_SURFACE_MAX_Z;
            proxyElevation[index] = Js.I8(Math.round(floorZ - CHASM_ABYSS_SURFACE_Z));
        }

        MaterializedTerrain proxyTerrain = materializeTerrainGrid(
            proxyTiles,
            terrain.width,
            terrain.height,
            proxyElevation,
            null,
            _CHASM_PROXY_TERRAIN_SCRATCH);
        _CHASM_PROXY_TERRAIN_SCRATCH = proxyTerrain;
        double offset = CHASM_ABYSS_SURFACE_Z;
        foreach (TerrainCell cell in proxyTerrain.cells)
        {
            cell.elevation += offset;
            cell.surfaceZ += offset;
            cell.baseZ += offset;
            cell.walkZ = shiftOptionalDatum(cell.walkZ, offset);
            cell.height.elevation += offset;
            cell.height.topZ += offset;
            cell.height.baseZ += offset;
            cell.height.walkZ = shiftOptionalDatum(cell.height.walkZ, offset);
            cell.height.waterLevel = shiftOptionalDatum(cell.height.waterLevel, offset);
            if (cell.height.solidVolume != null)
            {
                cell.height.solidVolume.bottom += offset;
                cell.height.solidVolume.top += offset;
            }
            if (cell.height.fluidVolume != null)
            {
                cell.height.fluidVolume.surface += offset;
                cell.height.fluidVolume.bottom += offset;
            }
            if (cell.height.overheadVolume != null)
            {
                cell.height.overheadVolume.bottom += offset;
                cell.height.overheadVolume.top += offset;
                cell.height.overheadVolume.negativeSupportTop += offset;
                cell.height.overheadVolume.positiveSupportTop += offset;
            }
            foreach (TerrainDirection direction in TerrainDirections)
            {
                TerrainContact contact = cell.contacts[direction.key]!;
                contact.elevation = shiftOptionalDatum(contact.elevation, offset);
                contact.surfaceZ = shiftOptionalDatum(contact.surfaceZ, offset);
                contact.waterLevel = shiftOptionalDatum(contact.waterLevel, offset);
                TerrainEdge edge = cell.edges[direction.key]!;
                edge.contactElevation = shiftOptionalDatum(edge.contactElevation, offset);
                edge.contactSurfaceZ = shiftOptionalDatum(edge.contactSurfaceZ, offset);
                edge.fromZ += offset;
                edge.toZ += offset;
                foreach (TerrainFaceSegment segment in edge.faceSegments)
                {
                    segment.fromZ += offset;
                    segment.toZ += offset;
                }
            }
        }
        proxyTerrain.floorUsage = terrain.floorUsage;

        // Every entry is written below.
        TerrainMaterial[] proxyMaterials = _CHASM_PROXY_MATERIALS_SCRATCH?.Length == count
            ? _CHASM_PROXY_MATERIALS_SCRATCH
            : _CHASM_PROXY_MATERIALS_SCRATCH = new TerrainMaterial[count];
        for (int index = 0; index < count; index++)
        {
            TerrainCell source = terrain.cells[index];
            TerrainMaterial? material = terrainCellCarriesChasmFloor(source)
                ? resolveChasmFloorMaterial(terrain, plan, source)
                : null;
            if (material == null)
            {
                for (int dy = -1; dy <= 1 && material == null; dy++)
                {
                    for (int dx = -1; dx <= 1 && material == null; dx++)
                    {
                        TerrainCell? neighbor = terrainCellAt(terrain, source.x + dx, source.y + dy);
                        if (neighbor != null && terrainCellCarriesChasmFloor(neighbor))
                            material = resolveChasmFloorMaterial(terrain, plan, neighbor);
                    }
                }
            }
            proxyMaterials[index] = material ?? STANDARD_TERRAIN_MATERIALS.chasm;
        }

        // `{ ...plan, terrain: proxyTerrain, materials: proxyMaterials }` — a shallow copy of the plan.
        TerrainRenderPlan proxyPlan = plan.Clone();
        proxyPlan.terrain = proxyTerrain;
        proxyPlan.materials = proxyMaterials;
        return new ChasmFloorCompilation
        {
            terrain = proxyTerrain,
            plan = proxyPlan,
        };
    }

    internal static (double kind, double strength) remapFloorSurfaceToChasm(
        double kind,
        double strength,
        TerrainSurfaceRemapContext context)
    {
        if (kind == SURF.floor || kind == SURF.rockCap)
        {
            double normalLength = Math.hypot(context.normalX, context.normalY, context.normalZ);
            if (!Js.Truthy(normalLength)) normalLength = 1;
            double upwardNormal = Math.abs(context.normalY / normalLength);
            double minimumHorizontalSpan = double.PositiveInfinity;
            if (upwardNormal > 0.985)
            {
                double minimumX = double.PositiveInfinity;
                double maximumX = double.NegativeInfinity;
                double minimumZ = double.PositiveInfinity;
                double maximumZ = double.NegativeInfinity;
                IReadOnlyList<P3> points = context.points;
                for (int index = 0; index < points.Count; index++)
                {
                    P3 point = points[index];
                    minimumX = Math.min(minimumX, point.x);
                    maximumX = Math.max(maximumX, point.x);
                    minimumZ = Math.min(minimumZ, point.z);
                    maximumZ = Math.max(maximumZ, point.z);
                }
                minimumHorizontalSpan = Math.min(maximumX - minimumX, maximumZ - minimumZ);
            }
            double narrowRimMaximumSpan = TERRAIN_ELEVATION_STEP_PX * 0.32;
            bool isEdgeFacet = upwardNormal <= 0.985 || minimumHorizontalSpan <= narrowRimMaximumSpan;
            // The ordinary Floor compiler emits the correct physical shoulder plus narrow top/side rim facets. At the
            // bottom of a Chasm those facets are still geometry, but they are the ABSORPTIVE edge of the deep floor,
            // never a second cap allowed to catch key light or atmospheric lift as a pale ruler.
            if (isEdgeFacet) return (SURF.chasmWall, Math.min(strength, 0.1));
            return (SURF.chasmFloor, strength);
        }
        if (kind == SURF.earthFace) return (SURF.chasmWall, Math.min(strength, 0.12));
        return (kind, strength);
    }

    /// <summary>Keep the canonical Floor pen work, but turn its unlit pigment into absorption instead of a bright halo.</summary>
    internal static (int hex, double alpha) remapFloorOverlayToChasm(int hex, double alpha) =>
        (mix(hex, 0x010201, 0.88), Math.min(alpha * 0.55, 0.18));
}

/// <summary>
/// Dave Hoskins-style sin-free hash noise: stable at large world coordinates, safe on mobile precision.
///
/// Stateful only through reusable scratch and the selected biome palette. Calls are serialized by the worker;
/// every returned typed array is an exact snapshot and can be transferred immediately.
/// </summary>
public sealed partial class TerrainGeometryCompiler : GroundDetailSource
{
    // Coherent organic corners by default: the worker and the game both compile with them, and a compiler that
    // silently falls back to the legacy radius policy produces different arcs beside worker tiles and breaks
    // the main-thread/worker payload parity the bake contract depends on.
    public TerrainGeometryCompiler(bool coherentContours = true)
    {
        this.coherentContours = coherentContours;
    }

    // ── Instance fields (TS 2875–2926). Parts C2–C6 use these by their TS names. ──────────────────────────
    internal readonly bool coherentContours;
    internal IReadOnlyList<string> bakeThemePalette = Array.Empty<string>();
    internal readonly Dictionary<string, PaintedThemeVisual> paintedThemeVisualCache = new();
    /// <summary>Per-instance: a worker owns one compiler, but the render layer may hold several at once.</summary>
    internal Dictionary<string, TerrainCompilerThemeVisual> themeVisualCatalog = new();
    internal Func<TerrainCell, double, TerrainMaterial>? runMaterialForCell;
    internal byte[] themeBuf = new byte[0];
    internal Biome? biome;
    public TerrainSurfaceProfile terrainSurfaceProfile = terrainSurfaceProfileForBiome(null);
    public int floorBiomeDryPole = 0xa58b63;
    public int floorBiomeSoilPole = 0x856b49;
    public int floorDryPole = 0xa58b63;
    public int floorSoilPole = 0x856b49;
    /// <summary>Wall-foot debris pigment — the wall's own material, lightly soiled.</summary>
    public int seamScreePole = 0x8d8d88;
    public int floorLushPole = 0x668c52;
    public int floorWetPole = 0x526c62;
    internal readonly GroundDetailContext groundDetail = createGroundDetailContext(
        terrainSurfaceProfileForBiome(null));
    public bool groundWear = true;
    public bool naturalGroundCover = true;
    /// <summary>TS `TerrainTileset` (= TerrainMaterialTileset &amp; { surfaceForTile?: unknown }); setBiome stores a
    /// TerrainMaterialTileset here.</summary>
    internal TerrainMaterialTileset? tileset;
    internal WorldStyle currentStyle = TERRAIN_GEOMETRY_DEFAULT_WORLD_STYLE;
    internal int vegetationBladeCap = 24;
    internal double cliffDressingDensity = 1;
    internal TerrainWallGrowthProfile wallGrowthProfile = TERRAIN_WALL_GROWTH_DISABLED;
    internal TerrainWallGrowthProfile chasmWallGrowthProfile = TERRAIN_WALL_GROWTH_DISABLED;
    internal bool visualGrounding = true;
    internal readonly TerrainVisualContourCorners terrainContourScratch = new TerrainVisualContourCorners
    {
        nw = 0,
        ne = 0,
        se = 0,
        sw = 0,
    };
    internal readonly LiquidChasmContour liquidChasmContourScratch = createLiquidChasmContour();
    internal readonly LiquidChasmEdgePoint[] liquidChasmEdgeScratch = createLiquidChasmEdgeScratch();
    internal bool cityTileset = false;
    internal bool olympianTileset = false;
    internal bool cathedralTileset = false;
    internal bool carnivalTileset = false;
    internal bool clockworkTileset = false;
    internal bool prismglassTileset = false;
    internal double styleStrata = 1;
    internal TerrainBakeFrame? bakeFrame;
    internal double ts = 40;

    private static LiquidChasmEdgePoint[] createLiquidChasmEdgeScratch()
    {
        var points = new LiquidChasmEdgePoint[LIQUID_CHASM_EDGE_SEGMENTS + 1];
        for (int index = 0; index < points.Length; index++) points[index] = new LiquidChasmEdgePoint { x = 0, z = 0 };
        return points;
    }

    // `syncGroundDetailContext(this.groundDetail, frame, terrain, this)` hands the compiler over as its
    // GroundDetailSource; TS satisfies that interface structurally with the public fields above.
    bool GroundDetailSource.groundWear => groundWear;
    bool GroundDetailSource.naturalGroundCover => naturalGroundCover;
    int GroundDetailSource.floorSoilPole => floorSoilPole;
    int GroundDetailSource.floorDryPole => floorDryPole;
    int GroundDetailSource.floorWetPole => floorWetPole;
    int GroundDetailSource.floorLushPole => floorLushPole;
    int GroundDetailSource.floorBiomeDryPole => floorBiomeDryPole;
    int GroundDetailSource.floorBiomeSoilPole => floorBiomeSoilPole;
    int GroundDetailSource.seamScreePole => seamScreePole;
    TerrainSurfaceProfile GroundDetailSource.terrainSurfaceProfile => terrainSurfaceProfile;

    /// <summary>Olympian normally uses gold as its architectural punctuation. Aegis reuses the same low-draw-call
    /// colonnade geometry but replaces every metal pick with a neutral limestone/iron value from its tileset.</summary>
    internal int olympianMetal(double seed)
    {
        if (biome?.materialDialect != "aegis-citadel") return olympianGold(seed);
        if (tileset == null) return 0xa9adb1;
        int[] neutral =
        {
            tileset.terrain.wallLit,
            tileset.decal.accent,
            tileset.terrain.wallDeep,
            tileset.decal.ink,
        };
        return neutral[(int)(Math.floor(hash(seed) * neutral.Length) % neutral.Length)];
    }

    public void setBiome(
        Biome biome,
        TerrainMaterialTileset tileset,
        WorldStyle style,
        IReadOnlyList<string>? terrainThemePalette = null,
        IReadOnlyList<TerrainCompilerThemeVisual>? themeVisuals = null,
        int vegetationBladeCap = 24,
        double cliffDressingDensity = 1,
        bool visualGrounding = true)
    {
        terrainThemePalette ??= Array.Empty<string>();
        themeVisuals ??= Array.Empty<TerrainCompilerThemeVisual>();
        this.biome = biome;
        this.tileset = tileset;
        currentStyle = style;
        string construction = tileset.construction;
        this.vegetationBladeCap = vegetationBladeCap;
        this.cliffDressingDensity = Math.max(0, Math.min(1, cliffDressingDensity));
        this.visualGrounding = visualGrounding;
        terrainSurfaceProfile = terrainSurfaceProfileForBiome(biome.key);
        int derivedDryPole = mix(
            tileset.terrain.floorTone,
            mix(biome.groundAccentA, tileset.elevation.cliffFace, 0.45),
            0.52);
        floorBiomeDryPole = derivedDryPole;
        // Natural paths need a stable earth/value separation from the biome floor. Keeping roughly half of the
        // authored palette preserves each world's identity; the warm ochre/umber poles stop green worlds from
        // tinting their navigation lanes back into near-invisible green-on-green noise.
        floorDryPole = terrainSurfaceProfile.organicGround
            ? mix(derivedDryPole, 0xb28d5d, 0.54)
            : derivedDryPole;
        int derivedSoilPole = mix(
            floorDryPole,
            mix(tileset.terrain.floorSeam, tileset.terrain.wallFace, 0.42),
            0.38);
        floorBiomeSoilPole = mix(
            derivedDryPole,
            mix(tileset.terrain.floorSeam, tileset.terrain.wallFace, 0.42),
            0.38);
        floorSoilPole = terrainSurfaceProfile.organicGround
            ? mix(derivedSoilPole, 0x684831, 0.58)
            : derivedSoilPole;
        seamScreePole = mix(tileset.terrain.wallFace, floorSoilPole, 0.28);
        // One definition, shared with the shader's `uMmoratFloorTurf`: the mat and the blades standing in it must
        // grow toward the same pole or they drift apart the moment either side is retuned.
        floorLushPole = terrainFloorTurfPole(tileset.decal.mid, biome.groundAccentB);
        wallGrowthProfile = createTerrainWallGrowthProfile(
            terrainWallGrowthAbundance(style.groundAccent, construction, biome.pattern),
            floorLushPole,
            tileset.terrain.wallLit,
            tileset.terrain.wallDeep,
            floorSoilPole,
            tileset.decal.accent);
        chasmWallGrowthProfile = createTerrainWallGrowthProfile(
            Math.max(0.54, terrainWallGrowthAbundance(style.groundAccent, "natural", biome.pattern)),
            floorLushPole,
            tileset.terrain.wallLit,
            tileset.terrain.wallDeep,
            floorSoilPole,
            tileset.decal.accent);
        // A bank borrows a restrained share of the water body but remains recognisably soil. This one palette pole
        // is sampled through the existing continuous floor field and vertex colour channel: no wet material,
        // texture lookup or draw call is introduced.
        floorWetPole = mix(
            floorSoilPole,
            mix(tileset.flood.body, biome.groundAccentB, 0.28),
            0.34);
        BUILDER.configureWallPigment(
            createTerrainWallPigmentProfile(
                terrainSurfaceProfile,
                tileset.terrain.wallLit,
                tileset.terrain.wallDeep,
                tileset.decal.mid));
        BUILDER.configureWallGrowth(wallGrowthProfile, chasmWallGrowthProfile);
        bakeThemePalette = terrainThemePalette;
        // Every world resolves its cells through one material family now — the sanctuary included. That is what
        // makes the worker and the live layer able to compile the SAME terrain: there is no main-thread-only
        // pigment field left for one space to need and another to lack.
        runMaterialForCell = createRunTerrainMaterialResolver(
            this.tileset,
            style,
            biome.key == "hub");
        cityTileset = construction == "city";
        olympianTileset = construction == "olympian";
        cathedralTileset = construction == "cathedral";
        carnivalTileset = construction == "carnival";
        clockworkTileset = construction == "clockwork";
        prismglassTileset = construction == "prismglass";
        // `hub` is a PRESENTATION palette, not a statement that the ground is an authored plaza. Fluitown's
        // permanent streamed world deliberately wears that calm palette while its descriptor publishes Highland
        // Endless terrain. Keying the material stack off `biome.key === 'hub'` therefore erased both halves of the
        // real ground system in production: `groundWear` removed paths/dirt and `naturalGroundCover` removed the
        // turf mat, blades and flowers, even though the chunk carried hundreds of explicit Grass cells. The sealed
        // Aegis court remains the one material dialect that opts out; ordinary natural construction always keeps
        // the MMORAT living-ground stack, regardless of the atmosphere/presentation palette wrapped around it.
        groundWear = biome.materialDialect != "aegis-citadel";
        naturalGroundCover =
            groundWear &&
            construction != "city" &&
            construction != "olympian" &&
            construction != "carnival" &&
            construction != "clockwork" &&
            construction != "prismglass" &&
            construction != "cathedral" &&
            tileset.decal.kind != "rainbow" &&
            tileset.decal.kind != "reef";
        styleStrata = style.strata;
        // `new Map(themeVisuals.map((visual) => [visual.key, visual]))` — a later duplicate key wins.
        var catalog = new Dictionary<string, TerrainCompilerThemeVisual>();
        foreach (TerrainCompilerThemeVisual visual in themeVisuals) catalog[visual.key] = visual;
        themeVisualCatalog = catalog;
        paintedThemeVisualCache.Clear();
    }

    /// <summary>Compile immutable carriers separately from mutable authored/ecology dressing.</summary>
    /// <remarks>PORT NOTE: synchronous; the checkpoint returns TRUE to continue and FALSE to cancel.</remarks>
    public TerrainGeometryLayerCompileResult buildTransferableTerrainTileLayersCooperatively(
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        Func<bool> checkpoint,
        int cellsPerCheckpoint = 16,
        byte[]? themeIndices = null)
    {
        themeIndices ??= themeBuf;
        bakeFrame = frame;
        ts = frame.tileSize;
        themeBuf = themeIndices;
        syncGroundDetail(terrain, frame);
        TileGeometryBuilder builder = BUILDER;
        builder.configureWallGrowth(wallGrowthProfile, chasmWallGrowthProfile);
        builder.reset();
        buildTerrainFoundation(builder, frame, terrain);
        int emittedCells = 0;
        ChasmFloorCompilation? chasmFloorCompilation = materializeChasmFloorCompilation(terrain, plan);
        foreach (TerrainCell cell in terrain.cells)
        {
            if (!buildTileCell(builder, frame, terrain, plan, cell, chasmFloorCompilation)) continue;
            emittedCells++;
            // JS `x % 0` is NaN (never === 0); C# would throw.
            if (cellsPerCheckpoint != 0 && emittedCells % cellsPerCheckpoint == 0 && !checkpoint())
            {
                builder.reset();
                return new TerrainGeometryLayerCompileResult { cancelled = true };
            }
        }
        buildTileStructuralEffects(builder, frame, terrain, plan);
        if (!checkpoint())
        {
            builder.reset();
            return new TerrainGeometryLayerCompileResult { cancelled = true };
        }
        TerrainGeometryCursor structuralEnd = builder.geometryCursor();
        buildTileDressing(builder, frame, terrain, plan);
        if (!checkpoint())
        {
            builder.reset();
            return new TerrainGeometryLayerCompileResult { cancelled = true };
        }
        TerrainGeometryCursor complete = builder.geometryCursor();
        var layers = new TerrainGeometryLayers
        {
            @base = builder.toTransferPayloadRange(EMPTY_GEOMETRY_CURSOR, structuralEnd),
            dynamic = builder.toTransferPayloadRange(structuralEnd, complete),
        };
        return new TerrainGeometryLayerCompileResult
        {
            cancelled = false,
            layers = layers,
            audit = createBakeAudit(terrain, plan, builder),
        };
    }

    /// <summary>Rebuild only placements/ecology whose dirty signature changed; cached structural carriers stay exact.</summary>
    /// <remarks>PORT NOTE: synchronous; the checkpoint returns TRUE to continue and FALSE to cancel.</remarks>
    public TerrainGeometryCompileResult buildTransferableTerrainDressingTileCooperatively(
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        Func<bool> checkpoint,
        TerrainGeometryBufferPool? pool = null,
        byte[]? themeIndices = null)
    {
        themeIndices ??= themeBuf;
        bakeFrame = frame;
        ts = frame.tileSize;
        themeBuf = themeIndices;
        syncGroundDetail(terrain, frame);
        TileGeometryBuilder builder = BUILDER;
        builder.configureWallGrowth(wallGrowthProfile, chasmWallGrowthProfile);
        builder.reset();
        if (!checkpoint()) return new TerrainGeometryCompileResult { cancelled = true };
        buildTileDressing(builder, frame, terrain, plan);
        if (!checkpoint())
        {
            builder.reset();
            return new TerrainGeometryCompileResult { cancelled = true };
        }
        TerrainGeometryPayload geometry = builder.toTransferPayload(pool);
        return new TerrainGeometryCompileResult
        {
            cancelled = false,
            geometry = geometry,
            audit = createBakeAudit(terrain, plan, builder),
        };
    }

    /// <summary>`plan.materials[id]` with the JS out-of-range read (`undefined` → null).</summary>
    internal static TerrainMaterial? planMaterialAt(TerrainRenderPlan plan, int id)
    {
        IReadOnlyList<TerrainMaterial> materials = plan.materials;
        return (uint)id < (uint)materials.Count ? materials[id] : null;
    }

    /// <summary>`terrain.cells[id]` with the JS out-of-range read (`undefined` → null).</summary>
    internal static TerrainCell? terrainCellById(MaterializedTerrain terrain, int id)
    {
        return (uint)id < (uint)terrain.cells.Length ? terrain.cells[id] : null;
    }

    private static readonly string[] AUDIT_CHASM_DIRECTIONS = { "s", "e", "w" };

    internal TerrainBakeAudit createBakeAudit(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TileGeometryBuilder builder)
    {
        int bridgeCells = 0;
        int bridgeOverlapCells = 0;
        double bridgeMinimumClearance = double.PositiveInfinity;
        int chasmCells = 0;
        int chasmWallContinuations = 0;
        int chasmWallColourMismatches = 0;
        int mistBanks = 0;
        double maximumMistAlpha = 0;
        double minimumMistHeight = double.PositiveInfinity;
        double maximumMistHeight = 0;
        int mistNonCoreAnchors = 0;
        double minimumVoidDrop = double.PositiveInfinity;
        double abyssSurfaceZMin = double.PositiveInfinity;
        double abyssSurfaceZMax = double.NegativeInfinity;
        int cleftCells = 0;
        int underpassCells = 0;
        foreach (TerrainCell cell in terrain.cells)
        {
            if (!ownCell(cell)) continue;
            if (cell.type == TileType.Cleft) cleftCells++;
            if (cell.type == TileType.Underpass) underpassCells++;
            if (cell.type == TileType.Bridge)
            {
                bridgeCells++;
                double? clearance = bridgeWaterClearanceForTerrainCell(cell);
                if (clearance != null)
                    bridgeMinimumClearance = Math.min(bridgeMinimumClearance, clearance.Value);
                if (cell.span != TileType.Chasm && !bridgeWaterIsSafelyBelowDeck(cell))
                    bridgeOverlapCells++;
            }
            if (terrainCellCarriesChasmFloor(cell))
            {
                double floorZ = terrainChasmFloorZAt(terrain, cell) ?? CHASM_ABYSS_SURFACE_Z;
                chasmCells++;
                minimumVoidDrop = Math.min(minimumVoidDrop, cell.surfaceZ - floorZ);
                abyssSurfaceZMin = Math.min(abyssSurfaceZMin, floorZ);
                abyssSurfaceZMax = Math.max(abyssSurfaceZMax, floorZ);
            }
            if (cell.type != TileType.Solid && cell.type != TileType.Floor) continue;
            TerrainMaterial? sourceMaterial = planMaterialAt(plan, cell.id);
            if (sourceMaterial == null) continue;
            foreach (string dir in AUDIT_CHASM_DIRECTIONS)
            {
                TerrainEdge edge = cell.edges[dir]!;
                bool hasChasmSegment = false;
                foreach (TerrainFaceSegment s in edge.faceSegments)
                {
                    if (s.role == "chasm")
                    {
                        hasChasmSegment = true;
                        break;
                    }
                }
                if (
                    edge.contactType != TileType.Chasm ||
                    !hasChasmSegment
                )
                    continue;
                chasmWallContinuations++;
                string edgeMaterial = chasmContinuationMaterial(cell, edge);
                TerrainMaterial continuedMaterial = resolveChasmFaceMaterial(terrain, plan, cell, dir);
                if (
                    continuedMaterial.id != sourceMaterial.id ||
                    terrainFaceBaseColor(continuedMaterial, edgeMaterial) !=
                    terrainFaceBaseColor(sourceMaterial, edgeMaterial)
                )
                    chasmWallColourMismatches++;
            }
        }
        foreach (TerrainChasmMistEffect mist in plan.effects.chasmMist)
        {
            TerrainCell? cell = terrainCellById(terrain, mist.id);
            if (cell == null || !ownCell(cell)) continue;
            mistBanks++;
            maximumMistAlpha = Math.max(maximumMistAlpha, mist.alpha);
            minimumMistHeight = Math.min(minimumMistHeight, mist.height);
            maximumMistHeight = Math.max(maximumMistHeight, mist.height);
            bool allChasm = true;
            foreach (TerrainEdge edge in cell.edges.values())
            {
                if (edge.contactType != TileType.Chasm)
                {
                    allChasm = false;
                    break;
                }
            }
            if (!allChasm)
                mistNonCoreAnchors++;
        }
        return new TerrainBakeAudit
        {
            cleftCells = cleftCells,
            underpassCells = underpassCells,
            underpassPlanks = builder.underpassPlanks,
            underpassVisibleGaps = builder.underpassVisibleGaps,
            underpassCableSegments = builder.underpassCableSegments,
            underpassHangers = builder.underpassHangers,
            underpassAnchorPosts = builder.underpassAnchorPosts,
            bridgeCells = bridgeCells,
            bridgeOverlapCells = bridgeOverlapCells,
            bridgeMinimumClearance = bridgeMinimumClearance,
            bridgeStructuralCells = builder.bridgeStructuralCells,
            bridgeAbutments = builder.bridgeAbutments,
            bridgePiers = builder.bridgePiers,
            bridgeRailSegments = builder.bridgeRailSegments,
            bridgeJoineryMarks = builder.bridgeJoineryMarks,
            chasmCells = chasmCells,
            abyssSurfaceCells = builder.chasmFloorCells,
            chasmWallContinuations = chasmWallContinuations,
            chasmWallColourMismatches = chasmWallColourMismatches,
            chasmDepthLedges = builder.chasmDepthLedges,
            chasmTalusClusters = builder.chasmTalusClusters,
            nearAtmosphereCells = 0,
            farAtmosphereCells = 0,
            mistBanks = mistBanks,
            maximumMistAlpha = maximumMistAlpha,
            minimumMistHeight = minimumMistHeight,
            maximumMistHeight = maximumMistHeight,
            mistNonCoreAnchors = mistNonCoreAnchors,
            minimumVoidDrop = minimumVoidDrop,
            abyssSurfaceZMin = abyssSurfaceZMin,
            abyssSurfaceZMax = abyssSurfaceZMax,
        };
    }

    /// <summary>Is this cell inside the tile's OWN cell range (not the sampled border context)? Only own cells emit
    /// geometry — adjacent tiles produce bit-identical vertices at the seam, so the tiling is watertight with
    /// zero double-drawn overlap. That guarantee is only as good as this predicate agreeing with the frame
    /// `threeTerrain.ts` cut, which is why both now derive from `terrainTileLattice.ts` rather than from two
    /// copies of the same two literals.</summary>
    internal bool ownCell(TerrainCell cell)
    {
        return isOwnLatticeCell(cell.x, cell.y);
    }

    /// <summary>
    /// The final terrain-owned layer beneath one bake tile.
    ///
    /// A Chasm floor cannot be a per-Chasm-cell safety card. Under the oblique production camera a ray through a
    /// high mouth reaches the canonical abyss datum many grid cells farther camera-away; that destination may be
    /// authored as Floor, Water or Solid and therefore used to have no deep carrier at all. The remote backdrop
    /// then became visible through otherwise valid waterfall and wall silhouettes.
    ///
    /// Every loaded bake owns exactly one non-overlapping rectangle at the shared abyss datum. Adjacent bakes
    /// publish byte-identical seam vertices, regular terrain occludes it, and detailed Chasm floors sit 0.045 px
    /// above it. Thus any residual view ray inside loaded terrain terminates on actual terrain, for every tile
    /// combination, at the cost of two triangles per bake rather than per-cell skirts or overlapping extensions.
    /// </summary>
    internal void buildTerrainFoundation(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain)
    {
        int localX0 = Math.min(TILE_BORDER, terrain.width);
        int localZ0 = Math.min(TILE_BORDER, terrain.height);
        int localX1 = Math.min(TILE_BORDER + TILE_CELLS, terrain.width);
        int localZ1 = Math.min(TILE_BORDER + TILE_CELLS, terrain.height);
        if (localX1 <= localX0 || localZ1 <= localZ0) return;
        double ts = frame.tileSize;
        double ownX0 = frame.originX + (frame.i0 + localX0) * ts;
        double ownX1 = frame.originX + (frame.i0 + localX1) * ts;
        double ownZ0 = frame.originY + (frame.j0 + localZ0) * ts;
        double ownZ1 = frame.originY + (frame.j0 + localZ1) * ts;
        double y = CHASM_ABYSS_SURFACE_Z * ELEV - 0.08;
        double highestY = 0;
        foreach (TerrainCell cell in terrain.cells) highestY = Math.max(highestY, cell.surfaceZ * ELEV);
        // At equal screen Y, lowering a point by D moves its ground intersection camera-away by D * H/G. Add two
        // cells for contour/bevel overhangs and conservative raster ownership. Express the foundation in the
        // camera's right/forward basis so this reserve grows only where a missing ray can travel, not on all sides.
        double projectionReach =
            ((highestY - y) * TERRAIN_VIEW_HEIGHT_SCALE) / TERRAIN_VIEW_GROUND_SCALE + ts * 2;
        double yawCos = Math.cos(TERRAIN_CAMERA_WORLD_YAW);
        double yawSin = Math.sin(TERRAIN_CAMERA_WORLD_YAW);
        double rightMin = double.PositiveInfinity;
        double rightMax = double.NegativeInfinity;
        double forwardMin = double.PositiveInfinity;
        double forwardMax = double.NegativeInfinity;
        double[] xs = { ownX0, ownX1 };
        double[] zs = { ownZ0, ownZ1 };
        foreach (double x in xs)
        {
            foreach (double z in zs)
            {
                double right = yawCos * x + yawSin * z;
                double forward = -yawSin * x + yawCos * z;
                rightMin = Math.min(rightMin, right);
                rightMax = Math.max(rightMax, right);
                forwardMin = Math.min(forwardMin, forward);
                forwardMax = Math.max(forwardMax, forward);
            }
        }
        forwardMin -= projectionReach;
        P3 pointAt(double right, double forward) => new P3(
            yawCos * right - yawSin * forward,
            y,
            yawSin * right + yawCos * forward);
        int color = tileset != null ? tileset.chasm.floor : STANDARD_TERRAIN_MATERIALS.chasm.topDark;
        builder.addSurface(
            new[]
            {
                pointAt(rightMin, forwardMin),
                pointAt(rightMax, forwardMin),
                pointAt(rightMax, forwardMax),
                pointAt(rightMin, forwardMax),
            },
            0,
            1,
            0,
            color,
            SURF.chasmFloor,
            0.06,
            UNIT_SHADE);
    }

    /// <summary>Returns true only for an own-cell that emitted geometry, which bounds cooperative checkpoints.</summary>
    internal bool buildTileCell(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell,
        ChasmFloorCompilation? chasmFloorCompilation)
    {
        if (!ownCell(cell)) return false;
        TerrainMaterial? sourceMaterial = planMaterialAt(plan, cell.id);
        if (sourceMaterial == null) return false;
        TerrainMaterial material =
            biome?.materialDialect == "aegis-citadel"
                ? materialForCell(cell, terrain)
                : sourceMaterial;
        // There is intentionally no Hub/Endless switch here. Both sources have already become the same shared
        // TileType.Chasm + unsigned-depth contract in materializeTerrainGrid, and every game mode must enter this
        // one mesh/shader path. A biome may supply palette colours; it may not supply alternative chasm geometry.
        if (cell.type == TileType.Chasm)
        {
            // A Chasm is a complete blocked terrain volume: a real cap lives at `baseZ`, surrounding dry faces reach
            // that exact datum, and sparse boundary ledges add scale above it. The global backdrop is only a fallback
            // beneath streamed geometry and is not part of the Chasm representation.
            buildChasmCell(builder, frame, terrain, plan, cell, chasmFloorCompilation);
            return true;
        }
        if (cell.type == TileType.Water)
        {
            buildWaterCell(builder, frame, terrain, plan, cell);
            buildWaterDropWalls(builder, frame, terrain, plan, cell);
            return true;
        }
        if (cell.type == TileType.Cleft)
        {
            buildCleftCell(builder, frame, terrain, plan, cell);
            return true;
        }
        if (cell.type == TileType.Underpass)
        {
            // The passage floor remains ordinary terrain. The +4-supported bridge is a separate wall-mask volume.
            buildSolidCell(builder, frame, terrain, plan, cell, material);
            buildUnderpassBridge(builder, frame, terrain, cell);
            return true;
        }
        // The shared model already caps every hidden span below the deck. Keep the same predicate at the final
        // geometry boundary as a fail-closed guard: malformed/custom terrain may lose its under-deck shimmer,
        // but can never paint opaque Water over a walkable Bridge again.
        if (
            cell.type == TileType.Bridge &&
            cell.span == TileType.Water &&
            bridgeWaterIsSafelyBelowDeck(cell)
        )
        {
            buildWaterCell(builder, frame, terrain, plan, cell);
            buildWaterDropWalls(builder, frame, terrain, plan, cell);
        }
        if (cell.type == TileType.Bridge && terrainCellCarriesChasmFloor(cell))
        {
            buildChasmFloor(builder, frame, terrain, plan, cell, chasmFloorCompilation);
            // A crossing suspends its deck over a real shaft, so it owes the same fluid seal an exposed Chasm cell
            // owes. Without it a pond meeting a Bridge-crossed shaft had no wall at all over the whole drop.
            buildChasmFluidBoundaryClosures(builder, frame, terrain, plan, cell);
        }
        buildSolidCell(builder, frame, terrain, plan, cell, material);
        return true;
    }

    internal void buildTileStructuralEffects(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        var waterfallBoundaries = new List<WaterfallCurtainBoundary>();
        foreach (TerrainWaterfallEffect waterfall in plan.waterfalls)
        {
            TerrainCell? source = terrainCellById(terrain, waterfall.sourceCellId);
            if (source != null && ownCell(source))
                buildWaterfall(builder, frame, terrain, plan, source, waterfall, waterfallBoundaries);
        }
        addRoundedWaterfallCorners(builder, terrain, waterfallBoundaries);
    }

    internal void buildTileDressing(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        // Structural crest ink follows real >=0.9-level north-facing rises only. The visible front face keeps its
        // physical bevel; excluding east/west overlays prevents long vertical marks across otherwise clean caps.
        if (tileset != null) buildRunDressing(builder, frame, terrain, plan);
    }
}
