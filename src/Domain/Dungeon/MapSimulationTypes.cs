// Port of packages/shared/src/domain/dungeon/mapSimulationTypes.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using System.Threading;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * The public shape of a **Simulate Map** build — layers, sweeps, stages, recipe, metrics and options.
 *
 * Split out of the composer (MapSimulation) so the client's playback, the ink pass and the generator UI can
 * depend on the contract without pulling the whole composition machine into their module graph, and so the
 * composer file stays about composition.
 */

/// <summary>The ordered layers a simulated map is built from — the animation's story beats, and the build's real passes.</summary>
public static class MapSimulationLayer
{
    public const string Bedrock = "bedrock";
    public const string Relief = "relief";
    public const string Hydrology = "hydrology";
    public const string Rifts = "rifts";
    public const string Crossings = "crossings";
    /// <summary>Monumental set pieces: the things in the world that were unmistakably built on purpose.</summary>
    public const string Wonders = "wonders";
    /// <summary>The circulation network — avenues, plazas and the stairs that climb the terraces.</summary>
    public const string Roads = "roads";
    public const string Theming = "theming";
    public const string Flora = "flora";
    public const string Landmarks = "landmarks";
}

/// <summary>How a layer's cells enter the world. Consumers turn this into per-cell reveal timing.</summary>
public static class MapRevealSweep
{
    /// <summary>Grows outward from an anchor cell — used where the world spreads from its heart.</summary>
    public const string Radial = "radial";
    /// <summary>Crosses the map along a direction — used where a layer reads as a survey pass.</summary>
    public const string Linear = "linear";
    /// <summary>Follows the layer's own long axis — water runs its course, rifts tear along their strike.</summary>
    public const string Flow = "flow";
    /// <summary>Individually placed records that pop in a deterministic scatter order.</summary>
    public const string Scatter = "scatter";
}

public sealed class MapRevealSweepPlan
{
    /// <summary>A <see cref="MapRevealSweep"/> value.</summary>
    public string kind = MapRevealSweep.Radial;
    /// <summary>Sweep origin in artifact cell coordinates.</summary>
    public double ax;
    public double ay;
    /// <summary>Unit sweep direction (linear/flow).</summary>
    public double dx;
    public double dy;
    /// <summary>0..1 — how ragged the advancing front is. 0 is a ruler edge, 1 a broken, organic front.</summary>
    public double jitter;
}

/// <summary>One monumental set piece the composition raised, with the footprint the camera can frame it by.</summary>
public sealed class PlacedMapWonder
{
    public string key = "";
    public string name = "";
    /// <summary>A <see cref="MapWonderForm"/> value.</summary>
    public string form = "";
    public int tx;
    public int ty;
    public double radius;
}

/// <summary>
/// One built layer. `cells` are the artifact cell indices this layer introduced; the snapshots are the layer
/// state *after* it ran, and are only present when that layer actually changed them (null = inherit the
/// previous layer's array). Placements are the records this layer adds, already in a pleasing reveal order.
/// </summary>
public sealed class MapSimulationStage
{
    /// <summary>A <see cref="MapSimulationLayer"/> value.</summary>
    public string layer = "";
    public string title = "";
    public string detail = "";
    /// <summary>
    /// Seconds this layer occupies at 1x playback.
    ///
    /// The composer derives this from the work the layer actually did, not from an authored constant: a beat
    /// that spends 1.5 s revealing nothing is the single loudest tell that a build cinematic is a progress bar
    /// wearing a costume. A layer that changed nothing gets exactly 0 and playback skips it outright.
    /// </summary>
    public double beatSeconds;
    public MapRevealSweepPlan sweep = new();
    public uint[] cells = Array.Empty<uint>();
    public byte[]? tiles;
    public sbyte[]? elevation;
    public byte[]? themeIndex;
    public IReadOnlyList<TerrainDecorationPlacement>? decorations;
    /// <summary>Wonders raised by this layer, in reveal order, for the cinematic's callouts.</summary>
    public IReadOnlyList<PlacedMapWonder>? wonders;

    /// <summary>Shallow copy — `{ ...stage }`.</summary>
    public MapSimulationStage Clone() => (MapSimulationStage)MemberwiseClone();
}

public sealed class SimulatedMapRecipe
{
    /// <summary>The instance-style id this world was rolled from — re-simulating it reproduces the map exactly.</summary>
    public string seedId = "";
    public MapSimulationArchetype archetype = null!;
    /// <summary>The macro layout the world was built on, when one survived its measured retry.</summary>
    public MapLandformMotif? motif;
    /// <summary>Theme keys in palette order. Index 0 is the artifact's base biome.</summary>
    public IReadOnlyList<string> themeKeys = Array.Empty<string>();
    public int width;
    public int height;
    /// <summary>Display name, e.g. `Highland Pass · River Delta`.</summary>
    public string name = "";
    /// <summary>Walkable cell the composition reads as its heart — the camera's and the reveal's anchor.</summary>
    public int heartTx;
    public int heartTy;
    /// <summary>0..1 dial this world was rolled at.</summary>
    public double chaos;
    /// <summary>A <see cref="MapFrameKind"/> value.</summary>
    public string frame = MapFrameKind.Open;
    /// <summary>Every wonder in the world, in the order they were raised.</summary>
    public IReadOnlyList<PlacedMapWonder> wonders = Array.Empty<PlacedMapWonder>();
    /// <summary>Total authored runtime of the build cinematic at 1x, in seconds.</summary>
    public double runtimeSeconds;
}

public sealed class SimulatedMapMetrics
{
    public int cells;
    public int walkableCells;
    public int waterCells;
    public int chasmCells;
    public int bridgeCells;
    public int decorations;
    public int themeRegions;
    /// <summary>Decorations per walkable cell — the Tutorial map sits at 0.137.</summary>
    public double floraPerWalkable;
    public int wonders;
    /// <summary>Cells claimed by the circulation network (avenues + plazas).</summary>
    public int roadCells;
    /// <summary>Distinct walkable elevation levels in use — the landform's readable shelf count.</summary>
    public int terraceLevels;
}

public sealed class SimulatedMap
{
    public SimulatedMapRecipe recipe = null!;
    /// <summary>The finished, validated artifact.</summary>
    public TerrainArtifact artifact = null!;
    public TerrainValidationResult validation = null!;
    public IReadOnlyList<MapSimulationStage> stages = Array.Empty<MapSimulationStage>();
    /// <summary>The blank sheet every stage builds on: rim-framed solid ground at a flat datum.</summary>
    public byte[] baseTiles = Array.Empty<byte>();
    public sbyte[] baseElevation = Array.Empty<sbyte>();
    /// <summary>Composition metrics, surfaced by the generator UI (and asserted by the quality tests).</summary>
    public SimulatedMapMetrics metrics = null!;
}

/// <summary>
/// TS `seed: number | string`. The composer only ever reads it as `String(options.seed)`, so this carries that
/// string form: a number converts through ECMAScript Number::toString (<see cref="Js.Str(double)"/>), exactly as
/// the original's template literal would print it.
/// </summary>
public readonly struct MapSimulationSeed
{
    private readonly string? text;

    public MapSimulationSeed(string text) => this.text = text;

    public static implicit operator MapSimulationSeed(string text) => new(text);

    /// <summary>`String(seed)`. A default-constructed seed reads as the empty string.</summary>
    public override string ToString() => text ?? "";
}

/// <summary>
/// The composition dials.
///
/// Everything below `archetypeKey` is a *bias*: a 0..2 multiplier over what the archetype already asks for,
/// with 1 meaning "exactly the archetype". STUDIO: 0 means none of the feature at all, on the finished map. Biases are deliberately not absolute values — an absolute water
/// share would mean the same number produces a lake district and a drowned atoll depending on the archetype,
/// which makes the dial unlearnable. A null field is the TS `undefined` (the documented default applies).
/// </summary>
public sealed class MapSimulationOptions
{
    /// <summary>Any seed. The same seed always composes the same world.</summary>
    public MapSimulationSeed seed;
    /// <summary>Default 96, clamped (rounded) to 24..384.</summary>
    public double? width;
    /// <summary>Default 72, clamped (rounded) to 24..256.</summary>
    public double? height;
    /// <summary>Candidate theme keys to roll from. Defaults to <see cref="MapSimulation.generatorThemeKeys"/>.</summary>
    public IReadOnlyList<string>? themeKeys;
    /// <summary>Pin the theme instead of rolling one. The whole world then wears exactly this theme.</summary>
    public string? themeKey;
    /// <summary>
    /// Partition the map into several theme regions (the Hub's composition language) instead of dressing it in
    /// one theme. Off by default: a map with an unrequested second theme in it reads as a bug, not as variety.
    /// </summary>
    public bool? blendThemes;
    /// <summary>Pin the archetype instead of rolling one.</summary>
    public string? archetypeKey;
    /// <summary>Pin the silhouette (a <see cref="MapFrameKind"/> value) instead of rolling one from the archetype's set.</summary>
    public string? frameKind;
    /// <summary>
    /// Pin the macro layout stamped over the relief. `""` forces a pure-landform world; a key from
    /// <see cref="MapSimulationMotifs.MAP_LANDFORM_MOTIFS"/> forces that motif. Null rolls one.
    /// </summary>
    public string? motifKey;
    /// <summary>
    /// 0..1 — how far the composition is allowed to stray from its archetype's centre.
    ///
    /// At 0 every scalar sits on the archetype's authored value, the frame is the archetype's first choice and
    /// only the calm half of the wonder catalog is reachable: the same archetype composes recognisably the same
    /// kind of world every time. At 1 every band is opened to its full width, exotic wonders unlock, the frame
    /// can be anything, sides drop out of the silhouette and the map grows things you did not ask for. Default
    /// 0.4 — visibly varied, still legible.
    /// </summary>
    public double? chaos;
    /// <summary>0..2 over the archetype's water budget.</summary>
    public double? waterBias;
    /// <summary>0..2 over the archetype's rift budget.</summary>
    public double? riftBias;
    /// <summary>0..2 over the archetype's rock and relief (STUDIO: the rock mass itself too; 0 is one flat floor).</summary>
    public double? reliefBias;
    /// <summary>0..2 over the archetype's flora density.</summary>
    public double? floraBias;
    /// <summary>0..2 over the archetype's landmark density.</summary>
    public double? landmarkBias;
    /// <summary>0..2 over the archetype's wonder density. 0 composes a world with no set pieces at all.</summary>
    public double? wonderBias;
    /// <summary>Wonder keys (<see cref="MapSetPieces.MAP_WONDERS"/>) this world never raises; the rest roll as usual.</summary>
    public IReadOnlyCollection<string>? excludedWonders;
    /// <summary>0..2 over the archetype's road density. 0 composes untouched wilderness.</summary>
    public double? roadBias;
}

/// <summary>One interruptible slice of composition work (TS `BuildStep`).</summary>
internal readonly struct MapSimulationBuildStep
{
    public readonly string label;
    public readonly double weight;

    public MapSimulationBuildStep(string label, double weight)
    {
        this.label = label;
        this.weight = weight;
    }
}

/// <summary>
/// A step-sliced build. Drive <see cref="step"/> until it returns true, then read <see cref="result"/>.
///
/// The pump is deliberately clock-free — shared domain code owns no timer. Interactive callers spend their own
/// frame budget by pumping single slices until their deadline; headless callers simply drain it.
///
/// PORT NOTE (threading): every build owns its complete composition state, so independent builds may run on
/// different threads at the same time. One build must be pumped from one thread at a time; <see cref="progress"/>,
/// <see cref="label"/>, <c>done</c> and <see cref="result"/> may be read from any thread while it runs.
/// </summary>
public sealed class MapSimulationBuild
{
    private readonly IEnumerator<MapSimulationBuildStep> runner;
    private readonly Func<SimulatedMap?> resultOf;
    private readonly double totalWeight;
    private double doneWeight;
    private string currentLabel = "Surveying bedrock";
    private volatile bool finished;
    private volatile SimulatedMap? finalResult;

    internal MapSimulationBuild(IEnumerator<MapSimulationBuildStep> runner, double totalWeight, Func<SimulatedMap?> resultOf)
    {
        this.runner = runner;
        this.totalWeight = totalWeight;
        this.resultOf = resultOf;
    }

    /// <summary>0..1 build progress, for a progress readout while the world is composed.</summary>
    public double progress => finished ? 1 : Math.min(0.995, Volatile.Read(ref doneWeight) / totalWeight);

    /// <summary>What the build is doing right now.</summary>
    public string label => finished ? "Ready" : Volatile.Read(ref currentLabel);

    public SimulatedMap? result => finalResult;

    /// <summary>Advance the build by at most `slices` units of work. Returns true once finished.</summary>
    public bool step(int slices = 1)
    {
        if (finished) return true;
        for (int i = 0; i < Math.max(1, slices); i++)
        {
            if (!runner.MoveNext())
            {
                // Result before the flag, so a reader that sees `done` also sees the world.
                finalResult = resultOf();
                Volatile.Write(ref doneWeight, totalWeight);
                finished = true;
                return true;
            }
            MapSimulationBuildStep next = runner.Current;
            Volatile.Write(ref doneWeight, doneWeight + next.weight);
            Volatile.Write(ref currentLabel, next.label);
        }
        return false;
    }
}

public static class MapSimulationTypes
{
    public static readonly IReadOnlyList<string> MAP_SIMULATION_LAYERS = Array.AsReadOnly(new[]
    {
        MapSimulationLayer.Bedrock,
        MapSimulationLayer.Relief,
        MapSimulationLayer.Hydrology,
        MapSimulationLayer.Rifts,
        MapSimulationLayer.Crossings,
        MapSimulationLayer.Wonders,
        MapSimulationLayer.Roads,
        MapSimulationLayer.Theming,
        MapSimulationLayer.Flora,
        MapSimulationLayer.Landmarks,
    });
}
