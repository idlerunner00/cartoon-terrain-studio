// Port of packages/client/src/render/theme.ts — keep in lockstep with the original.
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using static Fluitown.Render.Palette;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

// Central visual style guide — the single source of colour truth for the client.
//
// Other work packages may *import* from here (read-only) so sprites, FX and UI stay in palette with
// the world. The environment renderer (ground, lighting, storm, post-FX, camera) is driven entirely
// by the Biome descriptors below: each live space (the Hub plaza and the three Run biomes)
// gets a distinct, code-generated look — base ground, accents, fog, light tint, vignette, colour
// grade and storm palette — so a glance tells you *where* you are and *how dangerous* it is.
//
// Everything is data. Adding a new biome is a registry entry, not engine code.
//
// Colours are packed 0xRRGGBB numbers and stay `int` here (see RENDER_AGENT_BRIEF "Colours"). String-literal
// unions (`pattern`, `TilesetKind`, `DecalKind`, `materialDialect`) are `string`.

/// <summary>A colour-grade applied to the world container (multiplicative ColorMatrix terms).</summary>
public sealed class ColorGrade
{
    public double brightness;
    public double contrast;
    public double saturate;
    /// <summary>Hue rotation in degrees.</summary>
    public double hue;

    /// <summary>Shallow copy, the equivalent of `{ ...grade }`.</summary>
    public ColorGrade Clone() => (ColorGrade)MemberwiseClone();
}

/// <summary>A full description of how one live space looks. Pure data — consumed by the environment renderer.</summary>
public sealed class Biome
{
    public string key;
    /// <summary>Human label (debug / future UI).</summary>
    public string name;
    /// <summary>Deep ground base colour used to derive terrain materials.</summary>
    public int ground;
    /// <summary>Terrain accent colours used by generated floors, walls and decals.</summary>
    public int groundAccentA;
    public int groundAccentB;
    /// <summary>Pattern style for the procedural ground tile: 'plaza' | 'mossStone' | 'ashCracks' | 'voidVeins'.</summary>
    public string pattern;
    /// <summary>Bright material-detail colour. 0 disables it.</summary>
    public int detail;
    /// <summary>Parallax haze blob colour (far depth layer).</summary>
    public int haze;
    /// <summary>Ambient light tint for the player glow &amp; light field.</summary>
    public int lightTint;
    /// <summary>Vignette darkness (0..1) — how much the screen edges fall to black.</summary>
    public double vignette;
    /// <summary>Screen tint laid over the frame (multiply) to push the mood.</summary>
    public int gradeTint;
    /// <summary>Strength of the grade tint overlay (0..1).</summary>
    public double gradeTintAlpha;
    /// <summary>ColorMatrix grade applied to the world.</summary>
    public ColorGrade grade;
    /// <summary>Storm wall colours (core glow + body fill).</summary>
    public int stormCore;
    public int stormBody;
    /// <summary>Lightning bolt colour for this biome's storm.</summary>
    public int stormBolt;
    /// <summary>
    /// The biome's signature **floor-decal family** (DecalKind) — the SHAPE language drawn (baked) onto the opaque
    /// terrace tops, restoring the per-biome ground identity the elevation terraces otherwise hide (the rich
    /// <see cref="pattern"/> ground tile is fully covered in a run). Curated per biome on the descriptor; its tints
    /// derive from the biome colours (<see cref="Theme.decalOf"/>) so a new biome stays in palette for free.
    /// Purely cosmetic.
    /// </summary>
    public string decal;
    /// <summary>Authored water palette override — a theme whose water is NOT storm-derived (lava, meltwater, ink…)
    /// states it outright; <see cref="Theme.floodColors"/> returns it verbatim. Absent ⇒ the shared derivation.</summary>
    public FloodPalette? water;
    /// <summary>Timber hue anchor for this theme's bridge decks (charred, mossy, frost-bitten…). Absent ⇒ the shared
    /// WORLD_TIMBER warm brown. Consumed by <see cref="Theme.bridgeOf"/>.</summary>
    public int? timber;
    /// <summary>Stone hue anchor for this theme's rock walls. Only the HUE/saturation family is taken —
    /// <see cref="Theme.terrainOf"/> still lifts it into the bright wall-luminance band, so the "walls read as walls"
    /// contract holds in every theme. Absent ⇒ the neutral grey anchor.</summary>
    public int? stone;
    /// <summary>The MATERIAL colour of this theme's walkable ground — what the terrace ramp
    /// (<see cref="Theme.elevationOf"/>) actually mixes toward (deep moss, charred crust, packed snow…). Absent ⇒ the
    /// bright <see cref="detail"/> glint colour (the legacy derivation), which suits particles but washes a themed
    /// floor out.</summary>
    public int? groundTint;
    /// <summary>Per-theme world-surface styling (pattern gains, hue drift, water animation, material tint strengths) —
    /// pure uniform/derivation data, never a geometry rebuild. Absent ⇒ DEFAULT_WORLD_STYLE.</summary>
    public WorldStyle? style;
    /// <summary>The theme's **tileset language** (TilesetKind) — how the world bake shapes its terrain for this run
    /// (organic floors, contour warp, wall growth, themed ground dressing). Absent ⇒ DEFAULT_TILESET
    /// ('natural').</summary>
    public string? tileset;
    /// <summary>Optional authored material dialect ('aegis-citadel' | 'viking-ship-village') for a place whose value
    /// structure cannot be expressed as a hue tint alone. It survives run/raid escalation as immutable theme data
    /// and only changes derived palette values — never material count, shader count or terrain geometry.</summary>
    public string? materialDialect;

    public Biome() { }

    /// <summary>Shallow copy constructor, the equivalent of `{ ...source }` (grade/water/style stay shared).</summary>
    public Biome(Biome source)
    {
        key = source.key;
        name = source.name;
        ground = source.ground;
        groundAccentA = source.groundAccentA;
        groundAccentB = source.groundAccentB;
        pattern = source.pattern;
        detail = source.detail;
        haze = source.haze;
        lightTint = source.lightTint;
        vignette = source.vignette;
        gradeTint = source.gradeTint;
        gradeTintAlpha = source.gradeTintAlpha;
        grade = source.grade;
        stormCore = source.stormCore;
        stormBody = source.stormBody;
        stormBolt = source.stormBolt;
        decal = source.decal;
        water = source.water;
        timber = source.timber;
        stone = source.stone;
        groundTint = source.groundTint;
        style = source.style;
        tileset = source.tileset;
        materialDialect = source.materialDialect;
    }
}

// A theme's WALL/FLOOR **construction language** (TilesetKind) — the key that tunes how the 3D bake shapes its
// terrain (organic floors, contour warp, wall growth) and which themed ground dressing it scatters. Every language
// builds terrain only: rock cliffs, terraces, water and plants; the names are those of the worlds they came from.
//
// TilesetKind = 'natural' | 'combined-building' | 'city' | 'olympian' | 'prism' | 'carnival' | 'clockwork'
//             | 'prismglass' | 'cathedral' | 'viking-ship-village' | 'alien-ranch'  → `string`.

/// <summary>
/// Per-theme **world-surface style** — the numeric character of a theme's materials and air, consumed as
/// shader uniforms + derivation weights by the 3D terrain layer (zero per-frame rebuild cost; a biome change
/// evicts the tile cache anyway). This is what makes a theme's *material* differ, not just its colours:
/// mossy soft floors vs cracked cinder crust, still ink mirrors vs racing meltwater.
/// </summary>
public sealed class WorldStyle
{
    /// <summary>Multiplier on the floor caps' biome-terrace tint weight (1 = the shared chalk-paper default).</summary>
    public double floorTint;
    /// <summary>Multiplier on how much of the biome ACCENT the terrace ramp itself carries (<see cref="Theme.elevationOf"/>) —
    /// the lever that makes a hollow's ground actually LUSH and a maw's actually bruised, instead of a pale wash.
    /// Bounded in the derivation so floors always stay below the wall-luminance band.</summary>
    public double groundAccent;
    /// <summary>Multiplier on the rock caps' biome-stone tint weight — hue only, the wall-luminance contract holds.</summary>
    public double wallTint;
    /// <summary>Multiplier on the water surface's flood-palette tint weight (authored lava/ink water pushes this up).</summary>
    public double waterTint;
    /// <summary>Multiplier on the bridge deck's timber tint weight.</summary>
    public double bridgeTint;
    /// <summary>Micro-pattern gains for the surface shader: floor mottle, rock grain, face strata.</summary>
    public double floorGrain;
    public double rockGrain;
    public double strata;
    /// <summary>Painterly hue-drift poles (hex) + amplitude — the very-low-frequency wash across caps/floors. The two
    /// colours give the drift its palette (ember↔ash, ice↔steel…); brightness is normalized out.</summary>
    public int driftA;
    public int driftB;
    public double driftAmp;
    /// <summary>Water animation character: ripple speed, foam amount, crest-glint amount (1 = shared default).</summary>
    public double waveSpeed;
    public double foam;
    public double glint;
    /// <summary>Coherent vegetation motion: prevailing direction (radians), bend strength, tempo and gust envelope.</summary>
    public double windDirection;
    public double windStrength;
    public double windSpeed;
    public double windGust;
    /// <summary>Multipliers on the shared 3D light rig (1 = the rig exactly as authored): key sun, sky hemisphere,
    /// cool bounce fill and the flat ambient floor. Pure data here — the terrain layer consumes them as
    /// rig intensities, never per-frame work. Authored inside [0.5, 1.5] (theme tests enforce it) so a
    /// theme can go noir (dim sun, hard fill) or submarine (drowned sun, sea-dome hemisphere) without
    /// ever blowing out or blacking out the scene.</summary>
    public double sunLight;
    public double hemiLight;
    public double fillLight;
    public double ambientLight;

    public WorldStyle() { }

    /// <summary>Copy constructor, the equivalent of `{ ...source }` (every field is a number).</summary>
    public WorldStyle(WorldStyle source)
    {
        floorTint = source.floorTint;
        groundAccent = source.groundAccent;
        wallTint = source.wallTint;
        waterTint = source.waterTint;
        bridgeTint = source.bridgeTint;
        floorGrain = source.floorGrain;
        rockGrain = source.rockGrain;
        strata = source.strata;
        driftA = source.driftA;
        driftB = source.driftB;
        driftAmp = source.driftAmp;
        waveSpeed = source.waveSpeed;
        foam = source.foam;
        glint = source.glint;
        windDirection = source.windDirection;
        windStrength = source.windStrength;
        windSpeed = source.windSpeed;
        windGust = source.windGust;
        sunLight = source.sunLight;
        hemiLight = source.hemiLight;
        fillLight = source.fillLight;
        ambientLight = source.ambientLight;
    }
}

// The biome's signature **floor-decal family** (DecalKind) — the SHAPE language scattered across the terrace tops
// so each run reads as its own place underfoot (alpine scree, verdant flora, ashen cinder cracks, frost rime,
// wind-scoured gale, neon circuit traces/glitch barcodes, deepsea reef scars, void arcane crystal, bog reeds).
// Code-only, baked with the geometry.
//
// DecalKind = 'scree' | 'flora' | 'cinder' | 'rime' | 'gale' | 'circuit' | 'arcane' | 'bog' | 'cloud' | 'bubble'
//           | 'reef' | 'rainbow' | 'carnival' | 'gear' | 'confetti' | 'olympian' | 'prism' | 'glassShard' | 'star'
//           | 'cathedralStar' | 'runeKnot' | 'cropCircle'  → `string`.

/// <summary>
/// Tints for a biome's floor decals + wall accents. Only the DecalKind (the shape family) is curated
/// per biome; the colours are DERIVED from the biome's own palette here — so a verdant decal reads green, an
/// ashen decal reads hot rust/ember, a void decal violet, with no hand-authored table and a new biome in
/// palette for free. The single source consumed by the DungeonLayer.
/// </summary>
public sealed class DecalSpec
{
    /// <summary>DecalKind.</summary>
    public string kind;
    /// <summary>Dark biome-shadow ink — stone shards, crack bodies, roots, reed stems, lily-pad rims.</summary>
    public int ink;
    /// <summary>Biome material mid — pebbles, moss/grass tufts, ice slivers, crystal facets, lily pads.</summary>
    public int mid;
    /// <summary>Bright biome signature accent — ember spark, spore glint, ice shine, rune/crystal sheen, bog bubble.
    /// Reuses the biome's <see cref="Biome.detail"/> so the floor's bright specks match the drifting ambient air.</summary>
    public int accent;
}

/// <summary>
/// Material palette for the tiled dungeon geometry (textured floors + plastic, extruded walls). Derived
/// once per biome from its core colours — so every run/raid is materially distinct with no hand-authored
/// table, and a new biome stays in palette for free. This is the single source of the dungeon's stone/floor
/// colour truth, consumed by the DungeonLayer and the minimap.
/// </summary>
public sealed class TerrainPalette
{
    /// <summary>Elevation represented by wallFill[0].</summary>
    public int minLevel;
    /// <summary>Floor material tone laid (low alpha) over the biome ground tile so floor reads distinct from rock.</summary>
    public int floorTone;
    /// <summary>Brighter floor sheen for lit specks / open-field highlights.</summary>
    public int floorLit;
    /// <summary>Grout / seam ink between floor tiles.</summary>
    public int floorSeam;
    /// <summary>Rock wall body — the cap seen from straight above (≡ <see cref="wallFill"/>[2], the massif body level).</summary>
    public int wall;
    /// <summary>
    /// Per-level OPAQUE rock-cap colour (index = elevation − minLevel) — the single source for how a wall top
    /// is shaded by height. Unlike the floor's <see cref="ElevationPalette.topFill"/>, this is a deliberately BRIGHT,
    /// low-saturation biome-tinted STONE ramp that stays clearly ABOVE the floor's brightness band in every
    /// biome: a wall must read as a wall at a glance — its own contrasting colour/brightness, never "just a
    /// darker floor" (the dark biomes used to sink walls to a near-black slab indistinguishable from the floor).
    /// Consumed by the world bake AND the minimap so blocked rock looks the same underfoot and on the radar.
    /// </summary>
    public int[] wallFill;
    /// <summary>Lit top edge of a wall (the back rim that catches the key light from the north).</summary>
    public int wallLit;
    /// <summary>Extruded front (south) wall face — the visible "height" of the standing block.</summary>
    public int wallFace;
    /// <summary>Deep base of the wall face (gradient bottom) + east/west side shading.</summary>
    public int wallDeep;
    /// <summary>Bold outline ink around wall silhouettes (Don't-Starve style).</summary>
    public int wallLine;
    /// <summary>Floor-side contact shadow / ambient occlusion hugging the walls.</summary>
    public int contact;
    /// <summary>Deterministic floor-prop accent (rubble / cracks / inlays).</summary>
    public int prop;
}

/// <summary>
/// Material palette for the signed −25..+25 walkable-ground terraces
/// are shaded so "up" vs "down" reads at a glance, plus the cliff/step faces, top-lips and cast shadows
/// that join adjacent levels seamlessly. Derived once per biome from its core colours (like
/// <see cref="TerrainPalette"/>), so every space terraces in its own palette with no hand-authored table. Consumed by
/// the DungeonLayer and the minimap.
/// </summary>
public sealed class ElevationPalette
{
    /// <summary>Elevation represented by topFill[0].</summary>
    public int minLevel;
    /// <summary>
    /// Per-level OPAQUE terrace-top colour (index = level − minLevel): a clean monotonic ramp from a
    /// lighter low floor to a darker high plateau. The terrace tops are baked as flat opaque fills of these
    /// colours (NOT a tiled ground bitmap) — a detailed tile minified across a whole terrace moirés into the
    /// horizontal "streak/speckle" that made the runs look raw; a flat baked tone + sparse drawn detail stays
    /// clean at every distance and is the Hub-plaza discipline. Fine organic relief is drawn on top per terrace.
    /// </summary>
    public int[] topFill;
    /// <summary>The earthy front face of a terrace step (the visible "height" between two floor levels).</summary>
    public int cliffFace;
    /// <summary>Deep shaded base of a cliff/wall face (the bottom of the fake vertical gradient).</summary>
    public int cliffDeep;
    /// <summary>Bright sunlit rim along the TOP edge of a step/wall (light comes from the north-west).</summary>
    public int lit;
    /// <summary>Soft shadow a raised terrace/wall casts down onto the lower ground at the foot/side of its face.</summary>
    public int shadow;
}

/// <summary>The flood/water colour set for a biome (body → surface → foam crest). Pure; derived from the storm hues.</summary>
public sealed class FloodPalette
{
    /// <summary>The deep submerged body fill.</summary>
    public int body;
    /// <summary>A darker tone for the deepest water, used in the depth gradient.</summary>
    public int deep;
    /// <summary>The bright churning waterline surface.</summary>
    public int surface;
    /// <summary>Near-white foam crest along the breaking edge.</summary>
    public int foam;
    /// <summary>Caustic/lightning glint colour.</summary>
    public int glint;
    /// <summary>
    /// PROVENANCE, set only by <see cref="Theme.floodColors"/>' fallback: this palette was *derived* from the biome's
    /// own pigment family, nobody authored it. An authored <see cref="Biome.water"/> — and any palette a registered
    /// tileset factory states outright — never carries it. (TS type `derived?: true`; C# `null` = absent.)
    ///
    /// It exists because the renderer must treat the two cases differently and there is no other honest way to
    /// tell them apart. A theme that states its water has made an art statement (lava, oily canal, meltwater,
    /// ink), and the tile then IS that statement: no shared constant gets a vote. A derived palette is a
    /// best-effort guess, and there the untinted STANDARD_TERRAIN_MATERIALS.water still contributes the
    /// value structure that makes water read as a hole in the ground.
    ///
    /// Ignoring the distinction is what broke the previous attempt at this fix: neutralising the shared base
    /// (correct, it was biome-blind cyan) and reweighting the mix (correct for the 20 derived themes) silently
    /// repainted the 11 authored lakes as well — measured on the same pixels of the hub frame, the pool went
    /// from `#2b494b` (hue 183°, sat 0.292) to `#373735` (hue 57°, sat 0.031), i.e. it adopted the sand's hue.
    /// </summary>
    public bool? derived;

    /// <summary>Shallow copy, the equivalent of `{ ...flood }`.</summary>
    public FloodPalette Clone() => (FloodPalette)MemberwiseClone();

    /// <summary>
    /// terrainRenderPlan.ts: "The client's `FloodPalette` satisfies [TerrainWaterPigment]". TS checks that
    /// structurally; C# needs a conversion, made implicit so a FloodPalette can be passed wherever the domain expects
    /// a TerrainWaterPigment (e.g. <c>TerrainRenderPlanModule.waterTerrainMaterial(tileset.flood, …)</c>).
    /// </summary>
    public static implicit operator TerrainWaterPigment(FloodPalette flood) => new TerrainWaterPigment
    {
        body = flood.body,
        deep = flood.deep,
        surface = flood.surface,
        foam = flood.foam,
        glint = flood.glint,
        derived = flood.derived,
    };
}

/// <summary>A biome's **pigment family** — what an illustrator would call "the colours this sheet is painted in".</summary>
public sealed class PigmentFamily
{
    /// <summary>Saturation-weighted circular MEAN hue of the frame's covering surfaces, in degrees [0,360).</summary>
    public double hue;
    /// <summary>Mean HSL saturation of those surfaces — how loud the sheet is allowed to be.</summary>
    public double saturation;
    /// <summary>Mean HSL lightness of those surfaces — the value the water has to sit BELOW to read as depth.</summary>
    public double lightness;
}

/// <summary>Shared bridge material for the terrain tileset. Renderers consume this; they do not invent bridge colours.</summary>
public sealed class BridgePalette
{
    /// <summary>Main weathered deck fill.</summary>
    public int body;
    /// <summary>Dark deck shade, also used where water tucks under a bridge.</summary>
    public int bodyShadow;
    /// <summary>Broad inner deck highlight.</summary>
    public int center;
    /// <summary>Sparse grain/detail line.</summary>
    public int grain;
    /// <summary>Rare plank join.</summary>
    public int seam;
    /// <summary>Exposed deck outline.</summary>
    public int edge;
    /// <summary>Sunlit top lip.</summary>
    public int lip;
    /// <summary>Rail/cap strip over void edges.</summary>
    public int rail;
    /// <summary>Rail highlight.</summary>
    public int railLit;
    /// <summary>Land-approach cuff.</summary>
    public int cuff;
}

public static partial class Theme
{
    /// <summary>Shared paper/ink world tokens, matching the new UI identity.</summary>
    public static class WORLD_INK
    {
        public const int ink = 0x050505;
        public const int paper = 0xfffdf3;
        public const int paperWarm = 0xf2efe4;
        public const int paperDeep = 0xb9aa80;
        public const int yellow = 0xf2d22e;
        public const int cyan = 0x32b9c6;
        public const int red = 0xf05a49;
        public const int green = 0x42b96d;
        public const int violet = 0x8e61d1;
    }

    /// <summary>Rainbowland's fixed prism stripe set, reused by terrain decals and 3D dressing so the run's colours stay
    /// recognisably the same across floor paint, wall facets, bridge rails and glitter.</summary>
    public static readonly IReadOnlyList<int> RAINBOW_PRISM_COLORS = new[] { 0xff3f7a, 0xffe95b, 0x5cff72, 0x45c7ff, 0x8f5cff };

    /// <summary>Sugarstorm-Carnival's midway set: frosting pink, dangerous syrup magenta, mint neon, violet candy rail and
    /// harsh bulb-gold. Kept separate from Rainbowland so Run 8 reads as a fairground, not a prism repaint.</summary>
    public static readonly IReadOnlyList<int> SUGARSTORM_CARNIVAL_COLORS = new[]
    {
        0xff4f96, 0xd02c86, 0x61e8c7, 0x8c4dd8, 0xffe36a,
    };

    /// <summary>Shared timber ramp for bridge decks and other readable world wood. It stays warm-brown rather than
    /// UI-yellow, so crossings read as material instead of command chrome.</summary>
    public static class WORLD_TIMBER
    {
        public const int mid = 0x9f6a2e;
    }

    /// <summary>
    /// Project-wide **ART DIRECTION** — the SINGLE SOURCE for the screen-space "look" the one post-process pass
    /// (PostFx) lays over *every* live space. It is the unifying character that binds the whole game into one
    /// coherent picture in the spirit of the reference art:
    ///
    ///  - a very subtle **CRT / scanline** grade with static fine texture,
    ///  - a gentle **chromatic-aberration** colour fringe toward the screen corners,
    ///  - a heavier, framing **vignette**, and
    ///  - a cohesive **duotone cohesion wash** — a muted violet-indigo pulled (multiply) over the frame so the
    ///    palette reads as one family (muted violet tones) while the cyan/teal mids and the warm orange/gold FX
    ///    pops still sing through it.
    ///
    /// Per-biome <see cref="Biome.grade"/>/<see cref="Biome.gradeTint"/> still set each space's individual mood ON TOP
    /// of this global character — so a glance still tells you *where* you are, but every space now belongs to one
    /// world. Tune the look here and nowhere else.
    /// </summary>
    public static class ARTDIRECTION
    {
        /// <summary>Project-wide grade baked once into terrain albedo by the terrain compiler. Post-processing applies only
        /// a restrained residual of each biome's grade, so this remains the single global palette control.
        ///  - saturateBoost: added saturation (the biggest lever against the washed-out, desaturated read),
        ///  - contrastBoost: added contrast (deeper darks, brighter lights — bold DST value structure),
        ///  - brightnessBoost: multiplicative lift so the mids sit up off near-black without bleaching.</summary>
        public const double saturateBoost = 0.15;
        public const double contrastBoost = 0.08;
        public const double brightnessBoost = 1.01;
    }

    /// <summary>The founding tileset every theme uses unless it authors another.</summary>
    public const string DEFAULT_TILESET = "natural";

    /// <summary>Resolve a biome's tileset construction language (authored or the founding default). Pure.</summary>
    public static string tilesetOf(Biome biome)
    {
        return biome.tileset ?? DEFAULT_TILESET;
    }

    /// <summary>The shared default style — reproduces the pre-theme look exactly; every field is a neutral 1 / the
    /// original warm↔cool drift pair.</summary>
    public static readonly WorldStyle DEFAULT_WORLD_STYLE = new()
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

    /// <summary>Resolve a biome's world style (authored or the shared default). Pure.</summary>
    public static WorldStyle worldStyleOf(Biome biome)
    {
        return biome.style ?? DEFAULT_WORLD_STYLE;
    }

    /// <summary>Derive the floor-decal palette for a biome from its core colours (pure; cache at the call site).</summary>
    public static DecalSpec decalOf(Biome biome)
    {
        if (biome.decal == "runeKnot")
        {
            return new DecalSpec
            {
                kind = biome.decal,
                ink = 0x17100d,
                mid = mix(0x6e3c24, biome.groundAccentA, 0.28),
                accent = mix(0xffc96a, biome.detail, 0.34),
            };
        }
        if (biome.decal == "cropCircle")
        {
            return new DecalSpec
            {
                kind = biome.decal,
                ink = 0x241634, // ultraviolet scorch where the beam pressed the turf flat
                mid = mix(0x9adf4a, biome.detail, 0.35), // combed acid-chlorophyll swirl against the violet turf
                accent = mix(0xd6ff7e, biome.detail, 0.4), // residual harvest glow at the glyph's heart
            };
        }
        if (biome.decal == "rainbow")
        {
            return new DecalSpec
            {
                kind = biome.decal,
                ink = mix(WORLD_INK.ink, RAINBOW_PRISM_COLORS[4], 0.2),
                mid = RAINBOW_PRISM_COLORS[1],
                accent = RAINBOW_PRISM_COLORS[3],
            };
        }
        if (biome.decal == "carnival")
        {
            return new DecalSpec
            {
                kind = biome.decal,
                ink = 0x7a225a,
                mid = SUGARSTORM_CARNIVAL_COLORS[0],
                accent = SUGARSTORM_CARNIVAL_COLORS[4],
            };
        }
        if (biome.decal == "reef")
        {
            return new DecalSpec
            {
                kind = biome.decal,
                ink = 0x010712,
                mid = mix(0x06243a, biome.groundAccentB, 0.48),
                accent = mix(0xb8fff4, biome.detail, 0.55),
            };
        }
        if (biome.decal == "olympian")
        {
            if (biome.materialDialect == "aegis-citadel")
            {
                return new DecalSpec
                {
                    kind = biome.decal,
                    ink = WORLD_INK.ink,
                    mid = 0xe8e7e1,
                    accent = 0xa9adb1,
                };
            }
            return new DecalSpec
            {
                kind = biome.decal,
                ink = 0x8a7440,
                mid = mix(0xf7f1df, biome.groundAccentA, 0.24),
                accent = biome.detail,
            };
        }
        if (biome.decal == "glassShard")
        {
            return new DecalSpec
            {
                kind = biome.decal,
                ink = 0x070816,
                mid = mix(0xbff7ff, biome.groundAccentB, 0.26),
                accent = mix(0xffffff, biome.detail, 0.36),
            };
        }
        if (biome.decal == "cathedralStar")
        {
            return new DecalSpec
            {
                kind = biome.decal,
                ink = 0x050309,
                mid = mix(0xf0e3c4, biome.groundAccentA, 0.18),
                accent = mix(0xfff0bd, biome.detail, 0.52),
            };
        }
        if (biome.decal == "gear")
        {
            return new DecalSpec
            {
                kind = biome.decal,
                ink = 0x070605,
                mid = mix(0xc8963c, biome.groundAccentA, 0.48),
                accent = mix(0x72ffe0, biome.groundAccentB, 0.24),
            };
        }
        int material = mix(WORLD_INK.paperWarm, biome.groundAccentB, 0.34);
        return new DecalSpec
        {
            kind = biome.decal,
            ink = WORLD_INK.ink,
            mid = material,
            accent = biome.detail,
        };
    }

    /// <summary>Neutral lit-rock grey the biome accent is pulled toward, so a wall keeps biome hue yet always lands as a
    /// bright, light stone that contrasts the (darker, more saturated) walkable floor regardless of the biome hue.
    /// Code-only constant — the neutral anchor of the "walls are bright biome-tinted stone" readability contract.</summary>
    private const int STONE_GREY = 0xd9d4bf;

    /// <summary>Derive the dungeon material palette for a biome from its core colours (pure; cache at the call site).</summary>
    public static TerrainPalette terrainOf(Biome biome)
    {
        // ── WALL READABILITY + IDENTITY CONTRACT ─────────────────────────────────────────────────────────────
        // A wall must read as a wall AT A GLANCE in every biome — its OWN clearly contrasting brightness AND hue,
        // never "just a darker floor" — AND it must look like THIS biome's rock (ashen warm stone, frost blue stone,
        // void violet stone, the verdant family mossy), not the uniform desaturated grey every biome shared before.
        // Both at once is the trick: take the biome's stone hue (its shadow↔key accents), pull it only LIGHTLY toward
        // the neutral grey anchor (so the hue survives), then lift it in **HSL lightness** to a bright stone band while
        // HOLDING a floor saturation. Lifting toward white in RGB (the old derivation) washed every biome back to the
        // same pale grey; lifting lightness keeps the rock visibly tinted. The result sits a clear, measured band ABOVE
        // the floor's capped ElevationPalette.topFill — the readability gap (min wall lum − max floor lum) is
        // ≥ ~28 in every biome (audited by scripts/paletteaudit.mjs). The elevation ramp then darkens/brightens around
        // this base for gentle height relief; faces are shaded clearly darker for volume; the bold wallLine
        // silhouette + lit rim the bake strokes around every wall remain the universal backstop edge cue.
        int accent = biome.detail != 0 ? biome.detail : biome.groundAccentB;
        bool citadel = biome.materialDialect == "aegis-citadel";
        int floorBase = citadel
            ? 0x282a2e
            : darken(mix(WORLD_INK.paperWarm, biome.groundAccentB, 0.18), 0.035);
        // A theme may state its own stone HUE family (ice stone, pumice, violet slate…) via Biome.stone;
        // the HSL lift below still forces the bright wall-luminance band, so the readability contract survives any
        // authored hue. Saturation headroom is slightly wider for authored stone so the family actually shows.
        int stoneAnchor = biome.stone ?? STONE_GREY;
        // Character-alignment pass: the old caps (0.20/0.26) chalked every biome's rock into the same pale
        // grey band. A wider saturation window keeps the readable bright-stone lightness while the hue
        // actually SHOWS — the same move the suit block makes (saturated colour, bounded, never neon).
        double stoneSatCap = citadel ? 0.045 : biome.stone != null ? 0.34 : 0.28;
        var stoneHsl = rgbToHsl(
            mix(mix(stoneAnchor, biome.groundAccentB, 0.16), WORLD_INK.paper, 0.14));
        int stoneBase = hslToRgb(
            stoneHsl.h,
            citadel
                ? Math.min(stoneSatCap, stoneHsl.s * 0.18)
                : Math.max(0.1, Math.min(stoneSatCap, stoneHsl.s + 0.06)),
            citadel ? 0.9 : 0.64);
        // A per-level rock-cap ramp long enough to shade a wall at ANY structural height (walls now tower to the
        // full signed ceiling as rock cliffs). Indexed by
        // `wallCapColorAt` as `round(surfaceZ) − MIN_ELEVATION`, so it also covers sub-zero walls. The first bands
        // are the valley/massif-body anchors the readability audit pins; above the body it lifts gently and
        // SATURATES, so a towering summit stays bright biome stone instead of blowing out to white.
        var wallFill = new int[ELEVATION_LEVELS + 3];
        for (int i = 0; i < wallFill.Length; i++)
        {
            if (i == 0) { wallFill[i] = darken(stoneBase, 0.08); continue; } // valley rock — still plainly a bright biome stone
            if (i == 1) { wallFill[i] = darken(stoneBase, 0.035); continue; }
            if (i == 2) { wallFill[i] = stoneBase; continue; } // the massif body (the anchor the wall-readability contract pins)
            double t = Math.min(1, (double)(i - 2) / 10);
            // High geology keeps pigment and middle-value modelling. The former +0.25 ramp turned large authored
            // massifs into paper-white slabs and made sub-pixel cliff edges the brightest thing in motion. Peak
            // facets and the key light still supply highlights; the structural cap itself now remains material.
            wallFill[i] = lighten(stoneBase, 0.025 + 0.085 * t);
        }
        return new TerrainPalette
        {
            minLevel = MIN_ELEVATION,
            floorTone = floorBase,
            floorLit = citadel ? 0x9ea1a4 : mix(WORLD_INK.paper, accent, 0.16),
            floorSeam = citadel ? 0x08090a : mix(WORLD_INK.ink, accent, 0.08),
            wall = stoneBase,
            wallFill = wallFill,
            wallLit = citadel ? 0xf7f7f3 : WORLD_INK.paper,
            wallFace = citadel
                ? 0xd7d7d2
                : darken(mix(WORLD_INK.paperDeep, biome.groundAccentB, 0.28), 0.02), // shaded vertical face
            wallDeep = citadel ? 0x55575b : mix(WORLD_INK.ink, biome.groundAccentB, 0.14), // deep base of the face / E-W side shading / mottle + striation ink
            wallLine = WORLD_INK.ink, // bold near-black silhouette ink (Don't-Starve style)
            contact = WORLD_INK.ink,
            prop = citadel ? 0xb7b9bb : accent,
        };
    }

    /// <summary>Derive the elevation/terrace palette for a biome from its core colours (pure; cache at the call site).</summary>
    public static ElevationPalette elevationOf(Biome biome)
    {
        // Height reads as a gameplay rule first: LOW = lighter, HIGH = darker. The previous "higher gets sunlit"
        // ramp fought that rule in play. This keeps the biome hue, drops the whole field below the old paper-white
        // band, and lets cliff faces/lit lips provide physical light cues on top of the value code.
        // How much of the biome ACCENT the walkable ground itself carries is theme data (WorldStyle.groundAccent):
        // a themed run's floor should read lush/charred/frozen/bruised at a glance, not
        // as a pale chalk wash. Bounded so LOW stays lighter than HIGH and the whole ramp stays below the bright
        // wall-luminance band (the walls-read-as-walls contract).
        bool citadel = biome.materialDialect == "aegis-citadel";
        int visualLevels = ELEVATION_LEVELS;
        if (citadel)
        {
            // The arena floor is a black-stone processional field, not the warm paper terrace used by ordinary runs.
            // A restrained neutral lift at low levels preserves navigation relief while leaving white masonry as the
            // unequivocal highest value in the frame. This also keeps the raised Heart mound readable without hue.
            var citadelTopFill = new int[visualLevels];
            for (int i = 0; i < visualLevels; i++)
            {
                double t = (double)i / (visualLevels - 1);
                citadelTopFill[i] = mix(0x3d4045, 0x15171a, t);
            }
            return new ElevationPalette
            {
                minLevel = MIN_ELEVATION,
                topFill = citadelTopFill,
                cliffFace = 0x303338,
                cliffDeep = 0x0b0c0e,
                lit = 0xc7c9c8,
                shadow = 0x030405,
            };
        }
        double g = biome.style?.groundAccent ?? 1;
        int accent = biome.groundTint ?? (biome.detail != 0 ? biome.detail : biome.groundAccentB);
        // Accent shares carry a baseline + slope so even an unstyled run (g=1) reads as PLACE, not chalk —
        // the character-alignment pass: the suit block keeps ~66% of its colour, the ground now keeps ~26–47%
        // of its biome accent (runs→hub) instead of 14–34%. Still clearly quieter than any actor.
        int low = darken(mix(WORLD_INK.paperWarm, accent, Math.min(0.62, 0.11 + 0.15 * g)), 0.045);
        int highBase = mix(WORLD_INK.paperDeep, biome.groundAccentA, 0.34);
        int high = darken(mix(highBase, accent, Math.min(0.5, 0.07 + 0.11 * g)), 0.18);
        // The terrace ramp must shade a floor at ANY walkable level — endless ground now climbs the full 1..N range,
        // not the old thin low band. Sized to the structural ceiling so `topFill[level]` is always in range; the ramp
        // is stretched across it so mid terraces stay readable and only genuine high plateaus reach the darkest tone.
        var topFill = new int[visualLevels];
        for (int i = 0; i < visualLevels; i++)
        {
            double t = (double)i / (visualLevels - 1);
            topFill[i] = darken(mix(low, high, t), t * 0.04);
        }
        // Terrace STEP faces stay EARTHY (the biome ground lifted toward its accent) — deliberately NOT the grey
        // TerrainPalette.wallFace stone. A walkable terrace must never read as a rock wall: floors cue
        // height with a gentle earthy step + lit lip, walls are the bright bold-outlined stone massif. Keeping the
        // two face materials distinct is what separates "ground that rises" from "barrier you can't cross".
        int cliffFace = darken(mix(WORLD_INK.paperDeep, biome.groundAccentA, 0.34), 0.04);
        int cliffDeep = mix(WORLD_INK.ink, biome.groundAccentA, 0.18);
        int lit = WORLD_INK.paper;
        int shadow = WORLD_INK.ink;
        return new ElevationPalette { minLevel = MIN_ELEVATION, topFill = topFill, cliffFace = cliffFace, cliffDeep = cliffDeep, lit = lit, shadow = shadow };
    }

    /// <summary>
    /// Measure the pigment family of a biome from the surfaces that actually COVER its frame: the terrace tops
    /// (most of the picture), the rock caps, the floor tone and the cliff faces — weighted the way they appear.
    ///
    /// The hue is a circular mean weighted by each sample's own saturation, because a near-grey chalk carries no
    /// hue opinion and must not drag the mean; the saturation/lightness are plain weighted means. Deriving from
    /// the DERIVED palettes (not from the raw <see cref="Biome"/> anchors) is the point: the anchors are a designer's
    /// intent, while `terrainOf`/`elevationOf` are what the bake paints — and "does this object belong to the
    /// picture" is a question about the painted picture.
    ///
    /// Pure; a handful of array reads, called once per biome change (never per frame, never per tile).
    /// </summary>
    public static PigmentFamily pigmentFamilyOf(Biome biome)
    {
        var terrain = terrainOf(biome);
        var elevation = elevationOf(biome);
        double x = 0;
        double y = 0;
        double saturation = 0;
        double lightness = 0;
        double weight = 0;
        void sample(int hex, double w)
        {
            var (h, s, l) = rgbToHsl(hex);
            double chroma = w * s;
            x += Math.cos((h * Math.PI) / 180) * chroma;
            y += Math.sin((h * Math.PI) / 180) * chroma;
            saturation += s * w;
            lightness += l * w;
            weight += w;
        }
        // Terrace tops carry the frame; rock caps are the second mass; floor tone, wall body and cliff faces are
        // the remaining named surfaces. The weights are the coverage order, not a tuning knob.
        foreach (int hex in elevation.topFill) sample(hex, 3.0 / elevation.topFill.Length);
        foreach (int hex in terrain.wallFill) sample(hex, 2.0 / terrain.wallFill.Length);
        sample(terrain.floorTone, 1);
        sample(terrain.wall, 1);
        sample(elevation.cliffFace, 1);
        return new PigmentFamily
        {
            hue = ((Math.atan2(y, x) * 180) / Math.PI + 360) % 360,
            saturation = saturation / weight,
            lightness = lightness / weight,
        };
    }

    /// <summary>
    /// How the DERIVED water of a theme relates to that theme's own pigment family. These are the only numbers in
    /// the water fallback, and each one is a rule, not a taste:
    ///
    ///  - `coolPole` / `coolArcDeg` — water is the ground pigment seen through depth, so it drifts toward the
    ///    world's single cool pole, but by a BOUNDED arc. The bound is what makes "the lake belongs to this
    ///    frame" a structural property instead of a lucky mix: a derived lake can never sit further than
    ///    `coolArcDeg` from its own biome's mean hue.
    ///  - `saturationGain` / `saturationRange` — a wet pigment is deeper than the dry ground, so the family's
    ///    saturation is lifted; the range keeps a chalky biome from producing concrete and a loud biome from
    ///    producing a swimming pool. The shared water material still dilutes this body on the tile, so the BODY
    ///    pole is authored richer than the sheet that finally shows.
    ///  - `depthScale` / `depthRange` — water reads as a HOLE in the ground: its value must fall clearly below
    ///    the terraces around it. That, not chroma, is what makes it read as water at a glance. The range is a
    ///    BAND, not a floor: pushed too dark (the first round used 0.14–0.34) the pigment loses its weight against
    ///    the sheet's fixed white load — crest, glint, caustic, specular — and the lit lake stops being a pigment
    ///    at all. Measured through the real rig + ACES at PRESENTATION_EXPOSURE: at 0.14–0.34 a derived
    ///    lake rendered 69 display units below its own shore and its saturation collapsed to 0.13× the ground's
    ///    once the white load was applied; at 0.22–0.42 the same lakes land −58…−2 and 0.24…1.8×.
    ///  - `deepLightnessScale` — the deep pole is a bounded VALUE step below the body **in the body's own
    ///    hue and saturation**, not a mix toward ink. The old `mix(WORLD_INK.ink, body, 0.16)` was 84 % ink; it
    ///    was authored against the old cyan body at L≈0.49 and, against a family-derived body, rendered the deep
    ///    cell of a pool as a near-black mass next to its own shallow cell — one pool, two substances.
    /// </summary>
    public static class WATER_FAMILY
    {
        public const double coolPole = 196;
        public const double coolArcDeg = 14;
        public const double saturationBias = 0.08;
        public const double saturationGain = 1.62;
        public static readonly IReadOnlyList<double> saturationRange = new[] { 0.22, 0.74 };
        public const double depthScale = 0.68;
        public static readonly IReadOnlyList<double> depthRange = new[] { 0.22, 0.42 };
        public const double deepLightnessScale = 0.8;
    }

    private static double clampNumber(double value, double lo, double hi)
    {
        return value < lo ? lo : value > hi ? hi : value;
    }

    /// <summary>
    /// Derive the flood palette for a biome (pure). A theme with authored water (lava, oily canal, meltwater,
    /// ink…) carries it on <see cref="Biome.water"/> and gets it back verbatim — one source either way.
    ///
    /// The fallback used to be `mix(WORLD_INK.cyan, stormCore, 0.12)` — 88 % of the UI's cool ACCENT, identical
    /// for every theme that did not author its own water. Measured on real frames that put one pool-cyan lake
    /// (hue 192°, sat 0.47) into a sepia canyon whose whole-frame mean was hue 46° / sat 0.30: the single object
    /// in the picture that was outside the pigment palette, in a look whose whole premise is that the frame reads
    /// as one illustrator's work on one sheet. It also meant a desert lake, an autumn-forest lake and a bone-waste
    /// lake were literally the same colour.
    ///
    /// Now the water is derived from the biome's OWN <see cref="pigmentFamilyOf"/> — one expression, no per-biome data,
    /// no new branch: every theme (including any added later, as data) gets a lake that belongs to its own sheet.
    /// </summary>
    public static FloodPalette floodColors(Biome biome)
    {
        if (biome.water != null) return biome.water;
        var family = pigmentFamilyOf(biome);
        double toPole = ((WATER_FAMILY.coolPole - family.hue + 540) % 360) - 180;
        double hue =
            (family.hue + clampNumber(toPole, -WATER_FAMILY.coolArcDeg, WATER_FAMILY.coolArcDeg) + 360) %
            360;
        double saturation = clampNumber(
            WATER_FAMILY.saturationBias + family.saturation * WATER_FAMILY.saturationGain,
            WATER_FAMILY.saturationRange[0],
            WATER_FAMILY.saturationRange[1]);
        double lightness = clampNumber(
            family.lightness * WATER_FAMILY.depthScale,
            WATER_FAMILY.depthRange[0],
            WATER_FAMILY.depthRange[1]);
        int body = hslToRgb(hue, saturation, lightness);
        return new FloodPalette
        {
            // Nobody authored this lake — see FloodPalette.derived. The renderer needs to know, because an
            // authored statement is used verbatim while this guess still leans on the shared value structure.
            derived = true,
            body = body,
            // One substance at two depths: the SAME pigment, one bounded value step down. Mixing toward ink instead
            // produced a second material inside the same pool (see WATER_FAMILY.deepLightnessScale).
            deep = hslToRgb(hue, saturation, lightness * WATER_FAMILY.deepLightnessScale),
            surface = mix(body, WORLD_INK.paper, 0.42),
            foam = WORLD_INK.paper,
            // The caustic glint stays the biome's electric note: a SMALL bright accent is exactly where a hot hue
            // belongs in this look — it is the broad body fill that had to rejoin the palette, not the sparkle.
            glint = biome.stormBolt,
        };
    }

    /// <summary>`timberAnchor` defaults to WORLD_TIMBER.mid when absent (JS default parameter: also when `undefined`
    /// is passed explicitly, e.g. an unauthored `biome.timber`).</summary>
    public static BridgePalette bridgeOf(
        TerrainPalette terrain,
        ElevationPalette elevation,
        FloodPalette flood,
        int? timberAnchor = null)
    {
        int anchor = timberAnchor ?? WORLD_TIMBER.mid;
        int timber = mix(anchor, elevation.cliffFace, 0.58);
        int body = darken(mix(timber, terrain.floorTone, 0.28), 0.045);
        int edge = darken(mix(body, terrain.floorSeam, 0.42), 0.24);
        return new BridgePalette
        {
            body = body,
            bodyShadow = darken(mix(body, flood.deep, 0.24), 0.18),
            center = lighten(mix(body, elevation.lit, 0.08), 0.015),
            grain = mix(darken(body, 0.28), terrain.floorSeam, 0.18),
            seam = mix(edge, flood.deep, 0.18),
            edge = edge,
            lip = mix(elevation.lit, body, 0.66),
            rail = darken(mix(body, terrain.floorTone, 0.16), 0.18),
            railLit = mix(elevation.lit, body, 0.74),
            cuff = mix(body, elevation.cliffFace, 0.28),
        };
    }

    /// <summary>
    /// The permanent safe Hub — the **Forest Village Sanctuary**: a cosy timber-and-moss mountain
    /// village living AMONG its pines. Warm afternoon light through green air, loamy lanes between laid-stone
    /// plazas, brooks, groves woven through every ward and an inviting village square. Calm and low-threat, but
    /// deep, readable and alive as a social hub.
    /// </summary>
    private static readonly Biome HUB_BIOME = new()
    {
        key = "hub",
        name = "Forest Village Sanctuary",
        ground = WORLD_INK.paperWarm,
        groundAccentA = 0xb59c6b, // warm loam under the village lanes
        groundAccentB = 0x3fae64, // rich forest green
        pattern = "mossStone",
        detail = 0x77c879, // moss glints in the village air
        haze = 0xf0f7e6, // green-tinted forest light in the distance
        lightTint = 0xfff2cf, // warm late-afternoon village sun
        vignette = 0.18,
        gradeTint = WORLD_INK.paperWarm,
        gradeTintAlpha = 0.02,
        grade = new ColorGrade { brightness = 1.03, contrast = 1.06, saturate = 1.02, hue = 0 },
        stormCore = WORLD_INK.cyan,
        stormBody = 0x165a62,
        stormBolt = WORLD_INK.paper,
        // Clear mountain water is a stable blue material statement in the Hub. Deriving it from the surrounding
        // moss/loam pigment family pulled the broad lake body toward green and made it read as flooded ground.
        water = new FloodPalette
        {
            body = 0x287aa3,
            deep = 0x0b3558,
            surface = 0x72c1dd,
            foam = 0xeafcff,
            glint = 0xc4efff,
        },
        decal = "flora",
        timber = 0xa8763a, // warm worked village timber (bridges, decks)
        stone = 0xccd0ac, // moss-warm village stone
        groundTint = 0x86b46a, // village green — grass and loam between the lanes
        style = new WorldStyle(DEFAULT_WORLD_STYLE)
        {
            floorTint = 1.5,
            groundAccent = 2.4,
            wallTint = 1.4,
            waterTint = 1.65,
            floorGrain = 1.1, // lived-in, mossy ground
            driftA = 0xd9eab0, // leafy sun-dapple
            driftB = 0x9ab88a, // shade-moss
            driftAmp = 0.14,
            waveSpeed = 0.9, // easy village brooks
            foam = 1.1,
            glint = 1,
            windDirection = -0.48,
            windStrength = 0.92,
            windSpeed = 0.72,
            windGust = 0.72,
            // Hold the cap luminance while moving energy out of the flat sky dome and into the warm key. This restores
            // shape on characters and cliffs without raising exposure or clipping the pale paper ground.
            sunLight = 1.08,
            hemiLight = 1,
            fillLight = 0.98,
            ambientLight = 1.02,
        },
    };

    /// <summary>The Hub palette without Hub-only district inlays, landmarks or portal dressing.</summary>
    private static readonly Biome POCKETTOWN_BIOME = new(HUB_BIOME)
    {
        key = "pockettown",
        name = "Pockettown Sanctuary",
        grade = HUB_BIOME.grade.Clone(),
        style = HUB_BIOME.style,
    };

    /// <summary>The Hub's terrain and atmosphere without Hub-only district dressing.</summary>
    private static readonly Biome MUSEUM_BIOME = new(HUB_BIOME)
    {
        key = "museum",
        name = "Soul & Flui Museum",
        grade = HUB_BIOME.grade.Clone(),
        style = HUB_BIOME.style,
    };
    /// <summary>
    /// The five **mood families** the ten runs draw from, keyed by the run's `biomeKey`. Each is a distinct,
    /// code-generated look (lush → ashen → frozen → storm-wracked → void); a run resolves its own appearance by
    /// picking its family palette and **escalating** it by run index (runBiomeFor) — deeper runs darken,
    /// gain contrast and storm intensity — so all ten descents read distinctly and progressively more dangerous
    /// with no copy-pasted palettes. (A `Record` that is never iterated → Dictionary.)
    /// </summary>
    private static readonly Dictionary<string, Biome> MOOD_BIOMES = new()
    {
        // The first run — a high alpine pass: cold grey-green stone, misty blue haze, glacial rivers and snow.
        ["mountain"] = new Biome
        {
            key = "mountain",
            name = "Highland Pass",
            ground = WORLD_INK.paperWarm,
            groundAccentA = WORLD_INK.paperDeep,
            groundAccentB = 0x5fbf9a,
            pattern = "mossStone",
            detail = WORLD_INK.cyan,
            haze = WORLD_INK.paper,
            lightTint = WORLD_INK.paper,
            vignette = 0.24,
            gradeTint = WORLD_INK.paperWarm,
            gradeTintAlpha = 0.02,
            grade = new ColorGrade { brightness = 1.03, contrast = 1.07, saturate = 1.0, hue = 0 },
            stormCore = WORLD_INK.cyan,
            stormBody = 0x165a62,
            stormBolt = WORLD_INK.paper,
            decal = "scree", // alpine: angular rock chips, lichen patches, sparse highland tufts
        },
        ["verdant"] = new Biome
        {
            key = "verdant",
            name = "Verdant",
            ground = WORLD_INK.paperWarm,
            groundAccentA = WORLD_INK.paperDeep,
            groundAccentB = WORLD_INK.green,
            pattern = "mossStone",
            detail = WORLD_INK.green,
            haze = WORLD_INK.paper,
            lightTint = WORLD_INK.paper,
            vignette = 0.25,
            gradeTint = WORLD_INK.paperWarm,
            gradeTintAlpha = 0.02,
            grade = new ColorGrade { brightness = 1.02, contrast = 1.06, saturate = 1.02, hue = 0 },
            stormCore = WORLD_INK.green,
            stormBody = 0x17623a,
            stormBolt = WORLD_INK.paper,
            decal = "flora", // verdant: moss patches, grass tufts, leaf/spore glints
        },
        ["ashen"] = new Biome
        {
            key = "ashen",
            name = "Ashen",
            ground = WORLD_INK.paperWarm,
            groundAccentA = 0xd4b06d,
            groundAccentB = WORLD_INK.red,
            pattern = "ashCracks",
            detail = WORLD_INK.red,
            haze = WORLD_INK.paper,
            lightTint = WORLD_INK.paper,
            vignette = 0.3,
            gradeTint = WORLD_INK.paperWarm,
            gradeTintAlpha = 0.02,
            grade = new ColorGrade { brightness = 1.0, contrast = 1.09, saturate = 1.04, hue = 0 },
            stormCore = WORLD_INK.red,
            stormBody = 0x761c16,
            stormBolt = WORLD_INK.yellow,
            decal = "cinder", // ashen: glowing ember cracks, charred flecks, scorch sparks
        },
        ["frost"] = new Biome
        {
            key = "frost",
            name = "Frostbound",
            ground = WORLD_INK.paperWarm,
            groundAccentA = 0xb7d6d9,
            groundAccentB = WORLD_INK.cyan,
            pattern = "mossStone",
            detail = WORLD_INK.cyan,
            haze = WORLD_INK.paper,
            lightTint = WORLD_INK.paper,
            vignette = 0.28,
            gradeTint = WORLD_INK.paperWarm,
            gradeTintAlpha = 0.02,
            grade = new ColorGrade { brightness = 1.02, contrast = 1.08, saturate = 1.0, hue = 0 },
            stormCore = WORLD_INK.cyan,
            stormBody = 0x155966,
            stormBolt = WORLD_INK.paper,
            decal = "rime", // frost: ice shards, frost-star cracks, soft snow drifts
        },
        ["storm"] = new Biome
        {
            key = "storm",
            name = "Storm",
            ground = WORLD_INK.paperWarm,
            groundAccentA = 0xb8afd8,
            groundAccentB = WORLD_INK.violet,
            pattern = "voidVeins",
            detail = WORLD_INK.yellow,
            haze = WORLD_INK.paper,
            lightTint = WORLD_INK.paper,
            vignette = 0.32,
            gradeTint = WORLD_INK.paperWarm,
            gradeTintAlpha = 0.02,
            grade = new ColorGrade { brightness = 1.0, contrast = 1.1, saturate = 1.04, hue = 0 },
            stormCore = WORLD_INK.violet,
            stormBody = 0x3b206d,
            stormBolt = WORLD_INK.yellow,
            decal = "gale", // storm: wind-scour streaks, sparse wind-bent grass, dust ripples
        },
        ["void"] = new Biome
        {
            key = "void",
            name = "Void",
            ground = WORLD_INK.paperWarm,
            groundAccentA = 0xc4afd8,
            groundAccentB = WORLD_INK.violet,
            pattern = "voidVeins",
            detail = WORLD_INK.violet,
            haze = WORLD_INK.paper,
            lightTint = WORLD_INK.paper,
            vignette = 0.36,
            gradeTint = WORLD_INK.paperWarm,
            gradeTintAlpha = 0.02,
            grade = new ColorGrade { brightness = 0.98, contrast = 1.12, saturate = 1.06, hue = 0 },
            stormCore = WORLD_INK.violet,
            stormBody = 0x3b206d,
            stormBolt = WORLD_INK.paper,
            decal = "arcane", // void: crystal shards, glowing rune motes
        },
        // ── entries the TS adds by assignment after the literal (MOOD_BIOMES.x = {...}), same order ──

        // The Aegis White Citadel — tier 6's monumental defense arena. Limestone ramparts, black processional
        // courts and an ink-dark mirror moat form a near-monochrome value hierarchy; the Heart is the one living
        // colour statement. The existing Olympian construction supplies colonnades, laid masonry and balustrades in
        // the terrain batch, while this dedicated material dialect removes its garden palette from the fort.
        ["aegisCitadel"] = new Biome
        {
            key = "aegis_citadel",
            name = "Aegis White Citadel",
            ground = 0x111216,
            groundAccentA = 0x4c4f54,
            groundAccentB = 0x090a0c,
            pattern = "plaza",
            detail = 0xe8e7e1,
            haze = 0xd9dce0,
            lightTint = 0xf7f7f3,
            vignette = 0.34,
            gradeTint = 0xe2e3e5,
            gradeTintAlpha = 0.015,
            grade = new ColorGrade { brightness = 1, contrast = 1.26, saturate = 0, hue = 0 },
            stormCore = 0xf4f3ee,
            stormBody = 0x111318,
            stormBolt = 0xffffff,
            decal = "olympian",
            water = new FloodPalette
            {
                body = 0x08090b,
                deep = 0x010102,
                surface = 0x292c30,
                foam = 0xfafaf6,
                glint = 0xffffff,
            },
            timber = 0x2a2c30,
            stone = 0xf7f6ef,
            groundTint = 0x17191d,
            tileset = "olympian",
            materialDialect = "aegis-citadel",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.9,
                groundAccent = 4.05,
                wallTint = 3.15,
                waterTint = 2.15,
                bridgeTint = 2.05,
                floorGrain = 0.48,
                rockGrain = 0.64,
                strata = 1.72,
                driftA = 0xffffff,
                driftB = 0x181a1e,
                driftAmp = 0.08,
                waveSpeed = 0.38,
                foam = 0.82,
                glint = 0.62,
                windDirection = -0.72,
                windStrength = 0.74,
                windSpeed = 0.66,
                windGust = 0.46,
                sunLight = 0.79,
                hemiLight = 0.92,
                fillLight = 0.68,
                ambientLight = 0.94,
            },
        },

        // The Drone Foundry — the Engineer's soul-dungeon: a cold gunmetal machine room lit by cyan conduit-glow
        // (the per-soul dedicated dungeons each get their own theme; this is the reference). Drawn through the same
        // unified run renderer as everything else — only the palette + grade differ.
        ["machineworks"] = new Biome
        {
            key = "machineworks",
            name = "Drone Foundry",
            ground = WORLD_INK.paperWarm,
            groundAccentA = 0xb9c4c5,
            groundAccentB = WORLD_INK.cyan,
            pattern = "voidVeins", // glowing conduit veins through the deck
            detail = WORLD_INK.cyan,
            haze = WORLD_INK.paper,
            lightTint = WORLD_INK.paper,
            vignette = 0.28,
            gradeTint = WORLD_INK.paperWarm,
            gradeTintAlpha = 0.02,
            grade = new ColorGrade { brightness = 1.01, contrast = 1.1, saturate = 1.02, hue = 0 },
            stormCore = WORLD_INK.cyan,
            stormBody = 0x155966,
            stormBolt = WORLD_INK.paper,
            decal = "arcane", // crystalline glints read as scattered machine components
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.55,
                groundAccent = 2.6,
                wallTint = 2.3,
                waterTint = 1.45, // glowing coolant channels
                bridgeTint = 1.6,
                floorGrain = 0.62, // machined deck plate — detail comes from the conduit veins
                rockGrain = 1.55, // riveted gunmetal
                strata = 1.95, // plating courses on the foundry walls
                driftA = 0x5ff2ff, // conduit cyan
                driftB = 0x39434c, // gunmetal shadow
                driftAmp = 0.24,
                waveSpeed = 0.5, // sluggish coolant
                foam = 0.5,
                glint = 2.1, // hard machined specular
                windDirection = 0.62,
                windStrength = 0.44, // dry vent draught, not weather
                windSpeed = 1.28,
                windGust = 1.05, // abrupt exhaust bursts — choppy, mechanical
                sunLight = 0.78, // no sky in the machine room
                hemiLight = 0.9,
                fillLight = 1.28, // cyan conduit bounce lights the floor
                ambientLight = 0.82,
            },
        },

        // The Tide Cage — the Leviathan Angler's soul-dungeon: a drowned sea-fortress of ring-islands. Deep teal
        // water-light over salt-bleached stone, kelp-green accents, spray in the air. Its authored water (the ring
        // channels) carries the scene; the palette keeps the land cold and tide-washed.
        ["tidecage"] = new Biome
        {
            key = "tidecage",
            name = "The Tide Cage",
            ground = WORLD_INK.paperWarm,
            groundAccentA = 0x9fc4bf,
            groundAccentB = 0x2fa8b8,
            pattern = "mossStone", // salt-crusted fortress masonry
            detail = 0x2fa8b8,
            haze = WORLD_INK.paper,
            lightTint = WORLD_INK.paper,
            vignette = 0.3,
            gradeTint = WORLD_INK.paperWarm,
            gradeTintAlpha = 0.02,
            grade = new ColorGrade { brightness = 0.99, contrast = 1.1, saturate = 1.06, hue = 0 },
            stormCore = 0x2fa8b8,
            stormBody = 0x14555f,
            stormBolt = WORLD_INK.paper,
            decal = "bog", // tide pools, weed clumps, rising brine bubbles between the rings
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.6,
                groundAccent = 2.7,
                wallTint = 2.1,
                waterTint = 1.9, // the ring channels ARE the scene
                bridgeTint = 1.5,
                floorGrain = 1.25, // salt-crusted masonry
                rockGrain = 1.4,
                strata = 1.6, // tide-line banding on the fortress walls
                driftA = 0x8fdcd4, // spray teal
                driftB = 0x1d5f6e, // deep brine
                driftAmp = 0.24,
                waveSpeed = 1.3, // a working tide, always churning
                foam = 1.75,
                glint = 1.5,
                windDirection = -0.82, // steady onshore push
                windStrength = 1.05, // heavy wet air leaning on everything...
                windSpeed = 0.46, // ...but sluggish, waterlogged
                windGust = 0.6,
                sunLight = 0.7, // drowned fortress light
                hemiLight = 1.22, // sea-light dome over the rings
                fillLight = 1.12,
                ambientLight = 0.92,
            },
        },

        // The Thousand Folds — the Origami Shaper's soul-dungeon: a folded paper mountain. Parchment-cream shelves
        // creased in indigo, stamped with crimson seal accents; the terraced elevation carries the drama, the
        // palette keeps it serene temple-paper.
        ["papertemple"] = new Biome
        {
            key = "papertemple",
            name = "The Thousand Folds",
            ground = WORLD_INK.paperWarm,
            groundAccentA = 0xd9d2ba,
            groundAccentB = 0x7b6fe0,
            pattern = "voidVeins", // indigo crease-lines running through the parchment shelves
            detail = 0xd6435e, // crimson ink-seal accents
            haze = WORLD_INK.paper,
            lightTint = WORLD_INK.paper,
            vignette = 0.3,
            gradeTint = WORLD_INK.paperWarm,
            gradeTintAlpha = 0.02,
            grade = new ColorGrade { brightness = 1.02, contrast = 1.08, saturate = 1.0, hue = 0 },
            stormCore = 0x7b6fe0,
            stormBody = 0x3b2f75,
            stormBolt = WORLD_INK.paper,
            decal = "arcane", // folded glints + rune-like seal marks along the pilgrim path
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.45,
                groundAccent = 2.2,
                wallTint = 1.9,
                waterTint = 1.1, // quiet ink-wash pools
                bridgeTint = 1.4,
                floorGrain = 0.55, // smooth parchment shelves
                rockGrain = 0.5,
                strata = 1.5, // indigo crease terraces
                driftA = 0xe8dcc0, // parchment cream
                driftB = 0x8f84e8, // indigo crease
                driftAmp = 0.18,
                waveSpeed = 0.5,
                foam = 0.6,
                glint = 1.15,
                windDirection = -0.55,
                windStrength = 0.68, // a steady paper breeze...
                windSpeed = 0.66,
                windGust = 0.35, // ...almost without gusts — serene temple air
                sunLight = 0.98,
                hemiLight = 1.18, // soft temple sky through the folds
                fillLight = 1.02,
                ambientLight = 1.06,
            },
        },

        // The Wyrmforge — Wyrmbreath's soul dungeon: a blackstone citadel built around a living caldera. Deep
        // obsidian floors carry iron-red heat cracks, molten gold marks active furnaces and the air is ash-heavy.
        ["wyrmforge"] = new Biome
        {
            key = "wyrmforge",
            name = "The Wyrmforge",
            ground = 0x2a2020,
            groundAccentA = 0x4a2924,
            groundAccentB = 0xb93a24,
            pattern = "ashCracks",
            detail = 0xffa62b,
            haze = 0x59413a,
            lightTint = 0xffd2a0,
            vignette = 0.46,
            gradeTint = 0x7a3126,
            gradeTintAlpha = 0.055,
            grade = new ColorGrade { brightness = 0.94, contrast = 1.18, saturate = 1.06, hue = 0 },
            stormCore = 0xff6a28,
            stormBody = 0x4a1618,
            stormBolt = 0xffc247,
            decal = "cinder",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.7,
                groundAccent = 3.1,
                wallTint = 1.4,
                waterTint = 0.7,
                bridgeTint = 1.2,
                floorGrain = 1.45,
                rockGrain = 1.7,
                strata = 1.55,
                driftA = 0x7a241d,
                driftB = 0xff8a2a,
                driftAmp = 0.22,
                waveSpeed = 0.48,
                foam = 0.32,
                glint = 1.25,
                windDirection = -0.22,
                windStrength = 0.82,
                windSpeed = 0.74,
                windGust = 1.12,
                sunLight = 0.84,
                hemiLight = 0.7,
                fillLight = 0.82,
                ambientLight = 0.7,
            },
        },

        ["moonroot"] = new Biome
        {
            key = "moonroot",
            name = "The Moonroot Canopy",
            ground = 0x263f2d,
            groundAccentA = 0x496438,
            groundAccentB = 0x8fba58,
            pattern = "mossStone",
            detail = 0xd9f5a8,
            haze = 0x18271f,
            lightTint = 0xdfffc1,
            vignette = 0.5,
            gradeTint = 0x365f43,
            gradeTintAlpha = 0.07,
            grade = new ColorGrade { brightness = 0.9, contrast = 1.16, saturate = 1.08, hue = 0 },
            stormCore = 0xb9f27c,
            stormBody = 0x16271d,
            stormBolt = 0xe8ffc1,
            decal = "flora",
            groundTint = 0x385a3d,
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.65,
                groundAccent = 2.85,
                wallTint = 1.55,
                waterTint = 0.75,
                bridgeTint = 1.45,
                floorGrain = 1.3,
                rockGrain = 1.25,
                strata = 1.2,
                driftA = 0xb9f27c,
                driftB = 0x6a8f52,
                driftAmp = 0.18,
                waveSpeed = 0.34,
                foam = 0.4,
                glint = 1.15,
                windDirection = 0.55,
                windStrength = 0.72,
                windSpeed = 0.58,
                windGust = 0.48,
                sunLight = 0.72,
                hemiLight = 0.78,
                fillLight = 0.86,
                ambientLight = 0.76,
            },
        },

        // The Crown Bower — brilliant tropical canopy courts suspended over blue-green cloudforest. Warm gold sun
        // hits turquoise leaf floors; coral fruit/flowers and cobalt bower ornaments keep it jewel-bright without
        // flattening the deep canopy void beneath the feather bridges.
        ["crownbower"] = new Biome
        {
            key = "crownbower",
            name = "The Crown Bower",
            ground = 0x326f5a,
            groundAccentA = 0x19c7b1,
            groundAccentB = 0xffd45c,
            pattern = "mossStone",
            detail = 0xe8468c,
            haze = 0xdff6e9,
            lightTint = 0xffefb2,
            vignette = 0.3,
            gradeTint = 0x8ae8c8,
            gradeTintAlpha = 0.045,
            grade = new ColorGrade { brightness = 1.03, contrast = 1.12, saturate = 1.13, hue = 0 },
            stormCore = 0xffd45c,
            stormBody = 0x177e78,
            stormBolt = 0xfff4c7,
            decal = "flora",
            timber = 0x7e5438,
            stone = 0xbad79b,
            groundTint = 0x3d8b63,
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.72,
                groundAccent = 3.15,
                wallTint = 1.48,
                waterTint = 1.65,
                bridgeTint = 1.85,
                floorGrain = 1.2,
                rockGrain = 1.0,
                strata = 1.1,
                driftA = 0xffd45c,
                driftB = 0x19c7b1,
                driftAmp = 0.24,
                waveSpeed = 0.72,
                foam = 0.85,
                glint = 1.7,
                windDirection = 0.35,
                windStrength = 1.05,
                windSpeed = 0.92,
                windGust = 0.78,
                sunLight = 1.24,
                hemiLight = 1.2,
                fillLight = 1.08,
                ambientLight = 0.96,
            },
        },

        // The Resonance Eyrie — an open-air harp temple over a blue-black gulf. Ivory soundboard floors, lapis
        // string lanes and warm tuning-peg gold make the place read as crafted acoustics rather than generic arcana.
        ["resonanceeyrie"] = new Biome
        {
            key = "resonanceeyrie",
            name = "The Resonance Eyrie",
            ground = 0x263b72,
            groundAccentA = 0xe7dfc8,
            groundAccentB = 0xf2c866,
            pattern = "voidVeins",
            detail = 0xdbe7ff,
            haze = 0xb9c9ea,
            lightTint = 0xffefc2,
            vignette = 0.4,
            gradeTint = 0x3156a4,
            gradeTintAlpha = 0.06,
            grade = new ColorGrade { brightness = 0.98, contrast = 1.16, saturate = 1.08, hue = 0 },
            stormCore = 0xf2c866,
            stormBody = 0x17254d,
            stormBolt = 0xf7f0dc,
            decal = "arcane",
            timber = 0xa47737,
            stone = 0xe7dfc8,
            groundTint = 0x3156a4,
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.82,
                groundAccent = 3.25,
                wallTint = 2.05,
                waterTint = 0.8,
                bridgeTint = 2.05,
                floorGrain = 0.48,
                rockGrain = 0.72,
                strata = 1.45,
                driftA = 0xf2c866,
                driftB = 0x9f70c5,
                driftAmp = 0.2,
                waveSpeed = 0.42,
                foam = 0.38,
                glint = 2.1,
                windDirection = 0.18,
                windStrength = 0.9,
                windSpeed = 0.78,
                windGust = 0.56,
                sunLight = 1.08,
                hemiLight = 1.12,
                fillLight = 1.18,
                ambientLight = 0.92,
            },
        },

        // The Regrowth Canals — a clear turquoise nursery basin under coral gill causeways and indigo lakebed stone.
        ["regrowthcanals"] = new Biome
        {
            key = "regrowthcanals",
            name = "The Regrowth Canals",
            ground = 0x275d68,
            groundAccentA = 0x31d4cf,
            groundAccentB = 0xf06f92,
            pattern = "mossStone",
            detail = 0xffa8bb,
            haze = 0xd8f5ef,
            lightTint = 0xffe8e7,
            vignette = 0.32,
            gradeTint = 0x73ebe2,
            gradeTintAlpha = 0.045,
            grade = new ColorGrade { brightness = 1.02, contrast = 1.11, saturate = 1.12, hue = 0 },
            stormCore = 0xf0b34d,
            stormBody = 0x173b54,
            stormBolt = 0x8df5eb,
            decal = "flora",
            timber = 0x6c4d51,
            stone = 0x7f9fa2,
            groundTint = 0x31aeb1,
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.65,
                groundAccent = 3.05,
                wallTint = 1.5,
                waterTint = 2.1,
                bridgeTint = 1.72,
                floorGrain = 0.75,
                rockGrain = 0.88,
                strata = 0.82,
                driftA = 0xf06f92,
                driftB = 0x31d4cf,
                driftAmp = 0.22,
                waveSpeed = 0.86,
                foam = 1.05,
                glint = 1.8,
                windDirection = 0.42,
                windStrength = 0.58,
                windSpeed = 0.68,
                windGust = 0.4,
                sunLight = 1.12,
                hemiLight = 1.18,
                fillLight = 1.1,
                ambientLight = 0.98,
            },
        },

        // The Star Ossuary — a floating alien necropolis at night: chalk-bone grave islands and stelae over a
        // dark void-violet floor, lit from above by cold abduction-green saucer light instead of any sun.
        // The Hollow Cartography — warm packed clay tunnels cut by moss scale seams and cold mineral
        // listening light. High rock strata and a low amber haze sell an inhabited world below the world.
        ["hollowcartography"] = new Biome
        {
            key = "hollowcartography",
            name = "The Hollow Cartography",
            ground = 0x4a3025,
            groundAccentA = 0x8a5b3d,
            groundAccentB = 0x304b3e,
            pattern = "mossStone",
            detail = 0xe8d6ad,
            haze = 0xd8c2a5,
            lightTint = 0xf3dfb8,
            vignette = 0.4,
            gradeTint = 0x6b4936,
            gradeTintAlpha = 0.055,
            grade = new ColorGrade { brightness = 0.98, contrast = 1.16, saturate = 1.08, hue = 0 },
            stormCore = 0x75d8bd,
            stormBody = 0x251b18,
            stormBolt = 0xe8d6ad,
            decal = "flora",
            timber = 0x594131,
            stone = 0x7a5a43,
            groundTint = 0x704a34,
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.78,
                groundAccent = 3.12,
                wallTint = 2.16,
                waterTint = 0.72,
                bridgeTint = 1.62,
                floorGrain = 0.9,
                rockGrain = 1.26,
                strata = 1.48,
                driftA = 0xe8d6ad,
                driftB = 0x507561,
                driftAmp = 0.19,
                waveSpeed = 0.32,
                foam = 0.28,
                glint = 1.42,
                windDirection = -0.62,
                windStrength = 0.28,
                windSpeed = 0.38,
                windGust = 0.24,
                sunLight = 0.5,
                hemiLight = 0.92,
                fillLight = 1.18,
                ambientLight = 0.86,
            },
        },

        ["starossuary"] = new Biome
        {
            key = "starossuary",
            name = "The Star Ossuary",
            ground = 0x241c38,
            groundAccentA = 0xe2dfc9,
            groundAccentB = 0x8cf0a8,
            pattern = "voidVeins",
            detail = 0x5ce0c4,
            haze = 0xa79fc4,
            lightTint = 0xd6f5df,
            vignette = 0.44,
            gradeTint = 0x3a2c55,
            gradeTintAlpha = 0.06,
            grade = new ColorGrade { brightness = 0.97, contrast = 1.17, saturate = 1.06, hue = 0 },
            stormCore = 0x8cf0a8,
            stormBody = 0x241c38,
            stormBolt = 0xdefbe6,
            decal = "arcane",
            timber = 0x7d6f94,
            stone = 0xe2dfc9,
            groundTint = 0x3a2c55,
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.7,
                groundAccent = 3.1,
                wallTint = 2.1,
                waterTint = 0.7,
                bridgeTint = 1.9,
                floorGrain = 0.55,
                rockGrain = 0.7,
                strata = 1.3,
                driftA = 0x8cf0a8,
                driftB = 0x54407a,
                driftAmp = 0.22,
                waveSpeed = 0.3,
                foam = 0.3,
                glint = 2.2,
                windDirection = 0.9,
                windStrength = 0.34,
                windSpeed = 0.44,
                windGust = 0.26,
                sunLight = 0.58, // no sun at all — the necropolis floats in night void
                hemiLight = 1.06,
                fillLight = 1.22, // …but the abduction beams pour a hard cool fill over the graves
                ambientLight = 0.9,
            },
        },
    };
    /// <summary>
    /// The ten **run world-themes**, keyed by the run's own RunDef.themeKey
    /// — one fully authored identity per endless run (palette, water, timber, stone, surface style, atmosphere),
    /// matched to its name. This replaces the old "five mood families, darkened by depth" model: a run is its own
    /// place, not a dimmer copy of its bracket sibling. The shared terrain registry (`dungeon/terrain.ts`) keys the
    /// matching LANDFORM off the same theme keys, so what the ground *does* and how it *looks* stay one identity.
    /// </summary>
    private static readonly Dictionary<string, Biome> RUN_BIOMES = new()
    {
        // Run 1 — Highland Pass: a fresh, sunlit alpine meadow pass. Organic and inviting (the nursery): green
        // pasture over warm stone, clear glacial brooks, pollen in the morning light.
        ["highland_pass"] = new Biome
        {
            key = "highland_pass",
            name = "Highland Pass",
            ground = WORLD_INK.paperWarm,
            groundAccentA = WORLD_INK.paperDeep,
            groundAccentB = 0x69c98a,
            pattern = "mossStone",
            detail = 0x8fd6b4,
            haze = 0xeef7ee,
            lightTint = 0xfff8e0,
            vignette = 0.22,
            gradeTint = WORLD_INK.paperWarm,
            gradeTintAlpha = 0.02,
            grade = new ColorGrade { brightness = 1.04, contrast = 1.06, saturate = 1.04, hue = 0 },
            stormCore = WORLD_INK.cyan,
            stormBody = 0x165a62,
            stormBolt = WORLD_INK.paper,
            decal = "scree", // alpine rock chips + highland tufts (tinted meadow-fresh)
            timber = 0x795038, // warm structural beams for bridges and decks
            stone = 0x8c9487, // alpine retaining stone
            groundTint = 0x7bbf7f, // meadow grass
            tileset = "combined-building",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.5,
                groundAccent = 2.6,
                wallTint = 1.6,
                driftA = 0xd9f2c9,
                driftB = 0xa6c8ff,
                driftAmp = 0.14,
                waveSpeed = 1.15, // lively brooks
                foam = 1.2,
                glint = 1.1,
                windDirection = -0.34,
                windStrength = 1.18,
                windSpeed = 1.02,
                windGust = 0.92,
                sunLight = 1.08, // fresh alpine morning, close to the shared rig
                hemiLight = 1.04,
                fillLight = 0.92,
                ambientLight = 0.98,
            },
        },
        // Run 2 — Noir Sprawl: cyberpunk megacity turned feral. The base read is still wet-black concrete and hard
        // noir contrast, but the identity now comes from cyan/magenta/acid-green failures: circuit-lane scars under
        // your feet, holo signage on tower faces, chromatic rain and oily canals that catch the broken ad light.
        ["noir_sprawl"] = new Biome
        {
            key = "noir_sprawl",
            name = "Noir Sprawl",
            ground = 0x090a0f, // rain-black asphalt under neon spill
            groundAccentA = 0x020307, // deep street shadow / oil
            groundAccentB = 0xff1fd2, // magenta ad-light scarring the concrete
            pattern = "plaza", // rectilinear paving reads as city blocks under the street decals
            detail = 0x33ffe7, // cyan sign/neon glints & rain sparkle
            haze = 0x0b1019, // low smog with ad-glow in it
            lightTint = 0x7ffcff, // cold hologram key
            vignette = 0.58, // moody noir edges
            gradeTint = 0x110319,
            gradeTintAlpha = 0.11,
            grade = new ColorGrade { brightness = 0.92, contrast = 1.34, saturate = 1.24, hue = -5 },
            stormCore = 0xff2bd6, // magenta data-storm core
            stormBody = 0x060811,
            stormBolt = 0xb6ff3d, // acid-green arc failures
            decal = "circuit", // broken circuit lanes, barcode scars and crosswalk glyphs
            water = new FloodPalette { body = 0x07090f, deep = 0x010204, surface = 0x18293b, foam = 0x33ffe7, glint = 0xb6ff3d }, // oily canal
            timber = 0x56606c, // chromed steel walkway / industrial bridge deck
            stone = 0x9aa1b0, // cool concrete/glass (lifted to the bright wall band by terrainOf)
            groundTint = 0x0e0f15, // asphalt underfoot
            tileset = "city",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.62,
                groundAccent = 2.85,
                wallTint = 2.25,
                waterTint = 1.35,
                bridgeTint = 1.7,
                floorGrain = 0.5, // slick asphalt, with detail coming from circuit decals
                rockGrain = 1.65, // gritty concrete-grey rock
                strata = 2.2, // strong horizontal banding on the tall walls
                driftA = 0x38ffe0, // cyan signage
                driftB = 0xff2bd6, // magenta signage
                driftAmp = 0.26,
                waveSpeed = 0.54, // oily canal shimmer under neon
                foam = 0.55,
                glint = 2.4, // sharp wet-surface specular
                windDirection = 0.14,
                windStrength = 0.62,
                windSpeed = 1.34,
                windGust = 0.42,
                sunLight = 0.72, // smog-choked sun — the city lights itself
                hemiLight = 0.82,
                fillLight = 1.35, // hard cool hologram bounce for the noir contrast
                ambientLight = 0.78,
            },
        },
        // Run 3 - Olympian Sky Borough: an ancient marble old-town on floating cloud islands. White walls,
        // gold accents, cloud canals, divine lightning and cloud-scroll floor ornaments make this the Olympus run.
        ["olympian_sky_borough"] = new Biome
        {
            key = "olympian_sky_borough",
            name = "Olympian Sky Borough",
            ground = 0xf1ead8,
            groundAccentA = 0x9ed3ff,
            groundAccentB = 0xd8aa38,
            pattern = "plaza",
            detail = 0xffe08a,
            haze = 0xdff5ff,
            lightTint = 0xfff0bf,
            vignette = 0.2,
            gradeTint = 0xf8fcff,
            gradeTintAlpha = 0.035,
            grade = new ColorGrade { brightness = 1.08, contrast = 1.14, saturate = 1.08, hue = -1 },
            stormCore = 0xffd15a,
            stormBody = 0x4c78b8,
            stormBolt = 0xffffff,
            decal = "olympian",
            water = new FloodPalette { body = 0x83cfff, deep = 0x1b5f9f, surface = 0xc9f5ff, foam = 0xffffff, glint = 0xffe58a },
            timber = 0xcaa14a,
            stone = 0xf7f1df,
            groundTint = 0xe5edf2,
            tileset = "olympian",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.68,
                groundAccent = 2.95,
                wallTint = 2.55,
                waterTint = 1.52,
                bridgeTint = 1.9,
                floorGrain = 0.46,
                rockGrain = 0.42,
                strata = 0.5,
                driftA = 0xffffff,
                driftB = 0x8fd3ff,
                driftAmp = 0.14,
                waveSpeed = 1.22,
                foam = 1.7,
                glint = 2.15,
                windDirection = -0.12,
                windStrength = 1.36,
                windSpeed = 1.18,
                windGust = 1.08,
                sunLight = 1.15, // divine above-the-clouds glare on the marble
                hemiLight = 1.08,
                fillLight = 0.9,
                ambientLight = 1.04,
            },
        },
        // Run 4 - Sakura Temple Dream: lacquer bridges, temple stone, pale mist and soft shrine-water.
        ["sakura_temple_dream"] = new Biome
        {
            key = "sakura_temple_dream",
            name = "Sakura Temple Dream",
            ground = 0xf4efe2,
            groundAccentA = 0x88b77a,
            groundAccentB = 0xe87798,
            pattern = "mossStone",
            detail = 0xffcfe1,
            haze = 0xffedf4,
            lightTint = 0xffead8,
            vignette = 0.23,
            gradeTint = 0xfff0f5,
            gradeTintAlpha = 0.04,
            grade = new ColorGrade { brightness = 1.045, contrast = 1.09, saturate = 1.1, hue = 0 },
            stormCore = 0xff7aa8,
            stormBody = 0x6e1f3c,
            stormBolt = 0xfff5f8,
            // Sakura uses the complete Highland terrain vocabulary. Its identity comes from pigment and material,
            // never from a decoration kind that the normal terrain cannot also place.
            decal = "scree",
            water = new FloodPalette { body = 0x5aa89e, deep = 0x173b38, surface = 0xaee6d9, foam = 0xfff1f5, glint = 0xffc6d8 },
            timber = 0xb51f31,
            stone = 0xefe3d5,
            groundTint = 0x8db47a,
            tileset = "combined-building",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.72,
                groundAccent = 2.35,
                wallTint = 2.05,
                bridgeTint = 1.55,
                waterTint = 1.18,
                floorGrain = 0.68,
                rockGrain = 0.56,
                strata = 0.48,
                driftA = 0xffcfe1,
                driftB = 0xa7c98c,
                driftAmp = 0.16,
                waveSpeed = 0.58,
                foam = 0.72,
                glint = 1.08,
                windDirection = -0.62,
                windStrength = 0.76,
                windSpeed = 0.58,
                windGust = 0.54,
                sunLight = 0.96,
                hemiLight = 1.15, // soft petal-fog sky wraps the shrine light
                fillLight = 1.04,
                ambientLight = 1.08,
            },
        },
        // Run 5 - Abyssal Deepsea: black trench floors, wet pressure-stone walls, bioluminescent reef scars,
        // wreck-plank crossings and dark water channels lit by plankton glints.
        ["abyssal_deepsea"] = new Biome
        {
            key = "abyssal_deepsea",
            name = "Abyssal Deepsea",
            ground = 0x020713,
            groundAccentA = 0x061a2f,
            groundAccentB = 0x19f0cb,
            pattern = "voidVeins",
            detail = 0x9dfff0,
            haze = 0x031222,
            lightTint = 0x65e8d6,
            vignette = 0.6,
            gradeTint = 0x020716,
            gradeTintAlpha = 0.14,
            grade = new ColorGrade { brightness = 0.86, contrast = 1.28, saturate = 1.22, hue = -2 },
            stormCore = 0x8dfff0,
            stormBody = 0x031427,
            stormBolt = 0xe6fffb,
            decal = "reef",
            water = new FloodPalette { body = 0x041f34, deep = 0x00040a, surface = 0x0d6f87, foam = 0x89fff1, glint = 0xe6fffb },
            timber = 0x314754,
            stone = 0x2f6f82,
            groundTint = 0x061829,
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 2.25,
                groundAccent = 4.2,
                wallTint = 2.85,
                waterTint = 2.3,
                bridgeTint = 1.7,
                floorGrain = 1.45,
                rockGrain = 1.72,
                strata = 2.15,
                driftA = 0x9dfff0,
                driftB = 0x062f72,
                driftAmp = 0.32,
                waveSpeed = 0.22,
                foam = 1.7,
                glint = 2.25,
                windDirection = 0.2,
                windStrength = 0.34,
                windSpeed = 0.34,
                windGust = 0.28,
                sunLight = 0.55, // the sun drowned kilometres up
                hemiLight = 1.3, // submarine water-column dome is the key light
                fillLight = 1.18, // bioluminescent bounce
                ambientLight = 0.85,
            },
        },
        // Run 6 - Rainbowland: over-bright prism kingdom. The whole terrain kit wears the identity: striped lacquer
        // floors, candy wall stone, fizzy soapwater, loud bridge paint and glitter air over lethal storm pressure.
        ["rainbowland"] = new Biome
        {
            key = "rainbowland",
            name = "Rainbowland",
            ground = WORLD_INK.paperWarm,
            groundAccentA = 0xffe95b,
            groundAccentB = 0x45c7ff,
            pattern = "plaza",
            detail = 0xff3fbd,
            haze = 0xfffbef,
            lightTint = 0xfff2a6,
            vignette = 0.24,
            gradeTint = 0xfffbe8,
            gradeTintAlpha = 0.035,
            grade = new ColorGrade { brightness = 1.08, contrast = 1.18, saturate = 1.35, hue = 0 },
            stormCore = 0xff3fbd,
            stormBody = 0x3512a8,
            stormBolt = 0xffffff,
            decal = "rainbow",
            water = new FloodPalette { body = 0x58ddff, deep = 0x2a237e, surface = 0xcafff7, foam = 0xffffff, glint = 0xffe95b },
            timber = 0xff5fd2,
            stone = 0xffd8ee,
            groundTint = 0x8dff62,
            tileset = "prism",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.7,
                groundAccent = 4.4,
                wallTint = 2.4,
                waterTint = 1.8,
                bridgeTint = 2,
                floorGrain = 0.68,
                rockGrain = 0.72,
                strata = 0.55,
                driftA = 0xff3fbd,
                driftB = 0xffe95b,
                driftAmp = 0.34,
                waveSpeed = 1.95,
                foam = 1.9,
                glint = 2.25,
                windDirection = -0.2,
                windStrength = 1.08,
                windSpeed = 1.42,
                windGust = 0.94,
                sunLight = 1.1, // over-bright prism-kingdom daylight
                hemiLight = 1.12,
                fillLight = 1.06,
                ambientLight = 1.1,
            },
        },
        // Run 7 - Clockwork Moon Bazaar: a night market on a broken moon. Dark moon-dust floor, brass alleys,
        // gear-tooth walls, teal mercury gutters and lantern-warm market machinery.
        ["clockwork_moon_bazaar"] = new Biome
        {
            key = "clockwork_moon_bazaar",
            name = "Clockwork Moon Bazaar",
            ground = 0x151415,
            groundAccentA = 0xc18a32,
            groundAccentB = 0x38c7b0,
            pattern = "plaza",
            detail = 0x72ffe0,
            haze = 0x221f22,
            lightTint = 0xffd48a,
            vignette = 0.5,
            gradeTint = 0x21170d,
            gradeTintAlpha = 0.095,
            grade = new ColorGrade { brightness = 0.9, contrast = 1.28, saturate = 1.14, hue = -2 },
            stormCore = 0xffc15a,
            stormBody = 0x20170f,
            stormBolt = 0x72ffe0,
            decal = "gear",
            water = new FloodPalette { body = 0x4a5659, deep = 0x070909, surface = 0xa9b4b3, foam = 0xffcf6a, glint = 0xdffffa },
            timber = 0xaa762a,
            stone = 0xc49a57,
            groundTint = 0x2a2725,
            tileset = "clockwork",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 2.05,
                groundAccent = 3.55,
                wallTint = 2.55,
                waterTint = 1.85,
                bridgeTint = 2.25,
                floorGrain = 0.78,
                rockGrain = 1.62,
                strata = 2.35,
                driftA = 0xffc15a,
                driftB = 0x38c7b0,
                driftAmp = 0.3,
                waveSpeed = 0.38,
                foam = 0.62,
                glint = 2.35,
                windDirection = 0.44,
                windStrength = 0.52,
                windSpeed = 0.7,
                windGust = 0.38,
                sunLight = 0.8, // broken-moon night market
                hemiLight = 0.85,
                fillLight = 1.15, // warm brass lantern bounce off the machinery
                ambientLight = 0.76,
            },
        },
        // Run 8 - Sugarstorm-Carnival Run: frosting roads, syrup canals, tent-light glare, candy bridges and
        // confetti sparks over a playful but unsafe midway.
        ["sugarstorm_carnival"] = new Biome
        {
            key = "sugarstorm_carnival",
            name = "Sugarstorm-Carnival Run",
            ground = 0xfff0df,
            groundAccentA = 0xff72b7,
            groundAccentB = 0x61e8c7,
            pattern = "plaza",
            detail = SUGARSTORM_CARNIVAL_COLORS[4],
            haze = 0xffeaf7,
            lightTint = 0xffefb6,
            vignette = 0.34,
            gradeTint = 0xffe6f3,
            gradeTintAlpha = 0.055,
            grade = new ColorGrade { brightness = 1.035, contrast = 1.16, saturate = 1.3, hue = 0 },
            stormCore = 0xff2f8f,
            stormBody = 0x4b174e,
            stormBolt = SUGARSTORM_CARNIVAL_COLORS[4],
            decal = "carnival",
            water = new FloodPalette { body = 0xc73586, deep = 0x421033, surface = 0xff86c4, foam = 0xfff4c7, glint = 0x61e8c7 },
            timber = 0xff5aa6,
            stone = 0xffdced,
            groundTint = 0xf5c8d8,
            tileset = "carnival",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.85,
                groundAccent = 4.15,
                wallTint = 2.35,
                waterTint = 2.05,
                bridgeTint = 2.25,
                floorGrain = 0.52,
                rockGrain = 0.66,
                strata = 0.38,
                driftA = 0xff72b7,
                driftB = 0x61e8c7,
                driftAmp = 0.32,
                waveSpeed = 0.36,
                foam = 1.65,
                glint = 2.35,
                windDirection = -0.16,
                windStrength = 1.02,
                windSpeed = 1.24,
                windGust = 0.88,
                sunLight = 1.1, // bright midway afternoon
                hemiLight = 1.02,
                fillLight = 1.12, // tent-light glare filling the alleys
                ambientLight = 1,
            },
        },
        // Run 9 - Prismglass Archive: broken mirror-glass archive. Shelf-wall silhouettes, cyan-violet glass floors,
        // reflective channels, transparent bridges and shard marks make it a cold quiet place, not a palette swap.
        ["prismglass_archive"] = new Biome
        {
            key = "prismglass_archive",
            name = "Prismglass Archive",
            ground = 0x080b1f,
            groundAccentA = 0x7af4ff,
            groundAccentB = 0xb174ff,
            pattern = "voidVeins",
            detail = 0xf2fdff,
            haze = 0x111c38,
            lightTint = 0xbff4ff,
            vignette = 0.55,
            gradeTint = 0x070918,
            gradeTintAlpha = 0.115,
            grade = new ColorGrade { brightness = 0.9, contrast = 1.32, saturate = 1.24, hue = -4 },
            stormCore = 0x9ff7ff,
            stormBody = 0x150c3d,
            stormBolt = 0xffffff,
            decal = "glassShard",
            water = new FloodPalette { body = 0x121749, deep = 0x01030d, surface = 0x61e7ff, foam = 0xf2fdff, glint = 0xcba8ff },
            timber = 0xa7ecff,
            stone = 0xdce9ff,
            groundTint = 0x283a7b,
            tileset = "prismglass",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 2.08,
                groundAccent = 4.25,
                wallTint = 2.95,
                waterTint = 2.05,
                bridgeTint = 2.15,
                floorGrain = 0.34,
                rockGrain = 0.46,
                strata = 1.75,
                driftA = 0x7af4ff,
                driftB = 0xb174ff,
                driftAmp = 0.36,
                waveSpeed = 0.34,
                foam = 0.9,
                glint = 2.6,
                windDirection = 0.08,
                windStrength = 0.24,
                windSpeed = 0.42,
                windGust = 0.2,
                sunLight = 0.68, // no direct sun reaches the stacks
                hemiLight = 1.25, // diffuse indoor glass-light from everywhere
                fillLight = 1.2,
                ambientLight = 0.9,
            },
        },
        // Run 10 - Starforged Cathedral Endrun: black star-rivers, bone-white arches, molten constellations and
        // altar terraces at the bottom of the descent.
        ["starforged_cathedral_endrun"] = new Biome
        {
            key = "starforged_cathedral_endrun",
            name = "Starforged Cathedral Endrun",
            ground = 0x05030a,
            groundAccentA = 0xf0e3c4,
            groundAccentB = 0xff9f2e,
            pattern = "voidVeins",
            detail = 0xffc55a,
            haze = 0x0b0612,
            lightTint = 0xffd08a,
            vignette = 0.66,
            gradeTint = 0x08030f,
            gradeTintAlpha = 0.14,
            grade = new ColorGrade { brightness = 0.86, contrast = 1.34, saturate = 1.18, hue = 0 },
            stormCore = 0xffb13b,
            stormBody = 0x12040c,
            stormBolt = 0xfff1c2,
            decal = "cathedralStar",
            water = new FloodPalette { body = 0x06030c, deep = 0x000002, surface = 0x160b20, foam = 0xffbe57, glint = 0xfff1c2 },
            timber = 0x2b1720,
            stone = 0xf0e3c4,
            groundTint = 0x1b1322,
            tileset = "cathedral",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 2.25,
                groundAccent = 4.1,
                wallTint = 3.05,
                waterTint = 2.2,
                bridgeTint = 2.05,
                floorGrain = 0.8,
                rockGrain = 0.9,
                strata = 2.35,
                driftA = 0xffc55a,
                driftB = 0xf0e3c4,
                driftAmp = 0.34,
                waveSpeed = 0.3,
                foam = 0.48,
                glint = 2.35,
                windDirection = -0.28,
                windStrength = 0.48,
                windSpeed = 0.62,
                windGust = 0.4,
                sunLight = 0.85, // no daylight at the bottom of the descent
                hemiLight = 0.75,
                fillLight = 1.08, // molten constellations under-light the arches
                ambientLight = 0.7, // sombre nave gloom
            },
        },
    };
    /// <summary>
    /// Finite authoring themes intentionally absent from every run/raid registry. Their keys remain resolvable so
    /// exported Terrain Studio artifacts keep their exact visual identity, but only the standalone map generator
    /// advertises them as paint choices.
    /// </summary>
    public static readonly IReadOnlyList<string> GENERATOR_ONLY_THEME_KEYS = new[]
    {
        "viking_ship_village",
        "alien_ranch",
    };

    private static readonly Dictionary<string, Biome> GENERATOR_ONLY_BIOMES = new()
    {
        ["viking_ship_village"] = new Biome
        {
            key = "viking_ship_village",
            name = "Viking Ship Village",
            ground = 0x28352d,
            groundAccentA = 0x775039,
            groundAccentB = 0x9ab6ac,
            pattern = "mossStone",
            detail = 0xffc86a,
            haze = 0x9eb4bc,
            lightTint = 0xffd79a,
            vignette = 0.38,
            gradeTint = 0x19303a,
            gradeTintAlpha = 0.075,
            grade = new ColorGrade { brightness = 0.96, contrast = 1.22, saturate = 1.12, hue = -3 },
            stormCore = 0x79c9d8,
            stormBody = 0x172b36,
            stormBolt = 0xf3f8e9,
            decal = "runeKnot",
            water = new FloodPalette
            {
                body = 0x193f4d,
                deep = 0x071820,
                surface = 0x477b86,
                foam = 0xd9efe8,
                glint = 0xffcf78,
            },
            timber = 0x5b2f22,
            stone = 0x788781,
            groundTint = 0x405d4a,
            tileset = "viking-ship-village",
            materialDialect = "viking-ship-village",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.85,
                groundAccent = 3.15,
                wallTint = 2.1,
                waterTint = 1.72,
                bridgeTint = 2.45,
                floorGrain = 1.3,
                rockGrain = 1.56,
                strata = 1.82,
                driftA = 0x8eb7ad,
                driftB = 0xffb85a,
                driftAmp = 0.2,
                waveSpeed = 1.12,
                foam = 1.34,
                glint = 1.72,
                windDirection = -0.78,
                windStrength = 1.34,
                windSpeed = 1.18,
                windGust = 1.04,
                sunLight = 0.88,
                hemiLight = 1.12,
                fillLight = 1.04,
                ambientLight = 0.9,
            },
        },
        // The **Alien Ranch** — a pastoral farm world tended by keepers nobody has ever seen. Lavender turf
        // steppes roll under a chartreuse sky-milk; luminous nutrient goo idles through irrigation canals and
        // pools into paddock ponds; sunbaked coral-clay bluffs terrace into grazing mesas; and the ground
        // everywhere carries pressed-flat crop-circle glyphs still warm with harvest glow. Deliberately the
        // inverse of every earthly biome: the GRASS is violet, the WATER is green, and the sky hums.
        ["alien_ranch"] = new Biome
        {
            key = "alien_ranch",
            name = "Alien Ranch",
            ground = 0x3c2b48, // deep ultraviolet loam under the turf
            // groundAccentA is the LANDSCAPE accent — it floods terrace ramps, step faces and the dry pole, so it
            // must carry the turf's violet. The acid chlorophyll lives in detail/decal/goo only, never here (an
            // acid A washes the whole pasture olive).
            groundAccentA = 0x8a5fae,
            groundAccentB = 0xe25fa4, // hot orchid bloom drifting through the fields
            pattern = "mossStone",
            detail = 0xd8ff5e, // harvest-glow specks in the air and underfoot
            haze = 0xd6e8a8, // pale chartreuse sky-milk on the horizon
            lightTint = 0xf1f2d8,
            vignette = 0.3,
            gradeTint = 0x3a2450,
            gradeTintAlpha = 0.06,
            grade = new ColorGrade { brightness = 1.0, contrast = 1.14, saturate = 1.22, hue = 0 },
            stormCore = 0xb6ff3d, // the storm wall is a harvester sweep — acid light in ultraviolet murk
            stormBody = 0x2c1440,
            stormBolt = 0xeaffb0,
            decal = "cropCircle",
            water = new FloodPalette
            {
                // Nutrient goo, not water: viscous luminous green ordered dark→bright body→foam so the shared
                // readability contract (lum(body) < lum(foam)) holds while the palette reads unmistakably WRONG
                // for water — exactly the point.
                body = 0x4c8f1c,
                deep = 0x1d4110,
                surface = 0x86d63a,
                foam = 0xe4ffc2,
                glint = 0xff8ff2, // pink sparkle riding the goo crests
            },
            timber = 0x7a6ea8, // anodized alloy walkway decks — the ranch builds no wooden bridges
            stone = 0xd8a58a, // sunbaked coral-clay bluff faces
            groundTint = 0x6f4e82, // the deep lavender the pasture terraces actually mix toward
            tileset = "alien-ranch",
            style = new WorldStyle(DEFAULT_WORLD_STYLE)
            {
                floorTint = 1.7,
                groundAccent = 2.9, // the turf must read saturated lavender, never a pale wash
                wallTint = 2.0,
                waterTint = 1.9, // authored goo pushes hard over the storm derivation
                bridgeTint = 2.2,
                floorGrain = 1.25,
                rockGrain = 1.35,
                strata = 1.5,
                driftA = 0xd977e8, // orchid ↔ chartreuse — the ranch's two impossible pigments
                driftB = 0xa9e26a,
                driftAmp = 0.24,
                waveSpeed = 0.55, // goo is viscous: slow crests…
                foam = 1.5, // …but churning froth where it moves
                glint = 1.9,
                windDirection = 0.62,
                windStrength = 1.15,
                windSpeed = 0.75, // heavy slow waves rolling through the turf
                windGust = 0.9,
                sunLight = 1.06,
                hemiLight = 1.18, // the big humming sky IS the light source
                fillLight = 1.12, // green bounce off the goo
                ambientLight = 0.94,
            },
        },
    };
    public static IReadOnlyList<Biome> generatorOnlyBiomes()
    {
        return GENERATOR_ONLY_THEME_KEYS.map((key) => GENERATOR_ONLY_BIOMES[key]);
    }

    /// <summary>The mood family each raid biome key wears — kept in lockstep with RAID_MOOD_BY_TIER.</summary>
    private static readonly Dictionary<string, string> RAID_BIOME_MOOD = new()
    {
        ["raid_verdant"] = "machineworks",
        ["raid_tidecage"] = "tidecage",
        ["raid_thousandfolds"] = "papertemple",
        ["raid_wyrmforge"] = "wyrmforge",
        ["raid_crownbower"] = "crownbower",
        ["raid_starossuary"] = "starossuary",
        ["raid_resonanceeyrie"] = "resonanceeyrie",
        ["raid_regrowthcanals"] = "regrowthcanals",
        ["raid_hollowcartography"] = "hollowcartography",
        ["raid_moonroot"] = "moonroot",
        ["raid_holdthefort"] = "aegisCitadel",
    };

    /// <summary>
    /// The biome for a run/raid world key — for read-only consumers (e.g. the minimap, the standalone generator)
    /// that have the descriptor's `biomeKey` but not a full instance id. Run world-theme keys resolve to their
    /// authored <see cref="RUN_BIOMES"/> identity; raid/legacy family keys resolve through the mood table. Falls back
    /// to the Hub palette for an unknown key.
    ///
    /// The TS registries are plain objects, so `key in GENERATOR_ONLY_BIOMES` / `RUN_BIOMES[key]` would also see
    /// Object.prototype members ("toString", "constructor", …). No theme key is ever such a name; the dictionaries
    /// here answer only for their own entries.
    /// </summary>
    public static Biome biomeForKey(string? key)
    {
        if (key == "arena") return ARENA_BIOME;
        if (key == "faction_war") return FACTION_WAR_BIOME;
        if (key == "faction_light") return FACTION_LIGHT_BIOME;
        if (key == "faction_middle") return FACTION_MIDDLE_BIOME;
        if (key == "faction_shadow") return FACTION_SHADOW_BIOME;
        if (key == "pockettown") return POCKETTOWN_BIOME;
        if (key == "museum") return MUSEUM_BIOME;
        if (!string.IsNullOrEmpty(key) && GENERATOR_ONLY_BIOMES.TryGetValue(key, out var generatorOnly))
            return generatorOnly;
        if (!string.IsNullOrEmpty(key) && RUN_BIOMES.TryGetValue(key, out var run)) return run;
        Biome? mood = !string.IsNullOrEmpty(key)
            ? (MOOD_BIOMES.TryGetValue(RAID_BIOME_MOOD.TryGetValue(key, out var raidMood) ? raidMood : key, out var family)
                ? family
                : MOOD_BIOMES.TryGetValue(key, out var direct) ? direct : null)
            : null;
        return mood ?? HUB_BIOME;
    }

    /// <summary>The PvP Arena — a dead **volcano**: a brooding, ash-choked basalt battlefield veined with glowing lava and
    /// ringed by towering peaks. Dark and rotted, lit from below by the fire; the ruined refuge sits at its heart.</summary>
    private static readonly Biome ARENA_BIOME = new()
    {
        key = "arena",
        name = "Mountain Canyon Arena",
        ground = WORLD_INK.paperWarm,
        groundAccentA = WORLD_INK.paperDeep,
        groundAccentB = WORLD_INK.red,
        pattern = "mossStone",
        detail = WORLD_INK.red,
        haze = WORLD_INK.paper,
        lightTint = WORLD_INK.paper,
        vignette = 0.28,
        gradeTint = WORLD_INK.paperWarm,
        gradeTintAlpha = 0.02,
        grade = new ColorGrade { brightness = 1.0, contrast = 1.1, saturate = 1.04, hue = 0 },
        stormCore = WORLD_INK.red,
        stormBody = 0x761c16,
        stormBolt = WORLD_INK.yellow,
        decal = "scree",
        style = new WorldStyle(DEFAULT_WORLD_STYLE)
        {
            floorTint = 1.35,
            groundAccent = 2.1,
            wallTint = 1.9,
            waterTint = 1.6, // lava veins, not water
            bridgeTint = 1.3,
            floorGrain = 1.5, // cracked cinder crust
            rockGrain = 1.7,
            strata = 1.85, // hard canyon strata
            driftA = 0xffb45e, // ember haze
            driftB = 0x6e5648, // ash-brown basalt
            driftAmp = 0.2,
            waveSpeed = 0.4, // slow lava creep
            foam = 0.35,
            glint = 1.35,
            windDirection = 0.9,
            windStrength = 0.85,
            windSpeed = 1.05,
            windGust = 1, // dry dust-devil bursts through the canyon
            sunLight = 1.28, // glaring, pitiless volcano light
            hemiLight = 0.88,
            fillLight = 0.78,
            ambientLight = 0.72, // hard black shadows for the duel
        },
    };

    /// <summary>Shared battlefield grade, with per-tile Light/Shadow material families supplied by the terrain themes.</summary>
    private static readonly Biome FACTION_WAR_BIOME = new(ARENA_BIOME)
    {
        key = "faction_war",
        name = "The Convergence Lane",
        ground = 0xd8cfb5,
        groundAccentA = 0x87a5b3,
        groundAccentB = 0x704d77,
        detail = 0xd3ae55,
        haze = 0xd6d3c8,
        lightTint = 0xffedb0,
        vignette = 0.22,
        gradeTint = 0xc9b58b,
        gradeTintAlpha = 0.045,
        grade = new ColorGrade { brightness = 1.01, contrast = 1.08, saturate = 1.08, hue = 0 },
        stormCore = 0xe8c86a,
        stormBody = 0x76517c,
        stormBolt = 0xf8edbb,
        style = new WorldStyle(ARENA_BIOME.style ?? DEFAULT_WORLD_STYLE)
        {
            floorTint = 1.52,
            groundAccent = 1.8,
            wallTint = 1.72,
            waterTint = 1.25,
            bridgeTint = 1.65,
            floorGrain = 1.05,
            rockGrain = 1.15,
            strata = 1.25,
            driftA = 0xffe8a3,
            driftB = 0x755378,
            driftAmp = 0.17,
            windStrength = 0.48,
            windGust = 0.55,
            sunLight = 1.2,
            hemiLight = 0.98,
            fillLight = 0.92,
            ambientLight = 0.82,
        },
    };

    private static readonly Biome FACTION_LIGHT_BIOME = new(FACTION_WAR_BIOME)
    {
        key = "faction_light",
        name = "The Light Bastion",
        ground = 0xe1d8b8,
        groundAccentA = 0x9dc9cf,
        groundAccentB = 0xd8b85d,
        detail = 0xf0cb63,
        lightTint = 0xfff0b0,
        gradeTint = 0xc6e6e4,
        stormBody = 0x5e9bab,
    };

    /// <summary>The contested centre is neither team's recolour: weathered treaty stone, bronze and cold teal rifts.</summary>
    private static readonly Biome FACTION_MIDDLE_BIOME = new(FACTION_WAR_BIOME)
    {
        key = "faction_middle",
        name = "The Shattered Accord",
        ground = 0xc9c2aa,
        groundAccentA = 0x688f8d,
        groundAccentB = 0xa57946,
        detail = 0xe1b85d,
        lightTint = 0xe9e1c7,
        gradeTint = 0x8c8977,
        stormBody = 0x426f73,
    };

    private static readonly Biome FACTION_SHADOW_BIOME = new(FACTION_WAR_BIOME)
    {
        key = "faction_shadow",
        name = "The Shadow Bastion",
        ground = 0xb9a9b7,
        groundAccentA = 0x725574,
        groundAccentB = 0xb24f67,
        detail = 0xba6aa9,
        lightTint = 0xd8b4e7,
        gradeTint = 0x6d4b74,
        stormBody = 0x43284f,
    };

    // `/^theme:([a-z0-9_]+)(?::\d+)?$/` with ECMAScript semantics: JS `$` (no `m` flag) matches only at the very
    // end of the input (.NET `$` also before a final "\n") — hence `\z`; JS `\d` is exactly [0-9].
    // STUDIO: any seed text as variant suffix (see Descriptor.THEME_ID).
    private static readonly Regex THEME_INSTANCE_ID = new(@"^theme:([a-z0-9_]+)(?::[A-Za-z0-9_.-]+)?\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolve the look of a live space from the authoritative `Welcome` fields.
    ///
    /// A **theme world** (`theme:&lt;key&gt;`) is a standalone preview of one authored world theme and wears that
    /// theme. The permanent world wears the same theme as its authoritative terrain descriptor; fixtures and
    /// unknown spaces retain the authored garden's calm Hub identity.
    /// </summary>
    public static Biome resolveBiome(double _instanceKind, string instanceId)
    {
        if (instanceId == WorldIdentity.SHARED_WORLD_ID) return biomeForKey(WorldIdentity.SHARED_WORLD_BIOME);
        var theme = THEME_INSTANCE_ID.Match(instanceId);
        return theme.Success ? biomeForKey(theme.Groups[1].Value) : HUB_BIOME;
    }
}
