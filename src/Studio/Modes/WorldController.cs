using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fluitown.Domain;
using Godot;
using TerrainStudio.Core;
using TerrainStudio.Terrain;
using TerrainStudio.Ui;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio;

/// <summary>
/// The Explore mode: rolls the endless, chunk-streamed world of a seed and a theme (the same generator the game
/// streams, <c>theme:&lt;biome&gt;:&lt;seed&gt;</c>), suggests seeds with pictures, shows a minimap, remembers places
/// and recent seeds, and hands any area to the painter.
/// </summary>
public sealed partial class WorldController : IModeController
{
    private readonly StudioMain _main;
    public readonly ExplorePanel Panel;
    private readonly Random _random = new();
    /// <summary>The theme choice of the panel (may be "any"); <see cref="_theme"/> is the theme of the open world.</summary>
    private string _choice, _theme, _seed;
    private bool _regions, _opened, _suggested;
    private int _suggestions;

    public WorldController(StudioMain main)
    {
        _main = main;
        var settings = main.Settings;
        _choice = settings.WorldThemeChoice;
        _theme = settings.WorldTheme;
        _seed = settings.WorldSeed;
        _regions = settings.WorldRegions;
        Panel = new ExplorePanel(_choice, _seed, _regions);
        Panel.RandomWorld += RandomWorld;
        Panel.OpenSeed += seed => Open(ThemeFor(_choice, seed), seed, 0, 0);
        Panel.ThemeChosen += choice =>
        {
            _choice = choice;
            _main.Settings.WorldThemeChoice = choice;
            Open(ThemeFor(choice, _seed), _seed, _main.View.FocusX, _main.View.FocusY);
            ShuffleSuggestions();
        };
        Panel.RegionsChanged += on =>
        {
            _regions = on;
            Open(_theme, _seed, _main.View.FocusX, _main.View.FocusY);
        };
        Panel.CopySeed += () =>
        {
            DisplayServer.ClipboardSet(_seed);
            _main.Toast(F("Seed “{0}” copied", _seed), StudioTheme.Mint);
        };
        Panel.RandomPlace += () => TravelTo((_random.NextDouble() * 2 - 1) * 60000, (_random.NextDouble() * 2 - 1) * 60000);
        Panel.BackToStart += () => TravelTo(0, 0);
        Panel.SavePlace += SavePlace;
        Panel.OpenPlace += mark => Open(mark.Theme, mark.Seed, mark.X, mark.Y);
        Panel.RemovePlace += mark =>
        {
            _main.Settings.Bookmarks.RemoveAll(b => b.name == mark.Name && b.seed == mark.Seed && b.x == mark.X && b.y == mark.Y);
            RefreshFavorites();
            _main.Settings.Save();
        };
        Panel.ShuffleDiscover += () => ShuffleSuggestions();
        Panel.OpenSuggestion += (seed, theme) => Open(theme, seed, 0, 0);
        Panel.OpenRecent += (seed, theme) => Open(theme, seed, 0, 0);
        Panel.EditArea += TakeArea;
        Panel.Minimap.Travel += TravelTo;
        RefreshFavorites();
        RefreshRecent();
    }

    public StudioMode Mode => StudioMode.World;
    Control IModeController.Panel => Panel;
    public Control? Rail => null;
    public bool Busy => !_main.View.ViewReady;
    public bool OwnsKeyboardMotion => false;

    public string Hint => T("WASD or right-drag: travel · Wheel: zoom · Click the map to jump");
    public string Cursor => "";

    /// <summary>The instance id of a theme and seed: the canonical world, or a theme world rolled from the seed.</summary>
    public static string InstanceId(string theme, string seed)
    {
        string clean = Regex.Replace(seed.Trim(), "[^A-Za-z0-9_.-]+", "-").Trim('-');
        if (clean.Length == 0) clean = "world";
        if (theme == ExplorePanel.ClassicWorld)
            return clean == "world" ? WorldIdentity.SHARED_WORLD_ID : $"theme:{WorldIdentity.SHARED_WORLD_BIOME}:{clean}";
        return $"theme:{theme}:{clean}";
    }

    public static string ThemeName(string theme) => theme == ExplorePanel.ClassicWorld ? T("Classic world") : ThemeCatalog.Name(theme);

    /// <summary>
    /// The theme a seed opens with. "Any theme" picks one from the seed itself, so a shared seed always grows the same
    /// world.
    /// </summary>
    private static string ThemeFor(string choice, string seed)
    {
        if (choice != ExplorePanel.AnyTheme) return choice;
        return ThemeForSeed(seed);
    }

    /// <summary>The theme "any theme" picks for a seed.</summary>
    public static string ThemeForSeed(string seed)
    {
        uint h = 2166136261;
        foreach (char c in seed) { h ^= c; h *= 16777619; }
        var all = ThemeCatalog.All;
        return all[(int)(h % (uint)all.Count)].Key;
    }

    // ── worlds ──────────────────────────────────────────────────────────────────────────────────────────────

    public void RandomWorld()
    {
        string seed = Seeds.Random(_random);
        Open(ThemeFor(_choice, seed), seed, 0, 0);
    }

    public void Open(string theme, string seed, double x, double y)
    {
        string id = InstanceId(theme, seed);
        try
        {
            double zoom = _main.View.Source == TerrainSource.OpenWorld ? _main.View.Zoom : 0.95;
            _main.View.OpenWorld(id, x, y, zoom, _regions);
            _theme = theme;
            _seed = seed;
            _opened = true;
            var settings = _main.Settings;
            settings.WorldTheme = theme;
            settings.WorldSeed = seed;
            settings.WorldRegions = _regions;
            settings.RecentSeeds.RemoveAll(r => r.seed == seed && r.theme == theme);
            settings.RecentSeeds.Insert(0, (seed, theme));
            if (settings.RecentSeeds.Count > 12) settings.RecentSeeds.RemoveRange(12, settings.RecentSeeds.Count - 12);
            Panel.SetWorld(_choice, seed, _regions, ThemeName(theme));
            Panel.Minimap.SetWorld(id, Descriptor.descriptorFromInstanceId(id));
            RefreshRecent();
        }
        catch (Exception e)
        {
            _main.Toast(F("Could not open the world: {0}", e.Message), StudioTheme.Danger);
        }
    }

    private void TravelTo(double x, double y)
    {
        _main.View.FocusX = x;
        _main.View.FocusY = y;
    }

    public void Activate(string? startFile)
    {
        if (!_opened || _main.View.Source != TerrainSource.OpenWorld)
            Open(_theme, _seed, _main.Settings.WorldX, _main.Settings.WorldY);
        if (!_suggested) ShuffleSuggestions();
    }

    public void Deactivate()
    {
        if (_main.View.Source != TerrainSource.OpenWorld) return;
        _main.Settings.WorldX = _main.View.FocusX;
        _main.Settings.WorldY = _main.View.FocusY;
    }

    // ── discover ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Four fresh seeds with pictures of their start area, drawn in the background; scripted runs may name
    /// the four worlds (seed and theme) instead.</summary>
    public void ShuffleSuggestions(IReadOnlyList<(string seed, string theme)>? picks = null)
    {
        _suggested = true;
        int generation = ++_suggestions;
        var cards = new List<SeedCard>();
        for (int i = 0; i < 4; i++)
        {
            string seed = picks != null ? picks[i].seed : Seeds.Random(_random);
            string theme = picks != null ? picks[i].theme : ThemeFor(_choice, seed);
            var card = new SeedCard(seed, theme, ThemeName(theme));
            cards.Add(card);
            var descriptor = Descriptor.descriptorFromInstanceId(InstanceId(theme, seed));
            if (descriptor == null) continue;
            WorldPreview.Request(() => WorldPreview.RenderArea(descriptor, 0, 0, 3), texture => card.Picture = texture,
                () => generation == _suggestions);
        }
        Panel.SetSuggestions(cards);
    }

    // ── places ──────────────────────────────────────────────────────────────────────────────────────────────

    private void SavePlace()
    {
        var (cx, cy) = WorldPreview.ChunkAt(_main.View.FocusX, _main.View.FocusY);
        string name = F("{0} · {1}, {2}", _seed, cx, cy);
        _main.Settings.Bookmarks.Insert(0, (name, _theme, _seed, _main.View.FocusX, _main.View.FocusY));
        RefreshFavorites();
        _main.Settings.Save();
        _main.Toast(T("Place saved"), StudioTheme.Mint);
    }

    private void RefreshFavorites() =>
        Panel.SetFavorites(_main.Settings.Bookmarks.Select(b => new WorldBookmark(b.name, b.theme, b.seed, b.x, b.y)).ToList());

    private void RefreshRecent() =>
        Panel.SetRecent(_main.Settings.RecentSeeds.Where(r => !(r.seed == _seed && r.theme == _theme)).Select(r => (r.seed, r.theme, ThemeName(r.theme))));

    public void TakeArea(int span)
    {
        var descriptor = Descriptor.descriptorFromInstanceId(InstanceId(_theme, _seed));
        if (descriptor == null) return;
        var (cx, cy) = WorldPreview.ChunkAt(_main.View.FocusX, _main.View.FocusY);
        var artifact = GeneratorArtifacts.artifactFromEndlessArea(descriptor, cx, cy, span);
        if (_main.ControllerOf(StudioMode.Paint) is IArtifactReceiver painter)
        {
            painter.ReceiveArtifact(artifact, $"{_seed}-{cx}_{cy}", F("Area of “{0}” opened in Paint", _seed));
            _main.SetMode(StudioMode.Paint);
        }
    }

    // ── frame ───────────────────────────────────────────────────────────────────────────────────────────────

    public void Tick(double delta)
    {
        if (_main.View.Source != TerrainSource.OpenWorld || Engine.GetProcessFrames() % 6 != 0) return;
        var view = _main.View;
        var size = _main.GetViewport().GetVisibleRect().Size;
        var corners = new List<Vector2>(4);
        foreach (var corner in new[] { Vector2.Zero, new Vector2(size.X, 0), size, new Vector2(0, size.Y) })
            if (view.TryPickGround(corner, out double wx, out double wy)) corners.Add(new Vector2((float)wx, (float)wy));
        Panel.Minimap.SetView(view.FocusX, view.FocusY, corners.ToArray());
        Panel.SetPosition(view.FocusX, view.FocusY);
    }

    public void AfterPresent() { }
    public void Pointer(InputEvent e) { }
    public bool Wheel(InputEventMouseButton e) => false;

    public bool Key(InputEventKey e)
    {
        if (e.Keycode == global::Godot.Key.R && !e.CtrlPressed) { RandomWorld(); return true; }
        return false;
    }

    public void Draw(ViewportInput canvas) { }
    public void Undo() { }
    public void Redo() { }

    public void Persist(StudioSettings settings)
    {
        if (_main.View.Source != TerrainSource.OpenWorld) return;
        settings.WorldX = _main.View.FocusX;
        settings.WorldY = _main.View.FocusY;
    }
}

/// <summary>A mode that accepts a map created elsewhere (a generated map, an area of the open world).</summary>
public interface IArtifactReceiver
{
    void ReceiveArtifact(TerrainArtifact artifact, string name, string notice);
}
