using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fluitown.Domain;
using Godot;
using TerrainStudio.Painting;
using TerrainStudio.Ui;

namespace TerrainStudio;

/// <summary>
/// Scripted checks of the studio (<c>--selftest paint|look|world|generate|suite|ui out-folder</c>): drives the modes
/// through their public controls, waits for the terrain to settle after each step and writes a screenshot per step
/// plus a report. Nothing here is reachable from the UI.
/// </summary>
public sealed partial class SelfTest : Node
{
    public required StudioMain Main;
    public required string Scenario;
    public required string Output;

    private readonly List<(string name, Action run)> _steps = new();
    private readonly List<string> _report = new();
    private int _index = -1, _settle, _wait;
    private double _stepStarted;

    public override void _Ready()
    {
        Directory.CreateDirectory(Output);
        // Keep the Godot editor from importing the screenshots when the output lies inside the project.
        char[] separators = { '/', '\\' };
        string project = Path.GetFullPath(ProjectSettings.GlobalizePath("res://")).TrimEnd(separators);
        string output = Path.GetFullPath(Output);
        string? top = output.StartsWith(project, StringComparison.OrdinalIgnoreCase)
            ? output[project.Length..].TrimStart(separators).Split(separators)[0] : null;
        if (!string.IsNullOrEmpty(top)) File.WriteAllText(Path.Combine(project, top, ".gdignore"), "");
        switch (Scenario)
        {
            case "paint": BuildPaint(); break;
            case "look": BuildLook(); break;
            case "world": BuildWorld(); break;
            case "generate": BuildGenerate(); break;
            case "suite": BuildSuite(); break;
            case "ui": BuildUi(); break;
            default: throw new ArgumentException($"unknown self-test '{Scenario}'");
        }
    }

    private PaintController Paint => (PaintController)Main.ControllerOf(StudioMode.Paint)!;
    private WorldController World => (WorldController)Main.ControllerOf(StudioMode.World)!;
    private Generate.GenerateController Generator => (Generate.GenerateController)Main.ControllerOf(StudioMode.Generate)!;

    private static IEnumerable<(int, int)> Line(int x0, int y0, int x1, int y1)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        for (int i = 0; i <= steps; i++)
            yield return ((int)Math.Round(x0 + (x1 - x0) * (double)i / Math.Max(1, steps)), (int)Math.Round(y0 + (y1 - y0) * (double)i / Math.Max(1, steps)));
    }

    private static IEnumerable<(int, int)> Arc(int cx, int cy, double r, double from, double to, int samples)
    {
        for (int i = 0; i <= samples; i++)
        {
            double a = from + (to - from) * i / samples;
            yield return ((int)Math.Round(cx + Math.Cos(a) * r), (int)Math.Round(cy + Math.Sin(a) * r));
        }
    }

    private void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
        _report.Add($"   check: {what}");
    }

    private void CloseModals()
    {
        foreach (var child in Main.Ui.GetChildren())
            if (child is Modal modal) modal.Close();
    }

    private void BuildPaint()
    {
        _steps.Add(("blank", () => { Main.SetMode(StudioMode.Paint); Paint.NewMap(64, 48); }));
        _steps.Add(("ridge", () =>
        {
            Paint.ChooseMaterial(TileType.Solid); Paint.SetHeight(7); Paint.SetBrush(size: 7, shape: TerrainBrushShape.Circle);
            Paint.ScriptStroke(Arc(20, 30, 14, Math.PI * 1.05, Math.PI * 1.95, 24));
        }));
        _steps.Add(("plateau", () =>
        {
            Paint.ChooseMaterial(TileType.Floor); Paint.SetHeight(3); Paint.SetBrush(size: 11);
            Paint.ScriptStroke(Line(40, 12, 54, 16));
        }));
        _steps.Add(("lake", () =>
        {
            Paint.ChooseMaterial(TileType.Water); Paint.SetHeight(0); Paint.SetBrush(size: 9);
            Paint.ScriptStroke(Line(28, 32, 38, 36));
        }));
        _steps.Add(("forest", () =>
        {
            Paint.ChooseNature(TerrainDecorationKind.Tree); Paint.SetBrush(size: 9);
            Paint.ScriptStroke(Line(8, 38, 20, 42));
        }));
        _steps.Add(("bridge", () =>
        {
            Paint.ChooseMaterial(TileType.Bridge); Paint.SetHeight(0); Paint.SetBrush(size: 2);
            Paint.ScriptStroke(Line(33, 29, 33, 39));
        }));
        _steps.Add(("theme", () =>
        {
            Paint.SetRail(PaintRail.Theme); Paint.SetTheme("rainbowland"); Paint.SetBrush(size: 13);
            Paint.ScriptStroke(Line(44, 12, 52, 16));
        }));
        _steps.Add(("sculpt", () =>
        {
            int before = Paint.StoredHeightAt(10, 12);
            Paint.SetRail(PaintRail.Sculpt); Paint.SetTool(PaintTool.Raise); Paint.SetSculptAmount(3); Paint.SetBrush(size: 9);
            Paint.ScriptStroke(Line(6, 12, 14, 12).Concat(Line(14, 12, 6, 12)));
            Check(Paint.StoredHeightAt(10, 12) == Math.Min(25, before + 3), $"raise lifts once per stroke ({before} → {Paint.StoredHeightAt(10, 12)})");
            Paint.SetTool(PaintTool.Lower); Paint.SetSculptAmount(1);
            Paint.ScriptStroke(Line(6, 12, 14, 12));
            Check(Paint.StoredHeightAt(10, 12) == Math.Min(25, before + 3) - 1, "lower sinks by the amount");
        }));
        _steps.Add(("undo-redo", () => { Paint.Undo(); Paint.Redo(); }));
        _steps.Add(("chasm", () =>
        {
            Paint.ChooseMaterial(TileType.Chasm); Paint.SetHeight(-10); Paint.SetBrush(size: 5);
            Paint.ScriptStroke(Line(4, 22, 16, 26));
        }));
    }

    private void BuildLook()
    {
        _steps.Add(("original", () => { }));
        foreach (var preset in Core.LookSettings.Presets.Skip(1))
        {
            var p = preset;
            _steps.Add((p.Key, () => { Main.Look.ApplyPreset(p); Main.SyncLookPanel(); }));
        }
        _steps.Add(("random", () => { Main.Look.Randomize(new Random(7), 0.8); Main.SyncLookPanel(); }));
        _steps.Add(("autumn", () => { Main.Look.Reset(); Main.Look.SetSeason(2); Main.SyncLookPanel(); }));
        _steps.Add(("winter", () => { Main.Look.SetSeason(3); Main.SyncLookPanel(); }));
        _steps.Add(("rain", () => { Main.Look.SetSeason(1); Main.Look.SetWeather(Core.LookSettings.WeatherRain); Main.SyncLookPanel(); }));
        _steps.Add(("dusk", () => { Main.Look.Reset(); Main.SyncLookPanel(); Main.View.DayPhase = 0.76; }));
        _steps.Add(("night", () => Main.View.DayPhase = 0.95));
    }

    private void BuildWorld()
    {
        _steps.Add(("classic", () => { Main.SetMode(StudioMode.World); World.Open(ExplorePanel.ClassicWorld, "world", 0, 0); }));
        foreach (var theme in new[] { "highland_pass", "abyssal_deepsea", "rainbowland", "noir_sprawl", "alien_ranch" })
        {
            string t = theme;
            _steps.Add((t, () => World.Open(t, "selftest", 0, 0)));
        }
        _steps.Add(("random-world", () => World.RandomWorld()));
        _steps.Add(("far", () => World.Open(ExplorePanel.ClassicWorld, "amber-vale-7", 42000, -18000)));
    }

    private void BuildGenerate()
    {
        _steps.Add(("generate", () => Main.SetMode(StudioMode.Generate)));
        BuildLiveDials();
    }

    /// <summary>
    /// The Fine-tune dials act on the map on screen at once: each one at zero leaves none of its feature, and back at
    /// their defaults the dials give exactly the map they started from.
    /// </summary>
    private void BuildLiveDials()
    {
        uint original = 0;
        double rock = 0;
        int dressing = 0;
        _steps.Add(("dials-default", () =>
        {
            original = ShownFingerprint();
            rock = ShownShare(t => t == TileType.Solid || t == TileType.Cleft);
            dressing = Main.View.Layout!.terrain?.decorations?.Count ?? 0;
            Check(rock > 0, $"the map has rock ({rock:P0})");
        }));
        _steps.Add(("mountains-0", () => Generator.SetDial("reliefBias", 0)));
        _steps.Add(("mountains-0-check", () =>
        {
            var layout = Main.View.Layout!;
            var levels = new HashSet<int>();
            for (int i = 0; i < layout.tiles.Length; i++)
                if (DungeonTypes.isWalkable(layout.tiles[i])) levels.Add(layout.elevation![i]);
            Check(ShownShare(t => t == TileType.Solid || t == TileType.Cleft) == 0, "Mountains 0%: no rock left");
            Check(levels.Count == 1, $"Mountains 0%: one flat ground level ({levels.Count})");
        }));
        _steps.Add(("mountains-200", () => Generator.SetDial("reliefBias", 2)));
        _steps.Add(("mountains-200-check", () =>
        {
            double more = ShownShare(t => t == TileType.Solid || t == TileType.Cleft);
            Check(more > rock, $"Mountains 200%: more rock ({rock:P0} → {more:P0})");
        }));
        _steps.Add(("water-0", () => { Generator.SetDial("reliefBias", 1); Generator.SetDial("waterBias", 0); }));
        _steps.Add(("water-0-check", () => Check(ShownShare(t => t == TileType.Water) == 0, "Water 0%: no water")));
        _steps.Add(("chasms-0", () => Generator.SetDial("riftBias", 0)));
        _steps.Add(("chasms-0-check", () => Check(ShownShare(t => t == TileType.Chasm) == 0, "Chasms 0%: no chasm")));
        _steps.Add(("forests-0", () => Generator.SetDial("floraBias", 0)));
        _steps.Add(("forests-0-check", () =>
        {
            int left = Main.View.Layout!.terrain?.decorations?.Count ?? 0;
            Check(left < dressing, $"Forests 0%: the forests are gone ({dressing} → {left} plants, the rest are wonders' and landmarks')");
        }));
        _steps.Add(("dials-back", () =>
        {
            foreach (var dial in Generate.GenerateDials.All) Generator.SetDial(dial.Key, dial.Default);
        }));
        _steps.Add(("dials-back-check", () => Check(ShownFingerprint() == original, "back at the defaults the dials give the same map again")));
    }

    /// <summary>Share of the shown map's cells whose tile passes the test.</summary>
    private double ShownShare(Func<int, bool> test)
    {
        var tiles = Main.View.Layout!.tiles;
        return (double)tiles.Count(t => test(t)) / tiles.Length;
    }

    /// <summary>Hash of the shown map's tiles and heights.</summary>
    private uint ShownFingerprint()
    {
        var layout = Main.View.Layout!;
        uint hash = 2166136261;
        for (int i = 0; i < layout.tiles.Length; i++)
        {
            hash = (hash ^ layout.tiles[i]) * 16777619;
            hash = (hash ^ (byte)layout.elevation![i]) * 16777619;
        }
        return hash;
    }

    private void BuildSuite()
    {
        string file = Path.Combine(Output, "roundtrip.terrain.json");
        uint before = 0;
        _steps.Add(("generate", () => Main.SetMode(StudioMode.Generate)));
        _steps.Add(("to-painter", () =>
        {
            Generator.EditInPainter();
            Check(Main.Mode == StudioMode.Paint, "generated map opened in the painter");
        }));
        _steps.Add(("paint-on-map", () =>
        {
            var (w, h) = Paint.MapSize;
            Paint.ChooseMaterial(TileType.Water); Paint.SetHeight(2); Paint.SetBrush(size: 7);
            Paint.ScriptStroke(Line(w / 4, h / 2, w * 3 / 4, h / 2));
        }));
        _steps.Add(("save-load", () =>
        {
            before = Paint.Fingerprint();
            Paint.SaveToPath(file);
            Check(File.Exists(file), "map file written");
            Paint.NewMap(32, 32);
            string? error = Paint.LoadFromPath(file);
            Check(error == null, "map file read back" + (error != null ? $" ({error})" : ""));
            Check(Paint.Fingerprint() == before, "round trip keeps tiles, heights, themes and dressing");
        }));
        _steps.Add(("form-blocky", () =>
        {
            Main.Look["form.organic"] = 0;
            if (Main.View.ApplyForm(Main.Look)) Main.View.Rebuild();
        }));
        _steps.Add(("form-organic", () =>
        {
            Main.Look["form.organic"] = 1;
            if (Main.View.ApplyForm(Main.Look)) Main.View.Rebuild();
        }));
        _steps.Add(("orbit", () => Main.ToggleOrbit()));
        _steps.Add(("orbit-pick", () =>
        {
            var centre = GetViewport().GetVisibleRect().Size / 2;
            Check(Main.View.TryPickGround(centre, out double x, out double y), $"3D view picks the ground ({x:0}, {y:0})");
        }));
        _steps.Add(("orbit-off", () => Main.ToggleOrbit()));
        _steps.Add(("world", () => { Main.SetMode(StudioMode.World); World.Open(ExplorePanel.ClassicWorld, "suite-seed", 0, 0); }));
        _steps.Add(("take-area", () =>
        {
            World.TakeArea(3);
            Check(Main.Mode == StudioMode.Paint && Paint.MapSize == (96, 96), "3×3 chunk area opened in the painter");
        }));
    }

    /// <summary>Screenshots of every panel and card, for reviewing the interface.</summary>
    private void BuildUi()
    {
        _steps.Add(("paint-terrain", () => { Main.SetMode(StudioMode.Paint); Paint.SetRail(PaintRail.Terrain); }));
        _steps.Add(("paint-sculpt", () => Paint.SetRail(PaintRail.Sculpt)));
        _steps.Add(("paint-nature", () => Paint.SetRail(PaintRail.Nature)));
        _steps.Add(("paint-build", () => { Paint.SetRail(PaintRail.Build); Paint.ChooseBuild(TerrainDepthStructureKind.Underpass); }));
        _steps.Add(("paint-theme", () => Paint.SetRail(PaintRail.Theme)));
        _steps.Add(("paint-select", () => { Paint.SetRail(PaintRail.Select); Paint.SelectAll(); }));
        _steps.Add(("checks", () => { Paint.Deselect(); Paint.SetRail(PaintRail.Terrain); Main.Ui.ChecksChip.EmitSignal(BaseButton.SignalName.Pressed); }));
        _steps.Add(("style", () =>
        {
            foreach (var popup in GetTree().Root.GetChildren().OfType<PopupPanel>()) popup.Hide();
            Main.Ui.StyleOpen = true;
        }));
        _steps.Add(("new-map", () => { Main.Ui.StyleOpen = false; Paint.ShowNewMap(); }));
        _steps.Add(("generate", () => { CloseModals(); Main.SetMode(StudioMode.Generate); }));
        _steps.Add(("explore", () => Main.SetMode(StudioMode.World)));
        _steps.Add(("explore-previews", () => { }));
        _steps.Add(("welcome", () => StudioModals.Welcome(Main.Ui, true, _ => { }, _ => { })));
        _steps.Add(("help", () => { CloseModals(); StudioModals.Help(Main.Ui); }));
        _steps.Add(("settings", () => { CloseModals(); Main.Ui.SettingsButton.EmitSignal(BaseButton.SignalName.Pressed); }));
        _steps.Add(("clean-view", () => { CloseModals(); Main.Ui.PanelsVisible = false; }));
    }

    public override void _Process(double delta)
    {
        bool busy = Main.ControllerOf(Main.Mode)?.Busy == true || !Main.View.ViewReady;
        if (_index >= 0)
        {
            if (busy)
            {
                _settle = 0;
                if (_wait % 120 == 0) GD.Print($"selftest: waiting ({_steps[_index].name}) tiles {Main.View.InstalledTiles} active {Main.View.ActiveTiles} stage {Main.View.Stage} memory {System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1 << 20)} MB");
                if (++_wait > 4000) Finish("timeout waiting for the terrain");
                return;
            }
            if (++_settle < (Scenario == "ui" ? 40 : 20)) return;
            string name = _steps[_index].name;
            string path = Path.Combine(Output, $"{_index:00}_{name}.png");
            GetViewport().GetTexture().GetImage().SavePng(path);
            double seconds = (Godot.Time.GetTicksMsec() - _stepStarted) / 1000.0;
            _report.Add($"{_index:00} {name,-16} ok  {seconds,6:0.00} s  tiles {Main.View.InstalledTiles,3}  installs {Main.View.TotalInstalls,4}  memory {System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1 << 20)} MB  {MemoryProbe()}");
            GD.Print($"selftest: {name} settled after {seconds:0.00} s");
        }
        _index++;
        if (_index >= _steps.Count) { Finish(null); return; }
        _settle = 0;
        _wait = 0;
        _stepStarted = Godot.Time.GetTicksMsec();
        try { _steps[_index].run(); }
        catch (Exception e)
        {
            _report.Add($"{_index:00} {_steps[_index].name,-16} FAILED {e.GetType().Name}: {e.Message}");
            GD.PushError($"selftest step {_steps[_index].name} failed: {e}");
        }
    }

    /// <summary>Managed heap after a full collection, Godot objects/resources/nodes and video memory.</summary>
    private static string MemoryProbe()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long managed = GC.GetTotalMemory(true) >> 20;
        double objects = Performance.GetMonitor(Performance.Monitor.ObjectCount);
        double resources = Performance.GetMonitor(Performance.Monitor.ObjectResourceCount);
        double nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        double video = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1 << 20);
        return $"managed {managed} MB  objects {objects:0}  resources {resources:0}  nodes {nodes:0}  video {video:0} MB";
    }

    private void Finish(string? error)
    {
        if (error != null) _report.Add("ERROR " + error);
        File.WriteAllLines(Path.Combine(Output, "report.txt"), _report);
        GD.Print(string.Join("\n", _report));
        GetTree().Quit(error == null && !_report.Any(r => r.Contains("FAILED")) ? 0 : 1);
        SetProcess(false);
    }
}
