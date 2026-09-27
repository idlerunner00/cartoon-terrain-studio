using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerrainStudio.Core;

/// <summary>One adjustable parameter of the procedural terrain look.</summary>
public sealed record LookParam(
    string Id, string Group, string Label, double Min, double Max, double Step, double Default, string Tooltip,
    bool Rebake = false, bool Toggle = false, string? Format = null);

/// <summary>
/// The adjustable procedural look of the terrain: colour grade per material family, material pattern gains and scales,
/// vegetation, water, weather, light and ink — every value is a live shader input (no re-bake) except the "Form"
/// group, whose switches change the baked geometry. Serialises to JSON and ships with presets and a tasteful
/// randomiser for endless variations.
/// </summary>
public sealed class LookSettings
{
    public const string GroupColour = "Colour grade";
    public const string GroupGround = "Ground";
    public const string GroupRock = "Rock";
    public const string GroupPattern = "Material pattern";
    public const string GroupVegetation = "Vegetation & wind";
    public const string GroupWater = "Water";
    public const string GroupWeather = "Weather";
    public const string GroupLight = "Light & ink";
    public const string GroupForm = "Terrain shape";
    /// <summary>Macro controls of the Style drawer (not listed among the detailed groups).</summary>
    public const string GroupQuick = "Quick";

    private const string Turns = "turns", Percent = "percent", Times = "times", Signed = "signed";

    public static readonly IReadOnlyList<LookParam> Params = new LookParam[]
    {
        new("color.hue", GroupColour, "Hue", -0.5, 0.5, 0.005, 0, "Turns the hue of the whole terrain (luma-preserving).", Format: Turns),
        new("color.sat", GroupColour, "Saturation", 0, 2, 0.01, 1, "Colour intensity of the whole terrain.", Format: Percent),
        new("color.value", GroupColour, "Brightness", 0.4, 1.8, 0.01, 1, "Pigment brightness of the whole terrain.", Format: Percent),
        new("color.warmth", GroupColour, "Warmth", -1, 1, 0.01, 0, "Shifts the pigments towards warm amber or cool blue.", Format: Signed),
        new("color.contrast", GroupColour, "Contrast", 0.5, 1.7, 0.01, 1, "Pigment contrast around the mid tones.", Format: Percent),

        new("floor.hue", GroupGround, "Hue", -0.5, 0.5, 0.005, 0, "Hue turn of open ground and meadow.", Format: Turns),
        new("floor.sat", GroupGround, "Saturation", 0, 2, 0.01, 1, "Colour intensity of open ground.", Format: Percent),
        new("floor.value", GroupGround, "Brightness", 0.4, 1.8, 0.01, 1, "Brightness of open ground.", Format: Percent),
        new("floor.warmth", GroupGround, "Warmth", -1, 1, 0.01, 0, "Warm/cool shift of open ground.", Format: Signed),
        new("floor.lush", GroupGround, "Lush meadow", 0, 3, 0.01, 1, "How strongly the lush green pole colours covered ground.", Format: Times),
        new("floor.turf", GroupGround, "Turf blend", 0, 2, 0.01, 1, "How much the turf colour tints covered ground.", Format: Times),
        new("floor.dry", GroupGround, "Dry soil", 0, 3, 0.01, 1, "How strongly bare ground turns to dry soil.", Format: Times),
        new("floor.wear", GroupGround, "Path wear", 0, 4, 0.01, 1, "Darkening of trodden paths and roads.", Format: Times),
        new("floor.broad", GroupGround, "Broad variation", 0, 3, 0.01, 1, "Large, soft light/dark patches across the ground.", Format: Times),
        new("floor.grain", GroupGround, "Grain & strokes", 0, 3, 0.01, 1, "Brush strokes, fine grain and meadow fibre.", Format: Times),
        new("floor.stones", GroupGround, "Stones & chips", 0, 1.4, 0.01, 0.7, "Embedded stone groups on bare ground.", Format: Percent),
        new("floor.drift", GroupGround, "Colour drift", 0, 3, 0.01, 1, "Broad geological colour drift across the landscape.", Format: Times),
        new("floor.paint", GroupGround, "Ground paint", 0, 1, 1, 1, "Stones, chips and meadow fibres on the ground.", Toggle: true),

        new("cap.hue", GroupRock, "Cap hue", -0.5, 0.5, 0.005, 0, "Hue turn of the rock tops.", Format: Turns),
        new("cap.sat", GroupRock, "Cap saturation", 0, 2, 0.01, 1, "Colour intensity of the rock tops.", Format: Percent),
        new("cap.value", GroupRock, "Cap brightness", 0.4, 1.8, 0.01, 1, "Brightness of the rock tops.", Format: Percent),
        new("wall.hue", GroupRock, "Cliff hue", -0.5, 0.5, 0.005, 0, "Hue turn of cliff faces, risers and chasm walls.", Format: Turns),
        new("wall.sat", GroupRock, "Cliff saturation", 0, 2, 0.01, 1, "Colour intensity of cliff faces.", Format: Percent),
        new("wall.value", GroupRock, "Cliff brightness", 0.4, 1.8, 0.01, 1, "Brightness of cliff faces.", Format: Percent),
        new("rock.warmth", GroupRock, "Rock warmth", -1, 1, 0.01, 0, "Warm/cool shift of all rock.", Format: Signed),
        new("rock.texture", GroupRock, "Rock texture", 0, 3, 0.01, 1, "Large variation and mineral grain of the rock.", Format: Times),
        new("rock.strata", GroupRock, "Strata", 0, 3, 0.01, 1, "Contrast of the rock beds and depth of their joints.", Format: Times),
        new("rock.moss", GroupRock, "Moss", 0, 1, 0.01, 0.7, "Moss patches at the foot of cliffs.", Format: Percent),
        new("rock.grass", GroupRock, "Crest grass", 0, 1, 0.01, 0.8, "Grass mantles on rock tops and fringes over the crests.", Format: Percent),
        new("rock.growth", GroupRock, "Growth coverage", -0.3, 0.3, 0.005, 0, "Shrinks or spreads the grassy mantles over the rock.", Format: Signed),
        new("rock.mountain", GroupRock, "Mountain strata", 0, 1, 1, 1, "Bedded strata, foot moss and crest greenery on the cliffs.", Toggle: true),

        new("pattern.broad", GroupPattern, "Ground field size", 0.25, 4, 0.01, 1, "Size of the broad ground patches.", Format: Times),
        new("pattern.brush", GroupPattern, "Brush stroke size", 0.25, 4, 0.01, 1, "Size of the painted brush strokes on the ground.", Format: Times),
        new("pattern.grain", GroupPattern, "Grain size", 0.25, 4, 0.01, 1, "Size of the fine ground grain.", Format: Times),
        new("pattern.stones", GroupPattern, "Stone size", 0.4, 3, 0.01, 1, "Size and spacing of stones and chips.", Format: Times),
        new("pattern.rock", GroupPattern, "Rock field size", 0.25, 4, 0.01, 1, "Size of the large rock variation.", Format: Times),
        new("pattern.mineral", GroupPattern, "Mineral grain size", 0.25, 4, 0.01, 1, "Size of the mineral grain in the rock.", Format: Times),
        new("pattern.beds", GroupPattern, "Bed thickness", 0.3, 3, 0.01, 1, "Thickness of the rock strata.", Format: Times),
        new("pattern.growth", GroupPattern, "Growth patch size", 0.25, 4, 0.01, 1, "Size of the moss and grass patches on rock.", Format: Times),

        new("foliage.hue", GroupVegetation, "Foliage hue", -0.5, 0.5, 0.005, 0, "Hue turn of leaves, crowns and flowers.", Format: Turns),
        new("foliage.sat", GroupVegetation, "Foliage saturation", 0, 2, 0.01, 1, "Colour intensity of foliage.", Format: Percent),
        new("foliage.value", GroupVegetation, "Foliage brightness", 0.4, 1.8, 0.01, 1, "Brightness of foliage.", Format: Percent),
        new("foliage.warmth", GroupVegetation, "Foliage warmth", -1, 1, 0.01, 0, "Warm (autumn) or cool shift of foliage.", Format: Signed),
        new("wind.strength", GroupVegetation, "Wind strength", 0, 3, 0.01, 1, "How far plants sway.", Format: Times),
        new("wind.tempo", GroupVegetation, "Wind tempo", 0.1, 3, 0.01, 1, "How fast plants sway.", Format: Times),
        new("wind.gusts", GroupVegetation, "Gusts", 0, 3, 0.01, 1, "Strength of travelling gust fronts.", Format: Times),

        new("water.hue", GroupWater, "Water hue", -0.5, 0.5, 0.005, 0, "Hue turn of shallow, deep water and foam.", Format: Turns),
        new("water.sat", GroupWater, "Water saturation", 0, 2, 0.01, 1, "Colour intensity of water.", Format: Percent),
        new("water.value", GroupWater, "Water brightness", 0.4, 1.8, 0.01, 1, "Brightness of water.", Format: Percent),
        new("water.depth", GroupWater, "Depth contrast", 0, 2, 0.01, 1, "How much darker deep water is than the shallows.", Format: Times),
        new("water.waves", GroupWater, "Wave speed", 0, 3, 0.01, 1, "Speed of the animated surface.", Format: Times),
        new("water.foam", GroupWater, "Foam", 0, 3, 0.01, 1, "Foam marks at banks and falls.", Format: Times),
        new("water.glint", GroupWater, "Glint", 0, 3, 0.01, 1, "Sun glint ribbons on the surface.", Format: Times),
        new("water.caustics", GroupWater, "Caustics", 0, 3, 0.01, 1, "Caustic light net in the shallows.", Format: Times),

        new("weather.wet", GroupWeather, "Wetness", 0, 1, 0.01, 0, "Wet, darkened ground with puddle sheen.", Format: Percent),
        new("weather.rain", GroupWeather, "Rain ripples", 0, 1, 0.01, 0, "Animated rain ripples on the ground.", Format: Percent),
        new("weather.snow", GroupWeather, "Snow cover", 0, 1, 0.01, 0, "Seasonal snow on upward-facing ground.", Format: Percent),

        new("light.sun", GroupLight, "Sun strength", 0, 2.5, 0.01, 1, "Energy of the sun (key light).", Format: Times),
        new("light.ambient", GroupLight, "Ambient light", 0, 2.5, 0.01, 1, "Energy of the sky light that fills the shadows.", Format: Times),
        new("light.softness", GroupLight, "Shadow softness", 0, 3, 0.01, 1, "Blur of the sun shadows.", Format: Times),
        new("ink.width", GroupLight, "Outline width", 0, 5, 0.05, 2.15, "Width of the comic outline around every form.", Format: "px"),

        new("form.organic", GroupForm, "Organic rock", 0, 1, 1, 1, "Rounded, weathered rock shapes with boulders instead of straight blocks.", Rebake: true, Toggle: true),
        new("form.relief", GroupForm, "Uneven ground", 0, 1, 1, 1, "Gentle relief on open floors.", Rebake: true, Toggle: true),
        new("form.vegetation", GroupForm, "3D vegetation", 0, 1, 1, 1, "Grown low-poly trees, bushes and meadow grass.", Rebake: true, Toggle: true),
        new("form.herbs", GroupForm, "Flowers & wall plants", 0, 1, 1, 1, "Wildflowers, ivy and ferns as real plants.", Rebake: true, Toggle: true),

        new("macro.season", GroupQuick, "Season", 0, 3, 0.01, 1, "Spring, summer, autumn or winter: foliage, meadows and snow together."),
    };

    public static readonly IReadOnlyDictionary<string, LookParam> ById = Params.ToDictionary(p => p.Id);

    /// <summary>The detailed groups (the macro controls are left out).</summary>
    public static IEnumerable<string> Groups => Params.Select(p => p.Group).Where(g => g != GroupQuick).Distinct();

    // ── macro controls ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Season keyframes (0 spring, 1 summer = the theme's own look, 2 autumn, 3 winter).</summary>
    private static readonly Dictionary<string, double>[] SeasonKeys =
    {
        new() { ["foliage.hue"] = 0.035, ["foliage.sat"] = 1.2, ["foliage.value"] = 1.12, ["floor.lush"] = 1.6, ["floor.dry"] = 0.55, ["color.warmth"] = -0.08, ["floor.value"] = 1.05 },
        new(),
        new() { ["foliage.hue"] = -0.1, ["foliage.warmth"] = 1, ["foliage.sat"] = 1.35, ["floor.warmth"] = 0.65, ["floor.hue"] = -0.04, ["floor.dry"] = 1.8, ["floor.lush"] = 0.5, ["rock.grass"] = 0.5, ["color.warmth"] = 0.3 },
        new() { ["weather.snow"] = 0.85, ["color.warmth"] = -0.4, ["color.sat"] = 0.75, ["foliage.sat"] = 0.5, ["foliage.value"] = 1.1, ["floor.lush"] = 0.4, ["rock.warmth"] = -0.3, ["light.ambient"] = 1.15 },
    };

    private static readonly string[] SeasonParams = SeasonKeys.SelectMany(k => k.Keys).Distinct().ToArray();

    /// <summary>Moves the season: every parameter a season touches is set between its two neighbouring keyframes.</summary>
    public void SetSeason(double season)
    {
        season = Math.Clamp(season, 0, 3);
        _values["macro.season"] = season;
        int a = Math.Min(2, (int)Math.Floor(season));
        double t = season - a;
        foreach (string id in SeasonParams)
        {
            double from = SeasonKeys[a].TryGetValue(id, out var va) ? va : ById[id].Default;
            double to = SeasonKeys[a + 1].TryGetValue(id, out var vb) ? vb : ById[id].Default;
            _values[id] = Math.Clamp(from + (to - from) * t, ById[id].Min, ById[id].Max);
        }
        Revision++;
    }

    public static string SeasonName(double season) => season switch
    {
        < 0.5 => "Spring",
        < 1.5 => "Summer",
        < 2.5 => "Autumn",
        _ => "Winter",
    };

    public const string WeatherClear = "clear", WeatherWet = "wet", WeatherRain = "rain", WeatherSnow = "snow";

    /// <summary>The weather the weather parameters describe.</summary>
    public string Weather => this["weather.snow"] > 0.4 ? WeatherSnow
        : this["weather.rain"] > 0.3 ? WeatherRain
        : this["weather.wet"] > 0.3 ? WeatherWet
        : WeatherClear;

    public void SetWeather(string weather)
    {
        (double wet, double rain, double snow) = weather switch
        {
            WeatherWet => (0.7, 0.0, 0.0),
            WeatherRain => (0.9, 0.8, 0.0),
            WeatherSnow => (0.0, 0.0, 0.85),
            _ => (0.0, 0.0, 0.0),
        };
        _values["weather.wet"] = wet;
        _values["weather.rain"] = rain;
        _values["weather.snow"] = snow;
        _values["light.sun"] = weather == WeatherRain ? 0.6 : weather == WeatherWet ? 0.85 : ById["light.sun"].Default;
        Revision++;
    }

    private readonly Dictionary<string, double> _values = new();

    /// <summary>Incremented on every change (live consumers compare it).</summary>
    public int Revision { get; private set; }

    public LookSettings()
    {
        foreach (var p in Params) _values[p.Id] = p.Default;
    }

    public double this[string id]
    {
        get => _values.TryGetValue(id, out var v) ? v : ById[id].Default;
        set
        {
            var p = ById[id];
            double clamped = Math.Clamp(value, p.Min, p.Max);
            if (_values.TryGetValue(id, out var old) && old == clamped) return;
            _values[id] = clamped;
            Revision++;
        }
    }

    public bool Flag(string id) => this[id] >= 0.5;

    public void CopyFrom(LookSettings other)
    {
        foreach (var (k, v) in other._values) _values[k] = v;
        Revision++;
    }

    /// <summary>Resets every live parameter (optionally one group) to its default; the form switches are kept.</summary>
    public void Reset(string? group = null)
    {
        foreach (var p in Params)
            if ((group == null && !p.Rebake) || p.Group == group) _values[p.Id] = p.Default;
        Revision++;
    }

    public bool IsDefault(string id) => Math.Abs(this[id] - ById[id].Default) < 1e-9;

    public static string FormatValue(LookParam p, double v) => p.Format switch
    {
        _ when p.Toggle => v >= 0.5 ? "on" : "off",
        Turns => $"{v * 360:+0;-0;0}°",
        Percent => $"{v * 100:0}%",
        Times => $"×{v.ToString("0.00", CultureInfo.InvariantCulture)}",
        Signed => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture),
        "deg" => $"{v:0}°",
        "px" => v.ToString("0.00", CultureInfo.InvariantCulture) + " px",
        _ => v.ToString("0.00", CultureInfo.InvariantCulture),
    };

    // ── presets ─────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record Preset(string Key, string Name, IReadOnlyDictionary<string, double> Values, int[] Swatch);

    public static readonly IReadOnlyList<Preset> Presets = new Preset[]
    {
        new("default", "Theme original", new Dictionary<string, double>(), new[] { 0x7fae5a, 0xb9a27a, 0x5f9fcf }),
        new("golden_autumn", "Golden autumn", new Dictionary<string, double>
        {
            ["foliage.hue"] = -0.1, ["foliage.warmth"] = 1, ["foliage.sat"] = 1.35, ["floor.warmth"] = 0.8, ["floor.hue"] = -0.045,
            ["floor.sat"] = 1.15, ["color.warmth"] = 0.35, ["floor.dry"] = 1.9, ["floor.lush"] = 0.45, ["rock.warmth"] = 0.45,
            ["wind.gusts"] = 1.6, ["rock.grass"] = 0.5, ["light.sun"] = 1.1,
        }, new[] { 0xd9892f, 0xb8542a, 0xe8c15a }),
        new("frost", "Frost morning", new Dictionary<string, double>
        {
            ["weather.snow"] = 0.85, ["color.warmth"] = -0.45, ["color.sat"] = 0.7, ["foliage.sat"] = 0.55, ["foliage.value"] = 1.1,
            ["water.hue"] = 0.03, ["water.value"] = 1.15, ["light.ambient"] = 1.2, ["rock.warmth"] = -0.4,
        }, new[] { 0xe9f1f7, 0x9fb8cc, 0x5d7a94 }),
        new("desert", "Sunbaked desert", new Dictionary<string, double>
        {
            ["floor.hue"] = -0.05, ["floor.warmth"] = 0.8, ["floor.dry"] = 2.6, ["floor.lush"] = 0.15, ["floor.stones"] = 1.1, ["floor.wear"] = 1.6,
            ["foliage.sat"] = 0.6, ["foliage.warmth"] = 0.5, ["rock.warmth"] = 0.75, ["cap.value"] = 1.15, ["light.sun"] = 1.3,
            ["water.hue"] = -0.04, ["rock.grass"] = 0.15, ["rock.moss"] = 0.1,
        }, new[] { 0xe0b46e, 0xc07a45, 0x8fa36b }),
        new("alien", "Alien bloom", new Dictionary<string, double>
        {
            ["floor.hue"] = 0.42, ["foliage.hue"] = 0.33, ["foliage.sat"] = 1.5, ["cap.hue"] = 0.2, ["wall.hue"] = 0.25,
            ["water.hue"] = -0.28, ["color.sat"] = 1.25, ["pattern.broad"] = 0.6, ["pattern.rock"] = 0.7, ["floor.lush"] = 2.2,
        }, new[] { 0x9b5fd6, 0x3fd3b0, 0xf06aa8 }),
        new("noir", "Ink noir", new Dictionary<string, double>
        {
            ["color.sat"] = 0.08, ["color.contrast"] = 1.45, ["ink.width"] = 3.2, ["rock.strata"] = 2.2, ["floor.grain"] = 1.8,
            ["light.ambient"] = 0.7, ["light.sun"] = 1.25,
        }, new[] { 0xe8e8e8, 0x8a8a8a, 0x2a2a2a }),
        new("pastel", "Pastel dream", new Dictionary<string, double>
        {
            ["color.sat"] = 0.7, ["color.value"] = 1.2, ["color.contrast"] = 0.75, ["foliage.hue"] = 0.06, ["ink.width"] = 1.4,
            ["light.ambient"] = 1.35, ["floor.grain"] = 0.6, ["rock.texture"] = 0.6, ["floor.stones"] = 0.3,
        }, new[] { 0xf4c6d6, 0xbfe3d0, 0xc9d4f5 }),
        new("tropic", "Lush tropics", new Dictionary<string, double>
        {
            ["floor.lush"] = 2.4, ["floor.dry"] = 0.3, ["foliage.sat"] = 1.45, ["foliage.hue"] = 0.03, ["water.hue"] = 0.05,
            ["water.sat"] = 1.4, ["water.caustics"] = 2, ["rock.moss"] = 1, ["rock.grass"] = 1, ["color.sat"] = 1.15,
        }, new[] { 0x3fb35a, 0x1fb5c9, 0xf2d15a }),
        new("storm", "Rain storm", new Dictionary<string, double>
        {
            ["weather.wet"] = 0.9, ["weather.rain"] = 0.8, ["color.sat"] = 0.75, ["color.value"] = 0.85, ["light.sun"] = 0.55,
            ["light.ambient"] = 1.1, ["wind.strength"] = 2.2, ["wind.gusts"] = 2.4, ["water.foam"] = 2,
        }, new[] { 0x56657a, 0x7e8f73, 0x3c4a5c }),
    };

    /// <summary>Applies a preset over the defaults (form switches are kept).</summary>
    public void ApplyPreset(Preset preset)
    {
        Reset();
        foreach (var (k, v) in preset.Values) _values[k] = Math.Clamp(v, ById[k].Min, ById[k].Max);
        Revision++;
    }

    /// <summary>
    /// A fresh, tasteful random look: harmonious hue turns (a shared base turn plus small family offsets), moderate
    /// saturation/value, rock that takes part in the colour turn, random pattern scales and material gains, occasional
    /// weather. Several candidates are rolled and the one that differs most from the current look and the last few
    /// shuffled ones wins, so pressing "Shuffle look" again always shows a clearly new picture rather than a near-repeat.
    /// </summary>
    public void Randomize(Random random, double wildness = 0.5)
    {
        var recent = new List<IReadOnlyDictionary<string, double>>(_shuffled) { new Dictionary<string, double>(_values) };
        Dictionary<string, double>? best = null;
        double bestDistance = -1;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Roll(random, wildness);
            double distance = recent.Min(look => LookDistance(look, candidate));
            if (distance > bestDistance) { best = candidate; bestDistance = distance; }
        }
        Reset();
        foreach (var (key, value) in best!) _values[key] = value;
        _shuffled.Add(best);
        if (_shuffled.Count > 3) _shuffled.RemoveAt(0);
        Revision++;
    }

    /// <summary>The last shuffled looks, kept apart from the next one.</summary>
    private readonly List<IReadOnlyDictionary<string, double>> _shuffled = new();

    /// <summary>One random look: the values that differ from the defaults.</summary>
    private static Dictionary<string, double> Roll(Random random, double wildness)
    {
        var v = new Dictionary<string, double>();
        double R(double spread) => (random.NextDouble() * 2 - 1) * spread;
        double w = Math.Clamp(wildness, 0, 1);
        double baseTurn = random.NextDouble() < 0.35 + 0.4 * w ? R(0.5 * w + 0.05) : R(0.04);
        v["floor.hue"] = Math.Clamp(baseTurn + R(0.06), -0.5, 0.5);
        v["foliage.hue"] = Math.Clamp(baseTurn + R(0.1 + 0.2 * w), -0.5, 0.5);
        v["cap.hue"] = Math.Clamp(baseTurn * 0.6 + R(0.05 + 0.1 * w), -0.5, 0.5);
        v["wall.hue"] = Math.Clamp(baseTurn * 0.6 + R(0.05 + 0.1 * w), -0.5, 0.5);
        // Rock is mostly low in colour, so a hue turn alone hardly shows on it: its saturation and brightness move too.
        v["cap.sat"] = Math.Clamp(1 + 0.25 * w + R(0.2 + 0.3 * w), 0.4, 1.9);
        v["wall.sat"] = Math.Clamp(1 + 0.2 * w + R(0.2 + 0.3 * w), 0.4, 1.9);
        v["cap.value"] = Math.Clamp(1 + R(0.06 + 0.1 * w), 0.8, 1.25);
        v["water.hue"] = Math.Clamp(R(0.08 + 0.25 * w), -0.5, 0.5);
        // Colour and contrast lean vivid: a pale, flat roll reads as washed out rather than as a look.
        v["color.sat"] = Math.Clamp(1.05 + R(0.3), 0.7, 1.7);
        v["foliage.sat"] = Math.Clamp(1 + R(0.4), 0.45, 1.8);
        v["color.warmth"] = R(0.5);
        v["foliage.warmth"] = R(0.7);
        v["rock.warmth"] = R(0.5 + 0.2 * w);
        v["color.contrast"] = Math.Clamp(1.05 + R(0.2), 0.85, 1.5);
        v["floor.lush"] = Math.Clamp(1 + R(0.9), 0, 3);
        v["floor.dry"] = Math.Clamp(1 + R(0.9), 0, 3);
        v["floor.turf"] = Math.Clamp(1 + R(0.6), 0, 2);
        v["floor.broad"] = Math.Clamp(1 + R(0.8), 0, 3);
        v["floor.grain"] = Math.Clamp(1 + R(0.6), 0.2, 2.5);
        v["floor.stones"] = Math.Clamp(0.7 + R(0.5), 0, 1.4);
        v["rock.texture"] = Math.Clamp(1 + R(0.6), 0.2, 2.5);
        v["rock.strata"] = Math.Clamp(1 + R(0.7), 0.1, 2.8);
        v["floor.drift"] = Math.Clamp(1 + R(0.8), 0, 3);
        v["pattern.broad"] = Math.Clamp(Math.Exp(R(0.8)), 0.3, 3.5);
        v["pattern.brush"] = Math.Clamp(Math.Exp(R(0.6)), 0.3, 3.5);
        v["pattern.rock"] = Math.Clamp(Math.Exp(R(0.8)), 0.3, 3.5);
        v["pattern.beds"] = Math.Clamp(Math.Exp(R(0.6)), 0.35, 2.8);
        v["rock.growth"] = R(0.15);
        v["rock.moss"] = random.NextDouble();
        v["rock.grass"] = random.NextDouble();
        v["water.caustics"] = Math.Clamp(1 + R(0.8), 0, 3);
        if (random.NextDouble() < 0.15 * (1 + w)) v["weather.snow"] = 0.4 + random.NextDouble() * 0.6;
        else if (random.NextDouble() < 0.15 * (1 + w)) v["weather.wet"] = 0.4 + random.NextDouble() * 0.6;
        return v;
    }

    /// <summary>How different two looks read: hue turns (on the colour wheel), vividness, warmth, greenery and snow.</summary>
    private static double LookDistance(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        double Get(IReadOnlyDictionary<string, double> v, string id) => v.TryGetValue(id, out double x) ? x : ById[id].Default;
        double Turn(string id)
        {
            double d = Math.Abs(Get(a, id) - Get(b, id)) % 1;
            return Math.Min(d, 1 - d);
        }
        double Diff(string id) => Math.Abs(Get(a, id) - Get(b, id));
        return Turn("floor.hue") * 3 + Turn("foliage.hue") * 2 + (Turn("cap.hue") + Turn("wall.hue")) * 0.75 + Turn("water.hue")
            + Diff("color.warmth") * 0.4 + Diff("color.sat") * 0.5 + (Diff("cap.sat") + Diff("wall.sat")) * 0.2
            + Math.Abs(Get(a, "floor.lush") - Get(a, "floor.dry") - Get(b, "floor.lush") + Get(b, "floor.dry")) * 0.12
            + Diff("weather.snow") * 0.6;
    }

    // ── JSON ────────────────────────────────────────────────────────────────────────────────────────────────

    public JsonObject ToJson()
    {
        var values = new JsonObject();
        foreach (var p in Params)
            if (!IsDefault(p.Id)) values[p.Id] = Math.Round(this[p.Id], 5);
        return new JsonObject { ["kind"] = "cartoon-terrain-studio.look", ["version"] = 1, ["values"] = values };
    }

    public static LookSettings FromJson(JsonNode? node)
    {
        var look = new LookSettings();
        if (node?["values"] is JsonObject values)
            foreach (var (key, value) in values)
                if (ById.ContainsKey(key) && value is JsonValue v && v.TryGetValue(out double d)) look[key] = d;
        return look;
    }
}
