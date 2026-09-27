// Port of packages/client/src/generatorSimulation.ts (MapSimulationPlayback, easeInOutBeat, simulationShot,
// SimulationFocusTracker) — the build cinematic of "Simulate Map". Engine-free.
using System;
using System.Collections.Generic;
using System.Linq;
using Fluitown.Domain;

namespace TerrainStudio.Core;

/// <summary>A cell that just became real (drawn as a short-lived ring).</summary>
public readonly record struct SimulationPop(int Tx, int Ty, double Age, string Layer, bool Placement);

/// <summary>One frame of the cinematic.</summary>
public readonly record struct SimulationPlaybackFrame(
    bool Dirty, bool Finished, MapSimulationStage? Stage, int StageIndex, double StageProgress, double Progress,
    int Cells, int Placements, PlacedMapWonder? Wonder);

/// <summary>
/// Playback for Simulate Map: turns a composed <see cref="SimulatedMap"/> into a world that builds itself on screen,
/// layer by layer. It keeps one live <see cref="DungeonLayout"/> — the finished world's own arrays — walks it back to a
/// blank sheet, then lets each layer's cells arrive on the beat in sweep order. The renderer re-bakes a tile only when
/// the content under it changes, so the world materialises along the front. Pacing is frame-rate independent.
/// </summary>
public sealed partial class MapSimulationPlayback
{
    private const int MAX_CELLS_PER_FRAME = 7000;
    private const double COMMIT_HEADROOM = 1.35;
    private const int MAX_PLACEMENTS_PER_FRAME = 220;
    private const double FRONT_BAND = 0.16;
    private const double MAX_STEP_SECONDS = 0.5;
    private const int POP_CAPACITY = 512;
    private const double POP_LIFETIME = 0.55;
    private const int POP_STRIDE_TARGET = 26;

    private sealed class StagePlan
    {
        public required uint[] Order;
        public required float[] Time;
        public int Cursor;
        public int PlacementCursor;
    }

    public readonly DungeonLayout Layout;
    private readonly SimulatedMap _sim;
    private readonly int _width, _height;
    private readonly StagePlan?[] _plans;
    private readonly List<TerrainDecorationPlacement> _decorations;
    private readonly List<int> _played = new();
    private readonly double _totalSeconds;

    private int _stageIndex;
    private double _stageElapsed, _speed, _frontProgress, _elapsedBefore, _clock;
    private bool _done;
    private int _frontFrom, _frontTo, _committedCells, _committedPlacements;

    private readonly int[] _popTx = new int[POP_CAPACITY], _popTy = new int[POP_CAPACITY];
    private readonly double[] _popBorn = new double[POP_CAPACITY];
    private readonly string[] _popLayer = new string[POP_CAPACITY];
    private readonly bool[] _popPlacement = new bool[POP_CAPACITY];
    private int _popHead, _popCount;

    public MapSimulationPlayback(SimulatedMap sim, DungeonLayout layout, double speed = 1)
    {
        _sim = sim;
        Layout = layout;
        _width = layout.width;
        _height = layout.height;
        _speed = Math.Max(0.15, speed);
        _plans = new StagePlan?[sim.stages.Count];
        for (int i = 0; i < sim.stages.Count; i++)
            if (sim.stages[i].beatSeconds > 0) _played.Add(i);
        if (_played.Count == 0 && sim.stages.Count > 0) _played.Add(sim.stages.Count - 1);
        double total = _played.Sum(i => Math.Max(0, sim.stages[i].beatSeconds));
        _totalSeconds = total > 0 ? total : 1e-3;
        _stageIndex = _played.Count > 0 ? _played[0] : 0;

        // Walk the finished world back to the blank sheet; later commits are in-place writes.
        Array.Copy(sim.baseTiles, layout.tiles, Math.Min(sim.baseTiles.Length, layout.tiles.Length));
        if (layout.elevation != null) Array.Copy(sim.baseElevation, layout.elevation, Math.Min(sim.baseElevation.Length, layout.elevation.Length));
        var terrain = layout.terrain ??= new DungeonTerrainLayers { schemaVersion = DungeonTypes.TERRAIN_ARTIFACT_SCHEMA_VERSION };
        if (terrain.themeIndex != null) Array.Fill(terrain.themeIndex, (byte)DungeonTypes.TERRAIN_THEME_INHERIT);
        _decorations = new List<TerrainDecorationPlacement>();
        terrain.decorations = _decorations;
    }

    /// <summary>Advance one frame by real elapsed time.</summary>
    public SimulationPlaybackFrame Advance(double dtSec, double frameSec)
    {
        double step = Math.Min(MAX_STEP_SECONDS, Math.Max(0, dtSec)) * _speed;
        _clock += step;
        if (_done) return Frame(false);
        bool dirty = false;
        double remaining = step;
        int ceiling = FrameCeiling(frameSec);
        int guard = _sim.stages.Count + 2;
        while (remaining > 0 && !_done && guard-- > 0)
        {
            if (_stageIndex >= _sim.stages.Count) { _done = true; break; }
            var stage = _sim.stages[_stageIndex];
            var plan = PlanFor(_stageIndex, stage);
            double beat = Math.Max(0.12, stage.beatSeconds);
            double spent = Math.Min(remaining, Math.Max(0, beat - _stageElapsed));
            _stageElapsed += spent;
            remaining -= spent;
            double linear = Math.Min(1, _stageElapsed / beat);
            double target = EaseInOutBeat(linear);
            _frontProgress = target;
            double share = beat > 0 ? Math.Min(1, (spent + 1e-6) / beat) : 1;
            int cellBudget = (int)Math.Min(ceiling, Math.Ceiling(plan.Order.Length * share * COMMIT_HEADROOM) + 48);
            int placementBudget = (int)Math.Min(MAX_PLACEMENTS_PER_FRAME, Math.Ceiling(PlacementCount(stage) * share * COMMIT_HEADROOM) + 4);
            var committed = CommitStage(stage, plan, target, cellBudget, placementBudget);
            dirty |= committed.cells > 0 || committed.placements > 0;
            bool exhausted = plan.Cursor >= plan.Order.Length && plan.PlacementCursor >= PlacementCount(stage);
            if (linear < 1) break;
            if (!exhausted)
            {
                CommitStage(stage, plan, 1, int.MaxValue, int.MaxValue);
                dirty = true;
            }
            _elapsedBefore += beat;
            _stageElapsed = 0;
            _frontProgress = 0;
            int at = _played.IndexOf(_stageIndex);
            if (at < 0 || at + 1 >= _played.Count) _done = true;
            else _stageIndex = _played[at + 1];
        }
        if (_done) FinishAll();
        return Frame(dirty);
    }

    /// <summary>Commit the rest of the world immediately (Skip, and the end of the cinematic).</summary>
    public void FinishAll()
    {
        for (int index = 0; index < _sim.stages.Count; index++)
        {
            var stage = _sim.stages[index];
            CommitStage(stage, PlanFor(index, stage), 1, int.MaxValue, int.MaxValue, silent: true);
        }
        _stageIndex = _sim.stages.Count;
        _done = true;
        _frontFrom = _frontTo = 0;
        _popCount = 0;
    }

    /// <summary>The cells on the advancing front (offset −1..1: built … still ahead).</summary>
    public void ForEachFrontCell(Action<int, int, double> visit)
    {
        if (_done || _stageIndex >= _sim.stages.Count) return;
        var plan = _plans[_stageIndex];
        if (plan == null) return;
        for (int i = _frontFrom; i < _frontTo; i++)
        {
            uint index = plan.Order[i];
            visit((int)(index % _width), (int)(index / _width), (plan.Time[i] - _frontProgress) / FRONT_BAND);
        }
    }

    /// <summary>Everything that became real in the last <see cref="POP_LIFETIME"/> seconds, newest first.</summary>
    public void ForEachPop(Action<SimulationPop> visit)
    {
        for (int i = 0; i < _popCount; i++)
        {
            int slot = ((_popHead - 1 - i) % POP_CAPACITY + POP_CAPACITY) % POP_CAPACITY;
            double age = (_clock - _popBorn[slot]) / POP_LIFETIME;
            if (age >= 1) break;
            visit(new SimulationPop(_popTx[slot], _popTy[slot], Math.Max(0, age), _popLayer[slot], _popPlacement[slot]));
        }
    }

    private SimulationPlaybackFrame Frame(bool dirty)
    {
        var stage = _done || _stageIndex >= _sim.stages.Count ? null : _sim.stages[_stageIndex];
        double elapsed = _done ? _totalSeconds : _elapsedBefore + _stageElapsed;
        return new SimulationPlaybackFrame(dirty, _done, stage, Math.Min(_stageIndex, _sim.stages.Count - 1),
            _done ? 1 : _frontProgress, Math.Min(1, elapsed / _totalSeconds), _committedCells, _committedPlacements, ActiveWonder(stage));
    }

    private PlacedMapWonder? ActiveWonder(MapSimulationStage? stage)
    {
        var wonders = stage?.wonders;
        if (wonders == null || wonders.Count == 0) return null;
        int at = Math.Min(wonders.Count - 1, (int)Math.Floor(_frontProgress * wonders.Count));
        return wonders[at];
    }

    private static int PlacementCount(MapSimulationStage stage) => stage.decorations?.Count ?? 0;

    private void Pop(int tx, int ty, string layer, bool placement)
    {
        _popTx[_popHead] = tx;
        _popTy[_popHead] = ty;
        _popBorn[_popHead] = _clock;
        _popLayer[_popHead] = layer;
        _popPlacement[_popHead] = placement;
        _popHead = (_popHead + 1) % POP_CAPACITY;
        if (_popCount < POP_CAPACITY) _popCount++;
    }

    private (int cells, int placements) CommitStage(MapSimulationStage stage, StagePlan plan, double target, int cellBudget, int placementBudget, bool silent = false)
    {
        var layout = Layout;
        var elevation = layout.elevation;
        var themeIndex = layout.terrain?.themeIndex;
        int cells = 0;
        int stride = Math.Max(1, (int)Math.Round(Math.Min(plan.Order.Length, (double)cellBudget) / POP_STRIDE_TARGET));
        while (plan.Cursor < plan.Order.Length && plan.Time[plan.Cursor] <= target && cells < cellBudget)
        {
            int index = (int)plan.Order[plan.Cursor];
            if (stage.tiles != null) layout.tiles[index] = stage.tiles[index];
            if (stage.elevation != null && elevation != null) elevation[index] = stage.elevation[index];
            if (stage.themeIndex != null && themeIndex != null) themeIndex[index] = stage.themeIndex[index];
            if (!silent && cells % stride == 0) Pop(index % _width, index / _width, stage.layer, false);
            plan.Cursor++;
            cells++;
        }
        _committedCells += cells;
        _frontFrom = LowerBound(plan.Time, target - FRONT_BAND);
        _frontTo = LowerBound(plan.Time, target + FRONT_BAND);

        int placements = 0;
        int total = PlacementCount(stage);
        while (plan.PlacementCursor < total && placements < placementBudget)
        {
            double share = total <= 1 ? 1 : plan.PlacementCursor / (double)(total - 1);
            if (share > target) break;
            int cursor = plan.PlacementCursor;
            (int tx, int ty)? record = null;
            if (stage.decorations != null && cursor < stage.decorations.Count)
            {
                var d = stage.decorations[cursor];
                _decorations.Add(d);
                record = (d.tx, d.ty);
            }
            if (!silent && record is { } r && placements % 3 == 0) Pop(r.tx, r.ty, stage.layer, true);
            plan.PlacementCursor++;
            placements++;
        }
        _committedPlacements += placements;
        return (cells, placements);
    }

    private StagePlan PlanFor(int index, MapSimulationStage stage)
    {
        if (_plans[index] is { } cached) return cached;
        int count = stage.cells.Length;
        var keys = new float[count];
        var sweep = stage.sweep;
        double span = Math.Max(1, Math.Sqrt((double)_width * _width + (double)_height * _height));
        for (int i = 0; i < count; i++)
        {
            uint cell = stage.cells[i];
            int tx = (int)(cell % _width), ty = (int)(cell / _width);
            double key = sweep.kind switch
            {
                MapRevealSweep.Radial => Math.Sqrt((tx - sweep.ax) * (tx - sweep.ax) + (ty - sweep.ay) * (ty - sweep.ay)) / span,
                MapRevealSweep.Linear or MapRevealSweep.Flow => ((tx - sweep.ax) * sweep.dx + (ty - sweep.ay) * sweep.dy) / span,
                _ => Hash01(tx, ty),
            };
            keys[i] = (float)(key + (Hash01(tx * 7 + 13, ty * 11 + 5) - 0.5) * sweep.jitter * 0.22);
        }
        var sorted = Enumerable.Range(0, count).ToArray();
        Array.Sort(sorted, (a, b) =>
        {
            int c = keys[a].CompareTo(keys[b]);
            return c != 0 ? c : stage.cells[a].CompareTo(stage.cells[b]);
        });
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        foreach (float v in keys) { if (v < min) min = v; if (v > max) max = v; }
        float range = max - min;
        if (range == 0 || float.IsNaN(range)) range = 1;
        var order = new uint[count];
        var time = new float[count];
        for (int i = 0; i < count; i++)
        {
            int source = sorted[i];
            order[i] = stage.cells[source];
            time[i] = (keys[source] - min) / range;
        }
        var plan = new StagePlan { Order = order, Time = time };
        _plans[index] = plan;
        return plan;
    }

    private static int FrameCeiling(double frameSec)
    {
        if (!(frameSec > 0)) return MAX_CELLS_PER_FRAME;
        double relief = Math.Min(1, 1.0 / 30 / frameSec);
        return Math.Max(600, (int)Math.Round(MAX_CELLS_PER_FRAME * (0.45 + 0.55 * relief)));
    }

    public static double EaseInOutBeat(double t)
    {
        double c = Math.Clamp(t, 0, 1);
        double eased = c < 0.5 ? 2 * c * c : 1 - Math.Pow(-2 * c + 2, 2.4) / 2;
        return Math.Clamp(eased, 0, 1);
    }

    private static int LowerBound(float[] values, double value)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (values[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static double Hash01(int x, int y)
    {
        int h = unchecked((x * 0x27d4eb2f) ^ (y * 0x165667b1));
        h = unchecked((h ^ (int)((uint)h >> 15)) * 0x2c1b3c6d);
        h = unchecked((h ^ (int)((uint)h >> 13)) * 0x297a2d39);
        h ^= (int)((uint)h >> 16);
        return (uint)h / 4294967296.0;
    }

    public static double Smoothstep(double t)
    {
        double c = Math.Clamp(t, 0, 1);
        return c * c * (3 - 2 * c);
    }
}

/// <summary>Eases the camera's lean toward whatever the current beat is raising (never a cut).</summary>
public sealed class SimulationFocusTracker
{
    private string _key = "";
    public (double x, double y)? Point { get; private set; }
    public double Weight { get; private set; }

    public void Reset()
    {
        Point = null;
        Weight = 0;
        _key = "";
    }

    public void Track(PlacedMapWonder? wonder, double dtSec, Func<PlacedMapWonder, (double x, double y)> locate)
    {
        if (wonder != null && wonder.key != _key)
        {
            _key = wonder.key;
            Point = locate(wonder);
        }
        if (wonder == null) _key = "";
        double wanted = wonder != null ? 1 : 0;
        double rate = wanted > Weight ? 1.1 : 0.7;
        double step = Math.Min(1, Math.Max(0, dtSec) * rate);
        Weight += (wanted - Weight) * step;
        if (Weight < 0.01) Point = null;
    }
}
