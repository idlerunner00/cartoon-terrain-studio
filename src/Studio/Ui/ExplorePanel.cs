using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using TerrainStudio.Core;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio.Ui;

/// <summary>A remembered place in an open world.</summary>
public sealed record WorldBookmark(string Name, string Theme, string Seed, double X, double Y);

/// <summary>
/// The Explore panel: every seed grows an endless world. One button rolls a new one; the seed can be typed, copied
/// and shared; "Discover" shows pictures of worlds to pick from; the map shows where the camera is and travels on a
/// click; places can be saved, and any area can be opened in the painter.
/// </summary>
public sealed partial class ExplorePanel : VBoxContainer
{
    /// <summary>Theme choice: a random theme for every new world.</summary>
    public const string AnyTheme = "*any";
    /// <summary>Theme choice: the canonical world palette with regional colours.</summary>
    public const string ClassicWorld = "*world";

    public readonly WorldMinimap Minimap = new();
    private readonly LineEdit _seed;
    private readonly OptionButton _theme;
    private readonly CheckButton _regions;
    private readonly Label _world, _position;
    private readonly HFlowContainer _recent = new();
    private readonly GridContainer _discover = new() { Columns = 2 };
    private readonly VBoxContainer _favorites = W.Column(4);
    private readonly Section _favoritesSection;
    private readonly Segmented _area = new();

    /// <summary>"New random world" (scripted runs click it).</summary>
    public Button RandomButton { get; }
    public event Action? RandomWorld;
    public event Action<string>? OpenSeed;
    public event Action<string>? ThemeChosen;
    public event Action<bool>? RegionsChanged;
    public event Action? CopySeed;
    public event Action? RandomPlace;
    public event Action? BackToStart;
    public event Action? SavePlace;
    public event Action<WorldBookmark>? OpenPlace;
    public event Action<WorldBookmark>? RemovePlace;
    public event Action? ShuffleDiscover;
    public event Action<string, string>? OpenSuggestion;
    public event Action<string, string>? OpenRecent;
    public event Action<int>? EditArea;

    public ExplorePanel(string themeChoice, string seed, bool regions)
    {
        AddThemeConstantOverride("separation", 14);
        AddChild(W.Title(T("Explore")));
        AddChild(W.Hint(T("Every seed grows its own endless world. Move with WASD or drag with the right mouse button.")));

        RandomButton = W.Primary(T("New random world"), IconKind.Dice, () => RandomWorld?.Invoke(), T("Roll a new seed and open its world (R)"));
        AddChild(RandomButton);

        // ── seed ──
        _seed = new LineEdit { Text = seed, PlaceholderText = T("Type any word or number"), SelectAllOnFocus = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _seed.TextSubmitted += _ => Go();
        var go = W.IconButton(IconKind.Play, T("Open this seed (Enter)"), Go, 36, 16);
        var copy = W.IconButton(IconKind.Copy, T("Copy the seed to share it"), () => CopySeed?.Invoke(), 36, 16);
        _world = W.Label("", 12, StudioTheme.TextFaint);
        _recent.AddThemeConstantOverride("h_separation", 6);
        _recent.AddThemeConstantOverride("v_separation", 6);
        AddChild(W.Group(T("Seed"), W.Row(_seed, go, copy), _world, _recent));

        // ── theme ──
        var options = new List<(string, string)> { (T("Any theme (surprise)"), AnyTheme), (T("Classic world"), ClassicWorld) };
        options.AddRange(ThemeCatalog.All.Select(e => (e.Name, e.Key)));
        _theme = W.Options(options, themeChoice, key => ThemeChosen?.Invoke(key));
        _regions = W.Toggle(T("Regional colours"), regions, on => RegionsChanged?.Invoke(on),
            T("The world changes colour and plants from region to region (cherry meadows, conifer woods, autumn, coast …)"));
        AddChild(W.Group(T("Theme"), _theme, _regions));

        // ── discover ──
        var discover = new Section(T("Discover worlds"), true, IconKind.Sparkle);
        var shuffle = W.Button(T("Show others"), IconKind.Shuffle, () => ShuffleDiscover?.Invoke(), T("Four new seeds to choose from"));
        W.Fill(shuffle);
        discover.Add(_discover);
        discover.Add(shuffle);
        AddChild(discover);

        // ── map ──
        var map = new Section(T("Map"), true, IconKind.Map);
        _position = W.Label("", 12, StudioTheme.TextFaint);
        map.Add(Minimap);
        map.Add(_position);
        var random = W.Button(T("Random"), IconKind.Dice, () => RandomPlace?.Invoke(), T("Jump somewhere far away in this world"));
        var start = W.Button(T("Start"), IconKind.Target, () => BackToStart?.Invoke(), T("Back to where this world begins"));
        var save = W.Button(T("Save|place"), IconKind.Star, () => SavePlace?.Invoke(), T("Remember this place of this world"));
        var row = W.Row(random, start, save);
        row.AddThemeConstantOverride("separation", 4);
        foreach (var b in new[] { random, start, save })
        {
            W.Fill(b);
            b.AddThemeFontSizeOverride("font_size", 12);
            b.AddThemeConstantOverride("h_separation", 4);
            b.AddThemeStyleboxOverride("normal", StudioTheme.Box(StudioTheme.Raised, new Color(0, 0, 0, 0), 9, 0, 6, 8));
            b.AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Hover, new Color(0, 0, 0, 0), 9, 0, 6, 8));
        }
        map.Add(row);
        AddChild(map);

        _favoritesSection = new Section(T("Saved places"), true, IconKind.StarFilled);
        _favoritesSection.Add(_favorites);
        AddChild(_favoritesSection);

        // ── hand-off ──
        var edit = new Section(T("Edit this area in Paint"), false, IconKind.Brush);
        _area.Add("1", T("Small"), null, T("One chunk: 32 × 32 cells (80 m) with every detail"));
        _area.Add("3", T("Medium"), null, T("3 × 3 chunks: 96 × 96 cells (240 m)"));
        _area.Add("5", T("Large"), null, T("5 × 5 chunks: 160 × 160 cells (400 m)"));
        _area.Set("3");
        edit.Add(_area);
        edit.Add(W.Hint(T("Copies the land around the middle of the view into a new map you can paint on.")));
        edit.Add(W.Primary(T("Open in Paint"), IconKind.Brush, () => EditArea?.Invoke(int.Parse(_area.Current, CultureInfo.InvariantCulture))));
        AddChild(edit);
    }

    private void Go()
    {
        string seed = _seed.Text.Trim();
        if (seed.Length == 0) { RandomWorld?.Invoke(); return; }
        _seed.ReleaseFocus();
        OpenSeed?.Invoke(seed);
    }

    public void SetWorld(string themeChoice, string seed, bool regions, string themeName)
    {
        W.Select(_theme, themeChoice);
        if (!_seed.HasFocus()) _seed.Text = seed;
        _regions.SetPressedNoSignal(regions);
        _world.Text = F("World of “{0}” · {1}", seed, themeName);
    }

    public void SetPosition(double x, double y)
    {
        var (cx, cy) = WorldPreview.ChunkAt(x, y);
        _position.Text = F("Chunk {0}, {1} · {2} m east, {3} m south", cx, cy, Math.Round(x / 25), Math.Round(y / 25));
    }

    public void SetRecent(IEnumerable<(string seed, string theme, string themeName)> recent)
    {
        foreach (var child in _recent.GetChildren()) child.QueueFree();
        foreach (var (seed, theme, themeName) in recent.Take(6))
        {
            var chip = new Chip(seed, F("{0} · {1}", seed, themeName)) { ToggleMode = false };
            chip.Pressed += () => OpenRecent?.Invoke(seed, theme);
            _recent.AddChild(chip);
        }
    }

    public void SetSuggestions(IEnumerable<SeedCard> cards)
    {
        foreach (var child in _discover.GetChildren()) child.QueueFree();
        foreach (var card in cards)
        {
            var c = card;
            c.Pressed += () => OpenSuggestion?.Invoke(c.Seed, c.ThemeKey);
            _discover.AddChild(c);
        }
    }

    public void SetFavorites(IReadOnlyList<WorldBookmark> marks)
    {
        foreach (var child in _favorites.GetChildren()) child.QueueFree();
        _favoritesSection.Visible = marks.Count > 0;
        foreach (var mark in marks)
        {
            var captured = mark;
            var open = W.Button(mark.Name, IconKind.StarFilled, () => OpenPlace?.Invoke(captured),
                F("{0} · {1} · {2} m, {3} m", mark.Seed, ThemeCatalog.Name(mark.Theme), Math.Round(mark.X / 25), Math.Round(mark.Y / 25)), filled: false);
            open.Alignment = HorizontalAlignment.Left;
            open.ClipText = true;
            open.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            open.AddThemeColorOverride("icon_normal_color", StudioTheme.Accent);
            var remove = W.IconButton(IconKind.Close, T("Forget this place"), () => RemovePlace?.Invoke(captured), 30, 14);
            _favorites.AddChild(W.Row(open, remove));
        }
    }
}
