using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Fluitown.Domain;
using Godot;
using TerrainStudio.Core;
using TerrainStudio.Ui;
using static TerrainStudio.Ui.Tr;
using Section = TerrainStudio.Ui.Section;

namespace TerrainStudio.Generate;

/// <summary>Everything the composer is asked for: recipe, seed, size and dials (persisted between sessions).</summary>
public sealed class GenerateRecipe
{
    public string Seed = "";
    public int Width = 96, Height = 72;
    /// <summary>"" = random theme, "*blend" = blended regions, else a theme key.</summary>
    public string Theme = "";
    public string Archetype = "";
    public string Frame = "";
    /// <summary>"" = roll, "*none" = pure landform, else a motif key.</summary>
    public string Motif = "";
    public readonly Dictionary<string, double> Dials = GenerateDials.Defaults();
    public bool Animate = true;
    public double Speed = 3;
    /// <summary>Whether the Fine-tune section is unfolded.</summary>
    public bool FineTuneOpen;

    public JsonObject ToJson()
    {
        var dials = new JsonObject();
        foreach (var (k, v) in Dials) dials[k] = Math.Round(v, 3);
        return new JsonObject
        {
            ["seed"] = Seed, ["width"] = Width, ["height"] = Height, ["theme"] = Theme, ["archetype"] = Archetype,
            ["frame"] = Frame, ["motif"] = Motif, ["dials"] = dials, ["animate"] = Animate, ["speed"] = Speed,
            ["fineTune"] = FineTuneOpen,
        };
    }

    public static GenerateRecipe FromJson(JsonNode? node)
    {
        var r = new GenerateRecipe();
        if (node is not JsonObject o) return r;
        r.Seed = (string?)o["seed"] ?? "";
        r.Width = (int?)o["width"] ?? 96;
        r.Height = (int?)o["height"] ?? 72;
        r.Theme = (string?)o["theme"] ?? "";
        r.Archetype = (string?)o["archetype"] ?? "";
        r.Frame = (string?)o["frame"] ?? "";
        r.Motif = (string?)o["motif"] ?? "";
        r.Animate = (bool?)o["animate"] ?? true;
        r.Speed = (double?)o["speed"] ?? 3;
        r.FineTuneOpen = (bool?)o["fineTune"] ?? false;
        if (o["dials"] is JsonObject dials)
            foreach (var (k, v) in dials)
                if (r.Dials.ContainsKey(k) && v is JsonValue value && value.TryGetValue(out double d)) r.Dials[k] = d;
        return r;
    }
}

/// <summary>
/// The eight composer dials (label, range, default, tooltip). Apart from Surprises they scale what the landscape type
/// asks for: 0 = none of it, 1 (shown as 100%) = the landscape's own amount, 2 = twice as much.
/// </summary>
public static class GenerateDials
{
    public sealed record Dial(string Key, string Label, double Min, double Max, double Default, string Tooltip);

    public static readonly IReadOnlyList<Dial> All = new Dial[]
    {
        new("chaos", "Surprises", 0, 1, 0.4, "How far the map strays from its landscape type: layouts, rare wonders and wild outlines."),
        new("reliefBias", "Mountains", 0, 2, 1, "Rock, cliffs and height. 0% is flat, open ground; 200% towering massifs."),
        new("waterBias", "Water", 0, 2, 1, "Rivers, lakes and seas. 0% is a dry map."),
        new("riftBias", "Chasms", 0, 2, 1, "Chasms and ravines cutting through the land. 0% leaves the ground whole."),
        new("wonderBias", "Wonders", 0, 2, 1, "Monumental set pieces: craters, spires, springs, groves. 0% raises none."),
        new("roadBias", "Roads", 0, 2, 1, "Roads and plazas connecting places. 0% is untouched wilderness."),
        new("floraBias", "Forests", 0, 2, 1, "Trees, groves and thickets. 0% plants none."),
        new("landmarkBias", "Landmarks", 0, 2, 1, "Solitary old trees crowning high ground. 0% plants none."),
    };

    public static Dictionary<string, double> Defaults() => All.ToDictionary(d => d.Key, d => d.Default);
}

/// <summary>
/// The Generate panel: one button makes a new map; seed, landscape, theme and size shape it; the dials and the build
/// animation wait under "Fine-tune"; the result card hands the map to the painter. Every setting reshapes the map on
/// screen at once (<see cref="RecipeChanged"/>): the seed stays, the map is composed again without the animation.
/// </summary>
public sealed partial class GeneratePanel : VBoxContainer
{
    private static readonly (string key, string label, int w, int h)[] Sizes =
    {
        ("s", "S", 64, 48), ("m", "M", 96, 72), ("l", "L", 160, 120), ("xl", "XL", 256, 192),
    };

    private readonly GenerateRecipe _recipe;
    private readonly OptionButton _archetype, _theme, _motif, _frame;
    private readonly LineEdit _seed;
    private readonly Segmented _size = new();
    private readonly Label _sizeHint, _archetypeHint;
    private readonly Dictionary<string, SliderRow> _dials = new();
    private readonly CheckButton _animate;
    private readonly SliderRow _speed;
    private readonly Button _generate;
    private readonly ProgressBar _progress;
    private readonly Label _progressLabel, _resultTitle, _resultStats;
    private readonly Card _result = new();
    private readonly HFlowContainer _recent = new();
    private readonly Control _recentGroup;

    public event Action? GenerateNew;
    public event Action? Regenerate;
    public event Action? SurpriseRequested;
    public event Action? EditRequested;
    public event Action? SaveRequested;
    public event Action<string>? RecentRequested;
    /// <summary>A setting of the recipe changed: the controller rebuilds the current seed live.</summary>
    public event Action? RecipeChanged;

    public GeneratePanel(GenerateRecipe recipe)
    {
        _recipe = recipe;
        AddThemeConstantOverride("separation", 14);
        AddChild(W.Title(T("Generate")));
        AddChild(W.Hint(T("The studio composes a whole map for you: mountains, rivers, lakes and forests. Every seed makes a different one, and every setting below reshapes it at once.")));

        _generate = W.Primary(T("Generate new map"), IconKind.Sparkle, () => GenerateNew?.Invoke(), T("A new map with a fresh seed (Enter)"));
        _progress = new ProgressBar { MinValue = 0, MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 5), Visible = false };
        _progressLabel = W.Label("", 12, StudioTheme.TextMuted);
        _progressLabel.Visible = false;
        AddChild(W.Group("", _generate, _progress, _progressLabel));

        // ── result ──
        _resultTitle = W.Label("", 14, StudioTheme.Text, wrap: true);
        _resultStats = W.Label("", 12, StudioTheme.TextMuted, wrap: true);
        var edit = W.Button(T("Edit in Paint"), IconKind.Brush, () => EditRequested?.Invoke(), T("Continue this map by hand"));
        var save = W.Button(T("Save"), IconKind.Save, () => SaveRequested?.Invoke(), T("Save this map as a file"));
        W.Fill(edit);
        W.Fill(save);
        // "Edit in Paint" is the main way on and the longer label: it gets the larger share of the row.
        edit.SizeFlagsStretchRatio = 1.7f;
        _result.Body.AddChild(_resultTitle);
        _result.Body.AddChild(_resultStats);
        _result.Body.AddChild(W.Spacer(2));
        _result.Body.AddChild(W.Row(edit, save));
        _result.Visible = false;
        AddChild(_result);

        // ── seed ──
        _seed = new LineEdit { Text = recipe.Seed, PlaceholderText = T("Any word or number"), SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true };
        _seed.TextChanged += t => _recipe.Seed = t.Trim();
        _seed.TextSubmitted += _ => { _seed.ReleaseFocus(); Regenerate?.Invoke(); };
        var again = W.IconButton(IconKind.Reset, T("Build this seed again with the current settings"), () => Regenerate?.Invoke(), 36, 16);
        AddChild(W.Group(T("Seed"), W.Row(_seed, again)));

        // ── landscape and theme ──
        var archetypes = new List<(string, string)> { (T("Surprise me"), "") };
        archetypes.AddRange(MapSimulationArchetypes.MAP_SIMULATION_ARCHETYPES.Select(a => (T(a.name), a.key)));
        _archetype = W.Options(archetypes, recipe.Archetype, key => { _recipe.Archetype = key; UpdateArchetypeHint(); RecipeChanged?.Invoke(); });
        _archetypeHint = W.Hint("");
        AddChild(W.Group(T("Landscape"), _archetype, _archetypeHint));

        var themes = new List<(string, string)> { (T("Any theme"), ""), (T("Mixed regions"), "*blend") };
        themes.AddRange(ThemeCatalog.All.Select(e => (e.Name, e.Key)));
        _theme = W.Options(themes, recipe.Theme, key => { _recipe.Theme = key; RecipeChanged?.Invoke(); });
        AddChild(W.Group(T("Theme"), _theme));

        foreach (var (key, label, w, h) in Sizes) _size.Add(key, label, null, F("{0} × {1} cells", w, h));
        _size.Selected += key =>
        {
            var s = Sizes.First(x => x.key == key);
            if (_recipe.Width == s.w && _recipe.Height == s.h) return;
            _recipe.Width = s.w;
            _recipe.Height = s.h;
            UpdateSizeHint();
            RecipeChanged?.Invoke();
        };
        _sizeHint = W.Hint("");
        AddChild(W.Group(T("Size"), _size, _sizeHint));

        // ── fine-tune ──
        var fine = new Section(T("Fine-tune"), recipe.FineTuneOpen, IconKind.Gear);
        fine.Toggled += open => _recipe.FineTuneOpen = open;
        foreach (var dial in GenerateDials.All)
        {
            var d = dial;
            var row = new SliderRow(T(d.Label), d.Min, d.Max, 0.05, recipe.Dials[d.Key], d.Default, v => DialLabel(d, v), T(d.Tooltip));
            row.Changed += v => { _recipe.Dials[d.Key] = v; RecipeChanged?.Invoke(); };
            _dials[d.Key] = row;
            fine.Add(row);
        }
        var motifs = new List<(string, string)> { (T("Let the map decide"), ""), (T("Pure landform"), "*none") };
        motifs.AddRange(MapSimulationMotifs.MAP_LANDFORM_MOTIFS.Select(m => (m.name, m.key)));
        _motif = W.Options(motifs, recipe.Motif, key => { _recipe.Motif = key; RecipeChanged?.Invoke(); });
        fine.Add(W.Field(T("Layout"), _motif, 84));
        var frames = new List<(string, string)> { (T("Natural"), "") };
        frames.AddRange(MapSimulationArchetypes.MAP_FRAME_KINDS.Select(f => (char.ToUpperInvariant(f[0]) + f[1..], f)));
        _frame = W.Options(frames, recipe.Frame, key => { _recipe.Frame = key; RecipeChanged?.Invoke(); });
        fine.Add(W.Field(T("Outline"), _frame, 84));
        var surprise = W.Button(T("Surprise me"), IconKind.Dice, () => SurpriseRequested?.Invoke(), T("Wild settings and a fresh seed — then generate"));
        var reset = W.Button(T("Reset"), IconKind.Reset, ResetDials, T("All settings back to normal"));
        W.Fill(surprise);
        W.Fill(reset);
        fine.Add(W.Row(surprise, reset));
        AddChild(fine);

        var build = new Section(T("Build animation"), false, IconKind.Film);
        _animate = W.Toggle(T("Watch the map being built"), recipe.Animate, on => _recipe.Animate = on, T("The map builds itself layer by layer with a camera move. Space skips."));
        _speed = new SliderRow(T("Speed"), 0.5, 4, 0.1, recipe.Speed, 3, v => $"×{v:0.0}", T("Playback speed of the build animation"));
        _speed.Changed += v => _recipe.Speed = v;
        build.Add(_animate);
        build.Add(_speed);
        AddChild(build);

        _recentGroup = W.Group(T("Recent seeds"), _recent);
        _recentGroup.Visible = false;
        AddChild(_recentGroup);

        SyncFromRecipe();
    }

    /// <summary>A dial's value as a share: Surprises of its full range, the others of the landscape's own amount.</summary>
    private static string DialLabel(GenerateDials.Dial dial, double v) => $"{Math.Round(v * 100):0}%";

    private void UpdateArchetypeHint()
    {
        var archetype = MapSimulationArchetypes.MAP_SIMULATION_ARCHETYPES.FirstOrDefault(a => a.key == _recipe.Archetype);
        _archetypeHint.Text = archetype != null ? T(archetype.summary) : T("Every map picks one of twelve landscape types.");
    }

    private void UpdateSizeHint() =>
        _sizeHint.Text = F("{0} × {1} cells · {2} × {3} m", _recipe.Width, _recipe.Height, _recipe.Width * 2.5, _recipe.Height * 2.5);

    private void ResetDials()
    {
        foreach (var dial in GenerateDials.All)
        {
            _recipe.Dials[dial.Key] = dial.Default;
            _dials[dial.Key].SetSilently(dial.Default);
        }
        _recipe.Frame = "";
        _recipe.Motif = "";
        W.Select(_frame, "");
        W.Select(_motif, "");
        RecipeChanged?.Invoke();
    }

    /// <summary>Re-reads the controls from the recipe (after Surprise me).</summary>
    public void SyncFromRecipe()
    {
        W.Select(_archetype, _recipe.Archetype);
        W.Select(_theme, _recipe.Theme);
        W.Select(_motif, _recipe.Motif);
        W.Select(_frame, _recipe.Frame);
        if (!_seed.HasFocus()) _seed.Text = _recipe.Seed;
        var size = Sizes.OrderBy(s => Math.Abs(s.w - _recipe.Width) + Math.Abs(s.h - _recipe.Height)).First();
        _size.Set(size.key);
        foreach (var (k, row) in _dials) row.SetSilently(_recipe.Dials[k]);
        _animate.SetPressedNoSignal(_recipe.Animate);
        _speed.SetSilently(_recipe.Speed);
        UpdateArchetypeHint();
        UpdateSizeHint();
    }

    /// <summary>Shows a build in progress. A live rebuild (a setting changed) keeps the main button usable.</summary>
    public void SetBusy(bool busy, double progress, string label, bool live = false)
    {
        _generate.Disabled = busy && !live;
        _generate.Text = busy && !live ? T("Composing …") : T("Generate new map");
        _progress.Visible = busy;
        _progress.Value = progress;
        if (live) label = T("Updating the map …");
        _progressLabel.Visible = busy && label.Length > 0;
        _progressLabel.Text = label;
    }

    public void SetResult(SimulatedMap? map)
    {
        _result.Visible = map != null;
        if (map == null) return;
        var r = map.recipe;
        var m = map.metrics;
        _resultTitle.Text = F("{0} · “{1}”", r.name, r.seedId);
        double water = m.cells > 0 ? 100.0 * m.waterCells / m.cells : 0;
        string wonders = m.wonders == 1 ? T("1 wonder") : F("{0} wonders", m.wonders);
        _resultStats.Text = F("{0} × {1} cells · {2} · {3}% water", r.width, r.height, wonders, Math.Round(water))
            + (map.validation.ok ? "" : "\n" + F("{0} check hint(s) — the painter shows them", map.validation.issues.Count));
    }

    public void SetRecent(IEnumerable<string> seeds)
    {
        foreach (var child in _recent.GetChildren()) child.QueueFree();
        int count = 0;
        foreach (string seed in seeds.Take(6))
        {
            string s = seed;
            var chip = new Chip(s, T("Build this seed again")) { ToggleMode = false };
            chip.Pressed += () => RecentRequested?.Invoke(s);
            _recent.AddChild(chip);
            count++;
        }
        _recentGroup.Visible = count > 0;
    }
}
