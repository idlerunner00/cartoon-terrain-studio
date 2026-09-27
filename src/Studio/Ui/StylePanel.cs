using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using TerrainStudio.Core;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio.Ui;

/// <summary>
/// The Style drawer: how the terrain looks, live and without rebuilding it. From top to bottom it goes from big,
/// playful choices (looks, shuffle, time, season, weather) to the few sliders most people touch, and keeps every
/// detailed material control folded away under "All settings". Double-click any slider to reset it.
/// </summary>
public sealed partial class StylePanel : VBoxContainer
{
    private readonly LookSettings _look;
    private readonly Random _random = new();
    private readonly List<(string id, SliderRow row)> _rows = new();
    private readonly Dictionary<string, CheckButton> _toggles = new();
    private readonly Dictionary<string, SwatchCard> _presets = new();
    private readonly SliderRow _day, _season, _variety;
    private readonly Segmented _weather = new();
    private string? _preset = "default";

    /// <summary>A live look value changed.</summary>
    public event Action? LookChanged;
    /// <summary>A terrain-shape switch changed: the terrain must be rebuilt.</summary>
    public event Action? FormChanged;
    public event Action<double>? DayChanged;
    public event Action? SaveRequested;
    public event Action? LoadRequested;
    public event Action? CloseRequested;

    public StylePanel(LookSettings look, double day)
    {
        _look = look;
        AddThemeConstantOverride("separation", 14);

        var close = W.IconButton(IconKind.Close, T("Close"), () => CloseRequested?.Invoke(), 30, 16);
        var resetAll = W.IconButton(IconKind.Reset, T("Back to the theme's own look"), () =>
        {
            _look.Reset();
            _preset = "default";
            SyncFromLook();
            LookChanged?.Invoke();
        }, 30, 16);
        AddChild(W.Row(W.Title(T("Style")), W.Expand(), resetAll, close));
        AddChild(W.Hint(T("Colours, light and weather — changes show at once. Double-click a slider to reset it.")));

        // ── looks ──
        var grid = new GridContainer { Columns = 3 };
        foreach (var preset in LookSettings.Presets)
        {
            var p = preset;
            var card = new SwatchCard(T(ShortName(p.Key, p.Name)), p.Swatch.Select(c => W.Rgb(c)).ToArray(), T(p.Name), height: 58);
            card.Pressed += () =>
            {
                _look.ApplyPreset(p);
                _preset = p.Key;
                SyncFromLook();
                LookChanged?.Invoke();
            };
            _presets[p.Key] = card;
            grid.AddChild(card);
        }
        AddChild(W.Group(T("Looks"), grid));

        var shuffle = W.Primary(T("Shuffle look"), IconKind.Shuffle, Randomize, T("A fresh random look for the same map (L)"));
        _variety = new SliderRow(T("Variety"), 0, 1, 0.01, 0.45, 0.45, v => $"{v * 100:0}%", T("How far a shuffled look strays from the theme's own colours"));
        AddChild(W.Group("", shuffle, _variety));

        // ── time, season, weather ──
        _day = new SliderRow(T("Time of day"), 0, 1, 0.001, day, 0.5, DayLabel, T("Midnight, dawn, noon, dusk. Keys , and ."));
        _day.Changed += v => DayChanged?.Invoke(v);
        _season = new SliderRow(T("Season"), 0, 3, 0.01, _look["macro.season"], 1, v => T(LookSettings.SeasonName(v)),
            T("Spring, summer, autumn or winter: foliage, meadows and snow together"));
        _season.Changed += v =>
        {
            _look.SetSeason(v);
            Changed();
        };
        _weather.Add(LookSettings.WeatherClear, "", IconKind.Sun, T("Clear"));
        _weather.Add(LookSettings.WeatherWet, "", IconKind.Drop, T("Wet ground"));
        _weather.Add(LookSettings.WeatherRain, "", IconKind.Rain, T("Rain"));
        _weather.Add(LookSettings.WeatherSnow, "", IconKind.Snow, T("Snow"));
        _weather.Selected += key =>
        {
            _look.SetWeather(key);
            Changed();
        };
        AddChild(W.Group(T("Atmosphere"), _day, _season, W.Field(T("Weather"), _weather, 84)));

        // ── the few sliders most people need ──
        var quick = W.Group(T("Adjust"));
        foreach (var (id, label, tooltip) in new[]
        {
            ("color.hue", "Colour shift", "Turns every colour of the terrain around the colour wheel"),
            ("color.sat", "Vibrance", "How colourful the terrain is"),
            ("color.warmth", "Warmth", "Warm amber or cool blue light on the pigments"),
            ("floor.lush", "Lush meadows", "How green and lush open ground is"),
            ("wind.strength", "Wind", "How far trees and grass sway"),
            ("ink.width", "Outlines", "Width of the comic ink lines"),
        })
            quick.AddChild(Slider(id, T(label), T(tooltip)));
        AddChild(quick);

        // ── everything else ──
        var all = new Section(T("All settings"), false, IconKind.Gear);
        foreach (string group in LookSettings.Groups)
        {
            var section = new Section(T(group), false);
            all.Add(section);
            if (group == LookSettings.GroupForm)
                section.Add(W.Hint(T("These switches change the shape of the terrain; it is rebuilt.")));
            foreach (var p in LookSettings.Params.Where(p => p.Group == group))
            {
                if (p.Toggle)
                {
                    var param = p;
                    var toggle = W.Toggle(T(p.Label), _look.Flag(p.Id), on =>
                    {
                        _look[param.Id] = on ? 1 : 0;
                        _preset = null;
                        UpdatePresetCards();
                        if (param.Rebake) FormChanged?.Invoke();
                        else LookChanged?.Invoke();
                    }, T(p.Tooltip));
                    _toggles[p.Id] = toggle;
                    section.Add(toggle);
                    continue;
                }
                section.Add(Slider(p.Id, T(p.Label), T(p.Tooltip)));
            }
            if (group != LookSettings.GroupForm)
            {
                string g = group;
                var groupReset = W.Button(T("Reset this group"), IconKind.Reset, () =>
                {
                    _look.Reset(g);
                    Changed();
                }, filled: false);
                groupReset.AddThemeFontSizeOverride("font_size", 12);
                groupReset.AddThemeColorOverride("font_color", StudioTheme.TextMuted);
                groupReset.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
                section.Add(groupReset);
            }
        }
        AddChild(all);

        // ── files ──
        var load = W.Fill(W.Button(T("Load"), IconKind.Folder, () => LoadRequested?.Invoke(), T("Load a saved style")));
        var save = W.Fill(W.Button(T("Save"), IconKind.Save, () => SaveRequested?.Invoke(), T("Save this style as a file")));
        AddChild(new HSeparator());
        AddChild(W.Row(load, save));

        SyncFromLook();
    }

    private SliderRow Slider(string id, string label, string tooltip)
    {
        var p = LookSettings.ById[id];
        var row = new SliderRow(label, p.Min, p.Max, p.Step, _look[id], p.Default, v => LookSettings.FormatValue(p, v), tooltip);
        row.Changed += v =>
        {
            _look[id] = v;
            Changed(except: row);
        };
        _rows.Add((id, row));
        return row;
    }

    /// <summary>A value was changed by hand: the look is custom now, the other controls follow.</summary>
    private void Changed(SliderRow? except = null)
    {
        _preset = null;
        SyncFromLook(except);
        LookChanged?.Invoke();
    }

    /// <summary>Re-reads every control from the look (after a preset, shuffle, reset or load).</summary>
    public void SyncFromLook() => SyncFromLook(null);

    private void SyncFromLook(SliderRow? except)
    {
        foreach (var (id, row) in _rows)
            if (row != except) row.SetSilently(_look[id]);
        foreach (var (id, toggle) in _toggles) toggle.SetPressedNoSignal(_look.Flag(id));
        _season.SetSilently(_look["macro.season"]);
        _weather.Set(_look.Weather);
        UpdatePresetCards();
    }

    private void UpdatePresetCards()
    {
        foreach (var (key, card) in _presets) card.SetPressedNoSignal(key == _preset);
    }

    /// <summary>Forgets which look card was chosen (a look file or a map brought its own look).</summary>
    public void MarkCustom()
    {
        _preset = null;
        UpdatePresetCards();
    }

    public void SetDay(double day) => _day.SetSilently(day);

    public void Randomize()
    {
        _look.Randomize(_random, _variety.Value);
        _preset = null;
        SyncFromLook();
        LookChanged?.Invoke();
    }

    private static string ShortName(string key, string name) => key switch
    {
        "default" => "Original",
        "golden_autumn" => "Autumn",
        "frost" => "Frost",
        "desert" => "Desert",
        "alien" => "Alien",
        "noir" => "Noir",
        "pastel" => "Pastel",
        "tropic" => "Tropics",
        "storm" => "Storm",
        _ => name,
    };

    public static string DayLabel(double day)
    {
        double hours = day * 24;
        int h = (int)Math.Floor(hours) % 24, m = (int)Math.Floor((hours - Math.Floor(hours)) * 60);
        string phase = day is > 0.2 and < 0.3 ? T("dawn") : day is >= 0.3 and < 0.7 ? T("day") : day is >= 0.7 and < 0.8 ? T("dusk") : T("night");
        return $"{h:00}:{m:00} · {phase}";
    }
}
