using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrainStudio.Core;

/// <summary>The themes the studio offers, with short display names and a colour strip for choice cards.</summary>
public static class ThemeCatalog
{
    public sealed record Entry(string Key, string Name, int[] Colours);

    private static IReadOnlyList<Entry>? _all;

    public static IReadOnlyList<Entry> All => _all ??= GeneratorArtifacts.GENERATOR_THEME_OPTIONS.Select(o => Make(o.key)).ToList();

    public static Entry? Find(string key) => All.FirstOrDefault(e => e.Key == key);

    public static string Name(string key) => Find(key)?.Name ?? Pretty(key);

    private static Entry Make(string key)
    {
        var biome = Fluitown.Render.Theme.biomeForKey(key);
        var ground = Fluitown.Render.Theme.elevationOf(biome);
        var rock = Fluitown.Render.Theme.terrainOf(biome);
        int Level(int[] ramp, int min, int level) => ramp[Math.Clamp(level - min, 0, ramp.Length - 1)];
        int water = Fluitown.Render.Theme.floodColors(biome).surface;
        return new Entry(key, Pretty(key), new[]
        {
            Level(ground.topFill, ground.minLevel, 0), Level(ground.topFill, ground.minLevel, 6), rock.wall, water,
        });
    }

    private static string Pretty(string key)
    {
        if (key == "mountain") return "Classic Mountain";
        string name = Fluitown.Render.Theme.biomeForKey(key).name ?? "";
        if (name.Length == 0) name = GeneratorArtifacts.runThemeLabel(key);
        foreach (string suffix in new[] { " Endrun", " Run" })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        if (name.StartsWith("The ", StringComparison.Ordinal)) name = name[4..];
        return name;
    }
}
