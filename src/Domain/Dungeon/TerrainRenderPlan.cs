// Port of packages/shared/src/domain/dungeon/terrainRenderPlan.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.Grid;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Domain.TerrainCompositionField;
using static Fluitown.Domain.WorldDecoration;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// PORT NOTES
// * `export type { TerrainFloorCompositionPlan }` and `export { hashTerrainCell, terrainCompositionAt,
//   terrainEcologyPatchAt, terrainFocusPathWearAt, type TerrainCompositionSample }` re-export symbols owned by
//   terrainFloorComposition.ts / terrainCompositionField.ts. C# has no re-exports, and forwarding them here would make
//   every file that `using static`s both module classes ambiguous, so callers reference them on
//   TerrainFloorComposition / TerrainCompositionField directly.
// * Colours are 24-bit `int`s (every producer builds them with bitwise operators). `TerrainEdgeDirection`,
//   `TerrainEdgeMaterial` and the other string-literal unions are `string` (constants in TerrainModel.cs).
// * Calls into the concurrently ported modules (terrainAmbient, treeVisual, terrainFloorComposition, treeLife) are
//   qualified with their module class instead of `using static`, so unknown helper names cannot collide.
// * `interface TerrainTreeDressingEffect extends TreeVisual` (and the felled stump's `TreeVisual & { id }`) derive from
//   the unsealed TreeVisual and copy the spread visual through its copy constructor. `TerrainWorldDecorationEffect
//   extends WorldDecorationVisual` cannot derive (WorldDecorationVisual is sealed), so it keeps the exact visual it was
//   spread from, exposes its fields under their TS names (read-only) and converts implicitly to it.

/// <summary>`TerrainMaterial`. Colour channels are 24-bit hex ints; the optional water-only tones are nullable.</summary>
public sealed class TerrainMaterial
{
    public string id = "";
    public int top;
    public int topLight;
    public int topDark;
    public int side;
    public int edgeLight;
    public int edgeDark;
    public int detail;
    public double roughness;
    public int? shallow;
    public int? mid;
    public int? deep;
    public int? shadow;
    public int? highlight;

    /// <summary>Shallow copy, the equivalent of `{ ...material }`.</summary>
    public TerrainMaterial Clone() => (TerrainMaterial)MemberwiseClone();
}

/// <summary>
/// A theme's complete statement about its water, in the structural form the material rule needs. The client's
/// `FloodPalette` satisfies it structurally in TS; the class is left unsealed so the client type can extend it.
/// </summary>
public class TerrainWaterPigment
{
    public int body;
    public int deep;
    public int surface;
    public int foam;
    public int glint;
    /// <summary>
    /// `true` only when the palette was DERIVED from the biome's pigment family rather than authored by the
    /// theme (client `floodColors`' fallback). Absent ⇒ someone stated this water on purpose.
    /// </summary>
    public bool? derived;
}

/// <summary>`TerrainWaterInfo['flow']` shape: `{ x; y }`.</summary>
public sealed class TerrainWaterFlow
{
    public double x;
    public double y;
}

public sealed class TerrainWaterInfo
{
    public double depth;
    /// <summary>Non-fluid cardinal neighbours (0..4).</summary>
    public int shoreline;
    public bool calm;
    public TerrainWaterFlow flow = null!;
    public double flowSpeed;
}

public sealed class TerrainEdgeReliefProfile
{
    public double crest;
    public double undercut;
    public double strata;
    public double fracture;
    public double chip;
}

/// <summary>Not sealed: <see cref="TerrainRimRun"/> extends it, as in TS.</summary>
public class TerrainEdgeRun
{
    /// <summary><see cref="TerrainEdgeDirection"/>.</summary>
    public string direction = "";
    public List<TerrainCell> cells = null!;
    public TerrainMaterial material = null!;
    /// <summary><see cref="TerrainEdgeMaterial"/>.</summary>
    public string edgeMaterial = "";
    public double fromZ;
    public double toZ;
    public double drop;
    /// <summary>'surface' | 'chasm' (<see cref="TerrainFaceSegmentRole"/>).</summary>
    public string faceRole = "";
    public double visibility;
    public double seed;
    public TerrainEdgeReliefProfile relief = null!;
    public int sortX;
    public int sortY;
}

public sealed class TerrainRimRun : TerrainEdgeRun
{
}

public sealed class TerrainContactShadow
{
    public TerrainEdgeRun run = null!;
    /// <summary><see cref="TerrainEdgeDirection"/>.</summary>
    public string direction = "";
    public double drop;
    public double depth;
    public double alpha;
}

public sealed class TerrainAmbientOcclusionPatch
{
}

public sealed class TerrainMicroDetail
{
}

public sealed class TerrainLodSettings
{
    /// <summary>0 | 1 | 2.</summary>
    public int level;
    public int microLimit;
    public int waterWaveLimit;
    public int bubbleLimit;
    public int rockLimit;
    public int grassLimit;
    public int birdLimit;
    public int lilyLimit;
    public int fishLimit;
    public int reedLimit;
    public int butterflyLimit;
    public int rockGrassLimit;
    public int fireflyLimit;
    public int grassPatchLimit;
    public int treeLimit;
    public int wallStrandLimit;
}

public sealed class TerrainWaterWaveEffect
{
    public int id;
    public double ox;
    public double oy;
    public double phase;
    public double speed;
    public double radius;
    public double angle;
    public double alpha;
}

/// <summary>
/// A physical water sheet emitted where visible or under-Bridge Water opens into lower Water or a deep Chasm.
/// Chasm sheets continue past the per-cell cloud-entry datum to the materialised receiving floor. A joined
/// curtain may follow several terraces, while every shared corner resolves one common lowest bottom datum.
/// </summary>
public sealed class TerrainWaterfallEffect
{
    public int id;
    public int sourceCellId;
    public int targetCellId;
    /// <summary><see cref="TerrainEdgeDirection"/>.</summary>
    public string direction = "";
    /// <summary>'water' | 'chasm'.</summary>
    public string landing = "";
    public double topZ;
    public double bottomZ;
    public double drop;
    public double width;
    public double phase;
    public double intensity;
}

public sealed class TerrainBubbleEffect
{
    public int id;
    public double ox;
    public double oy;
    public double phase;
    public double speed;
    public double radius;
    public double drift;
    public double rise;
    public double wobble;
}

public sealed class TerrainRockFallEffect
{
    public int id;
    /// <summary><see cref="TerrainEdgeDirection"/>.</summary>
    public string direction = "";
    public double t;
    public double phase;
    public double speed;
    public double jitter;
    public double size;
    public int color;
    public double alpha;
}

public sealed class TerrainRollingGrassEffect
{
    public int id;
    public double startX;
    public double startY;
    public double endX;
    public double endY;
    public double startZ;
    public double endZ;
    public double phase;
    public double speed;
    public double spin;
    public double radius;
    public double sway;
    public double alpha;
}

/// <summary>Not sealed: the module's private perch candidate extends it, as in TS.</summary>
public class TerrainBirdPerch
{
    public int id;
    public double ox;
    public double oy;
    public double z;
}

public sealed class TerrainBirdEffect
{
    public List<TerrainBirdPerch> perches = null!;
    public double speed;
    public double phase;
    public double perchSec;
    public double arc;
    public double flap;
    public int count;
    public double wingSpan;
    public double alpha;
    public double scale;
}

/// <summary>A lily pad resting on calm deep water: a bobbing leaf, some with a blossom.</summary>
public sealed class TerrainLilyPadEffect
{
    public int id;
    public double ox;
    public double oy;
    public double radius;
    public double phase;
    public double bob;
    public double notchAngle;
    public bool blossom;
    /// <summary>Number of overlapping leaves in the same calm-water colony.</summary>
    public int cluster;
    /// <summary>0..1 deterministic blossom/leaf tint variation.</summary>
    public double bloomHue;
}

/// <summary>A small fish group moving below the animated water skin on a deterministic loop.</summary>
public sealed class TerrainFishShoalEffect
{
    public int id;
    public double ox;
    public double oy;
    public int count;
    public double length;
    public double radius;
    public double angle;
    public double phase;
    public double speed;
    public double depth;
    public double alpha;
}

/// <summary>Reeds rooted in shallow shoreline water, swaying with the same wind as grass.</summary>
public sealed class TerrainReedBedEffect
{
    public int id;
    public double ox;
    public double oy;
    public int blades;
    public double height;
    public double phase;
    public double sway;
    /// <summary>One bank tuft or several close root crowns.</summary>
    public int clumps;
    /// <summary>0..1 share of tall stems that carry a seed head.</summary>
    public double seedHeads;
    /// <summary>0..1 resistance to the shared wind field.</summary>
    public double stiffness;
    public double alpha;
}

/// <summary>A butterfly fluttering low over walkable ground on a wavering figure path.</summary>
public sealed class TerrainButterflyEffect
{
    public int id;
    public double ox;
    public double oy;
    public double range;
    public double phase;
    public double speed;
    public double flap;
    public double size;
    public double alpha;
}

/// <summary>One world-anchored animated signature of the active terrain theme.</summary>
public sealed class TerrainAmbientMotifEffect
{
    public int id;
    /// <summary>A TerrainAmbientMotifKind value.</summary>
    public string kind = "";
    /// <summary>A TerrainAmbientMotion value.</summary>
    public string motion = "";
    /// <summary>Local position inside the anchor cell, in tile fractions.</summary>
    public double ox;
    public double oy;
    /// <summary>Elevation-level offset above the materialized surface.</summary>
    public double height;
    /// <summary>Horizontal motion envelope in tile units.</summary>
    public double travel;
    public double phase;
    public double speed;
    public double spin;
    public double scale;
    public double alpha;
    /// <summary>Stable 0..1 colour variation consumed by the procedural client geometry.</summary>
    public double tint;
}

/// <summary>A tuft of long grass at the foot of a wall face, swaying in the wind.</summary>
public sealed class TerrainRockGrassEffect
{
    public int id;
    /// <summary><see cref="TerrainEdgeDirection"/>.</summary>
    public string direction = "";
    public double t;
    public int blades;
    public double height;
    public double phase;
    public double sway;
    public double alpha;
}

/// <summary>A small drifting glow (firefly/spark) near water or moss, pulsing softly.</summary>
public sealed class TerrainFireflyEffect
{
    public int id;
    public double ox;
    public double oy;
    public double radius;
    public double phase;
    public double speed;
    public double drift;
    public double alpha;
}

/// <summary>A themed grass clump on walkable ground: dense enough to read as a patch, animated by the client wind pass.</summary>
public sealed class TerrainGrassPatchEffect
{
    public int id;
    public double ox;
    public double oy;
    public int blades;
    /// <summary>One broad tuft or several nested crowns. Authored in the deterministic plan so all clients agree.</summary>
    public int clumps;
    public double radius;
    public double height;
    public double phase;
    public double sway;
    public double lean;
    /// <summary>0..1 amount of seed-head / blossom punctuation carried by this patch.</summary>
    public double flowering;
    /// <summary>0..1 resistance to wind. Dry alpine blades bend less than wet meadow grass.</summary>
    public double stiffness;
    /// <summary>0..1 habitat-core membership; drives crown mass and secondary blade volume.</summary>
    public double? density;
    /// <summary>0..1 within-habitat age/height variation, stable across clients.</summary>
    public double? variation;
    public double alpha;
}

// `export type TerrainTreeDressingStyle = TreeVisualStyle;` → string ('sentinel' | 'windfan' | 'spire').

/// <summary>A large collision-neutral tree rooted on a terrain margin or inside one coherent grove patch.</summary>
public sealed class TerrainTreeDressingEffect : TreeVisual
{
    public int id;
    public double ox;
    public double oy;
    /// <summary>Procedural grove anchor on walkable floor; absent trees retain the protected solid-margin contract.</summary>
    public bool? floorAnchor;
    /// <summary>Explicit editor placement; bypasses procedural biome placement restrictions.</summary>
    public bool? authored;
    /// <summary>Explicit author-selected visual theme; absent procedural effects use the active biome.</summary>
    public string? themeKey;

    /// <summary>The `...visual` half of `{ id, ox, oy, …, ...visual }`: copies every TreeVisual field.</summary>
    public TerrainTreeDressingEffect(TreeVisual visual) : base(visual) { }
}

/// <summary>
/// `TreeVisual &amp; { readonly id: number }` — the mature specimen a tree-life stump came from
/// (<see cref="TerrainWorldDecorationEffect.felledTree"/>). Structurally the client's `TerrainTreeLike`.
/// </summary>
public sealed class TerrainFelledTreeVisual : TreeVisual
{
    /// <summary>The felled decoration's seed.</summary>
    public double id;

    /// <summary>`{ id, ...visual }`.</summary>
    public TerrainFelledTreeVisual(double id, TreeVisual visual) : base(visual) => this.id = id;
}

/// <summary>
/// A shared collision-neutral prop anchored to a generated terrain cell. Hub placements use the same visual
/// DNA through `HubProp`; only the placement authority differs.
///
/// `extends WorldDecorationVisual`: that record is sealed, so this one keeps the exact visual it was spread from
/// (<see cref="visual"/>), exposes its fields under their TS names (read-only) and converts implicitly to it.
/// </summary>
public sealed class TerrainWorldDecorationEffect
{
    public int id;
    public double ox;
    public double oy;

    /// <summary>The WorldDecorationVisual whose fields `{ ...visual }` spread into this record.</summary>
    public readonly WorldDecorationVisual visual;
    public double scale => visual.scale;

    /// <summary>Stable global composition cell. Equal ids belong to one readable prop patch, even across bake seams.</summary>
    public double patchId;
    /// <summary>0..1 local membership in the patch; renderers may ignore it without changing placement.</summary>
    public double patchStrength;
    /// <summary>Explicit editor placement; bypasses procedural biome placement restrictions.</summary>
    public bool? authored;
    /// <summary>Explicit author-selected visual theme; absent procedural effects use the active biome.</summary>
    public string? themeKey;
    /// <summary>
    /// The mature specimen a freshly felled stump came from.
    ///
    /// Ordinary decorative stumps leave this absent. A tree-life stump carries it so the renderer can reuse
    /// the canonical tree generator's exact base radius instead of inventing a generic, much wider drum.
    /// </summary>
    public TerrainFelledTreeVisual? felledTree;

    public TerrainWorldDecorationEffect(WorldDecorationVisual visual) => this.visual = visual;

    public static implicit operator WorldDecorationVisual(TerrainWorldDecorationEffect effect) => effect.visual;
}

/// <summary>A broad atmospheric cloud bank suspended inside an open Chasm shaft. Tile-space radii make the effect
/// scale with every renderer while `height` stays a physical fraction between its negative datum and rim.</summary>
public sealed class TerrainChasmMistEffect
{
    public int id;
    public double ox;
    public double oy;
    public double radiusX;
    public double radiusY;
    /// <summary>0 = remote continuum datum, 1 = world-height-zero throat. Deep placement prevents fog reading as floor.</summary>
    public double height;
    public double phase;
    public double drift;
    public double alpha;
}

/// <summary>`TerrainWallStrandStyle = 'vine' | 'root'`.</summary>
public static class TerrainWallStrandStyle
{
    public const string Vine = "vine";
    public const string Root = "root";
}

/// <summary>
/// Hanging wall growth: a vine reaching down a face, or an aerial root doing it.
///
/// There used to be a third style — `banner` — and it was the same mistake as every other prop this world has
/// shed: a drape, a caution skirt or an awning hanging on a rock face is furniture somebody hung, and nobody
/// in this world hung it. A cliff carries what grows on it.
/// </summary>
public sealed class TerrainWallStrandEffect
{
    public int id;
    /// <summary><see cref="TerrainEdgeDirection"/>.</summary>
    public string direction = "";
    public double t;
    public double length;
    public int strands;
    public double phase;
    public double sway;
    public double alpha;
    /// <summary><see cref="TerrainWallStrandStyle"/>.</summary>
    public string style = "";
}

public sealed class TerrainProceduralEffects
{
    public List<TerrainWaterWaveEffect> waterWaves = null!;
    public List<TerrainBubbleEffect> bubbles = null!;
    public List<TerrainRockFallEffect> rockFalls = null!;
    public List<TerrainRollingGrassEffect> rollingGrass = null!;
    public List<TerrainBirdEffect> birds = null!;
    public List<TerrainLilyPadEffect> lilyPads = null!;
    public List<TerrainFishShoalEffect> fishShoals = null!;
    public List<TerrainReedBedEffect> reedBeds = null!;
    public List<TerrainButterflyEffect> butterflies = null!;
    public List<TerrainAmbientMotifEffect> ambientMotifs = null!;
    public List<TerrainRockGrassEffect> rockGrass = null!;
    public List<TerrainFireflyEffect> fireflies = null!;
    public List<TerrainGrassPatchEffect> grassPatches = null!;
    /// <summary>The canopy slot: the ONE canonical tree, in every world, recoloured per theme.</summary>
    public List<TerrainTreeDressingEffect> trees = null!;
    public List<TerrainWorldDecorationEffect> worldDecorations = null!;
    /// <summary>Soft, animated depth layers inside open Chasm shafts; deterministic and collision-neutral.</summary>
    public List<TerrainChasmMistEffect> chasmMist = null!;
    public List<TerrainWallStrandEffect> wallStrands = null!;
}

public sealed class TerrainRenderPlan
{
    public MaterializedTerrain? terrain;
    /// <summary>`TerrainMaterial[]`, one per cell (fixed size, filled by index).</summary>
    public TerrainMaterial[] materials = null!;
    public TerrainWaterInfo[] water = null!;
    /// <summary>Cached once for every cell and shared by material, micro-detail and dressing derivation.</summary>
    public double[] moisture = null!;
    public TerrainFloorCompositionPlan floor = null!;
    /// <summary>Internal worker scratch retained across plans to avoid re-sampling composition for world dressing.</summary>
    public TerrainRenderPlanModule.TerrainCellCompositionPlan? compositionCache;
    public List<TerrainEdgeRun> edgeRuns = null!;
    public List<TerrainEdgeRun> backsideDropRuns = null!;
    public List<TerrainRimRun> rimRuns = null!;
    public List<TerrainContactShadow> shadows = null!;
    public List<TerrainAmbientOcclusionPatch> aoPatches = null!;
    public List<TerrainMicroDetail> microDetails = null!;
    public List<TerrainWaterfallEffect> waterfalls = null!;
    /// <summary>One construction axis per connected Bridge component; zero for non-Bridge cells.</summary>
    public TerrainProceduralEffects effects = null!;
    public TerrainLodSettings lod = null!;

    /// <summary>Shallow copy, the equivalent of `{ ...plan }`.</summary>
    public TerrainRenderPlan Clone() => (TerrainRenderPlan)MemberwiseClone();
}

public sealed class TerrainRenderPlanOptions
{
    /// <summary>(cell, terrain, water, moisture) → material.</summary>
    public Func<TerrainCell, MaterializedTerrain, TerrainWaterInfo, double, TerrainMaterial>? materialForCell;
    /// <summary>World-theme key for cosmetic procedural dressing density/shape. Physics and materials stay separate.</summary>
    public string? biomeKey;
    /// <summary>Deterministic salt for procedural effects. Endless callers pass the run seed mixed with the global bake
    /// region so equal local chunks in different runs still get their own wildlife/dressing rhythm.</summary>
    public double? effectSeed;
    /// <summary>Absolute world-cell coordinate of local terrain cell (0,0). Supplying it lets independently planned
    /// chunks sample one continuous low-frequency ecology field instead of restarting scatter noise per chunk.
    /// (Callers derive it as `originX / tileSize + i0`, a double in TS.)</summary>
    public double? originCellX;
    public double? originCellY;
}

/// <summary>
/// One coherent stand of undergrowth, sampled at an absolute world cell.
///
/// A patch used to also carry a KIND — forest, deadfall, stone garden, crystal garden, ceremony — because the
/// vocabulary held stone furniture and fire and each of those wanted different ground. Growth is the whole
/// vocabulary now, so a patch is a patch: its identity is where it is and how strongly it holds, and which
/// growth stands in it is decided per cell by terrainDecorationCandidateScore.
/// </summary>
public sealed class TerrainDressingPatchSample
{
    /// <summary>The composition patch id (a uint32 from `>>> 0`).</summary>
    public double id;
    public double strength;
    public double edge;
    public double direction;
}

public static partial class TerrainRenderPlanModule
{
    /// <summary>
    /// A frozen `Record&lt;TerrainEdgeDirection, number&gt;` (declaration order n, w, e, s). The indexer mirrors
    /// `record[direction]` and answers null (JS undefined) for any other key.
    /// </summary>
    public sealed class TerrainDirectionNumbers
    {
        public readonly double n;
        public readonly double w;
        public readonly double e;
        public readonly double s;

        public TerrainDirectionNumbers(double n, double w, double e, double s)
        {
            this.n = n;
            this.w = w;
            this.e = e;
            this.s = s;
        }

        public double? this[string direction] => direction switch
        {
            "n" => n,
            "w" => w,
            "e" => e,
            "s" => s,
            _ => null,
        };
    }

    public static class TERRAIN_LIGHT_MODEL
    {
        public static readonly TerrainDirectionNumbers contact = new(n: 0.06, w: 0.08, e: 0.15, s: 0.19);
    }

    public static readonly TerrainDirectionNumbers TERRAIN_DEPTH_FACE_VISIBILITY = new(n: 0, w: 0.72, e: 0.78, s: 1);

    /// <summary>`Record&lt;TerrainEdgeMaterial, number&gt;` (frozen).</summary>
    public static class TERRAIN_EDGE_COLORS
    {
        // Last-resort waterline bank (only reached when a water material carries no dark tone at all). Kept in the
        // same neutral wet-slate family as the standard water above, so no biome-blind cyan survives anywhere in
        // the water palette.
        public const int bank = 0x849095;
        public const int deck = 0xad9b75;
        public const int earth = 0x7f8d86;
        public const int abyss = 0x5b5548;
    }

    /// <summary>The frozen standard palette, in declaration order. Entries are shared; never mutate them.</summary>
    public static class STANDARD_TERRAIN_MATERIALS
    {
        public static readonly TerrainMaterial floorCool = new()
        {
            id = "floorCool",
            top = 0xcdddd6,
            topLight = 0xebf6ef,
            topDark = 0xafb7a6,
            side = 0x87978f,
            edgeLight = 0xfffdf3,
            edgeDark = 0x68746d,
            detail = 0x5f6f69,
            roughness = 0.44,
        };
        public static readonly TerrainMaterial floorDamp = new()
        {
            id = "floorDamp",
            top = 0xc3d0c6,
            topLight = 0xebf6ef,
            topDark = 0x9c9e88,
            side = 0x828f88,
            edgeLight = 0xfffdf3,
            edgeDark = 0x68746d,
            detail = 0x56706e,
            roughness = 0.56,
        };
        public static readonly TerrainMaterial floorDry = new()
        {
            id = "floorDry",
            top = 0xb9c4b6,
            topLight = 0xcdddd6,
            topDark = 0x89866a,
            side = 0x7b877f,
            edgeLight = 0xfffdf3,
            edgeDark = 0x677060,
            detail = 0x66715d,
            roughness = 0.5,
        };
        public static readonly TerrainMaterial wallChalk = new()
        {
            id = "wallChalk",
            top = 0xc3cfbd,
            topLight = 0xe1eadb,
            topDark = 0xaab69e,
            side = 0x818d84,
            edgeLight = 0xfffdf3,
            edgeDark = 0x0c1411,
            detail = 0x0c1411,
            roughness = 0.68,
        };
        public static readonly TerrainMaterial wallMoss = new()
        {
            id = "wallMoss",
            top = 0xbdc8b6,
            topLight = 0xe1eadb,
            topDark = 0xaab69e,
            side = 0x7d897f,
            edgeLight = 0xfffdf3,
            edgeDark = 0x0c1411,
            detail = 0x0c1411,
            roughness = 0.72,
        };
        public static readonly TerrainMaterial bridgeWood = new()
        {
            id = "bridgeWood",
            top = 0xad9b75,
            topLight = 0xc9bca0,
            topDark = 0x6e6750,
            side = 0x93886b,
            edgeLight = 0xc2b496,
            edgeDark = 0x4f4a3a,
            detail = 0x685f48,
            roughness = 0.62,
        };
        /// <summary>
        /// The UNTHEMED water. This material is not "the colour of water" — it is water's VALUE structure: a wet
        /// slate that drops clearly below any walkable surface, a pale waterline band and a paper-white crest.
        /// The PIGMENT comes from the theme's flood palette, which the renderer mixes over this base.
        ///
        /// It used to be `0x32b9c6` / `0x88d6d9` verbatim — the UI's cool accent (chroma 0.58). Because the shared
        /// base keeps most of the weight when a theme tints it, that one biome-blind constant, not the theme,
        /// decided what every lake looked like: measured across the 20 themes without authored water, the final
        /// water tile landed at hue 182–187° and HSL saturation 0.47–0.60 in ALL of them — a pool-cyan lake in a
        /// sepia canyon, the only object in the frame outside the pigment palette.
        ///
        /// ### Why the chroma is 0.075 and not 0 — and not 0.13 either
        /// A base with NO chroma is not neutral in the frame, only in the file: water is the one non-opaque surface
        /// here, and an achromatic albedo under a warm key simply renders as the key's own colour (a lake came back
        /// ochre-cream, hue 18°, in an autumn frame). So the base carries a real cool cast. But every degree of it is
        /// imposed on all 20 derived themes at once, and that cost was measured on the same instrument: raising this
        /// chroma to 0.10/0.13 drags the worst derived lake 64°/94° off its own pigment family — i.e. it rebuilds the
        /// defect above in miniature. 0.075 is the largest cast that keeps every derived lake inside the ≤60° family
        /// bound the audit enforces; the REST of water's resistance to the key light comes from the theme's own
        /// pigment, whose share of the tile was raised for exactly this reason (see `deriveRunTerrainMaterial`).
        ///
        /// ### The VALUE half
        /// The old base sat at luminance 152 — as bright as a lit terrace — so a lake read as a light patch. At 84
        /// it is a laid wash below every walkable surface (worst-case shoreline separation over the derived themes
        /// rises from 5 to 24), while the pale waterline band and the paper crest above it are what give the sheet
        /// its light, exactly as a reserved white does on paper. The dark pole is 58, not 29: at 29 the deep half of
        /// a pool crushed to display black next to its own shallow half.
        /// </summary>
        public static readonly TerrainMaterial water = new()
        {
            id = "water",
            top = 0x49575c,
            topLight = 0x849095,
            topDark = 0x323c40,
            side = 0x323c40,
            edgeLight = 0xfffdf3,
            edgeDark = 0x323c40,
            detail = 0xfffdf3,
            roughness = 0.18,
            shallow = 0x849095,
            mid = 0x49575c,
            deep = 0x323c40,
            shadow = 0x323c40,
            highlight = 0xfffdf3,
        };
        public static readonly TerrainMaterial chasm = new()
        {
            id = "chasm",
            // Dry umber/slate with enough midtone range to retain readable geology at gameplay zoom.
            top = 0x716a59,
            topLight = 0x9b927b,
            topDark = 0x4d483c,
            side = 0x756e5e,
            edgeLight = 0xaaa18b,
            edgeDark = 0x423e34,
            detail = 0x827a67,
            roughness = 0.92,
        };
    }

    /// <summary>How much of a DERIVED lake the theme may ever be, and the least it may ever be. See
    /// <see cref="waterTerrainMaterial"/>. Both bounds bind the derivation; neither can reach a stated palette.</summary>
    public const double WATER_THEME_SHARE_CAP = 0.66;
    public const double WATER_THEME_SHARE_FLOOR = 0.85;

    /// <summary>
    /// **The** rule that turns a theme's water pigment into the material a Water cell is painted with. One home,
    /// three callers (`runTerrainMaterial`, the worker-leaf `terrainMaterialCompiler`, and `ThreeTerrainLayer`'s
    /// Hub/painted-theme path).
    ///
    /// It has one home because it previously had three, with three different weight sets (0.30/0.34/0.38 in the
    /// Hub copy, 0.50/0.55/0.60 in the other two) and a shared cyan constant in all of them. That is why two
    /// attempts to remove the biome-blind `0x32b9c6` from the water palette did not show up in the Hub frame at
    /// all: the Hub is painted by a copy neither of them touched. A rule that exists three times is a rule that
    /// cannot be fixed once — the charter's "zero duplicated logic" is not style advice here, it was the defect.
    ///
    /// ### Stated water is used verbatim
    /// An authored `Biome.water` (lava, oily canal, meltwater, ink) — and any palette a registered tileset factory
    /// or the aegis-citadel dialect hands over — is a finished art statement, so the untinted
    /// STANDARD_TERRAIN_MATERIALS.water contributes nothing to it and `waterTint` has nothing left to dilute.
    /// This is the extensibility contract made literal: author a lake and you get that lake, and no future change
    /// to the shared base can reach it. It also repairs real damage — while the base still had a vote, replacing
    /// it repainted eleven authored lakes that had nothing wrong with them (measured on the identical 8 378 pixels
    /// of the Hub pool: `#2b494b`, hue 183°, sat 0.292 → `#373735`, hue 57°, sat 0.031 — the sand's own hue).
    ///
    /// ### A derived palette still leans on the shared value structure
    /// `floodColors`' fallback is a guess, not a statement, so there the base still supplies the value structure
    /// that makes water read as a hole in the ground. The weight used to be `0.34 × waterTint` capped at 0.92 —
    /// the theme got a third of a say in the colour of its own water and one constant supplied the rest, which is
    /// exactly what made every unauthored lake the same colour. At 0.55 with a 0.66 cap the theme is the author.
    /// The FLOOR (0.85 of the standard share) matters as much as the cap: a quiet `waterTint` is a statement about
    /// how much theme colour a lake carries, never a licence for the shared constant to take the pen back — below
    /// it the lake collapses toward ground tone (measured: the 0.72-emphasis run sat at 0.49× its frame's mean
    /// saturation, a puddle of dirt rather than water).
    ///
    /// Pure; a handful of integer blends, called once per biome/material change — never per frame, never per tile.
    /// </summary>
    public static TerrainMaterial waterTerrainMaterial(TerrainWaterPigment flood, double waterTint)
    {
        TerrainMaterial water = STANDARD_TERRAIN_MATERIALS.water;
        if (flood.derived != true)
        {
            TerrainMaterial stated = water.Clone();
            stated.top = flood.surface;
            stated.topLight = mixColor(flood.surface, flood.foam, 0.18);
            stated.topDark = flood.deep;
            stated.side = flood.body;
            stated.edgeLight = flood.foam;
            stated.edgeDark = flood.deep;
            stated.detail = flood.glint;
            stated.shallow = flood.surface;
            stated.mid = flood.body;
            stated.deep = flood.deep;
            stated.highlight = flood.foam;
            return stated;
        }
        double wt(double weight) =>
            clamp(weight * waterTint, weight * WATER_THEME_SHARE_FLOOR, WATER_THEME_SHARE_CAP);
        TerrainMaterial derived = water.Clone();
        derived.top = mixColor(water.top, flood.surface, wt(0.5));
        derived.topLight = mixColor(water.topLight, flood.surface, wt(0.44));
        derived.topDark = mixColor(water.topDark, flood.deep, wt(0.6));
        derived.shallow = mixColor(water.shallow!.Value, flood.surface, wt(0.5));
        derived.mid = mixColor(water.mid!.Value, flood.body, wt(0.55));
        derived.deep = mixColor(water.deep!.Value, flood.deep, wt(0.6));
        derived.highlight = mixColor(water.highlight!.Value, flood.foam, wt(0.5));
        return derived;
    }

    /// <summary>
    /// Whether this transition owns an open hydraulic portal rather than merely connecting liquid datums.
    ///
    /// A Chasm waterfall replaces the complete Water/Chasm boundary from its source crest to the materialised
    /// receiving floor. Geological walls, bridge structure and basin shells may meet its lateral endpoints, but
    /// may not also occupy the portal.
    /// </summary>
    public static bool terrainWaterfallOwnsPortal(TerrainWaterfallEffect waterfall) =>
        waterfall.landing == "chasm";

    /// <summary>Resolve the portal, if any, authored on one directed source-cell edge.</summary>
    /// <remarks>PORT NOTE (allocation): TS `waterfalls.find(…)` as the equivalent first-match loop — the bake queries
    /// this per cell edge, and a capturing predicate allocated a closure and a delegate per query.</remarks>
    public static TerrainWaterfallEffect? terrainWaterfallPortalForEdge(
        IReadOnlyList<TerrainWaterfallEffect> waterfalls,
        int sourceCellId,
        string direction,
        int? targetCellId = null)
    {
        for (int i = 0; i < waterfalls.Count; i++)
        {
            TerrainWaterfallEffect waterfall = waterfalls[i];
            if (
                terrainWaterfallOwnsPortal(waterfall) &&
                waterfall.sourceCellId == sourceCellId &&
                waterfall.direction == direction &&
                (targetCellId == null || waterfall.targetCellId == targetCellId))
                return waterfall;
        }
        return null;
    }

    /// <summary>Resolve the same portal from either participating cell's view of their shared edge.</summary>
    /// <remarks>PORT NOTE (allocation): `waterfalls.find(…)` as a loop, see <see cref="terrainWaterfallPortalForEdge"/>.</remarks>
    public static TerrainWaterfallEffect? terrainWaterfallPortalAtCellEdge(
        IReadOnlyList<TerrainWaterfallEffect> waterfalls,
        int cellId,
        string direction)
    {
        string opposite = direction == "n" ? "s" : direction == "s" ? "n" : direction == "e" ? "w" : "e";
        for (int i = 0; i < waterfalls.Count; i++)
        {
            TerrainWaterfallEffect waterfall = waterfalls[i];
            if (
                terrainWaterfallOwnsPortal(waterfall) &&
                ((waterfall.sourceCellId == cellId && waterfall.direction == direction) ||
                    (waterfall.targetCellId == cellId && waterfall.direction == opposite)))
                return waterfall;
        }
        return null;
    }

    /// <summary>
    /// The physical receiving datum shared by every Chasm curtain that terminates at one grid corner.
    ///
    /// A broad fall is one sheet even when the materialised Chasm floor terraces beneath it. At an internal run
    /// corner either adjacent receiver may be lower, and a perpendicular fall may meet there as well. All of those
    /// owners must use the lowest participating floor: higher terrain occludes the harmless continuation, whereas
    /// choosing any higher datum leaves the lower receiver open to the backdrop. Keeping this rule beside portal
    /// ownership makes the compiler and corner join consume one authoritative hydraulic boundary.
    /// </summary>
    public static double? terrainWaterfallBottomZAtCorner(
        MaterializedTerrain terrain,
        IReadOnlyList<TerrainWaterfallEffect> waterfalls,
        int cornerX,
        int cornerY)
    {
        double? bottomZ = null;
        foreach (TerrainWaterfallEffect waterfall in waterfalls)
        {
            if (!terrainWaterfallOwnsPortal(waterfall)) continue;
            TerrainCell? source =
                (uint)waterfall.sourceCellId < (uint)terrain.cells.Length ? terrain.cells[waterfall.sourceCellId] : null;
            if (source == null) continue;
            bool northSouth = waterfall.direction == "n" || waterfall.direction == "s";
            int edgeCoordinate = northSouth
                ? source.y + (waterfall.direction == "s" ? 1 : 0)
                : source.x + (waterfall.direction == "e" ? 1 : 0);
            int alongCoordinate = northSouth ? source.x : source.y;
            bool touchesCorner = northSouth
                ? cornerY == edgeCoordinate &&
                    (cornerX == alongCoordinate || cornerX == alongCoordinate + 1)
                : cornerX == edgeCoordinate &&
                    (cornerY == alongCoordinate || cornerY == alongCoordinate + 1);
            if (!touchesCorner) continue;
            bottomZ = bottomZ == null ? waterfall.bottomZ : Math.min(bottomZ.Value, waterfall.bottomZ);
        }
        return bottomZ;
    }

    /// <summary>Layered shaft atmosphere. Individual layers stay translucent; independent currents build the visible volume.</summary>
    public const double TERRAIN_CHASM_MIST_MIN_ALPHA = 0.085;
    public const double TERRAIN_CHASM_MIST_MAX_ALPHA = 0.16;
    public const double TERRAIN_CHASM_MIST_MIN_HEIGHT = 0.38;
    public const double TERRAIN_CHASM_MIST_MAX_HEIGHT = 0.65;
    public const int TERRAIN_CHASM_MIST_CELLS_PER_BANK = 16;
    public const int TERRAIN_CHASM_MIST_MAX_BANKS = 20;

    /// <summary>
    /// ONE global ground-cover density dial (Living-Ground WP). Multiplies the per-biome grass-patch,
    /// wall-foot tuft and reed probabilities proportionally, so the hand-tuned biome RATIOS survive while
    /// the world sheds its bare-plateau read. Deterministic (pure probability scaling on the same seeded
    /// hashes); the per-tier effect LIMITS still cap the absolute budget, so mobile stays inside its bake
    /// envelope even with the richer candidate stream.
    /// </summary>
    public const double GROUND_COVER_DENSITY = 2.1;

    /// <summary>Reusable, full-precision cell samples shared by procedural dressing selectors within one render plan. The
    /// cache is worker-local and never crosses the geometry-transfer boundary. (Non-exported in TS; public here
    /// only because <see cref="TerrainRenderPlan.compositionCache"/> is.)</summary>
    public sealed class TerrainCellCompositionPlan
    {
        public byte[] valid = null!;
        public double[] grove = null!;
        public double[] landmark = null!;
        public double[] focus = null!;
        public double[] quiet = null!;
        public double[] direction = null!;
        public int[] patchId = null!;
        public double[] patchStrength = null!;
        public double[] patchEdge = null!;
    }

    /// <summary>
    /// The tree line, as a fraction of the world's vertical domain.
    ///
    /// It has to be a fraction rather than a height. The former gate was the absolute level `13.5`, and an absolute
    /// ceiling silently becomes a different rule every time the terrain's height distribution moves. Measured
    /// against the shared world's shipping raster, `13.5` admitted **4.1 % of walkable ground** — the walkable
    /// distribution runs p05 14, p50 19, p95 25, so the ceiling had ended up *below the fifth percentile* of the
    /// ground it was meant to divide. Trees were confined to the valley floors and nothing else, which is the other
    /// half of why this world reads as treeless. The non-guaranteed ceiling of `8.2` admitted **0.0 %**.
    ///
    /// Read off the same CDF, expressed against the domain so it cannot drift again:
    ///
    /// | ceiling            | walkable ground that can carry a tree |
    /// | ------------------ | ------------------------------------- |
    /// | 13.5 (was)         | 4.1 %                                 |
    /// | 18                 | 28.8 %                                |
    /// | 20                 | 58.2 %                                |
    /// | **21.6 (0.83 × domain)** | **73.9 %**                      |
    /// | 24                 | 84.9 %                                |
    ///
    /// 0.83 leaves the top quarter of the domain bare, which is what an alpine tree line looks like and what gives
    /// a summit its silhouette. A fraction of the global domain is also seam-free: normalising per chunk would band
    /// the treeline differently on each side of a chunk border.
    /// </summary>
    private const double TREE_LINE_DOMAIN_FRACTION = 0.83;
    /// <summary>The same line for themes that do not guarantee trees — kept where it was (`8.2 ≈ 0.32 × domain`).</summary>
    private const double SPARSE_TREE_LINE_DOMAIN_FRACTION = 0.32;

    /// <summary>
    /// Forest-patch strength at which a cell counts as **stand interior** rather than open country.
    ///
    /// One number for two decisions that have to agree: whether a cell is admitted at canopy density, and whether
    /// two placed crowns may pack at the tight in-patch spacing. They were 0.58 and 0.6, and the gap was a real
    /// defect — a cell admitted as forest at 0.59 was then spaced as if it stood in open country, which punched a
    /// hole in the edge of every stand.
    ///
    /// Read from the measured distribution over walkable Floor: p50 0.261, p75 0.514, p90 0.753. At 0.58 the stand
    /// interior is 21.1 % of walkable ground, which is about how much of this world should read as wooded.
    /// </summary>
    private const double FOREST_STAND_STRENGTH = 0.58;

    private static class LOD_SETTINGS
    {
        public const int denseCellLimit = 6500;
        public const int midCellLimit = 18000;
        public const int maxMicroDense = 340;
        public const int maxMicroMid = 220;
        public const int maxMicroLarge = 120;
    }

    private static double[] float64Layer(double[]? reuse, int count) =>
        reuse != null && reuse.Length == count ? reuse : new double[count];

    private static TerrainCellCompositionPlan createTerrainCellCompositionPlan(
        int count,
        TerrainCellCompositionPlan? reuse = null)
    {
        byte[] valid = reuse?.valid != null && reuse.valid.Length == count ? reuse.valid : new byte[count];
        valid.fill((byte)0);
        return new TerrainCellCompositionPlan
        {
            valid = valid,
            grove = float64Layer(reuse?.grove, count),
            landmark = float64Layer(reuse?.landmark, count),
            focus = float64Layer(reuse?.focus, count),
            quiet = float64Layer(reuse?.quiet, count),
            direction = float64Layer(reuse?.direction, count),
            patchId = reuse?.patchId != null && reuse.patchId.Length == count ? reuse.patchId : new int[count],
            patchStrength = float64Layer(reuse?.patchStrength, count),
            patchEdge = float64Layer(reuse?.patchEdge, count),
        };
    }

    /// <summary>Build one coherent floor ecology around the actual authored/generated circulation network.</summary>
    public static TerrainFloorCompositionPlan createTerrainFloorComposition(
        MaterializedTerrain terrain,
        double[] moisture,
        string? biomeKey = null,
        double originCellX = 0,
        double originCellY = 0,
        TerrainFloorCompositionPlan? reuse = null) =>
        TerrainFloorComposition.createTerrainFloorCompositionPlan(
            terrain,
            moisture,
            biomeKey,
            originCellX,
            originCellY,
            reuse,
            new TerrainFloorCompositionSamplers
            {
                compositionAt = (worldCellX, worldCellY, key, @out) =>
                    terrainCompositionAt(worldCellX, worldCellY, key, @out),
                ecologyAt = (worldCellX, worldCellY, key) => terrainEcologyPatchAt(worldCellX, worldCellY, key),
                focusPathWearAt = (worldCellX, worldCellY, key, scratch) =>
                    terrainFocusPathWearAt(worldCellX, worldCellY, key, scratch),
            });

    public static TerrainRenderPlan createTerrainRenderPlan(
        MaterializedTerrain? terrain,
        TerrainRenderPlanOptions? options = null,
        TerrainRenderPlan? reuse = null)
    {
        options ??= new TerrainRenderPlanOptions();
        if (terrain == null) return emptyTerrainRenderPlan();

        TerrainLodSettings lod = createTerrainLodSettings(terrain.cells.Length);
        int cellCount = terrain.cells.Length;
        TerrainWaterInfo[] water = reuse?.water ?? new TerrainWaterInfo[cellCount];
        double[] moisture = reuse?.moisture ?? new double[cellCount];
        TerrainMaterial[] materials = reuse?.materials ?? new TerrainMaterial[cellCount];
        // `array.length = cellCount` (truncates or extends in place; surviving entries are reused below).
        if (water.Length != cellCount) Array.Resize(ref water, cellCount);
        if (moisture.Length != cellCount) Array.Resize(ref moisture, cellCount);
        if (materials.Length != cellCount) Array.Resize(ref materials, cellCount);
        // Moisture used to be rescanned independently by material derivation, micro-detail and procedural dressing
        // (up to three 5x5 neighbourhood walks per cell). It is pure terrain data, so compute it once and share it.
        Func<TerrainCell, MaterializedTerrain, TerrainWaterInfo, double, TerrainMaterial> materialOf =
            options.materialForCell ??
            (Func<TerrainCell, MaterializedTerrain, TerrainWaterInfo, double, TerrainMaterial>)defaultTerrainMaterialForCell;
        for (int index = 0; index < cellCount; index++)
        {
            TerrainCell cell = terrain.cells[index];
            water[index] = waterInfoForCell(cell, terrain, water[index]);
            moisture[index] = moistureForCell(cell, terrain);
            materials[index] = materialOf(cell, terrain, water[index], moisture[index]);
        }
        TerrainFloorCompositionPlan floor = createTerrainFloorComposition(
            terrain,
            moisture,
            options.biomeKey,
            options.originCellX ?? 0,
            options.originCellY ?? 0,
            reuse?.floor);
        TerrainCellCompositionPlan compositionCache = createTerrainCellCompositionPlan(cellCount, reuse?.compositionCache);
        List<TerrainEdgeRun> edgeRuns = buildTerrainEdgeRuns(
            terrain,
            materials,
            shouldRenderTerrainDepthEdge,
            reuse?.edgeRuns);
        List<TerrainEdgeRun> backsideDropRuns = buildTerrainEdgeRuns(
            terrain,
            materials,
            shouldRenderBacksideDropEdge,
            reuse?.backsideDropRuns);
        List<TerrainRimRun> rimRuns = reuse?.rimRuns ?? new List<TerrainRimRun>();
        rimRuns.Clear();
        List<TerrainAmbientOcclusionPatch> aoPatches = reuse?.aoPatches ?? new List<TerrainAmbientOcclusionPatch>();
        aoPatches.Clear();
        List<TerrainMicroDetail> microDetails = reuse?.microDetails ?? new List<TerrainMicroDetail>();
        microDetails.Clear();
        List<TerrainContactShadow> shadows = reuse?.shadows ?? new List<TerrainContactShadow>();
        int shadowCount = 0;
        foreach (TerrainEdgeRun run in edgeRuns)
        {
            if (run.drop <= 0.12) continue;
            bool rock = isRockRun(run);
            double contact = orNumber(TERRAIN_LIGHT_MODEL.contact[run.direction], 0.08);
            TerrainContactShadow shadow =
                (shadowCount < shadows.Count ? shadows[shadowCount] : null) ?? new TerrainContactShadow();
            shadow.run = run;
            shadow.direction = run.direction;
            shadow.drop = run.drop;
            shadow.depth = clamp(
                4.8 + run.drop * (rock ? 4.3 : 3.2) + run.relief.undercut * 2.4,
                5.2,
                rock ? 24 : 18);
            shadow.alpha = clamp(
                contact * (0.28 + run.drop * (rock ? 0.15 : 0.11)),
                0.01,
                rock ? 0.068 : 0.052);
            // `shadows[shadowCount++] = shadow`
            if (shadowCount < shadows.Count) shadows[shadowCount] = shadow;
            else shadows.Add(shadow);
            shadowCount++;
        }
        // `shadows.length = shadowCount`
        if (shadows.Count > shadowCount) shadows.RemoveRange(shadowCount, shadows.Count - shadowCount);

        var planWithoutEffects = new TerrainRenderPlan
        {
            terrain = terrain,
            materials = materials,
            water = water,
            moisture = moisture,
            floor = floor,
            compositionCache = compositionCache,
            edgeRuns = edgeRuns,
            backsideDropRuns = backsideDropRuns,
            rimRuns = rimRuns,
            shadows = shadows,
            // Height is described by physical side faces, bevels and world-space contact shade. Screen-thin cap-rim
            // overlays are deliberately empty: at gameplay zoom they alias into apparently random horizontal and
            // vertical scratches around terrace corners. The clean low-poly terrain also carries no corner stickers
            // or per-cell scratch/chip scatter.
            aoPatches = aoPatches,
            microDetails = microDetails,
            waterfalls = createTerrainWaterfalls(terrain),
            effects = emptyTerrainEffects(),
            lod = lod,
        };
        // `{ ...planWithoutEffects, effects: createTerrainProceduralEffects(…) }`
        TerrainRenderPlan plan = planWithoutEffects.Clone();
        plan.effects = createTerrainProceduralEffects(
            terrain,
            planWithoutEffects,
            options.effectSeed ?? 0,
            options.biomeKey,
            options.originCellX ?? 0,
            options.originCellY ?? 0,
            compositionCache);
        return plan;
    }

    /// <summary>Build exactly the procedural-effect payload consumed by the ambient streaming renderer without retaining
    /// the geometry/edge/AO half of a full terrain render plan. The prerequisite water, moisture and material
    /// arrays use the same functions and ordering as <see cref="createTerrainRenderPlan"/>, so the resulting effects are
    /// byte-for-byte equivalent. This is primarily used in a worker where only the compact effect records cross
    /// back to the main thread. (`options` is `Pick&lt;TerrainRenderPlanOptions, 'biomeKey' | 'effectSeed' |
    /// 'originCellX' | 'originCellY'&gt;`; `materialForCell` is ignored.)</summary>
    public static TerrainProceduralEffects createTerrainProceduralEffectsPlan(
        MaterializedTerrain? terrain,
        TerrainRenderPlanOptions? options = null)
    {
        options ??= new TerrainRenderPlanOptions();
        if (terrain == null) return emptyTerrainEffects();
        TerrainLodSettings lod = createTerrainLodSettings(terrain.cells.Length);
        var water = new TerrainWaterInfo[terrain.cells.Length];
        var moisture = new double[terrain.cells.Length];
        var materials = new TerrainMaterial[terrain.cells.Length];
        for (int index = 0; index < terrain.cells.Length; index++)
        {
            TerrainCell cell = terrain.cells[index];
            water[index] = waterInfoForCell(cell, terrain);
            moisture[index] = moistureForCell(cell, terrain);
            materials[index] = defaultTerrainMaterialForCell(
                cell,
                terrain,
                water[index],
                moisture[index]);
        }
        TerrainFloorCompositionPlan floor = createTerrainFloorComposition(
            terrain,
            moisture,
            options.biomeKey,
            options.originCellX ?? 0,
            options.originCellY ?? 0);
        TerrainCellCompositionPlan compositionCache = createTerrainCellCompositionPlan(terrain.cells.Length);
        return createTerrainProceduralEffects(
            terrain,
            // `{ water, moisture, materials, floor, lod }` — the Pick<TerrainRenderPlan, …> the effects read.
            new TerrainRenderPlan { water = water, moisture = moisture, materials = materials, floor = floor, lod = lod },
            options.effectSeed ?? 0,
            options.biomeKey,
            options.originCellX ?? 0,
            options.originCellY ?? 0,
            compositionCache);
    }

    public static TerrainRenderPlan emptyTerrainRenderPlan()
    {
        TerrainLodSettings lod = createTerrainLodSettings(0);
        return new TerrainRenderPlan
        {
            terrain = null,
            materials = new TerrainMaterial[0],
            water = new TerrainWaterInfo[0],
            moisture = new double[0],
            floor = TerrainFloorComposition.emptyTerrainFloorCompositionPlan(),
            edgeRuns = new List<TerrainEdgeRun>(),
            backsideDropRuns = new List<TerrainEdgeRun>(),
            rimRuns = new List<TerrainRimRun>(),
            shadows = new List<TerrainContactShadow>(),
            aoPatches = new List<TerrainAmbientOcclusionPatch>(),
            microDetails = new List<TerrainMicroDetail>(),
            waterfalls = new List<TerrainWaterfallEffect>(),
            effects = emptyTerrainEffects(),
            lod = lod,
        };
    }

    public static List<TerrainWaterfallEffect> createTerrainWaterfalls(MaterializedTerrain terrain)
    {
        var waterfalls = new List<TerrainWaterfallEffect>();
        foreach (TerrainCell cell in terrain.cells)
        {
            if (!terrainTileCarriesWater(cell.type, cell.span) || cell.waterLevel == null) continue;
            if (cell.type == TileType.Bridge && !bridgeWaterIsSafelyBelowDeck(cell)) continue;
            int directionIndex = 0;
            foreach (TerrainDirection direction in TerrainDirections)
            {
                TerrainCell? target = terrainCellAt(terrain, cell.x + direction.dx, cell.y + direction.dy);
                double sourceLevel = cell.waterLevel ?? cell.surfaceZ;
                // A Chasm-crossing Bridge suspends its deck over the SAME shaft an exposed Chasm cell opens. Classifying
                // by raw tile type made such a neighbour "water", and since a Chasm-spanning deck carries no water level
                // the fall was then dropped entirely — the reported pond that ends at a shaft with nothing pouring in.
                bool chasmLanding = target != null && terrainCellCarriesChasmFloor(target);
                string landing = chasmLanding ? "chasm" : "water";
                double? landingZ = chasmLanding
                    ? terrainChasmThroatZAt(terrain, target!)
                    : target != null &&
                        terrainTileCarriesWater(target.type, target.span) &&
                        (target.type != TileType.Bridge || bridgeWaterIsSafelyBelowDeck(target))
                        ? target.waterLevel
                        : null;
                double entryDrop = landingZ == null ? 0 : sourceLevel - landingZ.Value;
                bool validDrop =
                    landing == "chasm" ? entryDrop >= 3.5 : entryDrop >= TERRAIN_WATERFALL_MIN_DROP;
                if (target == null || landingZ == null || !validDrop)
                {
                    directionIndex++;
                    continue;
                }
                // The throat is the mist/cloud-entry datum, not a physical landing. The geological boundary is opened as
                // one hydraulic portal to the deep floor, so the liquid sheet owns that complete interval. That datum is shared across
                // staggered -5..-8 cells, which also lets adjacent source cells merge into one uninterrupted curtain.
                double bottomZ = chasmLanding
                    ? (terrainChasmFloorZAt(terrain, target) ?? target.baseZ)
                    : landingZ.Value;
                double drop = sourceLevel - bottomZ;
                double h = hashTerrainCell((double)cell.id * 197 + directionIndex * 43 + 17);
                int runCoordinate =
                    direction.key == "n" || direction.key == "s"
                        ? cell.y * 4 + directionIndex
                        : cell.x * 4 + directionIndex;
                waterfalls.push(new TerrainWaterfallEffect
                {
                    id = cell.id * 4 + directionIndex,
                    sourceCellId = cell.id,
                    targetCellId = target.id,
                    direction = direction.key,
                    landing = landing,
                    topZ = sourceLevel,
                    bottomZ = bottomZ,
                    drop = drop,
                    // Slight overlap eliminates per-tile gaps. Phase is keyed to the whole edge line (not the individual
                    // source cell), so neighbours animate as one cascade while their narrow highlight ribbons still vary.
                    width = 1.018 + h * 0.036,
                    phase = hashTerrainCell((double)runCoordinate * 211 + 29),
                    intensity = clamp(0.66 + drop * 0.065, 0.7, 1),
                });
                directionIndex++;
            }
        }
        return waterfalls;
    }

    /// <summary>`clamp(Math.floor(cellCount / divisor), min, max)` for the integral LOD budgets.</summary>
    private static int lodLimit(int cellCount, double divisor, double min, double max) =>
        (int)clamp(Math.floor(cellCount / divisor), min, max);

    public static TerrainLodSettings createTerrainLodSettings(int cellCount)
    {
        if (cellCount <= LOD_SETTINGS.denseCellLimit)
        {
            return new TerrainLodSettings
            {
                level = 0,
                microLimit = LOD_SETTINGS.maxMicroDense,
                waterWaveLimit = lodLimit(cellCount, 150, 8, 80),
                bubbleLimit = lodLimit(cellCount, 320, 4, 45),
                rockLimit = lodLimit(cellCount, 220, 8, 70),
                grassLimit = lodLimit(cellCount, 2200, 2, 11),
                // SCREEN-relative, not chunk-relative. A gameplay screen is roughly an eighth of a 64x64 chunk, so
                // a per-chunk cap of 5 birds put ~0.6 birds on screen — which is why a world with a full bestiary
                // of ambient life still read as empty. These divisors put a handful of each lane in view at once.
                //
                // The FLOOR is what actually binds on a streamed 32x32 endless chunk (1024 cells), and it was still
                // the number doing the harm: at 6 birds per chunk a desktop screen carried 1.4 of them.
                birdLimit = lodLimit(cellCount, 620, 11, 34),
                lilyLimit = lodLimit(cellCount, 90, 12, 110),
                fishLimit = lodLimit(cellCount, 110, 10, 90),
                // Living-Ground WP: the desktop bake carries the densified ground cover (the candidate stream
                // is scaled by GROUND_COVER_DENSITY); the medium/low tiers keep their proven mobile envelopes.
                reedLimit = lodLimit(cellCount, 60, 24, 190),
                butterflyLimit = lodLimit(cellCount, 520, 8, 40),
                rockGrassLimit = lodLimit(cellCount, 140, 10, 100),
                fireflyLimit = lodLimit(cellCount, 700, 3, 22),
                grassPatchLimit = lodLimit(cellCount, 14, 32, 520),
                // Dense bakes need enough crowns for one small readable stand. Patch-aware spacing below keeps the
                // additional budget inside a grove instead of turning it into uniform scatter.
                /*
                 * Trees per plan at the dense tier — **the budget that decides whether this world has forests.**
                 *
                 * A streamed chunk is 1 024 cells, so the former `cellCount / 300` floored at 4 handed every chunk in the
                 * world exactly **4 trees, min 4, max 4, measured across 25 consecutive chunks**. A full desktop screen is
                 * roughly a quarter of a chunk, which put about *one* tree in view at a time: precisely the "scattered
                 * single trees instead of woodland" the world reads as. The divisor was sized for whole finite dungeon
                 * maps of several thousand cells and starves a chunk by an order of magnitude.
                 *
                 * The placement model below never needed fixing. It is already formation-aware — a candidate inside a
                 * forest core packs at 1.35 tiles (diagonal neighbours, i.e. a real stand) while open ground keeps 2.35 —
                 * so the honest way to size this is to let the spacing rule run unbounded and measure what it wants.
                 * Measured with the cap removed: **20.4 trees per chunk, min 8, max 34**, and that 8→34 spread *is* the
                 * formation signal, which a constant cap of 4 erased completely.
                 *
                 * So the cap is set above the natural maximum on purpose: spacing and formation decide density, and this
                 * only catches the pathological chunk. `cellCount / 21` gives 48 at 1 024 cells, comfortably clear of the
                 * measured 34. Note the old ceiling of 32 sat *below* the natural maximum — it bound even where the
                 * divisor did not.
                 */
                treeLimit = lodLimit(cellCount, 21, 24, 220),
                wallStrandLimit = lodLimit(cellCount, 300, 5, 34),
            };
        }
        if (cellCount <= LOD_SETTINGS.midCellLimit)
        {
            return new TerrainLodSettings
            {
                level = 1,
                microLimit = LOD_SETTINGS.maxMicroMid,
                waterWaveLimit = lodLimit(cellCount, 240, 6, 60),
                bubbleLimit = lodLimit(cellCount, 520, 3, 28),
                rockLimit = lodLimit(cellCount, 390, 6, 45),
                grassLimit = lodLimit(cellCount, 3600, 2, 8),
                // Same screen-relative rule at the mid tier, on its own proven envelope (about 60 % of dense).
                birdLimit = lodLimit(cellCount, 1050, 4, 20),
                lilyLimit = lodLimit(cellCount, 150, 8, 66),
                fishLimit = lodLimit(cellCount, 190, 6, 54),
                reedLimit = lodLimit(cellCount, 110, 14, 110),
                butterflyLimit = lodLimit(cellCount, 880, 5, 24),
                rockGrassLimit = lodLimit(cellCount, 320, 6, 45),
                fireflyLimit = lodLimit(cellCount, 1100, 2, 14),
                grassPatchLimit = lodLimit(cellCount, 42, 18, 240),
                treeLimit = lodLimit(cellCount, 560, 3, 20),
                wallStrandLimit = lodLimit(cellCount, 520, 4, 24),
            };
        }
        return new TerrainLodSettings
        {
            level = 2,
            microLimit = LOD_SETTINGS.maxMicroLarge,
            waterWaveLimit = lodLimit(cellCount, 420, 4, 38),
            bubbleLimit = lodLimit(cellCount, 840, 2, 18),
            rockLimit = lodLimit(cellCount, 780, 4, 28),
            grassLimit = lodLimit(cellCount, 6200, 1, 6),
            birdLimit = lodLimit(cellCount, 9500, 1, 3),
            lilyLimit = lodLimit(cellCount, 700, 2, 16),
            fishLimit = lodLimit(cellCount, 900, 2, 13),
            reedLimit = lodLimit(cellCount, 620, 3, 24),
            butterflyLimit = lodLimit(cellCount, 4200, 1, 4),
            rockGrassLimit = lodLimit(cellCount, 560, 4, 26),
            fireflyLimit = lodLimit(cellCount, 1900, 1, 8),
            grassPatchLimit = lodLimit(cellCount, 96, 10, 120),
            treeLimit = lodLimit(cellCount, 920, 2, 12),
            wallStrandLimit = lodLimit(cellCount, 900, 2, 14),
        };
    }

    // type TerrainEdgePredicate = (cell: TerrainCell, edge: TerrainEdge) => boolean  →  Func<TerrainCell, TerrainEdge, bool>

    private static List<TerrainEdgeRun> buildTerrainEdgeRuns(
        MaterializedTerrain terrain,
        IReadOnlyList<TerrainMaterial> materials,
        Func<TerrainCell, TerrainEdge, bool>? edgePredicate = null,
        List<TerrainEdgeRun>? reuse = null)
    {
        edgePredicate ??= shouldRenderTerrainDepthEdge;
        var runs = new List<TerrainEdgeRun>();
        // A Chasm contact can contribute two materially independent faces (normal terrain above zero, abyss below).
        // Iterate both possible model segments so no renderer has to infer or re-mix that height contract.
        for (int segmentIndex = 0; segmentIndex < 2; segmentIndex++)
        {
            foreach (TerrainDirection direction in TerrainDirections)
            {
                if (direction.key == "n" || direction.key == "s")
                {
                    for (int y = 0; y < terrain.height; y++)
                    {
                        int x = 0;
                        while (x < terrain.width)
                        {
                            TerrainCell start = terrain.cells[tileIndex(terrain.width, x, y)];
                            if (!edgeRunEligible(start, start.edges[direction.key]!, edgePredicate, segmentIndex))
                            {
                                x++;
                                continue;
                            }
                            TerrainEdgeRun? reusable = reuse != null && runs.Count < reuse.Count ? reuse[runs.Count] : null;
                            List<TerrainCell> cells = reusable?.cells ?? new List<TerrainCell>();
                            cells.Clear();
                            cells.push(start);
                            int endX = x + 1;
                            while (endX < terrain.width)
                            {
                                TerrainCell next = terrain.cells[tileIndex(terrain.width, endX, y)];
                                if (
                                    !sameEdgeRun(
                                        start,
                                        next,
                                        start.edges[direction.key]!,
                                        next.edges[direction.key]!,
                                        direction.key,
                                        materials[start.id],
                                        materials[next.id],
                                        edgePredicate,
                                        segmentIndex)
                                )
                                    break;
                                cells.push(next);
                                endX++;
                            }
                            runs.push(
                                makeTerrainEdgeRun(
                                    direction.key,
                                    cells,
                                    materials[start.id],
                                    start.edges[direction.key]!,
                                    segmentIndex,
                                    reusable));
                            x = endX;
                        }
                    }
                }
                else
                {
                    for (int x = 0; x < terrain.width; x++)
                    {
                        int y = 0;
                        while (y < terrain.height)
                        {
                            TerrainCell start = terrain.cells[tileIndex(terrain.width, x, y)];
                            if (!edgeRunEligible(start, start.edges[direction.key]!, edgePredicate, segmentIndex))
                            {
                                y++;
                                continue;
                            }
                            TerrainEdgeRun? reusable = reuse != null && runs.Count < reuse.Count ? reuse[runs.Count] : null;
                            List<TerrainCell> cells = reusable?.cells ?? new List<TerrainCell>();
                            cells.Clear();
                            cells.push(start);
                            int endY = y + 1;
                            while (endY < terrain.height)
                            {
                                TerrainCell next = terrain.cells[tileIndex(terrain.width, x, endY)];
                                if (
                                    !sameEdgeRun(
                                        start,
                                        next,
                                        start.edges[direction.key]!,
                                        next.edges[direction.key]!,
                                        direction.key,
                                        materials[start.id],
                                        materials[next.id],
                                        edgePredicate,
                                        segmentIndex)
                                )
                                    break;
                                cells.push(next);
                                endY++;
                            }
                            runs.push(
                                makeTerrainEdgeRun(
                                    direction.key,
                                    cells,
                                    materials[start.id],
                                    start.edges[direction.key]!,
                                    segmentIndex,
                                    reusable));
                            y = endY;
                        }
                    }
                }
            }
        }
        return runs.sort((a, b) =>
        {
            double bySortY = a.sortY - b.sortY;
            return Js.Truthy(bySortY) ? bySortY : a.sortX - b.sortX;
        });
    }

    private static bool edgeRunEligible(
        TerrainCell cell,
        TerrainEdge edge,
        Func<TerrainCell, TerrainEdge, bool> edgePredicate,
        int segmentIndex) =>
        edgePredicate(cell, edge) && segmentIndex < edge.faceSegments.Count;

    private static bool sameEdgeRun(
        TerrainCell first,
        TerrainCell next,
        TerrainEdge firstEdge,
        TerrainEdge nextEdge,
        string _direction,
        TerrainMaterial firstMaterial,
        TerrainMaterial nextMaterial,
        Func<TerrainCell, TerrainEdge, bool> edgePredicate,
        int segmentIndex)
    {
        if (!edgePredicate(next, nextEdge)) return false;
        TerrainFaceSegment? a = segmentIndex < firstEdge.faceSegments.Count ? firstEdge.faceSegments[segmentIndex] : null;
        TerrainFaceSegment? b = segmentIndex < nextEdge.faceSegments.Count ? nextEdge.faceSegments[segmentIndex] : null;
        return a != null &&
            b != null &&
            first.type == next.type &&
            firstEdge.contactType == nextEdge.contactType &&
            a.material == b.material &&
            a.role == b.role &&
            firstMaterial.id == nextMaterial.id &&
            // Authored mixed-theme maps can legitimately reuse a semantic material id (for example `wall:3`) with a
            // different palette. Never merge that boundary into one run or the first cell's colour would bleed across
            // the painted theme edge. Exact comparisons retain the original fast merge for cached single-theme runs.
            firstMaterial.top == nextMaterial.top &&
            firstMaterial.topLight == nextMaterial.topLight &&
            firstMaterial.topDark == nextMaterial.topDark &&
            firstMaterial.side == nextMaterial.side &&
            firstMaterial.edgeLight == nextMaterial.edgeLight &&
            firstMaterial.edgeDark == nextMaterial.edgeDark &&
            firstMaterial.detail == nextMaterial.detail &&
            Math.round(a.fromZ * 20) == Math.round(b.fromZ * 20) &&
            Math.round(a.toZ * 20) == Math.round(b.toZ * 20) &&
            Math.round(a.drop * 20) == Math.round(b.drop * 20);
    }

    private static TerrainEdgeRun makeTerrainEdgeRun(
        string direction,
        List<TerrainCell> cells,
        TerrainMaterial material,
        TerrainEdge edge,
        int segmentIndex,
        TerrainEdgeRun? reuse = null)
    {
        TerrainCell first = cells[0];
        TerrainCell last = cells[cells.Count - 1];
        TerrainFaceSegment segment = edge.faceSegments[segmentIndex];
        TerrainEdgeRun run = reuse ?? new TerrainEdgeRun();
        run.direction = direction;
        run.cells = cells;
        run.material = segment.role == "chasm" ? STANDARD_TERRAIN_MATERIALS.chasm : material;
        run.edgeMaterial = segment.material;
        run.fromZ = segment.fromZ;
        run.toZ = segment.toZ;
        run.drop = segment.drop;
        run.faceRole = segment.role;
        run.visibility = depthFaceVisibility(edge);
        run.seed = (double)first.id * 131 + (double)last.id * 17 + (int)direction[0];
        run.relief = createEdgeReliefProfile(
            direction,
            cells,
            material,
            segment.material,
            segment.drop,
            reuse?.relief);
        run.sortX = first.x;
        run.sortY = direction == "s" ? last.y + 1 : first.y;
        return run;
    }

    private static TerrainEdgeReliefProfile createEdgeReliefProfile(
        string direction,
        IReadOnlyList<TerrainCell> cells,
        TerrainMaterial material,
        string edgeMaterial,
        double edgeDrop,
        TerrainEdgeReliefProfile? reuse = null)
    {
        TerrainCell first = cells[0];
        TerrainCell last = cells[cells.Count - 1];
        double seed = (double)first.id * 131 + (double)last.id * 17 + (int)direction[0];
        bool rock =
            edgeMaterial == "rock" ||
            material.id.StartsWith("wall", StringComparison.Ordinal) ||
            cells.some((cell) => cell.type == TileType.Solid);
        double height = clamp(edgeDrop / 4, 0, 1);
        double length = clamp((double)cells.Count / 10, 0, 1);
        double rough = material.roughness;
        TerrainEdgeReliefProfile relief = reuse ?? new TerrainEdgeReliefProfile();
        relief.crest = clamp(
            0.55 + height * 0.26 + rough * 0.22 + hashTerrainCell(seed + 11) * 0.18,
            0.42,
            1);
        relief.undercut = clamp(
            0.42 + height * 0.42 + (rock ? 0.16 : 0) + hashTerrainCell(seed + 17) * 0.18,
            0.32,
            1);
        relief.strata = clamp(
            0.28 + height * 0.42 + length * 0.2 + hashTerrainCell(seed + 23) * 0.22,
            0.18,
            1);
        relief.fracture = clamp(
            0.26 + rough * 0.34 + height * 0.28 + hashTerrainCell(seed + 29) * 0.24,
            0.18,
            1);
        relief.chip = clamp(
            0.22 + rough * 0.26 + (rock ? 0.18 : 0.08) + hashTerrainCell(seed + 31) * 0.22,
            0.16,
            1);
        return relief;
    }

    private static int cardinalWalkableContactCount(TerrainCell cell)
    {
        int count = 0;
        foreach (TerrainDirection direction in TerrainDirections)
        {
            int contact = cell.edges[direction.key]!.contactType;
            if (contact != TERRAIN_CONTACT_OUTSIDE && isWalkable(contact)) count++;
        }
        return count;
    }

    private static List<string> terrainWallContactDirections(TerrainCell cell)
    {
        var @out = new List<string>();
        foreach (TerrainDirection direction in TerrainDirections)
        {
            TerrainEdge edge = cell.edges[direction.key]!;
            if (edge.contactType != TileType.Solid) continue;
            if (edge.passable && edge.drop <= 0.18) continue;
            @out.push(direction.key);
        }
        return @out;
    }

    private static string pickTerrainDirection(IReadOnlyList<string> directions, double salt)
    {
        if (directions.Count == 0) return "n";
        return directions[(int)Math.floor(hashTerrainCell(salt) * directions.Count) % directions.Count];
    }

    /// <summary>`[0, 0.34, -0.34, 0.68, -0.68, Math.PI] as const` (never written; shared across threads).</summary>
    private static readonly IReadOnlyList<double> ROLLING_GRASS_ROUTE_OFFSETS =
        Array.AsReadOnly(new[] { 0, 0.34, -0.34, 0.68, -0.68, Math.PI });

    /// <summary>A terrain-valid crosswind route for one tumbleweed. It never rolls through water, rock or a chasm.</summary>
    private static TerrainCell? rollingGrassRoute(
        MaterializedTerrain terrain,
        TerrainCell start,
        double salt)
    {
        double baseAngle = hashTerrainCell(salt + 3) * Math.PI * 2;
        IReadOnlyList<double> offsets = ROLLING_GRASS_ROUTE_OFFSETS;
        for (int attempt = 0; attempt < offsets.Count; attempt++)
        {
            double angle = baseAngle + offsets[attempt];
            int distance = 3 + (int)Math.floor(hashTerrainCell(salt + 11 + attempt * 17) * 5);
            int tx = (int)Math.round(start.x + Math.cos(angle) * distance);
            int ty = (int)Math.round(start.y + Math.sin(angle) * distance);
            TerrainCell? target = terrainCellAt(terrain, tx, ty);
            if (target == null || target.type != TileType.Floor || !target.walkable) continue;
            if (Math.abs(target.surfaceZ - start.surfaceZ) > 1) continue;
            bool clear = true;
            int samples = Math.max(6, distance * 2);
            double previousZ = start.surfaceZ;
            for (int i = 1; i <= samples; i++)
            {
                double t = (double)i / samples;
                TerrainCell? cell = terrainCellAt(
                    terrain,
                    (int)Math.round(start.x + (target.x - start.x) * t),
                    (int)Math.round(start.y + (target.y - start.y) * t));
                if (
                    cell == null ||
                    cell.type != TileType.Floor ||
                    !cell.walkable ||
                    Math.abs(cell.surfaceZ - previousZ) > 1
                )
                {
                    clear = false;
                    break;
                }
                previousZ = cell.surfaceZ;
            }
            if (clear) return target;
        }
        return null;
    }

    private static bool guaranteesTerrainTrees(string? biomeKey)
    {
        switch (biomeKey)
        {
            case "highland_pass":
            case "noir_sprawl":
            case "olympian_sky_borough":
            case "sakura_temple_dream":
            case "abyssal_deepsea":
            case "rainbowland":
            case "clockwork_moon_bazaar":
            case "sugarstorm_carnival":
            case "prismglass_archive":
            case "starforged_cathedral_endrun":
            case "raid_thousandfolds": // the folded paper mountain always grows its temple pines
            case "raid_crownbower": // the Crown Bower is suspended in giant tropical windfan crowns
            case "alien_ranch": // a ranch is farmed land — its engineered orchards never thin out entirely
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Resolve the readable dressing statement at one absolute world cell. The caller may pass its already sampled
    /// composition record so the terrain worker does not repeat the Voronoi search in its cell hot path.
    ///
    /// Undergrowth belongs to woodland twice over: it is thickest in a grove's core, and it is most legible along a
    /// wood's broken FRINGE, which is also where the stumps of felled trees stand. So the strength is the stronger
    /// of those two readings rather than a choice between them.
    /// </summary>
    public static TerrainDressingPatchSample terrainDressingPatchAt(
        double worldCellX,
        double worldCellY,
        string? biomeKey = null,
        TerrainCompositionSample? compositionOverride = null,
        TerrainDressingPatchSample? @out = null)
    {
        // Default parameter `{ id: 0, strength: 0, edge: 0, direction: 0 }`.
        @out ??= new TerrainDressingPatchSample();
        TerrainCompositionSample composition = compositionOverride ?? terrainCompositionAt(worldCellX, worldCellY, biomeKey);
        double forestEdge =
            (1 - clamp(Math.abs(composition.grove - 0.5) / 0.42, 0, 1)) *
            (0.7 + composition.landmark * 0.3);
        double core = composition.grove * (0.74 + (1 - composition.clearing) * 0.26);
        double fringe = forestEdge * 0.62 + composition.grove * 0.2;
        @out.id = composition.patchId;
        @out.strength = clamp(Math.max(core, fringe), 0, 1);
        @out.edge = clamp(forestEdge, 0, 1);
        @out.direction = composition.direction;
        return @out;
    }

    /// <summary>The `limits` record of createTerrainProceduralEffects.</summary>
    private sealed class TerrainProceduralEffectLimits
    {
        public int waterWaves;
        public int bubbles;
        public int rockFalls;
        public int rollingGrass;
        public int birds;
        public int lilyPads;
        public int fishShoals;
        public int reedBeds;
        public int butterflies;
        public int rockGrass;
        public int fireflies;
        public int grassPatches;
        public int trees;
        public int wallStrands;
    }

    private sealed class TerrainTreeCandidate
    {
        public TerrainCell cell = null!;
        public double score;
        public double forestStrength;
        public double patchId;
        public bool floorAnchor;
    }

    private sealed class TerrainGrassCandidate
    {
        public TerrainCell cell = null!;
        public double score;
        public TerrainGrassPatchEffect effect = null!;
    }

    private sealed class TerrainMistCandidate
    {
        public TerrainCell cell = null!;
        public double priority;
    }

    private static TerrainProceduralEffects createTerrainProceduralEffects(
        MaterializedTerrain? terrain,
        TerrainRenderPlan renderPlan,
        double effectSeed = 0,
        string? biomeKey = null,
        double originCellX = 0,
        double originCellY = 0,
        TerrainCellCompositionPlan? compositionCache = null)
    {
        compositionCache ??= createTerrainCellCompositionPlan(terrain?.cells.Length ?? 0);
        if (terrain == null) return emptyTerrainEffects();
        bool hubSanctuary = biomeKey == "hub";
        bool alpineMeadow = biomeKey == "highland_pass";
        bool olympianBorough = biomeKey == "olympian_sky_borough";
        bool rainbowland = biomeKey == "rainbowland";
        bool sakuraTemple = biomeKey == "sakura_temple_dream";
        bool cityCanopy = biomeKey == "noir_sprawl";
        bool abyssalDeepsea = biomeKey == "abyssal_deepsea";
        bool sugarstormCarnival = biomeKey == "sugarstorm_carnival";
        bool clockworkMoon = biomeKey == "clockwork_moon_bazaar";
        bool prismglassArchive = biomeKey == "prismglass_archive";
        bool starforgedEndrun = biomeKey == "starforged_cathedral_endrun";
        // The five sealed raids + the PvP arena (descriptor biome keys) — each wears its own dressing identity
        // instead of falling into the shared default densities.
        bool droneFoundry = biomeKey == "raid_verdant"; // The Drone Foundry — industrial machine room
        bool tideCage = biomeKey == "raid_tidecage"; // The Tide Cage — drowned ring-fortress
        bool paperTemple = biomeKey == "raid_thousandfolds"; // The Thousand Folds — serene paper mountain
        bool wyrmforge = biomeKey == "raid_wyrmforge"; // The Wyrmforge — volcanic blackstone citadel
        bool aegisCitadel = biomeKey == "raid_holdthefort"; // Hold the Fort — austere White Citadel
        bool arenaCanyon = biomeKey == "arena"; // the volcanic PvP canyon — barren, dusty, rockfall-scoured
        bool guaranteeTrees = guaranteesTerrainTrees(biomeKey);
        bool allowFloorTreePatches = guaranteeTrees && !(biomeKey?.StartsWith("raid_", StringComparison.Ordinal) ?? false);
        TerrainAmbientThemeProfile ambientProfile = TerrainAmbient.terrainAmbientThemeProfile(biomeKey);
        TerrainLodSettings lod = renderPlan.lod ?? createTerrainLodSettings(terrain.cells.Length);
        var limits = new TerrainProceduralEffectLimits
        {
            waterWaves = lod.waterWaveLimit,
            bubbles = lod.bubbleLimit,
            rockFalls = lod.rockLimit,
            rollingGrass = TerrainAmbient.ambientAllowsGrowth(ambientProfile) ? lod.grassLimit : 0,
            birds = lod.birdLimit,
            lilyPads = lod.lilyLimit,
            fishShoals = TerrainAmbient.ambientAllowsShoals(ambientProfile) ? lod.fishLimit : 0,
            reedBeds = lod.reedLimit,
            butterflies = TerrainAmbient.ambientAllowsFlutterers(ambientProfile) ? lod.butterflyLimit : 0,
            rockGrass = aegisCitadel ? 0 : lod.rockGrassLimit,
            fireflies = lod.fireflyLimit,
            grassPatches = aegisCitadel
                ? 0
                : arenaCanyon
                    ? Math.min(1, lod.grassPatchLimit)
                    : lod.grassPatchLimit,
            trees = lod.treeLimit,
            wallStrands = lod.wallStrandLimit,
        };

        int seedSalt = Math.imul(Js.ToInt32(effectSeed), 131);
        int ecologySalt = terrainEcologyBiomeSalt(biomeKey);
        double seededCell(TerrainCell cell, int multiplier, int salt) =>
            hashTerrainCell((double)cell.id * multiplier + salt + seedSalt);
        double ecologyAt(TerrainCell cell) =>
            terrainEcologyPatchWithSalt(originCellX + cell.x, originCellY + cell.y, ecologySalt);
        TerrainProceduralEffects effects = emptyTerrainEffects();
        var compositionScratch = new TerrainCompositionSample
        {
            grove = 0,
            clearing = 0,
            landmark = 0,
            focus = 0,
            quiet = 0,
            direction = 0,
            patchId = 0,
            patchRoll = 0,
        };
        var dressingPatchScratch = new TerrainDressingPatchSample
        {
            id = 0,
            strength = 0,
            edge = 0,
            direction = 0,
        };
        var treeCandidates = new List<TerrainTreeCandidate>();
        var grassCandidates = new List<TerrainGrassCandidate>();
        TerrainCell? fallbackTree = null;
        double fallbackTreeScore = 2;
        void addTreeEffect(TerrainCell cell, bool floorAnchor = false)
        {
            // `{ id, ox, oy, ...(floorAnchor ? { floorAnchor: true } : {}), ...createTreeVisual(…) }`
            double ox = 0.28 + seededCell(cell, 421, 19) * 0.44;
            double oy = 0.24 + seededCell(cell, 431, 23) * 0.48;
            effects.trees.push(new TerrainTreeDressingEffect(
                TreeVisualModule.createTreeVisual(seedSalt + (double)cell.id * 487, biomeKey))
            {
                id = cell.id,
                ox = ox,
                oy = oy,
                floorAnchor = floorAnchor ? true : null,
            });
        }

        // Pick cloud banks by a deterministic global rank instead of stopping after the first N candidates. Only
        // genuine four-neighbour core cells qualify: broad mist rooted on a perimeter cell reads as a pale horizontal
        // shelf and can spill past a narrow lip under the oblique camera. Core-only, lower-shaft wisps remain clipped
        // by real rock, leave most of the aperture black, and still reveal several physical depth layers.
        List<TerrainMistCandidate> mistCandidates = terrain.cells
            .filter(
                (cell) =>
                    cell.type == TileType.Chasm &&
                    cell.edges.everyValue((edge) => edge.contactType == TileType.Chasm))
            .map((cell) => new TerrainMistCandidate { cell = cell, priority = seededCell(cell, 1009, 367) })
            .sort((a, b) =>
            {
                double byPriority = a.priority - b.priority;
                return Js.Truthy(byPriority) ? byPriority : a.cell.id - b.cell.id;
            });
        int mistLimit = (int)clamp(
            Math.ceil((double)mistCandidates.Count / TERRAIN_CHASM_MIST_CELLS_PER_BANK),
            0,
            TERRAIN_CHASM_MIST_MAX_BANKS);
        foreach (TerrainMistCandidate mistCandidate in mistCandidates.slice(0, mistLimit))
        {
            TerrainCell cell = mistCandidate.cell;
            double ox = 0.24 + seededCell(cell, 1013, 373) * 0.52;
            int chasmCellsWest = 0;
            int chasmCellsEast = 0;
            while (terrainCellAt(terrain, cell.x - chasmCellsWest - 1, cell.y)?.type == TileType.Chasm)
                chasmCellsWest++;
            while (terrainCellAt(terrain, cell.x + chasmCellsEast + 1, cell.y)?.type == TileType.Chasm)
                chasmCellsEast++;
            double horizontalClearance = Math.min(ox + chasmCellsWest, 1 - ox + chasmCellsEast);
            // The renderer's broadest far wisp uses 1.08x this radius, its irregular feather ring reaches another
            // ~10%, and animated sway needs a slim residual gutter. Binding the authoring radius to the genuine Chasm
            // run prevents a large bank from brightening a straight wall in a narrow throat; broad components retain
            // their full generated span.
            double maximumContainedRadiusX = Math.max(0.55, (horizontalClearance - 0.12) / 1.22);
            effects.chasmMist.push(new TerrainChasmMistEffect
            {
                id = cell.id,
                ox = ox,
                oy = 0.24 + seededCell(cell, 1019, 379) * 0.52,
                radiusX = Math.min(1.15 + seededCell(cell, 1021, 383) * 1.1, maximumContainedRadiusX),
                radiusY = 0.42 + seededCell(cell, 1031, 389) * 0.2,
                height =
                    TERRAIN_CHASM_MIST_MIN_HEIGHT +
                    seededCell(cell, 1033, 397) *
                        (TERRAIN_CHASM_MIST_MAX_HEIGHT - TERRAIN_CHASM_MIST_MIN_HEIGHT),
                phase = seededCell(cell, 1039, 401),
                drift = 0.035 + seededCell(cell, 1049, 409) * 0.055,
                alpha =
                    TERRAIN_CHASM_MIST_MIN_ALPHA +
                    seededCell(cell, 1051, 419) * (TERRAIN_CHASM_MIST_MAX_ALPHA - TERRAIN_CHASM_MIST_MIN_ALPHA),
            });
        }

        foreach (TerrainCell cell in terrain.cells)
        {
            double h = hashTerrainCell((double)cell.id * 17 + 3 + seedSalt);
            if (cell.type == TileType.Water)
            {
                TerrainWaterInfo waterInfo =
                    (renderPlan.water != null && (uint)cell.id < (uint)renderPlan.water.Length
                        ? renderPlan.water[cell.id]
                        : null) ?? waterInfoForCell(cell, terrain);
                // Lily pads gather on CALM, deeper water away from the churn — a bobbing leaf, some with a blossom.
                if (
                    effects.lilyPads.Count < limits.lilyPads &&
                    waterInfo.calm &&
                    waterInfo.depth > 0.4 &&
                    seededCell(cell, 211, 9) <
                        (tideCage
                            ? 0.42
                            : aegisCitadel
                                ? 0
                                : droneFoundry
                                    ? 0
                                    : arenaCanyon
                                        ? 0
                                        : sakuraTemple
                                            ? 0.28
                                            : prismglassArchive
                                                ? 0.3
                                                : clockworkMoon
                                                    ? 0
                                                    : starforgedEndrun
                                                        ? 0
                                                        : abyssalDeepsea
                                                            ? 0.24
                                                            : sugarstormCarnival
                                                                ? 0.22
                                                                : 0.2)
                )
                {
                    effects.lilyPads.push(new TerrainLilyPadEffect
                    {
                        id = cell.id,
                        ox = 0.22 + seededCell(cell, 223, 11) * 0.56,
                        oy = 0.22 + seededCell(cell, 227, 13) * 0.56,
                        radius =
                            4.4 +
                            seededCell(cell, 229, 17) *
                                (sakuraTemple
                                    ? 3.6
                                    : prismglassArchive
                                        ? 2.6
                                        : abyssalDeepsea
                                            ? 3.2
                                            : sugarstormCarnival
                                                ? 5.2
                                                : 4.6),
                        phase = seededCell(cell, 233, 19),
                        bob = 0.5 + seededCell(cell, 239, 23) * 0.8,
                        notchAngle = seededCell(cell, 241, 29) * Math.PI * 2,
                        blossom =
                            seededCell(cell, 251, 31) <
                            (sakuraTemple
                                ? 0.62
                                : prismglassArchive
                                    ? 0.56
                                    : abyssalDeepsea
                                        ? 0.48
                                        : sugarstormCarnival
                                            ? 0.7
                                            : 0.3),
                        cluster = 1 + (int)Math.floor(seededCell(cell, 253, 37) * (sakuraTemple ? 3 : 2)),
                        bloomHue = seededCell(cell, 257, 41),
                    });
                }
                if (
                    effects.fishShoals.Count < limits.fishShoals &&
                    waterInfo.calm &&
                    waterInfo.depth > 0.46 &&
                    hashTerrainCell((double)cell.id * 607 + 149 + seedSalt) <
                        (waterInfo.shoreline == 0
                            ? tideCage
                                ? 0.32
                                : aegisCitadel
                                    ? 0
                                    : paperTemple
                                        ? 0
                                        : arenaCanyon
                                            ? 0
                                            : sakuraTemple
                                                ? 0.2
                                                : prismglassArchive
                                                    ? 0
                                                    : clockworkMoon
                                                        ? 0
                                                        : starforgedEndrun
                                                            ? 0
                                                            : abyssalDeepsea
                                                                ? 0.08
                                                                : sugarstormCarnival
                                                                    ? 0.1
                                                                    : 0.16
                            : tideCage
                                ? 0.18
                                : aegisCitadel
                                    ? 0
                                    : paperTemple
                                        ? 0
                                        : arenaCanyon
                                            ? 0
                                            : sakuraTemple
                                                ? 0.11
                                                : prismglassArchive
                                                    ? 0
                                                    : clockworkMoon
                                                        ? 0
                                                        : starforgedEndrun
                                                            ? 0
                                                            : abyssalDeepsea
                                                                ? 0.04
                                                                : sugarstormCarnival
                                                                    ? 0.06
                                                                    : 0.08)
                )
                {
                    effects.fishShoals.push(new TerrainFishShoalEffect
                    {
                        id = cell.id,
                        ox = 0.2 + hashTerrainCell((double)cell.id * 613 + 151 + seedSalt) * 0.6,
                        oy = 0.25 + hashTerrainCell((double)cell.id * 617 + 157 + seedSalt) * 0.5,
                        count =
                            2 +
                            (int)Math.floor(hashTerrainCell((double)cell.id * 619 + 163 + seedSalt) * (sakuraTemple ? 2 : 3)),
                        length =
                            4.2 +
                            hashTerrainCell((double)cell.id * 631 + 167 + seedSalt) *
                                (sakuraTemple ? 3.2 : abyssalDeepsea ? 5.2 : sugarstormCarnival ? 4.8 : 4.4),
                        radius = 0.18 + hashTerrainCell((double)cell.id * 641 + 173 + seedSalt) * 0.24,
                        angle =
                            Math.atan2(waterInfo.flow.y, orNumber(waterInfo.flow.x, 0.001)) +
                            (hashTerrainCell((double)cell.id * 643 + 179 + seedSalt) - 0.5) * 1.2,
                        phase = hashTerrainCell((double)cell.id * 647 + 181 + seedSalt),
                        speed =
                            0.05 +
                            hashTerrainCell((double)cell.id * 653 + 191 + seedSalt) *
                                (sakuraTemple
                                    ? 0.08
                                    : prismglassArchive
                                        ? 0.04
                                        : abyssalDeepsea
                                            ? 0.07
                                            : sugarstormCarnival
                                                ? 0.06
                                                : 0.13),
                        depth = waterInfo.depth,
                        alpha = 0.22 + hashTerrainCell((double)cell.id * 659 + 193 + seedSalt) * 0.18,
                    });
                }
                if (
                    effects.reedBeds.Count < limits.reedBeds &&
                    waterInfo.shoreline > 0 &&
                    waterInfo.shoreline < 4 &&
                    hashTerrainCell((double)cell.id * 661 + 197 + seedSalt) <
                        GROUND_COVER_DENSITY *
                            (tideCage
                                ? 0.5
                                : aegisCitadel
                                    ? 0
                                    : droneFoundry
                                        ? 0.04
                                        : arenaCanyon
                                            ? 0.05
                                            : sakuraTemple
                                                ? 0.15
                                                : prismglassArchive
                                                    ? 0.3
                                                    : clockworkMoon
                                                        ? 0
                                                        : starforgedEndrun
                                                            ? 0
                                                            : abyssalDeepsea
                                                                ? 0.34
                                                                : sugarstormCarnival
                                                                    ? 0.32
                                                                    : 0.22)
                )
                {
                    effects.reedBeds.push(new TerrainReedBedEffect
                    {
                        id = cell.id,
                        ox = 0.14 + hashTerrainCell((double)cell.id * 673 + 199 + seedSalt) * 0.72,
                        oy = 0.14 + hashTerrainCell((double)cell.id * 677 + 211 + seedSalt) * 0.72,
                        blades =
                            4 +
                            (int)Math.floor(
                                hashTerrainCell((double)cell.id * 683 + 223 + seedSalt) *
                                    (sakuraTemple
                                        ? 3
                                        : prismglassArchive
                                            ? 3
                                            : abyssalDeepsea
                                                ? 6
                                                : sugarstormCarnival
                                                    ? 5
                                                    : 5)),
                        height =
                            6 +
                            hashTerrainCell((double)cell.id * 691 + 227 + seedSalt) *
                                (sakuraTemple
                                    ? 5
                                    : prismglassArchive
                                        ? 7
                                        : abyssalDeepsea
                                            ? 10
                                            : sugarstormCarnival
                                                ? 8
                                                : 8),
                        phase = hashTerrainCell((double)cell.id * 701 + 229 + seedSalt) * Math.PI * 2,
                        sway = 0.35 + hashTerrainCell((double)cell.id * 709 + 233 + seedSalt) * 0.85,
                        clumps = 1 + (int)Math.floor(hashTerrainCell((double)cell.id * 711 + 235 + seedSalt) * 3),
                        seedHeads = clamp(0.18 + hashTerrainCell((double)cell.id * 713 + 237 + seedSalt) * 0.62, 0, 1),
                        stiffness = clamp(
                            (sakuraTemple ? 0.34 : abyssalDeepsea ? 0.18 : 0.42) +
                                hashTerrainCell((double)cell.id * 717 + 238 + seedSalt) * 0.34,
                            0,
                            1),
                        alpha = 0.34 + hashTerrainCell((double)cell.id * 719 + 239 + seedSalt) * 0.2,
                    });
                }
                if (
                    effects.waterWaves.Count < limits.waterWaves &&
                    h <
                        (waterInfo.shoreline != 0
                            ? sakuraTemple
                                ? 0.16
                                : prismglassArchive
                                    ? 0.24
                                    : clockworkMoon
                                        ? 0.34
                                        : starforgedEndrun
                                            ? 0.34
                                            : abyssalDeepsea
                                                ? 0.3
                                                : sugarstormCarnival
                                                    ? 0.34
                                                    : 0.24
                            : sakuraTemple
                                ? 0.1
                                : prismglassArchive
                                    ? 0.18
                                    : clockworkMoon
                                        ? 0.22
                                        : starforgedEndrun
                                            ? 0.22
                                            : abyssalDeepsea
                                                ? 0.2
                                                : sugarstormCarnival
                                                    ? 0.24
                                                    : 0.14)
                )
                {
                    effects.waterWaves.push(new TerrainWaterWaveEffect
                    {
                        id = cell.id,
                        ox = 0.2 + seededCell(cell, 31, 5) * 0.6,
                        oy = 0.2 + seededCell(cell, 37, 7) * 0.6,
                        phase = seededCell(cell, 41, 11),
                        speed =
                            (sakuraTemple
                                ? 0.12
                                : prismglassArchive
                                    ? 0.1
                                    : clockworkMoon
                                        ? 0.06
                                        : starforgedEndrun
                                            ? 0.07
                                            : sugarstormCarnival
                                                ? 0.08
                                                : 0.22) +
                            waterInfo.flowSpeed *
                                (sakuraTemple
                                    ? 0.18
                                    : prismglassArchive
                                        ? 0.08
                                        : clockworkMoon
                                            ? 0.1
                                            : starforgedEndrun
                                                ? 0.08
                                                : abyssalDeepsea
                                                    ? 0.16
                                                    : sugarstormCarnival
                                                        ? 0.12
                                                        : 0.36) +
                            seededCell(cell, 43, 13) *
                                (sakuraTemple
                                    ? 0.12
                                    : prismglassArchive
                                        ? 0.07
                                        : clockworkMoon
                                            ? 0.07
                                            : starforgedEndrun
                                                ? 0.06
                                                : abyssalDeepsea
                                                    ? 0.1
                                                    : sugarstormCarnival
                                                        ? 0.08
                                                        : 0.26),
                        radius =
                            3.2 +
                            waterInfo.depth * 5.2 +
                            seededCell(cell, 47, 17) *
                                (sakuraTemple
                                    ? 4.2
                                    : prismglassArchive
                                        ? 4.2
                                        : clockworkMoon
                                            ? 4.6
                                            : starforgedEndrun
                                                ? 7.2
                                                : abyssalDeepsea
                                                    ? 6.8
                                                    : 5.8),
                        angle =
                            Math.atan2(waterInfo.flow.y, orNumber(waterInfo.flow.x, 0.001)) * 0.25 +
                            (seededCell(cell, 53, 19) - 0.5) * 0.28,
                        alpha =
                            (sakuraTemple
                                ? 0.035
                                : prismglassArchive
                                    ? 0.045
                                    : clockworkMoon
                                        ? 0.085
                                        : starforgedEndrun
                                            ? 0.08
                                            : sugarstormCarnival
                                                ? 0.075
                                                : 0.06) +
                            waterInfo.depth *
                                (sakuraTemple
                                    ? 0.035
                                    : prismglassArchive
                                        ? 0.04
                                        : clockworkMoon
                                            ? 0.055
                                            : starforgedEndrun
                                                ? 0.06
                                                : abyssalDeepsea
                                                    ? 0.07
                                                    : sugarstormCarnival
                                                        ? 0.06
                                                        : 0.05) +
                            seededCell(cell, 59, 23) *
                                (sakuraTemple
                                    ? 0.045
                                    : prismglassArchive
                                        ? 0.055
                                        : clockworkMoon
                                            ? 0.09
                                            : starforgedEndrun
                                                ? 0.08
                                                : abyssalDeepsea
                                                    ? 0.095
                                                    : sugarstormCarnival
                                                        ? 0.1
                                                        : 0.08),
                    });
                }
                if (
                    effects.bubbles.Count < limits.bubbles &&
                    waterInfo.calm &&
                    waterInfo.depth > 0.48 &&
                    seededCell(cell, 61, 29) <
                        (sakuraTemple
                            ? 0.08
                            : prismglassArchive
                                ? 0.12
                                : clockworkMoon
                                    ? 0.32
                                    : starforgedEndrun
                                        ? 0.12
                                        : abyssalDeepsea
                                            ? 0.42
                                            : sugarstormCarnival
                                                ? 0.3
                                                : 0.18)
                )
                {
                    effects.bubbles.push(new TerrainBubbleEffect
                    {
                        id = cell.id,
                        ox = 0.18 + seededCell(cell, 67, 31) * 0.64,
                        oy = 0.25 + seededCell(cell, 71, 37) * 0.52,
                        phase = seededCell(cell, 73, 41),
                        speed =
                            (prismglassArchive
                                ? 0.08
                                : clockworkMoon
                                    ? 0.06
                                    : starforgedEndrun
                                        ? 0.07
                                        : abyssalDeepsea
                                            ? 0.07
                                            : sugarstormCarnival
                                                ? 0.08
                                                : 0.12) +
                            seededCell(cell, 79, 43) *
                                (prismglassArchive
                                    ? 0.08
                                    : clockworkMoon
                                        ? 0.11
                                        : starforgedEndrun
                                            ? 0.09
                                            : abyssalDeepsea
                                                ? 0.13
                                                : sugarstormCarnival
                                                    ? 0.12
                                                    : 0.18),
                        radius =
                            0.95 +
                            seededCell(cell, 83, 47) *
                                (prismglassArchive
                                    ? 1.2
                                    : clockworkMoon
                                        ? 2
                                        : starforgedEndrun
                                            ? 1.8
                                            : abyssalDeepsea
                                                ? 2.4
                                                : sugarstormCarnival
                                                    ? 2.2
                                                    : 1.85),
                        drift =
                            0.025 +
                            seededCell(cell, 85, 49) *
                                (prismglassArchive
                                    ? 0.035
                                    : clockworkMoon
                                        ? 0.065
                                        : starforgedEndrun
                                            ? 0.055
                                            : abyssalDeepsea
                                                ? 0.085
                                                : sugarstormCarnival
                                                    ? 0.075
                                                    : 0.055),
                        rise =
                            0.18 +
                            seededCell(cell, 87, 51) *
                                (prismglassArchive
                                    ? 0.12
                                    : clockworkMoon
                                        ? 0.22
                                        : starforgedEndrun
                                            ? 0.18
                                            : abyssalDeepsea
                                                ? 0.32
                                                : sugarstormCarnival
                                                    ? 0.24
                                                    : 0.2),
                        wobble = 0.7 + seededCell(cell, 91, 57) * 0.9,
                    });
                }
                continue;
            }

            if (cell.type == TileType.Chasm)
            {
                continue;
            }

            int edgePressure = cell.edges.countValues(
                (edge) => edge.visibleFace || !edge.passable);
            double moisture =
                (uint)cell.id < (uint)renderPlan.moisture.Length ? renderPlan.moisture[cell.id] : 0;
            List<string> wallContacts = terrainWallContactDirections(cell);
            TerrainMaterial? cellMaterial =
                (uint)cell.id < (uint)renderPlan.materials.Length ? renderPlan.materials[cell.id] : null;
            bool paved = cellMaterial?.id.Contains(":pv", StringComparison.Ordinal) ?? false;
            double ecologyPatch = ecologyAt(cell);
            double ecologyEase = ecologyPatch * ecologyPatch * (3 - 2 * ecologyPatch);
            // Range 0.38..1.62 with an expected value near 1.0: clearings give their scatter budget to neighbouring
            // groves instead of increasing the average instance count. Existing LOD limits still cap dense peaks.
            double ecologyGain = 0.38 + ecologyEase * 1.24;
            TerrainCompositionSample composition = terrainCompositionAt(
                originCellX + cell.x,
                originCellY + cell.y,
                biomeKey,
                compositionScratch,
                ecologyPatch);
            double grove = composition.grove;
            double clearing = composition.clearing;
            double landmark = composition.landmark;
            double focus = composition.focus;
            double quiet = composition.quiet;
            double compositionDirection = composition.direction;
            float[] floorRoute = renderPlan.floor.route;
            double routeWear =
                (uint)cell.id < (uint)floorRoute.Length ? floorRoute[cell.id] : composition.routeWear ?? 0;
            float[] floorGrass = renderPlan.floor.grass;
            double floorGrassHabitat = (uint)cell.id < (uint)floorGrass.Length ? floorGrass[cell.id] : 0;
            // Tiny sampled bake regions can land wholly inside the composition corridor, where the strict closed-turf
            // field is legitimately zero. Natural worlds still need a sparse verge there. This bounded fallback is
            // derived from the same grove/ecology hierarchy and remains disabled for the authored barren arena; LOD
            // selection below keeps the exact geometry ceiling unchanged.
            double grassHabitat = arenaCanyon
                ? floorGrassHabitat
                : Math.max(
                    floorGrassHabitat,
                    clamp(
                        grove * 0.2 + ecologyPatch * 0.09 - routeWear * 0.16 - focus * 0.1 - quiet * 0.03,
                        0,
                        0.24));
            TerrainDressingPatchSample dressingPatch = terrainDressingPatchAt(
                originCellX + cell.x,
                originCellY + cell.y,
                biomeKey,
                composition,
                dressingPatchScratch);
            compositionCache.valid[cell.id] = 1;
            compositionCache.grove[cell.id] = composition.grove;
            compositionCache.landmark[cell.id] = composition.landmark;
            compositionCache.focus[cell.id] = composition.focus;
            compositionCache.quiet[cell.id] = composition.quiet;
            compositionCache.direction[cell.id] = composition.direction;
            // Int32Array store: the uint32 patch id wraps to its signed bit pattern.
            compositionCache.patchId[cell.id] = Js.ToInt32(dressingPatch.id);
            compositionCache.patchStrength[cell.id] = dressingPatch.strength;
            compositionCache.patchEdge[cell.id] = dressingPatch.edge;
            double forestStrength = dressingPatch.strength;

            if (cell.type == TileType.Solid)
            {
                int walkableContacts = cardinalWalkableContactCount(cell);
                bool exposedCap = cell.edges.someValue(
                    (edge) => edge.contactType != TileType.Solid || edge.drop > 0.18);
                double treeChance = clamp(
                    (sakuraTemple
                        ? 0.018
                        : cityCanopy
                            ? 0.028
                            : prismglassArchive
                                ? 0.026
                                : clockworkMoon
                                    ? 0.032
                                    : starforgedEndrun
                                        ? 0.026
                                        : 0.01) +
                        walkableContacts *
                            (sakuraTemple
                                ? 0.028
                                : cityCanopy
                                    ? 0.036
                                    : prismglassArchive
                                        ? 0.034
                                        : abyssalDeepsea
                                            ? 0.032
                                            : starforgedEndrun
                                                ? 0.038
                                                : clockworkMoon
                                                    ? 0.04
                                                    : sugarstormCarnival
                                                        ? 0.034
                                                        : 0.018) +
                        moisture *
                            (sakuraTemple
                                ? 0.04
                                : cityCanopy
                                    ? 0.018
                                    : prismglassArchive
                                        ? 0.016
                                        : abyssalDeepsea
                                            ? 0.035
                                            : starforgedEndrun
                                                ? 0.006
                                                : clockworkMoon
                                                    ? 0.018
                                                    : sugarstormCarnival
                                                        ? 0.02
                                                        : 0.025) -
                        cell.elevation *
                            (sakuraTemple
                                ? 0.001
                                : cityCanopy
                                    ? 0.0005
                                    : prismglassArchive
                                        ? 0.001
                                        : abyssalDeepsea
                                            ? 0.0008
                                            : starforgedEndrun
                                                ? 0.0006
                                                : clockworkMoon
                                                    ? 0.0008
                                                    : sugarstormCarnival
                                                        ? 0.0012
                                                        : 0.002),
                    0,
                    sakuraTemple
                        ? 0.14
                        : cityCanopy
                            ? 0.18
                            : prismglassArchive
                                ? 0.18
                                : abyssalDeepsea
                                    ? 0.16
                                    : starforgedEndrun
                                        ? 0.2
                                        : clockworkMoon
                                            ? 0.19
                                            : sugarstormCarnival
                                                ? 0.2
                                                : 0.09);
                bool treeEligible =
                    limits.trees > 0 &&
                    walkableContacts > 0 &&
                    exposedCap &&
                    cell.surfaceZ <
                        MIN_ELEVATION +
                            (guaranteeTrees ? TREE_LINE_DOMAIN_FRACTION : SPARSE_TREE_LINE_DOMAIN_FRACTION) *
                                (ELEVATION_LEVELS - 1) &&
                    clearing < 0.78 &&
                    focus < 0.58 &&
                    quiet < 0.72;
                if (treeEligible && guaranteeTrees)
                {
                    double score =
                        hashTerrainCell((double)cell.id * 907 + 311 + seedSalt) * 0.58 +
                        (1 - ecologyPatch) * 0.16 -
                        forestStrength * 0.26;
                    if (score < fallbackTreeScore)
                    {
                        fallbackTree = cell;
                        fallbackTreeScore = score;
                    }
                }
                if (
                    treeEligible &&
                    (forestStrength > 0.62 ||
                        seededCell(cell, 401, 11) / ecologyGain <
                            treeChance * (0.38 + grove * 0.88 + forestStrength * 1.42))
                )
                    treeCandidates.push(new TerrainTreeCandidate
                    {
                        cell = cell,
                        score =
                            forestStrength * 0.68 +
                            grove * 0.16 +
                            landmark * 0.04 +
                            walkableContacts * 0.035 +
                            seededCell(cell, 409, 17) * 0.095 -
                            focus * 0.16 -
                            quiet * 0.1,
                        forestStrength = forestStrength,
                        patchId = dressingPatch.id,
                        floorAnchor = false,
                    });

                continue;
            }

            // Existing LOD budgets often remained unused in wide Floor fields because trees were restricted to
            // perimeter Solid caps. Admit only the core of a macro forest patch: this moves the already-budgeted crowns
            // into one readable stand without increasing tree count, draw calls or materials, while clearings and paved
            // travel courts stay open.
            // ## A stand has to be CONTIGUOUS
            //
            // The formation gate below is right and stays exactly as it was: a crown on ordinary open floor belongs to
            // the wall-cap path above, not here. What was wrong is what happened *inside* a formation. The admitted core
            // was then thinned by a flat `0.055 + forestStrength * 0.2 + grove * 0.07` — about **22 %** at a typical
            // core — so a wood was handed a random fifth of its own cells and came out as a sprinkle.
            //
            // Measured over 25 chunks of the shared world, the composition field was never the shortage: **16.2 % of
            // walkable Floor passes every gate here** (≈72 cells per chunk of genuine stand interior). Of those ~1 810
            // eligible cells only 320 received a crown, the largest stand in the whole world was **9 trees**, and there
            // were **no groves at all** (0 stands of 10+, clustered at ≤3 tiles).
            //
            // So the density now follows the formation instead of a constant: near-closed canopy in a core, tapering
            // with the patch's own strength toward its edge. The 1.35-tile in-patch spacing below becomes the regulator,
            // which is what makes a stand read as one mass rather than as scattered individuals, and the taper is what
            // gives it a soft edge instead of a cut line.
            double canopyChance = 0.24 + forestStrength * 0.7 + grove * 0.18;
            if (
                cell.type == TileType.Floor &&
                cell.walkable &&
                !paved &&
                allowFloorTreePatches &&
                clearing < 0.46 &&
                focus < 0.42 &&
                quiet < 0.58 &&
                routeWear < 0.42 &&
                (forestStrength > FOREST_STAND_STRENGTH || grove > 0.7) &&
                seededCell(cell, 937, 317) / ecologyGain < canopyChance
            )
                treeCandidates.push(new TerrainTreeCandidate
                {
                    cell = cell,
                    score =
                        forestStrength * 0.7 +
                        grove * 0.18 +
                        landmark * 0.03 +
                        seededCell(cell, 941, 331) * 0.08 -
                        focus * 0.15 -
                        quiet * 0.1,
                    forestStrength = forestStrength,
                    patchId = dressingPatch.id,
                    floorAnchor = true,
                });

            if (
                cell.type == TileType.Floor &&
                cell.walkable &&
                effects.butterflies.Count < limits.butterflies &&
                // Flutterer supply is the world's own ambient DATA, not a chain of biome-key comparisons in the render
                // plan. That chain listed thirteen worlds, could not be extended without editing engine code, and
                // answered a question `skyLife` had already answered once — which is how `prismglass_archive` ended up
                // declaring flutterers in its ecology and receiving 0.001 of them here.
                seededCell(cell, 257, 5) < ambientProfile.flutterDensity
            )
            {
                effects.butterflies.push(new TerrainButterflyEffect
                {
                    id = cell.id,
                    ox = 0.18 + seededCell(cell, 263, 7) * 0.64,
                    oy = 0.18 + seededCell(cell, 269, 11) * 0.64,
                    range = 1.6 + seededCell(cell, 271, 13) * 2.4,
                    phase = seededCell(cell, 277, 17),
                    speed = 0.16 + seededCell(cell, 281, 19) * 0.2,
                    flap = 7 + seededCell(cell, 283, 23) * 5,
                    size = 2.2 + seededCell(cell, 293, 29) * 1.6,
                    alpha = 0.5 + seededCell(cell, 307, 31) * 0.3,
                });
            }

            // Wind-blown grass tufts root at the FOOT of a wall face (the sheltered strip real grass grows in).
            if (
                cell.type == TileType.Floor &&
                cell.walkable &&
                effects.rockGrass.Count < limits.rockGrass &&
                wallContacts.Count > 0 &&
                seededCell(cell, 311, 3) <
                    GROUND_COVER_DENSITY *
                        (droneFoundry
                            ? 0.02 // bare machined deck — almost nothing roots on metal
                            : arenaCanyon
                                ? 0.08
                                : tideCage
                                    ? 0.4
                                    : aegisCitadel
                                        ? 0 // immaculate processional stone: the Citadel admits no opportunistic ground cover
                                        : hubSanctuary
                                            ? 0.38
                                            : sakuraTemple
                                                ? 0.42
                                                : prismglassArchive
                                                    ? 0.5
                                                    : clockworkMoon
                                                        ? 0.08
                                                        : starforgedEndrun
                                                            ? 0.04
                                                            : abyssalDeepsea
                                                                ? 0.52
                                                                : sugarstormCarnival
                                                                    ? 0.48
                                                                    : 0.3)
            )
            {
                string direction = pickTerrainDirection(wallContacts, (double)cell.id * 313 + 5 + seedSalt);
                effects.rockGrass.push(new TerrainRockGrassEffect
                {
                    id = cell.id,
                    direction = direction,
                    t = 0.12 + seededCell(cell, 313, 5) * 0.76,
                    blades =
                        3 +
                        (int)Math.floor(
                            seededCell(cell, 317, 7) *
                                (prismglassArchive
                                    ? 3
                                    : clockworkMoon
                                        ? 2
                                        : starforgedEndrun
                                            ? 2
                                            : sugarstormCarnival
                                                ? 4
                                                : 3)),
                    height =
                        5 +
                        seededCell(cell, 331, 11) *
                            (prismglassArchive
                                ? 6
                                : clockworkMoon
                                    ? 4
                                    : starforgedEndrun
                                        ? 3
                                        : sugarstormCarnival
                                            ? 7
                                            : 5),
                    phase = seededCell(cell, 337, 13) * Math.PI * 2,
                    sway = 0.5 + seededCell(cell, 347, 17) * 0.7,
                    alpha = 0.4 + seededCell(cell, 349, 19) * 0.25,
                });
            }

            if (
                cell.type == TileType.Floor &&
                cell.walkable &&
                effects.wallStrands.Count < limits.wallStrands &&
                wallContacts.Count > 0 &&
                hashTerrainCell((double)cell.id * 773 + 283 + seedSalt) <
                    clamp(
                        (droneFoundry
                            ? 0.056
                            : sakuraTemple
                                ? 0.034
                                : prismglassArchive
                                    ? 0.05
                                    : clockworkMoon
                                        ? 0.056
                                        : starforgedEndrun
                                            ? 0.056
                                            : sugarstormCarnival
                                                ? 0.052
                                                : 0.025) +
                            moisture *
                                (droneFoundry
                                    ? 0.04
                                    : sakuraTemple
                                        ? 0.11
                                        : prismglassArchive
                                            ? 0.06
                                            : abyssalDeepsea
                                                ? 0.13
                                                : clockworkMoon
                                                    ? 0.06
                                                    : starforgedEndrun
                                                        ? 0.035
                                                        : sugarstormCarnival
                                                            ? 0.1
                                                            : 0.08) +
                            edgePressure *
                                (droneFoundry
                                    ? 0.032
                                    : prismglassArchive
                                        ? 0.026
                                        : abyssalDeepsea
                                            ? 0.018
                                            : clockworkMoon
                                                ? 0.026
                                                : starforgedEndrun
                                                    ? 0.034
                                                    : sugarstormCarnival
                                                        ? 0.024
                                                        : 0.012),
                        0.01,
                        droneFoundry
                            ? 0.24
                            : sakuraTemple
                                ? 0.16
                                : prismglassArchive
                                    ? 0.2
                                    : abyssalDeepsea
                                        ? 0.18
                                        : clockworkMoon
                                            ? 0.24
                                            : starforgedEndrun
                                                ? 0.24
                                                : sugarstormCarnival
                                                    ? 0.22
                                                    : 0.12)
            )
            {
                string direction = pickTerrainDirection(wallContacts, (double)cell.id * 787 + 293 + seedSalt);
                double styleRoll = hashTerrainCell((double)cell.id * 797 + 307 + seedSalt);
                effects.wallStrands.push(new TerrainWallStrandEffect
                {
                    id = cell.id,
                    direction = direction,
                    t = 0.14 + hashTerrainCell((double)cell.id * 809 + 311 + seedSalt) * 0.72,
                    length = 0.24 + hashTerrainCell((double)cell.id * 811 + 313 + seedSalt) * 0.42,
                    strands = 2 + (int)Math.floor(hashTerrainCell((double)cell.id * 821 + 317 + seedSalt) * 4),
                    phase = hashTerrainCell((double)cell.id * 823 + 331 + seedSalt) * Math.PI * 2,
                    sway = 0.35 + hashTerrainCell((double)cell.id * 827 + 337 + seedSalt) * 0.8,
                    alpha = 0.24 + hashTerrainCell((double)cell.id * 829 + 347 + seedSalt) * 0.24,
                    // How much of a world's hanging growth is aerial ROOT rather than vine: a plated foundry and a brass
                    // bazaar grow the woody, rope-like kind; a temple garden and a reef grow the leafy one.
                    style =
                        styleRoll <
                        (droneFoundry
                            ? 0.82
                            : sakuraTemple
                                ? 0.34
                                : abyssalDeepsea
                                    ? 0.42
                                    : clockworkMoon
                                        ? 0.72
                                        : starforgedEndrun
                                            ? 0.7
                                            : sugarstormCarnival
                                                ? 0.38
                                                : prismglassArchive
                                                    ? 0.64
                                                    : 0.44)
                            ? TerrainWallStrandStyle.Root
                            : TerrainWallStrandStyle.Vine,
                });
            }

            if (
                cell.type == TileType.Floor &&
                cell.walkable &&
                effects.fireflies.Count < limits.fireflies &&
                seededCell(cell, 353, 7) <
                    (arenaCanyon
                        ? 0 // nothing alive glows over the cinder crust
                        : starforgedEndrun
                            ? 0.09 + edgePressure * 0.018
                            : moisture *
                                (aegisCitadel
                                    ? 0 // the Heart owns the only living glow in the monochrome court
                                    : sakuraTemple
                                        ? 0.2
                                        : prismglassArchive
                                            ? 0.18
                                            : abyssalDeepsea
                                                ? 0.24
                                                : clockworkMoon
                                                    ? 0.22
                                                    : sugarstormCarnival
                                                        ? 0.28
                                                        : 0.16))
            )
            {
                effects.fireflies.push(new TerrainFireflyEffect
                {
                    id = cell.id,
                    ox = 0.2 + seededCell(cell, 359, 11) * 0.6,
                    oy = 0.2 + seededCell(cell, 367, 13) * 0.6,
                    radius = 8 + seededCell(cell, 373, 17) * 14,
                    phase = seededCell(cell, 379, 19),
                    speed = 0.1 + seededCell(cell, 383, 23) * 0.14,
                    drift = 0.5 + seededCell(cell, 389, 29) * 0.8,
                    alpha = 0.3 + seededCell(cell, 397, 31) * 0.3,
                });
            }

            if (
                cell.type == TileType.Floor &&
                cell.walkable &&
                !paved &&
                !cityCanopy &&
                limits.grassPatches > 0 &&
                routeWear < 0.52 &&
                grassHabitat > (arenaCanyon ? 0.02 : wyrmforge ? 0.04 : 0.08) &&
                // The PvP arena is exempt from the Living-Ground densification: its barren read is an authored
                // combat-readability contract (test-pinned), not a lack of dressing budget.
                seededCell(cell, 527, 97) <
                    Math.max(
                        grassHabitat * (arenaCanyon ? 0.004 : alpineMeadow || hubSanctuary ? 0.82 : 0.68),
                        (arenaCanyon ? 1 : GROUND_COVER_DENSITY) *
                            clamp(
                                (wyrmforge
                                    ? 0.008
                                    : arenaCanyon
                                        ? 0.002
                                        : hubSanctuary
                                            ? 0.064
                                            : alpineMeadow
                                                ? 0.07
                                                : olympianBorough
                                                    ? 0.038
                                                    : rainbowland
                                                        ? 0.058
                                                        : sakuraTemple
                                                            ? 0.045
                                                            : prismglassArchive
                                                                ? 0.032
                                                                : clockworkMoon
                                                                    ? 0.06
                                                                    : starforgedEndrun
                                                                        ? 0.045
                                                                        : sugarstormCarnival
                                                                            ? 0.05
                                                                            : 0.028) +
                                    moisture *
                                        (arenaCanyon
                                            ? 0.005
                                            : hubSanctuary
                                                ? 0.2
                                                : alpineMeadow
                                                    ? 0.23
                                                    : olympianBorough
                                                        ? 0.06
                                                        : rainbowland
                                                            ? 0.15
                                                            : sakuraTemple
                                                                ? 0.18
                                                                : prismglassArchive
                                                                    ? 0.08
                                                                    : abyssalDeepsea
                                                                        ? 0.17
                                                                        : clockworkMoon
                                                                            ? 0.08
                                                                            : starforgedEndrun
                                                                                ? 0.02
                                                                                : sugarstormCarnival
                                                                                    ? 0.12
                                                                                    : 0.13) +
                                    edgePressure *
                                        (hubSanctuary
                                            ? 0.026
                                            : alpineMeadow
                                                ? 0.022
                                                : prismglassArchive
                                                    ? 0.035
                                                    : abyssalDeepsea
                                                        ? 0.026
                                                        : clockworkMoon
                                                            ? 0.03
                                                            : starforgedEndrun
                                                                ? 0.032
                                                                : sugarstormCarnival
                                                                    ? 0.024
                                                                    : 0.018) -
                                    Math.max(0, cell.elevation - 5) *
                                        (prismglassArchive
                                            ? 0.006
                                            : clockworkMoon
                                                ? 0.006
                                                : starforgedEndrun
                                                    ? 0.006
                                                    : sugarstormCarnival
                                                        ? 0.006
                                                        : 0.01),
                                arenaCanyon ? 0.004 : 0.012,
                                wyrmforge
                                    ? 0.08
                                    : arenaCanyon
                                        ? 0.09
                                        : hubSanctuary
                                            ? 0.3
                                            : alpineMeadow
                                                ? 0.32
                                                : sakuraTemple
                                                    ? 0.24
                                                    : prismglassArchive
                                                        ? 0.22
                                                        : abyssalDeepsea
                                                            ? 0.25
                                                            : clockworkMoon
                                                                ? 0.28
                                                                : starforgedEndrun
                                                                    ? 0.24
                                                                    : sugarstormCarnival
                                                                        ? 0.26
                                                                        : 0.18) *
                            ecologyGain *
                            (0.56 + grove * 1.08))
            )
            {
                var grassEffect = new TerrainGrassPatchEffect
                {
                    id = cell.id,
                    ox = 0.18 + seededCell(cell, 541, 101) * 0.64,
                    oy = 0.18 + seededCell(cell, 547, 103) * 0.64,
                    blades =
                        (hubSanctuary || alpineMeadow ? 10 : 7) +
                        (int)Math.floor(
                            seededCell(cell, 557, 107) *
                                (hubSanctuary
                                    ? 10
                                    : alpineMeadow
                                        ? 10
                                        : sakuraTemple
                                            ? 6
                                            : prismglassArchive
                                                ? 4
                                                : abyssalDeepsea
                                                    ? 5
                                                    : clockworkMoon
                                                        ? 5
                                                        : starforgedEndrun
                                                            ? 5
                                                            : sugarstormCarnival
                                                                ? 7
                                                                : 6)) +
                        (int)Math.floor(grassHabitat * 6),
                    clumps =
                        (hubSanctuary || alpineMeadow ? 2 : 1) +
                        (int)Math.floor(seededCell(cell, 559, 108) * (hubSanctuary || alpineMeadow ? 2 : 2)) +
                        (grassHabitat > 0.72 ? 1 : 0),
                    radius =
                        (hubSanctuary || alpineMeadow ? 0.26 : 0.18) +
                        seededCell(cell, 563, 109) *
                            (hubSanctuary
                                ? 0.32
                                : alpineMeadow
                                    ? 0.34
                                    : sakuraTemple
                                        ? 0.24
                                        : prismglassArchive
                                            ? 0.18
                                            : abyssalDeepsea
                                                ? 0.2
                                                : clockworkMoon
                                                    ? 0.22
                                                    : starforgedEndrun
                                                        ? 0.16
                                                        : sugarstormCarnival
                                                            ? 0.22
                                                            : 0.18) +
                        grassHabitat * 0.12,
                    height =
                        (hubSanctuary || alpineMeadow ? 0.42 : 0.3) +
                        seededCell(cell, 569, 113) *
                            (hubSanctuary
                                ? 0.46
                                : alpineMeadow
                                    ? 0.5
                                    : sakuraTemple
                                        ? 0.22
                                        : prismglassArchive
                                            ? 0.18
                                            : abyssalDeepsea
                                                ? 0.26
                                                : clockworkMoon
                                                    ? 0.24
                                                    : starforgedEndrun
                                                        ? 0.16
                                                        : sugarstormCarnival
                                                            ? 0.3
                                                            : 0.38),
                    phase = seededCell(cell, 571, 127),
                    sway = 0.35 + seededCell(cell, 577, 131) * 0.72,
                    lean = Math.sin(compositionDirection) * 0.2 + (seededCell(cell, 587, 137) - 0.5) * 0.42,
                    flowering = clamp(
                        wyrmforge
                            ? 0.02 // fixed ash-grass seed heads; moisture never blooms flowers in the caldera
                            : (hubSanctuary
                                ? 0.28
                                : alpineMeadow
                                    ? 0.36
                                    : sakuraTemple
                                        ? 0.58
                                        : abyssalDeepsea
                                            ? 0.2
                                            : rainbowland || sugarstormCarnival
                                                ? 0.52
                                                : starforgedEndrun
                                                    ? 0.44
                                                    : 0.12) +
                                moisture * 0.24 +
                                seededCell(cell, 589, 138) * 0.22,
                        0,
                        1),
                    stiffness = clamp(
                        (alpineMeadow ? 0.42 : prismglassArchive || starforgedEndrun ? 0.78 : 0.28) +
                            seededCell(cell, 591, 139) * 0.28 -
                            moisture * 0.14,
                        0.12,
                        0.94),
                    density = grassHabitat,
                    variation = seededCell(cell, 597, 143),
                    alpha = 0.46 + seededCell(cell, 593, 141) * 0.26,
                };
                grassCandidates.push(new TerrainGrassCandidate
                {
                    cell = cell,
                    score =
                        grassHabitat * 0.62 +
                        grove * 0.16 +
                        moisture * 0.1 +
                        (1 - routeWear) * 0.06 +
                        seededCell(cell, 599, 149) * 0.06 -
                        focus * 0.18 +
                        quiet * 0.04,
                    effect = grassEffect,
                });
            }

            if (
                cell.type == TileType.Floor &&
                cell.walkable &&
                effects.rollingGrass.Count < limits.rollingGrass &&
                h <
                    (droneFoundry
                        ? 0
                        : arenaCanyon
                            ? 0.012 // dry scrub tumbling through the canyon
                            : sakuraTemple
                                ? 0.012
                                : prismglassArchive
                                    ? 0.001
                                    : clockworkMoon
                                        ? 0
                                        : starforgedEndrun
                                            ? 0
                                            : abyssalDeepsea
                                                ? 0.0015
                                                : sugarstormCarnival
                                                    ? 0.014
                                                    : 0.006)
            )
            {
                TerrainCell? route = rollingGrassRoute(terrain, cell, (double)cell.id * 157 + seedSalt);
                if (route != null)
                {
                    effects.rollingGrass.push(new TerrainRollingGrassEffect
                    {
                        id = cell.id,
                        startX = cell.x + seededCell(cell, 89, 53),
                        startY = cell.y + 0.35 + seededCell(cell, 97, 59) * 0.3,
                        endX = route.x + 0.2 + seededCell(cell, 99, 60) * 0.6,
                        endY = route.y + 0.2 + seededCell(cell, 100, 61) * 0.6,
                        startZ = cell.surfaceZ,
                        endZ = route.surfaceZ,
                        phase = seededCell(cell, 101, 61),
                        speed =
                            (sakuraTemple ? 0.16 : sugarstormCarnival ? 0.22 : 0.28) +
                            seededCell(cell, 103, 67) * (sakuraTemple ? 0.18 : sugarstormCarnival ? 0.28 : 0.34),
                        spin =
                            (sakuraTemple ? 0.75 : sugarstormCarnival ? 1.9 : 1.6) +
                            seededCell(cell, 107, 71) * (sakuraTemple ? 0.7 : sugarstormCarnival ? 1.5 : 1.7),
                        radius = 2.7 + seededCell(cell, 109, 73) * (sakuraTemple ? 1.5 : 2),
                        sway = 0.12 + seededCell(cell, 111, 79) * 0.14,
                        alpha = 0.16 + seededCell(cell, 115, 83) * 0.12,
                    });
                }
            }

            if (effects.rockFalls.Count >= limits.rockFalls) continue;
            foreach (TerrainDirection direction in TerrainDirections)
            {
                TerrainEdge edge = cell.edges[direction.key]!;
                if (!shouldRenderTerrainDepthEdge(cell, edge) || edge.drop < 0.75) continue;
                double salt = (double)cell.id * 113 + (int)direction.key[0] + seedSalt;
                if (
                    hashTerrainCell(salt) >
                    (arenaCanyon
                        ? 0.4 // the canyon walls constantly shed scree
                        : sakuraTemple
                            ? 0.14
                            : prismglassArchive
                                ? 0.18
                                : clockworkMoon
                                    ? 0.22
                                    : starforgedEndrun
                                        ? 0.18
                                        : sugarstormCarnival
                                            ? 0.18
                                            : 0.24)
                )
                    continue;
                effects.rockFalls.push(new TerrainRockFallEffect
                {
                    id = cell.id,
                    direction = direction.key,
                    t = 0.12 + hashTerrainCell(salt + 5) * 0.76,
                    phase = hashTerrainCell(salt + 11),
                    speed = 0.16 + hashTerrainCell(salt + 17) * 0.22,
                    jitter = hashTerrainCell(salt + 23),
                    size = 1.2 + hashTerrainCell(salt + 29) * 2.2,
                    color = prismglassArchive
                        ? 0xbff7ff
                        : clockworkMoon
                            ? 0xb28a50
                            : starforgedEndrun
                                ? 0xffc55a
                                : sugarstormCarnival
                                    ? 0xffdced
                                    : cell.solid
                                        ? 0x9aa79f
                                        : 0x87978f,
                    alpha = cell.solid ? 0.52 : 0.4,
                });
                if (effects.rockFalls.Count >= limits.rockFalls) break;
            }
        }

        treeCandidates.sort((a, b) =>
        {
            double byScore = b.score - a.score;
            return Js.Truthy(byScore) ? byScore : a.cell.id - b.cell.id;
        });
        var selectedTreeCandidates = new List<TerrainTreeCandidate>();
        double openTreeSpacing =
            cityCanopy || clockworkMoon ? 2.6 : alpineMeadow || sakuraTemple ? 2.15 : 2.35;
        foreach (TerrainTreeCandidate candidate in treeCandidates)
        {
            if (selectedTreeCandidates.Count >= limits.trees) break;
            if (
                selectedTreeCandidates.some((other) =>
                {
                    int dx = other.cell.x - candidate.cell.x;
                    int dy = other.cell.y - candidate.cell.y;
                    bool sameForest =
                        other.patchId == candidate.patchId &&
                        other.forestStrength > FOREST_STAND_STRENGTH &&
                        candidate.forestStrength > FOREST_STAND_STRENGTH;
                    // A forest core may use diagonal neighbours, making an actual stand. Open terrain retains the former
                    // generous silhouette spacing, so the extra LOD budget never becomes an even tree carpet.
                    double spacing = sameForest ? 1.35 : openTreeSpacing;
                    return dx * dx + dy * dy < spacing * spacing;
                })
            )
                continue;
            selectedTreeCandidates.push(candidate);
            addTreeEffect(candidate.cell, candidate.floorAnchor);
        }
        if (effects.trees.Count == 0 && fallbackTree != null && limits.trees > 0) addTreeEffect(fallbackTree);

        grassCandidates.sort((a, b) =>
        {
            double byScore = b.score - a.score;
            return Js.Truthy(byScore) ? byScore : a.cell.id - b.cell.id;
        });
        var selectedGrassCells = new List<TerrainGrassCandidate>();
        double grassSpacing = hubSanctuary || alpineMeadow ? 1.25 : 1.35;
        double grassSpacingSq = grassSpacing * grassSpacing;
        foreach (TerrainGrassCandidate candidate in grassCandidates)
        {
            if (selectedGrassCells.Count >= limits.grassPatches) break;
            if (
                selectedGrassCells.some((other) =>
                {
                    int dx = other.cell.x - candidate.cell.x;
                    int dy = other.cell.y - candidate.cell.y;
                    return dx * dx + dy * dy < grassSpacingSq;
                })
            )
                continue;
            selectedGrassCells.push(candidate);
            effects.grassPatches.push(candidate.effect);
        }
        effects.birds = createTerrainBirdEffects(
            terrain,
            TerrainAmbient.ambientAllowsBirds(ambientProfile) ? limits.birds : 0,
            effectSeed);
        effects.ambientMotifs = createTerrainAmbientMotifEffects(terrain, ambientProfile, effectSeed);
        effects.worldDecorations = createTerrainWorldDecorationEffects(
            terrain,
            effectSeed,
            biomeKey,
            originCellX,
            originCellY,
            compositionCache);

        List<TerrainDecorationPlacement>? authoredDecorations = terrain.decorations;
        if (authoredDecorations != null)
        {
            // An explicit world-owned composition is authoritative as a set, not merely cell-by-cell. Mixing the
            // renderer's capped local scatter back into it would refill deliberate courts and turn Country edge ribbons
            // into uniform noise. Layouts without placements retain the generic fallback above.
            effects.trees.Clear();
            effects.worldDecorations.Clear();
            foreach (TerrainDecorationPlacement decoration in authoredDecorations)
            {
                TerrainCell? cell = terrainCellAt(terrain, decoration.tx, decoration.ty);
                if (cell == null) continue;
                // The final gate on ground a prop cannot stand on. An authored set arrives from four independent
                // producers (hand-painted Hub, Endless Country dressing, map simulation, editor stamps) and terrain is
                // routinely carved AFTER it was dressed, so the renderer asks the shared habitat rule rather than
                // trusting the payload: nothing grows in a ravine, and only water growth stands in water.
                if (!terrainTileAcceptsDecoration(decoration.kind, cell.type)) continue;
                // `...(decoration.themeKey ? { themeKey: decoration.themeKey } : {})`
                string? authoredThemeKey = !string.IsNullOrEmpty(decoration.themeKey) ? decoration.themeKey : null;
                if (decoration.kind == TerrainDecorationKind.Tree)
                {
                    TreeVisual mature = TreeVisualModule.createTreeVisual(decoration.seed, decoration.themeKey ?? biomeKey);
                    if (decoration.growthStage == TreeGrowthStage.Stump)
                    {
                        effects.worldDecorations.push(new TerrainWorldDecorationEffect(
                            createWorldDecorationVisual(
                                decoration.seed,
                                decoration.themeKey ?? biomeKey,
                                WorldDecorationKind.Stump))
                        {
                            id = cell.id,
                            ox = 0.5,
                            oy = 0.5,
                            authored = true,
                            themeKey = authoredThemeKey,
                            patchId = decoration.seed,
                            patchStrength = 1,
                            felledTree = new TerrainFelledTreeVisual(decoration.seed, mature),
                        });
                        continue;
                    }
                    effects.trees.push(new TerrainTreeDressingEffect(
                        // A regrowing tree draws as the smaller self of the very specimen that was felled — same
                        // seed, same profile, scaled down the shared stage ladder. The annotation is stamped by the
                        // client's dungeon mirror from the authoritative record; the artifact itself never carries it.
                        decoration.growthStage != null
                            ? WorldTreeLife.applyTreeGrowthToVisual(mature, decoration.growthStage.Value)
                            : mature)
                    {
                        id = cell.id,
                        ox = 0.5,
                        oy = 0.5,
                        authored = true,
                        themeKey = authoredThemeKey,
                    });
                    continue;
                }
                effects.worldDecorations.push(new TerrainWorldDecorationEffect(
                    createWorldDecorationVisual(
                        decoration.seed,
                        decoration.themeKey ?? biomeKey,
                        decoration.kind))
                {
                    id = cell.id,
                    ox = 0.5,
                    oy = 0.5,
                    authored = true,
                    themeKey = authoredThemeKey,
                    patchId = decoration.seed,
                    patchStrength = 1,
                });
            }
        }

        return effects;
    }

    private sealed class TerrainWorldDecorationCandidate
    {
        public TerrainCell cell = null!;
        public double noise;
        /// <summary>Read back from the Int32Array cache: the signed bit pattern of the uint32 composition id.</summary>
        public int patchId;
        public double patchStrength;
        public double patchEdge;
        public double grove;
        public double landmark;
        public double focus;
        public double quiet;
        public double direction;
    }

    private sealed class TerrainWorldDecorationPatch
    {
        public int id;
        public List<TerrainWorldDecorationCandidate> cap = null!;
        public List<TerrainWorldDecorationCandidate> floor = null!;
        public TerrainWorldDecorationCandidate centre = null!;
        public double score;
    }

    private sealed class TerrainSelectedDecoration
    {
        public TerrainCell cell = null!;
        public int patchId;
    }

    /// <summary>
    /// What a patch repeats, in the order it is offered.
    ///
    /// Scrub twice against one stump: an undergrowth stand is mostly living growth with the occasional cut trunk in
    /// it, and repeating the entry is how a weight is expressed in a table a patch walks in order.
    /// </summary>
    private static readonly IReadOnlyList<string> TERRAIN_PATCH_VOCABULARY = Array.AsReadOnly(new[]
    {
        WorldDecorationKind.Thicket,
        WorldDecorationKind.Thicket,
        WorldDecorationKind.Stump,
    });

    /// <summary>
    /// Where inside its patch each kind of growth prefers to stand.
    ///
    /// Scrub wants the patch's core and the grove around it; a stump wants the FRINGE, because a cut trunk reads as
    /// the edge of a wood that was worked rather than as something growing in its middle. Both retreat from the
    /// composition's focus and quiet sectors, which is what keeps a deliberate clearing clear.
    /// </summary>
    private static double terrainDecorationCandidateScore(
        TerrainWorldDecorationCandidate candidate,
        string kind)
    {
        switch (kind)
        {
            case WorldDecorationKind.Thicket:
                return (
                    candidate.patchStrength * 0.64 +
                    candidate.grove * 0.26 +
                    candidate.noise * 0.1 -
                    candidate.focus * 0.2 -
                    candidate.quiet * 0.12
                );
            case WorldDecorationKind.Stump:
                return (
                    candidate.patchStrength * 0.36 +
                    candidate.patchEdge * 0.43 +
                    candidate.noise * 0.11 -
                    candidate.focus * 0.08 -
                    candidate.quiet * 0.08
                );
            default:
                // The TS switch falls through to `undefined`, which never compares above or equal to a score.
                return double.NaN;
        }
    }

    /// <summary>
    /// Select a small number of coherent prop statements from real generated terrain. A patch repeats a restrained
    /// vocabulary around one centre (forest understorey, deadfall, stone garden, crystal garden or ceremony) rather
    /// than forcing one of every prop into every chunk. The two-cell inset mirrors the terrain bake border, so
    /// records are owned by the visible tile instead of being selected from context an adjacent bake will discard.
    /// </summary>
    private static List<TerrainWorldDecorationEffect> createTerrainWorldDecorationEffects(
        MaterializedTerrain terrain,
        double effectSeed,
        string? biomeKey,
        double originCellX,
        double originCellY,
        TerrainCellCompositionPlan compositionCache)
    {
        // Hub uses authored keep-outs and authored composition from `hubProps`; allowing a local bake to improvise
        // there could crowd portals. It still consumes the exact same visual registry and renderer.
        if (biomeKey == "hub") return new List<TerrainWorldDecorationEffect>();
        int inset = terrain.width >= 12 && terrain.height >= 12 ? 2 : 0;
        var capCandidates = new List<TerrainWorldDecorationCandidate>();
        var floorCandidates = new List<TerrainWorldDecorationCandidate>();
        int seedSalt = Math.imul(Js.ToInt32(effectSeed), 1489);
        foreach (TerrainCell cell in terrain.cells)
        {
            if (
                cell.x < inset ||
                cell.y < inset ||
                cell.x >= terrain.width - inset ||
                cell.y >= terrain.height - inset
            )
                continue;
            bool isCap = cell.type == TileType.Solid && cardinalWalkableContactCount(cell) > 0;
            // Broad Floor fields are where the production camera was visually empty. Interior cells may now anchor
            // one macro-coherent dressing patch; the strength gate below keeps travel lanes open and prevents uniform
            // scatter. Boundary floors remain eligible as before.
            bool isFloor = cell.type == TileType.Floor && cell.walkable;
            if (!isCap && !isFloor) continue;
            // The primary effects pass already sampled this exact integer world coordinate. Reusing its full-precision
            // result avoids a second Voronoi/ecology traversal for every eligible cap/floor while preserving every
            // patch score and tie-break bit-for-bit.
            if (compositionCache.valid[cell.id] != 1) continue;
            // Interior candidates remain patch-ranked below. They are never sprayed uniformly: the selected macro
            // patch receives the complete bounded vocabulary budget and every other open cell remains untouched.
            var candidate = new TerrainWorldDecorationCandidate
            {
                cell = cell,
                noise = hashTerrainCell(seedSalt + (double)cell.id * (isCap ? 1597 : 1601) + 17),
                patchId = compositionCache.patchId[cell.id],
                patchStrength = compositionCache.patchStrength[cell.id],
                patchEdge = compositionCache.patchEdge[cell.id],
                grove = compositionCache.grove[cell.id],
                landmark = compositionCache.landmark[cell.id],
                focus = compositionCache.focus[cell.id],
                quiet = compositionCache.quiet[cell.id],
                direction = compositionCache.direction[cell.id],
            };
            if (isCap)
            {
                capCandidates.push(candidate);
            }
            else floorCandidates.push(candidate);
        }
        if (capCandidates.Count == 0 && floorCandidates.Count == 0) return new List<TerrainWorldDecorationEffect>();

        var patches = new JsMap<int, TerrainWorldDecorationPatch>();
        void registerCandidate(TerrainWorldDecorationCandidate candidate, string anchor)
        {
            TerrainWorldDecorationPatch? patch = patches.get(candidate.patchId);
            double candidateScore =
                candidate.patchStrength * 0.7 +
                candidate.focus * 0.2 +
                candidate.noise * 0.1 -
                candidate.quiet * 0.16;
            if (patch == null)
            {
                patch = new TerrainWorldDecorationPatch
                {
                    id = candidate.patchId,
                    cap = new List<TerrainWorldDecorationCandidate>(),
                    floor = new List<TerrainWorldDecorationCandidate>(),
                    centre = candidate,
                    score = candidateScore,
                };
                patches.set(candidate.patchId, patch);
            }
            // `patch[anchor].push(candidate)`
            (anchor == "cap" ? patch.cap : patch.floor).push(candidate);
            if (candidateScore > patch.score)
            {
                patch.centre = candidate;
                patch.score = candidateScore;
            }
        }
        foreach (TerrainWorldDecorationCandidate candidate in capCandidates) registerCandidate(candidate, "cap");
        foreach (TerrainWorldDecorationCandidate candidate in floorCandidates) registerCandidate(candidate, "floor");

        int limit = (int)clamp(Math.floor((double)terrain.cells.Length / 170), 6, 18);
        // Split the unchanged prop budget over two macro statements when possible. One six-object knot left the rest
        // of a gameplay frame barren; two three-object groups create foreground/midground rhythm at identical
        // geometry, material and draw-call cost.
        int patchLimit = (int)clamp(Math.ceil((double)limit / 4), 2, 3);
        List<TerrainWorldDecorationPatch> selectedPatches = new List<TerrainWorldDecorationPatch>(patches.values())
            .filter((patch) => patch.cap.Count + patch.floor.Count >= 2)
            .sort((a, b) =>
            {
                double byScore = b.score - a.score;
                if (Js.Truthy(byScore)) return byScore;
                double bySize = b.cap.Count + b.floor.Count - (a.cap.Count + a.floor.Count);
                if (Js.Truthy(bySize)) return bySize;
                // int32 ids: subtract in double, exactly as JS does, so the difference cannot wrap.
                return (double)a.id - b.id;
            })
            .slice(0, patchLimit);
        var used = new HashSet<int>();
        var selected = new List<TerrainSelectedDecoration>();
        var @out = new List<TerrainWorldDecorationEffect>();
        bool pushKind(TerrainWorldDecorationPatch patch, string kind, int ordinal)
        {
            List<TerrainWorldDecorationCandidate> candidates = patch.cap.Count == 0 ? patch.floor : patch.cap;
            double radius = 6.4 + patch.centre.patchStrength * 1.8;
            TerrainWorldDecorationCandidate? candidate = null;
            double bestScore = double.NegativeInfinity;
            foreach (TerrainWorldDecorationCandidate entry in candidates)
            {
                if (used.Contains(entry.cell.id)) continue;
                int dxCentre = entry.cell.x - patch.centre.cell.x;
                int dyCentre = entry.cell.y - patch.centre.cell.y;
                if (dxCentre * dxCentre + dyCentre * dyCentre > radius * radius) continue;
                if (
                    selected.some((other) =>
                    {
                        int dx = other.cell.x - entry.cell.x;
                        int dy = other.cell.y - entry.cell.y;
                        double spacing = other.patchId == patch.id ? 1.35 : 2.8;
                        return dx * dx + dy * dy < spacing * spacing;
                    })
                )
                    continue;
                double score = terrainDecorationCandidateScore(entry, kind);
                if (
                    score > bestScore ||
                    (score == bestScore && entry.cell.id < (candidate != null ? candidate.cell.id : double.PositiveInfinity))
                )
                {
                    candidate = entry;
                    bestScore = score;
                }
            }
            if (candidate == null) return false;
            TerrainCell cell = candidate.cell;
            used.Add(cell.id);
            selected.push(new TerrainSelectedDecoration { cell = cell, patchId = patch.id });
            double worldCellX = originCellX + cell.x;
            double worldCellY = originCellY + cell.y;
            int visualSeed =
                seedSalt ^
                Math.imul(Js.ToInt32(worldCellX), 0x1f123bb5) ^
                Math.imul(Js.ToInt32(worldCellY), 0x5f356495) ^
                Math.imul(ordinal + 1, 71);
            WorldDecorationVisual visual = createWorldDecorationVisual(visualSeed, biomeKey, kind);
            @out.push(new TerrainWorldDecorationEffect(visual)
            {
                id = cell.id,
                ox = 0.24 + hashTerrainCell((double)visualSeed + 29) * 0.52,
                oy = 0.24 + hashTerrainCell((double)visualSeed + 31) * 0.52,
                patchId = patch.id,
                patchStrength = candidate.patchStrength,
            });
            return true;
        }

        for (
            int patchIndex = 0;
            patchIndex < selectedPatches.Count && @out.Count < limit;
            patchIndex++
        )
        {
            TerrainWorldDecorationPatch patch = selectedPatches[patchIndex];
            IReadOnlyList<string> vocabulary = TERRAIN_PATCH_VOCABULARY;
            int patchBudget = (int)Math.ceil((double)(limit - @out.Count) / (selectedPatches.Count - patchIndex));
            for (int ordinal = 0; ordinal < patchBudget && @out.Count < limit; ordinal++)
            {
                bool added = false;
                int offset = (int)Math.floor(
                    hashTerrainCell(seedSalt + (double)patch.id + ordinal * 1621) * vocabulary.Count);
                for (int step = 0; step < vocabulary.Count; step++)
                {
                    string kind = vocabulary[(offset + step) % vocabulary.Count];
                    if (pushKind(patch, kind, patchIndex * limit + ordinal))
                    {
                        added = true;
                        break;
                    }
                }
                if (!added) break;
            }
        }
        return @out;
    }

    private sealed class TerrainAmbientMotifCandidate
    {
        public TerrainCell cell = null!;
        public double score;
    }

    /// <summary>
    /// Rank terrain cells globally and keep the best theme anchors. This is intentionally selection-by-world-data,
    /// not an early "first N" scan: equal terrain + seed produces equal records regardless of camera, traversal or
    /// streaming order. Every endless theme has a non-zero minimum and all endless chunks contain walkable cells,
    /// so even ecologies that correctly suppress birds/butterflies still carry their own animated signature.
    /// </summary>
    private static List<TerrainAmbientMotifEffect> createTerrainAmbientMotifEffects(
        MaterializedTerrain terrain,
        TerrainAmbientThemeProfile profile,
        double effectSeed)
    {
        double limit = TerrainAmbient.terrainAmbientMotifLimit(profile, terrain.cells.Length);
        if (limit <= 0) return new List<TerrainAmbientMotifEffect>();
        int seedSalt = Math.imul(Js.ToInt32(effectSeed), 977);
        var candidates = new List<TerrainAmbientMotifCandidate>();
        foreach (TerrainCell cell in terrain.cells)
        {
            bool walkable =
                cell.walkable && (cell.type == TileType.Floor || cell.type == TileType.Bridge);
            bool water = cell.type == TileType.Water;
            bool eligible =
                profile.anchor == "walkable"
                    ? walkable
                    : profile.anchor == "water"
                        ? water
                        : walkable || water;
            if (!eligible) continue;
            // Avoid stacking every motif against the outer crop of a sampled bake region. Actual endless chunks have
            // real neighbours, while terrain bake frames carry a border; this inset gives both the same stable read.
            int inset = terrain.width >= 12 && terrain.height >= 12 ? 1 : 0;
            if (
                cell.x < inset ||
                cell.y < inset ||
                cell.x >= terrain.width - inset ||
                cell.y >= terrain.height - inset
            )
                continue;
            double salt = (double)cell.id * 1297 + seedSalt + 191;
            int exposure = cell.edges.countValues((edge) => edge.passable);
            candidates.push(new TerrainAmbientMotifCandidate
            {
                cell = cell,
                score =
                    hashTerrainCell(salt) * 1.7 +
                    exposure * 0.035 +
                    (water && profile.anchor == "either" ? 0.12 : 0),
            });
        }
        candidates.sort((a, b) =>
        {
            double byScore = b.score - a.score;
            return Js.Truthy(byScore) ? byScore : a.cell.id - b.cell.id;
        });

        var selected = new List<TerrainAmbientMotifCandidate>();
        double spacing = Math.max(1.3, Math.sqrt(terrain.cells.Length / Math.max(1, limit)) * 0.42);
        foreach (TerrainAmbientMotifCandidate candidate in candidates)
        {
            if (selected.Count >= limit) break;
            if (
                selected.some(
                    (other) =>
                        Math.hypot(other.cell.x - candidate.cell.x, other.cell.y - candidate.cell.y) < spacing)
            )
                continue;
            selected.push(candidate);
        }
        // Pathological narrow corridors may not satisfy the ideal spacing. Fill from the same global ranking so the
        // profile guarantee remains true without introducing traversal order or non-deterministic retries.
        foreach (TerrainAmbientMotifCandidate candidate in candidates)
        {
            if (selected.Count >= limit) break;
            if (!selected.includes(candidate)) selected.push(candidate);
        }

        return selected.map((selectedCandidate, index) =>
        {
            TerrainCell cell = selectedCandidate.cell;
            double salt = (double)cell.id * 1301 + index * 211 + seedSalt + 223;
            double sample(double offset) => hashTerrainCell(salt + offset);
            return new TerrainAmbientMotifEffect
            {
                id = cell.id,
                kind = profile.motif,
                motion = profile.motion,
                ox = 0.18 + sample(3) * 0.64,
                oy = 0.18 + sample(5) * 0.64,
                height = profile.heightMin + sample(7) * (profile.heightMax - profile.heightMin),
                travel = profile.travelMin + sample(11) * (profile.travelMax - profile.travelMin),
                phase = sample(13),
                speed = 0.16 + sample(17) * 0.34,
                spin = (sample(19) < 0.5 ? -1 : 1) * (0.45 + sample(23) * 2.2),
                scale = profile.scaleMin + sample(29) * (profile.scaleMax - profile.scaleMin),
                alpha = profile.alphaMin + sample(31) * (profile.alphaMax - profile.alphaMin),
                tint = sample(37),
            };
        });
    }

    private sealed class TerrainBirdPerchCandidate : TerrainBirdPerch
    {
        public TerrainCell cell = null!;
        public double score;
        public double exposure;
    }

    private static int birdPerchUseCount(Dictionary<int, int> perchUseCounts, int perchId) =>
        perchUseCounts.TryGetValue(perchId, out int count) ? count : 0;

    private static List<TerrainBirdEffect> createTerrainBirdEffects(
        MaterializedTerrain terrain,
        int limit,
        double effectSeed = 0)
    {
        if (limit <= 0) return new List<TerrainBirdEffect>();
        int seed = Js.ToInt32(effectSeed);
        List<TerrainBirdPerchCandidate> candidates = createTerrainBirdPerchCandidates(terrain, seed);
        if (candidates.Count < 2) return new List<TerrainBirdEffect>();

        var birds = new List<TerrainBirdEffect>();
        // `new Map<number, number>()` — only probed (get/has/set), never iterated.
        var perchUseCounts = new Dictionary<int, int>();
        double minDistance = clamp(Math.min(terrain.width, terrain.height) * 0.18, 4.2, 8.5);
        double maxDistance = clamp(Math.max(terrain.width, terrain.height) * 0.78, minDistance + 2, 22);

        for (int i = 0; i < limit; i++)
        {
            double salt =
                7901 + (double)terrain.width * 53 + (double)terrain.height * 97 + (double)i * 389 + Math.imul(seed, 131);
            TerrainBirdPerchCandidate? start = pickBirdStart(candidates, perchUseCounts, salt, i % 2 == 1);
            if (start == null) break;
            TerrainBirdPerchCandidate? second = pickBirdDestination(
                start,
                candidates,
                new HashSet<int> { start.id },
                perchUseCounts,
                salt + 17,
                minDistance,
                maxDistance);
            if (second == null)
            {
                markBirdPerchUsed(perchUseCounts, start.id);
                continue;
            }
            TerrainBirdPerchCandidate? third = pickBirdDestination(
                second,
                candidates,
                new HashSet<int> { start.id, second.id },
                perchUseCounts,
                salt + 37,
                minDistance,
                maxDistance);
            List<TerrainBirdPerchCandidate> perches = third != null
                ? new List<TerrainBirdPerchCandidate> { start, second, third }
                : new List<TerrainBirdPerchCandidate> { start, second };
            double routeDistance = birdRouteDistance(perches);
            foreach (TerrainBirdPerchCandidate perch in perches) markBirdPerchUsed(perchUseCounts, perch.id);

            birds.push(new TerrainBirdEffect
            {
                perches = perches.map((perch) => new TerrainBirdPerch { id = perch.id, ox = perch.ox, oy = perch.oy, z = perch.z }),
                speed = 2.15 + hashTerrainCell(salt + 41) * 1.25,
                phase = hashTerrainCell(salt + 43),
                perchSec = 1.05 + hashTerrainCell(salt + 47) * 1.65,
                arc = clamp(0.85 + routeDistance * 0.08 + hashTerrainCell(salt + 53) * 0.95, 1, 2.75),
                flap = 7.2 + hashTerrainCell(salt + 59) * 3.8,
                count = 1 + (hashTerrainCell(salt + 61) < 0.38 ? 1 : 0),
                wingSpan = 2.05 + hashTerrainCell(salt + 67) * 1.1,
                alpha = 0.42 + hashTerrainCell(salt + 71) * 0.22,
                scale = 0.86 + hashTerrainCell(salt + 73) * 0.22,
            });
        }

        return birds;
    }

    private static List<TerrainBirdPerchCandidate> createTerrainBirdPerchCandidates(
        MaterializedTerrain terrain,
        double effectSeed = 0)
    {
        // Endless chunks meet at real playable seams. A three-cell inset on BOTH neighbours created a six-cell
        // wildlife moat through every streamed world: routes were plentiful per chunk, yet none could occupy the
        // camera when the player crossed a boundary. Keep only the single bake-edge guard used by other ambient
        // lanes; global selection and route spacing already prevent duplicate visual knots.
        const int margin = 1;
        var candidates = new List<TerrainBirdPerchCandidate>();
        foreach (TerrainCell cell in terrain.cells)
        {
            if (
                cell.x < margin ||
                cell.y < margin ||
                cell.x >= terrain.width - margin ||
                cell.y >= terrain.height - margin
            )
                continue;

            // Natural sky life also needs coverage above open travel ground. Requiring a raised Solid cap for every
            // route left broad Hub lawns and Endless clearings with zero local supply; distant cliff routes could fill
            // the numeric pool while never crossing the screen. These lower-scored ground stops read as foraging/rest
            // beats between flights and are considered only in profiles that already opt into ordinary birds.
            if (cell.type == TileType.Floor && cell.walkable)
            {
                double groundSalt =
                    (double)cell.id * 811 + (double)terrain.width * 37 + (double)terrain.height * 43 +
                    Math.imul(Js.ToInt32(effectSeed), 97);
                candidates.push(new TerrainBirdPerchCandidate
                {
                    id = cell.id,
                    ox = 0.24 + hashTerrainCell(groundSalt + 11) * 0.52,
                    oy = 0.24 + hashTerrainCell(groundSalt + 13) * 0.52,
                    z = cell.surfaceZ + 0.5,
                    cell = cell,
                    exposure = 0.35,
                    score = cell.surfaceZ * 0.18 + hashTerrainCell(groundSalt + 5) * 0.52 - 0.45,
                });
                continue;
            }
            if (cell.type != TileType.Solid) continue;

            double exposure = 0;
            int openFaces = 0;
            string bestDirection = "s";
            double bestDrop = -1;
            foreach (TerrainDirection direction in TerrainDirections)
            {
                TerrainEdge edge = cell.edges[direction.key]!;
                bool opensToAir = edge.contactType != TileType.Solid || edge.relation == "outer";
                bool readableDrop = edge.drop > 0.5;
                if (!opensToAir && !readableDrop) continue;
                double faceExposure = Math.max(edge.drop, opensToAir ? 0.62 : 0);
                exposure += faceExposure;
                openFaces++;
                if (faceExposure > bestDrop)
                {
                    bestDrop = faceExposure;
                    bestDirection = direction.key;
                }
            }
            if (openFaces == 0 || exposure < 0.75) continue;

            double salt =
                (double)cell.id * 811 + (double)terrain.width * 37 + (double)terrain.height * 43 +
                Math.imul(Js.ToInt32(effectSeed), 97);
            (double ox, double oy) perch = birdPerchLocalOffset(bestDirection, salt);
            candidates.push(new TerrainBirdPerchCandidate
            {
                id = cell.id,
                ox = perch.ox,
                oy = perch.oy,
                z = cell.surfaceZ + 0.13,
                cell = cell,
                exposure = exposure,
                score =
                    cell.surfaceZ * 0.72 +
                    exposure * 1.35 +
                    openFaces * 0.18 +
                    hashTerrainCell(salt + 5) * 0.54,
            });
        }

        candidates.sort((a, b) =>
        {
            double byScore = b.score - a.score;
            return Js.Truthy(byScore) ? byScore : a.id - b.id;
        });
        return thinBirdPerchCandidates(candidates, terrain);
    }

    /// <summary>`Pick&lt;TerrainBirdPerch, 'ox' | 'oy'&gt;`.</summary>
    private static (double ox, double oy) birdPerchLocalOffset(string direction, double salt)
    {
        double along = 0.28 + hashTerrainCell(salt + 11) * 0.44;
        double inset = 0.18 + hashTerrainCell(salt + 13) * 0.12;
        if (direction == "n") return (along, inset);
        if (direction == "s") return (along, 1 - inset);
        if (direction == "e") return (1 - inset, along);
        return (inset, along);
    }

    private static List<TerrainBirdPerchCandidate> thinBirdPerchCandidates(
        List<TerrainBirdPerchCandidate> candidates,
        MaterializedTerrain terrain)
    {
        double minSpacing = clamp(Math.min(terrain.width, terrain.height) * 0.12, 2.8, 5.8);
        var kept = new List<TerrainBirdPerchCandidate>();
        int maxCandidates = Math.max(12, Math.min(48, (int)Math.floor((double)terrain.cells.Length / 18)));
        List<TerrainBirdPerchCandidate> raised = candidates.filter((candidate) => candidate.cell.type == TileType.Solid);
        List<TerrainBirdPerchCandidate> ground = candidates.filter((candidate) => candidate.cell.type == TileType.Floor);
        var coverageOrder = new List<TerrainBirdPerchCandidate>();
        for (int index = 0; index < Math.max(raised.Count, ground.Count); index++)
        {
            if (index < raised.Count) coverageOrder.push(raised[index]);
            if (index < ground.Count) coverageOrder.push(ground[index]);
        }
        foreach (TerrainBirdPerchCandidate c in coverageOrder)
        {
            if (kept.Count >= maxCandidates) break;
            if (kept.some((k) => birdPerchDistance(k, c) < minSpacing)) continue;
            kept.push(c);
        }
        return kept.Count >= 2 ? kept : candidates.slice(0, Math.min(candidates.Count, maxCandidates));
    }

    private static TerrainBirdPerchCandidate? pickBirdStart(
        List<TerrainBirdPerchCandidate> candidates,
        Dictionary<int, int> perchUseCounts,
        double salt,
        bool preferGround)
    {
        // Balance every stop in the route, not just its first one. The earlier start-only reservation still let all
        // birds converge on the same attractive second and third caps whenever they landed.
        double minimumUse = Math.min(
            candidates.map((candidate) => (double)birdPerchUseCount(perchUseCounts, candidate.id)).ToArray());
        bool hasLeastUsedGround =
            preferGround &&
            candidates.some(
                (candidate) =>
                    candidate.cell.type == TileType.Floor &&
                    birdPerchUseCount(perchUseCounts, candidate.id) == minimumUse);
        TerrainBirdPerchCandidate? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (TerrainBirdPerchCandidate c in candidates)
        {
            int useCount = birdPerchUseCount(perchUseCounts, c.id);
            if (useCount != minimumUse) continue;
            if (hasLeastUsedGround && c.cell.type != TileType.Floor) continue;
            double nearestUsed = double.PositiveInfinity;
            foreach (TerrainBirdPerchCandidate used in candidates)
            {
                if (!perchUseCounts.ContainsKey(used.id)) continue;
                nearestUsed = Math.min(nearestUsed, birdPerchDistance(c, used));
            }
            // Once a first cohort exists, spatial coverage is more important than repeatedly choosing the next-highest
            // exposed cap in the same ridge knot. This makes the bounded route budget cover chunk corners and seams;
            // the former score-only order could author dozens of birds while leaving a whole streamed viewport empty.
            double coverageBonus = Number.isFinite(nearestUsed) ? Math.min(14, nearestUsed) * 0.62 : 0;
            double score = c.score + coverageBonus + hashTerrainCell(salt + (double)c.id * 7) * 0.42;
            if (score <= bestScore) continue;
            best = c;
            bestScore = score;
        }
        return best;
    }

    private static TerrainBirdPerchCandidate? pickBirdDestination(
        TerrainBirdPerchCandidate from,
        List<TerrainBirdPerchCandidate> candidates,
        HashSet<int> blocked,
        Dictionary<int, int> perchUseCounts,
        double salt,
        double minDistance,
        double maxDistance)
    {
        List<TerrainBirdPerchCandidate> available = candidates.filter((candidate) =>
        {
            if (blocked.Contains(candidate.id)) return false;
            double distance = birdPerchDistance(from, candidate);
            return distance >= minDistance * 0.72 && distance <= maxDistance * 1.24;
        });
        // `Math.min(...[])` is +Infinity; the loop below then selects nothing.
        double minimumUse = Math.min(
            available.map((candidate) => (double)birdPerchUseCount(perchUseCounts, candidate.id)).ToArray());
        TerrainBirdPerchCandidate? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (TerrainBirdPerchCandidate c in available)
        {
            if (birdPerchUseCount(perchUseCounts, c.id) != minimumUse) continue;
            double distance = birdPerchDistance(from, c);
            double distanceFit =
                1 - Math.abs(distance - (minDistance + maxDistance) * 0.5) / Math.max(1, maxDistance);
            double verticalInterest = Math.min(1.5, Math.abs(c.z - from.z)) * 0.12;
            double score =
                c.score * 0.42 +
                distanceFit * 1.25 +
                verticalInterest +
                hashTerrainCell(salt + (double)c.id * 13 + (double)from.id * 5) * 0.62;
            if (score <= bestScore) continue;
            best = c;
            bestScore = score;
        }
        return best;
    }

    private static void markBirdPerchUsed(Dictionary<int, int> perchUseCounts, int perchId)
    {
        perchUseCounts[perchId] = birdPerchUseCount(perchUseCounts, perchId) + 1;
    }

    private static double birdRouteDistance(IReadOnlyList<TerrainBirdPerch> perches)
    {
        double total = 0;
        for (int i = 0; i < perches.Count; i++)
        {
            TerrainBirdPerch a = perches[i];
            TerrainBirdPerch b = perches[(i + 1) % perches.Count];
            total += birdPerchDistance(a, b);
        }
        return total / Math.max(1, perches.Count);
    }

    private static double birdPerchDistance(TerrainBirdPerch a, TerrainBirdPerch b)
    {
        double dx = a.ox - b.ox;
        double dy = a.oy - b.oy;
        // `'cell' in a` — only perch candidates carry their cell.
        double ax = a is TerrainBirdPerchCandidate ca ? ca.cell.x : 0;
        double ay = a is TerrainBirdPerchCandidate ca2 ? ca2.cell.y : 0;
        double bx = b is TerrainBirdPerchCandidate cb ? cb.cell.x : 0;
        double by = b is TerrainBirdPerchCandidate cb2 ? cb2.cell.y : 0;
        return Math.hypot(ax + dx - bx, ay + dy - by);
    }

    private static TerrainProceduralEffects emptyTerrainEffects() => new TerrainProceduralEffects
    {
        waterWaves = new List<TerrainWaterWaveEffect>(),
        bubbles = new List<TerrainBubbleEffect>(),
        rockFalls = new List<TerrainRockFallEffect>(),
        rollingGrass = new List<TerrainRollingGrassEffect>(),
        birds = new List<TerrainBirdEffect>(),
        lilyPads = new List<TerrainLilyPadEffect>(),
        fishShoals = new List<TerrainFishShoalEffect>(),
        reedBeds = new List<TerrainReedBedEffect>(),
        butterflies = new List<TerrainButterflyEffect>(),
        ambientMotifs = new List<TerrainAmbientMotifEffect>(),
        rockGrass = new List<TerrainRockGrassEffect>(),
        fireflies = new List<TerrainFireflyEffect>(),
        grassPatches = new List<TerrainGrassPatchEffect>(),
        trees = new List<TerrainTreeDressingEffect>(),
        worldDecorations = new List<TerrainWorldDecorationEffect>(),
        chasmMist = new List<TerrainChasmMistEffect>(),
        wallStrands = new List<TerrainWallStrandEffect>(),
    };

    private static TerrainMaterial defaultTerrainMaterialForCell(
        TerrainCell cell,
        MaterializedTerrain _terrain,
        TerrainWaterInfo _waterInfo,
        double moisture)
    {
        if (cell.type == TileType.Chasm) return STANDARD_TERRAIN_MATERIALS.chasm;
        if (cell.type == TileType.Water) return STANDARD_TERRAIN_MATERIALS.water;
        if (cell.type == TileType.Bridge) return STANDARD_TERRAIN_MATERIALS.bridgeWood;
        if (cell.type == TileType.Solid)
        {
            return moisture > 0.42
                ? STANDARD_TERRAIN_MATERIALS.wallMoss
                : STANDARD_TERRAIN_MATERIALS.wallChalk;
        }
        if (moisture > 0.36) return STANDARD_TERRAIN_MATERIALS.floorDamp;
        if (cell.elevation >= 4) return STANDARD_TERRAIN_MATERIALS.floorDry;
        return STANDARD_TERRAIN_MATERIALS.floorCool;
    }

    public static TerrainWaterInfo waterInfoForCell(
        TerrainCell cell,
        MaterializedTerrain? terrain,
        TerrainWaterInfo? reuse = null)
    {
        TerrainWaterInfo info = reuse ?? new TerrainWaterInfo();
        TerrainWaterFlow flow = info.flow ?? new TerrainWaterFlow { x = 0, y = 0 };
        if (terrain == null || !isFluidTerrainCell(cell))
        {
            info.depth = 0;
            info.shoreline = 0;
            info.calm = true;
            flow.x = 0;
            flow.y = 0;
            info.flow = flow;
            info.flowSpeed = 0;
            return info;
        }
        int cardinalFluid = 0;
        int allFluid = 0;
        int wallContacts = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                TerrainCell? n = terrainCellAt(terrain, cell.x + dx, cell.y + dy);
                if (isFluidTerrainCell(n)) allFluid++;
                if (Math.abs(dx) + Math.abs(dy) == 1)
                {
                    if (isFluidTerrainCell(n)) cardinalFluid++;
                    if (n?.type == TileType.Solid || n?.type == TileType.Chasm) wallContacts++;
                }
            }
        }
        int shoreline = 4 - cardinalFluid;
        double depth = clamp(
            0.18 + allFluid * 0.078 + (shoreline == 0 ? 0.16 : 0) - wallContacts * 0.025,
            0.16,
            0.96);
        int east = isFluidTerrainCell(terrainCellAt(terrain, cell.x + 1, cell.y)) ? 1 : 0;
        int west = isFluidTerrainCell(terrainCellAt(terrain, cell.x - 1, cell.y)) ? 1 : 0;
        int south = isFluidTerrainCell(terrainCellAt(terrain, cell.x, cell.y + 1)) ? 1 : 0;
        int north = isFluidTerrainCell(terrainCellAt(terrain, cell.x, cell.y - 1)) ? 1 : 0;
        flow.x = (east - west) * 0.55 + (hashTerrainCell((double)cell.id * 181 + 5) - 0.5) * 0.24;
        flow.y = (south - north) * 0.55 + (hashTerrainCell((double)cell.id * 191 + 7) - 0.5) * 0.24;
        double length = Math.hypot(flow.x, flow.y);
        if (length > 0.001)
        {
            flow.x /= length;
            flow.y /= length;
        }
        double flowSpeed = clamp(
            0.12 + Math.abs(east - west + south - north) * 0.18 + shoreline * 0.04,
            0.12,
            0.72);
        info.depth = depth;
        info.shoreline = shoreline;
        info.calm = shoreline <= 1 && flowSpeed < 0.34;
        info.flow = flow;
        info.flowSpeed = flowSpeed;
        return info;
    }

    public static double moistureForCell(TerrainCell cell, MaterializedTerrain? terrain)
    {
        if (terrain == null) return 0;
        double score = 0;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int distance = Math.abs(dx) + Math.abs(dy);
                if (distance > 3) continue;
                if (isFluidTerrainCell(terrainCellAt(terrain, cell.x + dx, cell.y + dy)))
                {
                    score += distance == 1 ? 0.24 : 0.09;
                }
            }
        }
        return clamp(score, 0, 1);
    }

    public static bool isFluidTerrainCell(TerrainCell? cell) => terrainCellCarriesWater(cell);

    public static bool shouldRenderTerrainDepthEdge(TerrainCell _cell, TerrainEdge edge) =>
        edge.visibleFace && edge.drop > 0.02 && depthFaceVisibility(edge) > 0;

    public static bool shouldRenderBacksideDropEdge(TerrainCell _cell, TerrainEdge edge) =>
        edge.visibleFace && edge.drop > 0.02 && depthFaceVisibility(edge) <= 0;

    public static double depthFaceVisibility(TerrainEdge? edge)
    {
        if (edge == null) return 1;
        return TERRAIN_DEPTH_FACE_VISIBILITY[edge.direction] ?? 1;
    }

    private static bool isRockRun(TerrainEdgeRun run) =>
        run.edgeMaterial == "rock" ||
        run.edgeMaterial == "abyss" ||
        run.material.id.StartsWith("wall", StringComparison.Ordinal) ||
        run.cells.some((cell) => cell.type == TileType.Solid);

    public static int terrainMaterialSurfaceTopColor(TerrainMaterial material, TerrainCell cell)
    {
        if (cell.type == TileType.Chasm) return material.top;
        double elevation = Number.isFinite(cell.elevation) ? cell.elevation : 0;
        double heightFactor = 1.035 - elevation * 0.018;
        // NOTE: no per-tile "large scale tone" band here. It used to vary the top color in 8x8-tile blocks keyed to
        // the cell's LOCAL bake-region index, so as the camera re-origined the bake (every block of travel) the band
        // re-hashed and the whole floor shimmered as a faint dark grid that crawled with movement. The surface stays a
        // clean, world-stable colour; texture comes from the (also world-stable) micro-detail layer, not tile tone.
        int @base =
            cell.type == TileType.Solid ? mixColor(material.top, material.topLight, 0.2) : material.top;
        return shadeColor(@base, clamp(heightFactor, 0.87, 1.08));
    }

    /// <summary>
    /// The UNLIT albedo of a cliff/wall/bank/deck face — the material's own side colour family WITHOUT any baked
    /// directional shading. The 2D painter layers TERRAIN_LIGHT_MODEL on top (<c>terrainFaceColorForRun</c>);
    /// a real-light renderer (the Three terrain base) feeds this straight to its lit materials so direction, sun and
    /// shadow come from actual lights instead of baked factors — one colour derivation, two lighting models.
    /// </summary>
    public static int terrainFaceBaseColor(TerrainMaterial material, string edgeMaterial)
    {
        if (edgeMaterial == "abyss")
        {
            return mixColor(
                orColor(material.side, STANDARD_TERRAIN_MATERIALS.chasm.side),
                TERRAIN_EDGE_COLORS.abyss,
                0.24);
        }
        if (material.id == "bridgeWood" || edgeMaterial == "deck")
        {
            return mixColor(
                orColor(material.side, TERRAIN_EDGE_COLORS.deck),
                orColor(material.edgeDark, orColor(material.topDark, TERRAIN_EDGE_COLORS.deck)),
                0.28);
        }
        if (edgeMaterial == "bank")
        {
            int bankBase = orColor(material.topDark, orColor(material.top, orColor(material.side, TERRAIN_EDGE_COLORS.bank)));
            return mixColor(bankBase, 0x6aaeb0, 0.24);
        }
        int topDark = orColor(material.topDark, orColor(material.top, orColor(material.side, TERRAIN_EDGE_COLORS.earth)));
        int neutralShadow = material.id.StartsWith("wall", StringComparison.Ordinal) ? 0x66736f : 0x687a76;
        int coolSide = mixColor(topDark, neutralShadow, material.id.StartsWith("wall", StringComparison.Ordinal) ? 0.42 : 0.34);
        return mixColor(coolSide, TERRAIN_EDGE_COLORS.earth, 0.12);
    }

    private static int mixColor(int a, int b, double amount)
    {
        double t = clamp(amount, 0, 1);
        int ar = (a >> 16) & 0xff;
        int ag = (a >> 8) & 0xff;
        int ab = a & 0xff;
        int br = (b >> 16) & 0xff;
        int bg = (b >> 8) & 0xff;
        int bb = b & 0xff;
        return (
            (Js.ToInt32(Math.round(ar + (br - ar) * t)) << 16) |
            (Js.ToInt32(Math.round(ag + (bg - ag) * t)) << 8) |
            Js.ToInt32(Math.round(ab + (bb - ab) * t))
        );
    }

    private static int shadeColor(int hex, double factor)
    {
        double r = clamp(Math.round(((hex >> 16) & 0xff) * factor), 0, 255);
        double g = clamp(Math.round(((hex >> 8) & 0xff) * factor), 0, 255);
        double b = clamp(Math.round((hex & 0xff) * factor), 0, 255);
        return (Js.ToInt32(r) << 16) | (Js.ToInt32(g) << 8) | Js.ToInt32(b);
    }

    private static double clamp(double value, double min, double max) => Math.max(min, Math.min(max, value));

    // ── Port helpers (JS `||` on possibly-undefined numbers) ─────────────────────────────────────────────────

    /// <summary>`value || fallback` for a `number | undefined` (0, NaN and undefined are falsy).</summary>
    private static double orNumber(double? value, double fallback) =>
        value.HasValue && Js.Truthy(value.Value) ? value.Value : fallback;

    /// <summary>`colour || fallback` for a colour channel (0 is falsy).</summary>
    private static int orColor(int value, int fallback) => value != 0 ? value : fallback;
}
