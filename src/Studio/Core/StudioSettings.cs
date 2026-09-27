using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrainStudio.Core;

/// <summary>What the studio remembers between sessions (user data folder, <c>settings.json</c>).</summary>
public sealed class StudioSettings
{
    public string Mode = "paint";
    public string Quality = "auto";
    public double Day = 0.5;
    public bool Animate = true;
    public bool ShowWelcome = true;
    public bool StyleOpen;
    /// <summary>The theme of the open world, and the theme choice of the Explore panel ("*any" rolls one per seed).</summary>
    public string WorldTheme = "*world";
    public string WorldThemeChoice = "*world";
    public string WorldSeed = "world";
    public bool WorldRegions = true;
    public double WorldX, WorldY;
    public List<(string name, string theme, string seed, double x, double y)> Bookmarks = new();
    public List<string> RecentFiles = new();
    public List<(string seed, string theme)> RecentSeeds = new();
    public JsonNode? Look;
    public JsonNode? Generator;

    public static string Folder { get; set; } = "";
    private static string PathOf => System.IO.Path.Combine(Folder, "settings.json");

    public static StudioSettings Load()
    {
        var settings = new StudioSettings();
        try
        {
            if (!File.Exists(PathOf)) return settings;
            var root = JsonNode.Parse(File.ReadAllText(PathOf)) as JsonObject;
            if (root == null) return settings;
            settings.Mode = (string?)root["mode"] ?? settings.Mode;
            settings.Quality = (string?)root["quality"] ?? settings.Quality;
            settings.Day = (double?)root["day"] ?? settings.Day;
            settings.Animate = (bool?)root["animate"] ?? true;
            settings.ShowWelcome = (bool?)root["showWelcome"] ?? true;
            settings.StyleOpen = (bool?)root["styleOpen"] ?? false;
            settings.WorldTheme = (string?)root["worldTheme"] ?? settings.WorldTheme;
            settings.WorldThemeChoice = (string?)root["worldThemeChoice"] ?? settings.WorldThemeChoice;
            settings.WorldSeed = (string?)root["worldSeed"] ?? settings.WorldSeed;
            settings.WorldRegions = (bool?)root["worldRegions"] ?? true;
            settings.WorldX = (double?)root["worldX"] ?? 0;
            settings.WorldY = (double?)root["worldY"] ?? 0;
            if (root["bookmarks"] is JsonArray marks)
                foreach (var m in marks)
                    if (m is JsonObject o)
                        settings.Bookmarks.Add(((string?)o["name"] ?? "Place", (string?)o["theme"] ?? "*world", (string?)o["seed"] ?? "world",
                            (double?)o["x"] ?? 0, (double?)o["y"] ?? 0));
            if (root["recentSeeds"] is JsonArray seeds)
                foreach (var entry in seeds)
                    if (entry is JsonObject o && (string?)o["seed"] is { } seed)
                        settings.RecentSeeds.Add((seed, (string?)o["theme"] ?? "*world"));
            if (root["recent"] is JsonArray recent)
                foreach (var r in recent)
                    if ((string?)r is { } file) settings.RecentFiles.Add(file);
            settings.Look = root["look"]?.DeepClone();
            settings.Generator = root["generator"]?.DeepClone();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"studio settings unreadable, starting fresh: {e.Message}");
        }
        return settings;
    }

    public void Save()
    {
        try
        {
            var marks = new JsonArray();
            foreach (var (name, theme, seed, x, y) in Bookmarks)
                marks.Add(new JsonObject { ["name"] = name, ["theme"] = theme, ["seed"] = seed, ["x"] = Math.Round(x), ["y"] = Math.Round(y) });
            var recent = new JsonArray();
            foreach (var file in RecentFiles) recent.Add(file);
            var seeds = new JsonArray();
            foreach (var (seed, theme) in RecentSeeds) seeds.Add(new JsonObject { ["seed"] = seed, ["theme"] = theme });
            var root = new JsonObject
            {
                ["mode"] = Mode,
                ["quality"] = Quality,
                ["day"] = Math.Round(Day, 4),
                ["animate"] = Animate,
                ["showWelcome"] = ShowWelcome,
                ["styleOpen"] = StyleOpen,
                ["worldTheme"] = WorldTheme,
                ["worldThemeChoice"] = WorldThemeChoice,
                ["worldSeed"] = WorldSeed,
                ["worldRegions"] = WorldRegions,
                ["worldX"] = Math.Round(WorldX),
                ["worldY"] = Math.Round(WorldY),
                ["bookmarks"] = marks,
                ["recent"] = recent,
                ["recentSeeds"] = seeds,
                ["look"] = Look?.DeepClone(),
                ["generator"] = Generator?.DeepClone(),
            };
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathOf, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"studio settings not saved: {e.Message}");
        }
    }

    public void RememberFile(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 8) RecentFiles.RemoveRange(8, RecentFiles.Count - 8);
    }
}
