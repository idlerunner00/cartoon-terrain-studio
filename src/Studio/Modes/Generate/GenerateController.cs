using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Fluitown.Domain;
using Godot;
using TerrainStudio.Core;
using TerrainStudio.Terrain;
using TerrainStudio.Ui;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio.Generate;

/// <summary>
/// The Generate tab: the map composer (bedrock → relief → hydrology → rifts → crossings → wonders → roads → theming →
/// flora → landmarks) runs on a worker; the finished map then builds itself on screen layer by layer in
/// sweep order with a single-take camera move, and can be handed to the painter. Changing a setting composes the
/// same seed again at once and swaps the map in place, without the animation and without moving the camera; while a
/// dial is dragged the map follows it a few times a second and always lands on the dial's latest value.
/// </summary>
public sealed partial class GenerateController : IModeController
{
    private readonly StudioMain _main;
    private readonly GenerateRecipe _recipe;
    private readonly GeneratePanel _panel;
    private readonly List<string> _recent = new();

    private MapSimulationBuild? _build;
    private Task<BuildOutcome?>? _buildTask;
    /// <summary>Whether the running build is a live rebuild (a setting changed) rather than a new map.</summary>
    private bool _buildLive;
    private CancellationTokenSource? _cancel;
    /// <summary>Builds asked for while another one was still running; each new request replaces the last one.</summary>
    private bool _fullWanted, _liveWanted;
    /// <summary>Frames a finished build waits before the view's readiness counts (the re-bake starts a frame later).</summary>
    private int _settleFrames;
    private SimulatedMap? _map;
    /// <summary>The compiled form (auto-cliffs) of the map on screen, installed when its build animation ends.</summary>
    private DungeonLayout? _compiled;
    private DungeonLayout? _layout;
    private MapSimulationPlayback? _playback;
    private readonly SimulationFocusTracker _focus = new();
    private double _cinematicClock, _invalidateCooldown;
    private bool _invalidatePending;
    private double _fitZoom, _heartX, _heartY, _centreX, _centreY;
    private double? _cameraX, _cameraY, _cameraZoom;
    private bool _finishedPending;

    public GenerateController(StudioMain main)
    {
        _main = main;
        _recipe = GenerateRecipe.FromJson(main.Settings.Generator);
        if (main.Settings.Generator?["recent"] is JsonArray recent)
            foreach (var s in recent) if ((string?)s is { } seed) _recent.Add(seed);
        _panel = new GeneratePanel(_recipe);
        _panel.GenerateNew += () => { _recipe.Seed = Seeds.Random(); _panel.SyncFromRecipe(); Generate(); };
        _panel.Regenerate += Generate;
        _panel.SurpriseRequested += Surprise;
        _panel.EditRequested += EditInPainter;
        _panel.SaveRequested += SaveMap;
        _panel.RecentRequested += seed => { _recipe.Seed = seed; _panel.SyncFromRecipe(); Generate(); };
        _panel.RecipeChanged += RebuildLive;
        _panel.SetRecent(_recent);
        _main.Ui.Cinematic.SkipRequested += Skip;
    }

    public StudioMode Mode => StudioMode.Generate;
    public Control Panel => _panel;
    public Control? Rail => null;
    public bool Busy => _buildTask != null || _playback != null || _finishedPending || !_main.View.ViewReady;
    public bool OwnsKeyboardMotion => _playback != null;

    public string Hint => _playback != null
        ? T("Space skips the animation")
        : T("Enter: new map · Right-drag: move · Wheel: zoom");

    public string Cursor => "";

    // ── activation ──────────────────────────────────────────────────────────────────────────────────────────

    public void Activate(string? startFile)
    {
        if (_layout != null && (_main.View.Source != TerrainSource.Authored || _main.View.Layout != _layout))
            _main.View.OpenAuthored(_layout, _cameraX, _cameraY, _cameraZoom);
        else if (_layout == null && _buildTask == null) Generate();
    }

    public void Deactivate()
    {
        if (_playback != null) Skip();
        if (_main.View.Source == TerrainSource.Authored && _main.View.Layout == _layout)
        {
            _cameraX = _main.View.FocusX; _cameraY = _main.View.FocusY; _cameraZoom = _main.View.Zoom;
        }
        _main.Ui.Cinematic.Visible = false;
    }

    // ── compose ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Wonder kinds the composer leaves out (scripted runs); null composes from the whole catalog.</summary>
    public IReadOnlyCollection<string>? ExcludedWonders { get; set; }

    private MapSimulationOptions Options(string seed)
    {
        var options = new MapSimulationOptions
        {
            seed = seed,
            width = _recipe.Width,
            height = _recipe.Height,
            themeKeys = GeneratorArtifacts.GENERATOR_THEME_OPTIONS.Select(o => o.key).ToList(),
            themeKey = _recipe.Theme is "" or "*blend" ? null : _recipe.Theme,
            blendThemes = _recipe.Theme == "*blend" ? true : null,
            archetypeKey = _recipe.Archetype == "" ? null : _recipe.Archetype,
            frameKind = _recipe.Frame == "" ? null : _recipe.Frame,
            motifKey = _recipe.Motif == "*none" ? "" : _recipe.Motif == "" ? null : _recipe.Motif,
            chaos = _recipe.Dials["chaos"],
            reliefBias = _recipe.Dials["reliefBias"],
            waterBias = _recipe.Dials["waterBias"],
            riftBias = _recipe.Dials["riftBias"],
            wonderBias = _recipe.Dials["wonderBias"],
            excludedWonders = ExcludedWonders,
            roadBias = _recipe.Dials["roadBias"],
            floraBias = _recipe.Dials["floraBias"],
            landmarkBias = _recipe.Dials["landmarkBias"],
        };
        return options;
    }

    /// <summary>What a build hands back: the map, its draft layout (the animation plays on it) and its compiled form.</summary>
    private sealed record BuildOutcome(SimulatedMap Map, DungeonLayout Draft, DungeonLayout Compiled);

    /// <summary>
    /// Composes the recipe's seed as a new map (with the build animation when it is switched on). A build still
    /// running is cancelled, so the map that lands is always the one of the seed in the field.
    /// </summary>
    public void Generate()
    {
        string seed = _recipe.Seed.Length > 0 ? _recipe.Seed : Seeds.Random();
        _recipe.Seed = seed;
        _panel.SyncFromRecipe();
        _fullWanted = true;
        _liveWanted = false;
        StartWanted();
        Persist(_main.Settings);
    }

    /// <summary>A setting changed: compose the map on screen again with it: same seed, no animation, same camera.</summary>
    public void RebuildLive()
    {
        if (_recipe.Seed.Length == 0) { Generate(); return; }
        _liveWanted = true;
        _fullWanted = false;
        StartWanted();
        Persist(_main.Settings);
    }

    /// <summary>Sets one dial the way its slider does (scripted runs): the panel follows and the map rebuilds live.</summary>
    public void SetDial(string key, double value)
    {
        if (!_recipe.Dials.ContainsKey(key)) throw new ArgumentException($"unknown dial '{key}'");
        _recipe.Dials[key] = value;
        _panel.SyncFromRecipe();
        RebuildLive();
    }

    /// <summary>
    /// Starts the build that was asked for. While a live rebuild runs, a newer setting waits for it: the map on screen
    /// then keeps following a dial that is being dragged, a few times a second. A new map, or a setting changed while
    /// a new map is composed, cancels the running build instead.
    /// </summary>
    private void StartWanted()
    {
        if (!_fullWanted && !_liveWanted) return;
        if (_buildTask != null)
        {
            if (_fullWanted || !_buildLive) _cancel?.Cancel();
            return;
        }
        if (_playback != null) Skip();
        bool live = !_fullWanted;
        _fullWanted = _liveWanted = false;
        var build = MapSimulation.beginMapSimulation(Options(_recipe.Seed));
        var cancel = new CancellationTokenSource();
        _build = build;
        _buildLive = live;
        _cancel = cancel;
        _buildTask = Task.Run(() => Compose(build, cancel.Token));
        _panel.SetBusy(true, 0, "", live);
    }

    /// <summary>Worker: runs the composer (stopping early when cancelled) and compiles the finished map.</summary>
    private static BuildOutcome? Compose(MapSimulationBuild build, CancellationToken cancel)
    {
        while (!build.step(2))
            if (cancel.IsCancellationRequested) return null;
        var map = build.result!;
        var draft = TerrainEditor.draftDungeonLayoutFromTerrainArtifact(map.artifact);
        // The composer already validated and repaired the artifact; its compiled form adds the auto-cliffs.
        var compiled = TerrainArtifactModule.compileTerrainArtifactToDungeonLayout(TerrainEditor.cloneTerrainArtifact(map.artifact),
            TerrainArtifactModule.TERRAIN_EDITOR_VALIDATION_OPTIONS).layout;
        return new BuildOutcome(map, draft, compiled is { } c && c.width == draft.width && c.height == draft.height ? c : draft);
    }

    private void Surprise()
    {
        var random = new Random();
        _recipe.Dials["chaos"] = 1;
        foreach (var dial in GenerateDials.All)
            if (dial.Key != "chaos") _recipe.Dials[dial.Key] = Math.Round((0.4 + random.NextDouble() * 1.5) * 20) / 20;
        _recipe.Archetype = "";
        _recipe.Frame = "";
        _recipe.Seed = Seeds.Random();
        _panel.SyncFromRecipe();
        Generate();
    }

    private void Landed(BuildOutcome outcome, bool live)
    {
        var map = outcome.Map;
        _map = map;
        _compiled = outcome.Compiled;
        _panel.SetResult(map);
        _recent.Remove(map.recipe.seedId);
        _recent.Insert(0, map.recipe.seedId);
        if (_recent.Count > 10) _recent.RemoveRange(10, _recent.Count - 10);
        _panel.SetRecent(_recent);

        var layout = outcome.Draft;
        var view = _main.View;
        double ts = layout.tileSize;
        _centreX = layout.originX + layout.width * ts / 2;
        _centreY = layout.originY + layout.height * ts / 2;
        _heartX = layout.originX + (map.recipe.heartTx + 0.5) * ts;
        _heartY = layout.originY + (map.recipe.heartTy + 0.5) * ts;
        if (_main.Mode != StudioMode.Generate)
        {
            // The user moved on while the composer ran: keep the finished map for when the tab is opened again.
            if (!live || _layout == null || _layout.width != layout.width || _layout.height != layout.height)
                _cameraX = _cameraY = _cameraZoom = null;
            _layout = outcome.Compiled;
            if (!live) _main.Toast(F("Your generated map is ready: {0}", map.recipe.name), StudioTheme.Mint);
            return;
        }
        if (live)
        {
            InstallLive(outcome.Compiled);
            return;
        }
        _layout = layout;
        if (_recipe.Animate && map.stages.Count > 0)
        {
            _playback = new MapSimulationPlayback(map, layout, _recipe.Speed);
            view.OpenAuthored(layout, _heartX, _heartY);
            _fitZoom = view.FitZoom(layout);
            view.Zoom = Math.Min(view.MaxZoom, _fitZoom * 2.15);
            _cinematicClock = 0;
            _focus.Reset();
            _main.Toast(F("{0} — watch it being built (Space skips)", map.recipe.name));
        }
        else
        {
            view.OpenAuthored(layout);
            FinishBuild();
        }
    }

    /// <summary>
    /// Puts a live rebuild on screen. The same map at the same size and theme swaps its arrays in place, so only the
    /// tiles that changed re-bake while the old ones stay visible; a new theme reopens the map under the same camera,
    /// a new size frames the whole map again.
    /// </summary>
    private void InstallLive(DungeonLayout next)
    {
        var view = _main.View;
        var current = _layout;
        bool shown = current != null && view.Source == TerrainSource.Authored && view.Layout == current;
        bool sameSize = shown && current!.width == next.width && current.height == next.height;
        if (sameSize && current!.biomeKey == next.biomeKey && current.tier == next.tier && current.seed == next.seed
            && current.tileSize == next.tileSize && current.originX == next.originX && current.originY == next.originY)
        {
            // The dungeon holds the layout object itself: keep its identity and swap in the new arrays.
            current.tiles = next.tiles;
            current.elevation = next.elevation;
            current.terrain = next.terrain;
            current.rooms = next.rooms;
            current.doors = next.doors;
            view.InvalidateLayout();
            view.RefreshAmbient();
        }
        else if (sameSize)
        {
            double fx = view.FocusX, fy = view.FocusY, zoom = view.Zoom;
            _layout = next;
            view.OpenAuthored(next, fx, fy, zoom);
        }
        else
        {
            _layout = next;
            view.OpenAuthored(next);
        }
        _finishedPending = true;
        _settleFrames = 2;
    }

    public void Skip()
    {
        if (_playback == null) return;
        _playback.FinishAll();
        _main.View.InvalidateLayout();
        FinishBuild();
    }

    private void FinishBuild()
    {
        _playback = null;
        _main.Ui.Cinematic.Visible = false;
        _finishedPending = true;
        _settleFrames = 2;
        // Install the compiled form (auto-cliffs) the worker made alongside the map.
        if (_compiled is { } layout && _layout != null && layout != _layout && layout.width == _layout.width && layout.height == _layout.height)
        {
            _layout.tiles = layout.tiles;
            _layout.elevation = layout.elevation;
            _layout.terrain = layout.terrain;
        }
        _main.View.InvalidateLayout();
        _main.View.RefreshAmbient();
        if (_main.View.Layout == _layout) _main.View.FitView();
    }

    // ── frame ───────────────────────────────────────────────────────────────────────────────────────────────

    public void Tick(double delta)
    {
        if (_build != null && _buildTask != null)
        {
            _panel.SetBusy(true, _build.progress, _build.label, _buildLive);
            if (_buildTask.IsCompleted)
            {
                var task = _buildTask;
                bool live = _buildLive;
                _buildTask = null;
                _build = null;
                _cancel?.Dispose();
                _cancel = null;
                _panel.SetBusy(false, 1, "");
                if (!task.IsCompletedSuccessfully)
                    _main.Toast(F("The map could not be generated: {0}", task.Exception?.GetBaseException().Message ?? ""), StudioTheme.Danger, 6);
                // A cancelled build hands back nothing: a newer request is already waiting.
                else if (task.Result is { } outcome) Landed(outcome, live);
                StartWanted();
            }
        }
        if (_playback != null) TickCinematic(delta);
        if (_settleFrames > 0) _settleFrames--;
        else if (_finishedPending && _main.View.ViewReady && _playback == null) _finishedPending = false;
    }

    private void TickCinematic(double delta)
    {
        var playback = _playback!;
        var frame = playback.Advance(delta, delta);
        _cinematicClock += delta;
        if (frame.Dirty) _invalidatePending = true;
        _invalidateCooldown -= delta;
        if (_invalidatePending && _invalidateCooldown <= 0)
        {
            _main.View.InvalidateLayout();
            _invalidatePending = false;
            _invalidateCooldown = RefreshSeconds;
        }
        // The single-take shot: from the map's heart out to the whole map, leaning toward the wonder being raised.
        var layout = _layout!;
        double t = frame.Progress;
        double pull = t < 0.62 ? MapSimulationPlayback.Smoothstep(t / 0.62) : 1;
        double zoom = _fitZoom * 2.15 + (_fitZoom - _fitZoom * 2.15) * pull;
        double framing = t < 0.78 ? MapSimulationPlayback.Smoothstep(t / 0.78) : 1;
        double fx = _heartX + (_centreX - _heartX) * framing, fy = _heartY + (_centreY - _heartY) * framing;
        _focus.Track(frame.Wonder, delta, w => (layout.originX + (w.tx + 0.5) * layout.tileSize, layout.originY + (w.ty + 0.5) * layout.tileSize));
        if (_focus.Point is { } focus && _focus.Weight > 0)
        {
            double lean = _focus.Weight * 0.34;
            fx += (focus.x - fx) * lean;
            fy += (focus.y - fy) * lean;
        }
        double drift = (1 - framing) * 90;
        fx += Math.Sin(_cinematicClock * 0.31) * drift;
        fy += Math.Cos(_cinematicClock * 0.24) * drift * 0.6;
        _main.View.FocusX = fx;
        _main.View.FocusY = fy;
        _main.View.Zoom = zoom;
        if (frame.Stage is { } stage)
        {
            int index = Array.IndexOf(MapSimulationTypes.MAP_SIMULATION_LAYERS.ToArray(), stage.layer) + 1;
            string wonder = frame.Wonder != null ? $" — {frame.Wonder.name}" : "";
            _main.Ui.Cinematic.Show(F("Step {0} of {1}", index, MapSimulationTypes.MAP_SIMULATION_LAYERS.Count),
                T(stage.title) + wonder, T(stage.detail), frame.Progress);
        }
        if (frame.Finished) FinishBuild();
    }

    /// <summary>How often the build animation re-bakes the terrain it changed (recordings use 0: every frame).</summary>
    public double RefreshSeconds { get; set; } = 0.12;

    public void AfterPresent() { }

    // ── cinematic ink (2D) ──────────────────────────────────────────────────────────────────────────────────

    public void Draw(ViewportInput canvas)
    {
        if (_playback == null || _layout == null) return;
        var view = _main.View;
        var layout = _layout;
        double ts = layout.tileSize;
        int drawn = 0;
        _playback.ForEachFrontCell((tx, ty, offset) =>
        {
            if (offset <= 0 || ++drawn > 1400) return;
            double h = SampleHeight(tx, ty);
            double x0 = layout.originX + tx * ts, y0 = layout.originY + ty * ts;
            var a = view.WorldToScreen(x0, y0, h);
            var b = view.WorldToScreen(x0 + ts, y0, h);
            var c = view.WorldToScreen(x0 + ts, y0 + ts, h);
            var d = view.WorldToScreen(x0, y0 + ts, h);
            float alpha = (float)Math.Clamp(1 - offset, 0, 1) * 0.55f;
            var ink = new Color(0.08f, 0.07f, 0.06f, alpha);
            canvas.DrawPolyline(new[] { a, b, c, d, a }, ink, 1.2f, true);
        });
        _playback.ForEachPop(pop =>
        {
            double h = SampleHeight(pop.Tx, pop.Ty);
            var centre = view.WorldToScreen(layout.originX + (pop.Tx + 0.5) * ts, layout.originY + (pop.Ty + 0.5) * ts, h);
            float radius = (float)(6 + pop.Age * 26) * (float)Math.Max(0.4, view.Zoom);
            var colour = LayerColour(pop.Layer);
            colour.A = (float)(1 - pop.Age) * 0.9f;
            canvas.DrawArc(centre, radius, 0, Mathf.Tau, 28, colour, pop.Placement ? 2.4f : 1.6f, true);
        });
    }

    private double SampleHeight(int tx, int ty)
    {
        var layout = _layout!;
        return _main.View.SurfaceHeightPx(layout.originX + (tx + 0.5) * layout.tileSize, layout.originY + (ty + 0.5) * layout.tileSize);
    }

    private static Color LayerColour(string layer) => layer switch
    {
        MapSimulationLayer.Bedrock => new Color("d9c7a3"),
        MapSimulationLayer.Relief => new Color("ffd36b"),
        MapSimulationLayer.Hydrology => new Color("5fd3ff"),
        MapSimulationLayer.Rifts => new Color("c792ff"),
        MapSimulationLayer.Crossings => new Color("ffb173"),
        MapSimulationLayer.Wonders => new Color("ff7ad9"),
        MapSimulationLayer.Roads => new Color("f5e2b8"),
        MapSimulationLayer.Theming => new Color("9df5c9"),
        MapSimulationLayer.Flora => new Color("7ee08a"),
        _ => new Color("ffffff"),
    };

    // ── hand-off ────────────────────────────────────────────────────────────────────────────────────────────

    public void EditInPainter()
    {
        if (_map == null) return;
        if (_playback != null) Skip();
        if (_main.ControllerOf(StudioMode.Paint) is IArtifactReceiver painter)
        {
            painter.ReceiveArtifact(TerrainEditor.cloneTerrainArtifact(_map.artifact), MapName(), F("{0} is ready to paint", _map.recipe.name));
            _main.SetMode(StudioMode.Paint);
        }
    }

    private string MapName()
    {
        var r = _map!.recipe;
        return System.Text.RegularExpressions.Regex.Replace($"{r.archetype.key}-{r.seedId}-{r.width}x{r.height}".ToLowerInvariant(), "[^a-z0-9._-]", "-");
    }

    public void SaveMap()
    {
        if (_map == null) return;
        Dialogs.SaveFile(_main, T("Save map"), MapName() + ".json", new[] { "*.json ; " + T("Terrain maps") }, path =>
        {
            string json = TerrainEditor.terrainEditorDocumentToJsonText(_map.artifact, MapName(),
                $"Generated: {_map.recipe.name}, seed {_map.recipe.seedId}", pretty: true);
            var node = JsonNode.Parse(json)!.AsObject();
            node["look"] = _main.Look.ToJson();
            node["recipe"] = _recipe.ToJson();
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _main.Settings.RememberFile(path);
            _main.RefreshFileMenu();
            _main.Toast(F("Saved {0}", Path.GetFileName(path)), StudioTheme.Ok);
        });
    }

    // ── input ───────────────────────────────────────────────────────────────────────────────────────────────

    public void Pointer(InputEvent e) { }
    public bool Wheel(InputEventMouseButton e) => false;

    public bool Key(InputEventKey e)
    {
        if (e.Keycode == global::Godot.Key.Space && _playback != null) { Skip(); return true; }
        if (e.Keycode is global::Godot.Key.Enter or global::Godot.Key.KpEnter && (_buildTask == null || _buildLive))
        {
            _recipe.Seed = Seeds.Random();
            _panel.SyncFromRecipe();
            Generate();
            return true;
        }
        return false;
    }

    public void Undo() { }
    public void Redo() { }

    public void Persist(StudioSettings settings)
    {
        var json = _recipe.ToJson();
        var recent = new JsonArray();
        foreach (var s in _recent) recent.Add(s);
        json["recent"] = recent;
        settings.Generator = json;
    }
}
