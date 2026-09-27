using System;

namespace TerrainStudio.Core;

/// <summary>Readable random seeds ("misty-harbour-417"): easy to remember, say and share.</summary>
public static class Seeds
{
    private static readonly string[] Adjectives =
    {
        "amber", "misty", "sunny", "hidden", "silver", "mossy", "windy", "golden", "quiet", "wild", "frosty", "crimson",
        "velvet", "lucky", "ancient", "copper", "dusky", "emerald", "gentle", "hollow", "ivory", "jade", "lonely", "mellow",
        "north", "olive", "pale", "rusty", "sleepy", "tangled", "violet", "whispering", "azure", "bright", "cloudy", "drifting",
    };

    private static readonly string[] Nouns =
    {
        "vale", "ridge", "harbour", "grove", "peaks", "meadow", "canyon", "isles", "hollow", "terrace", "falls", "steppe",
        "delta", "summit", "brook", "cove", "dunes", "fjord", "glade", "heath", "lagoon", "marsh", "mesa", "moor",
        "oasis", "pass", "reef", "spire", "tarn", "thicket", "upland", "valley", "wold", "crag", "basin", "bluff",
    };

    private static readonly Random Shared = new();

    /// <summary>The seed the next <see cref="Random"/> call hands out instead of a rolled one (scripted recordings pick
    /// their worlds this way); used once.</summary>
    public static string? Next { get; set; }

    public static string Random(Random? random = null)
    {
        if (Next is { } next)
        {
            Next = null;
            return next;
        }
        var r = random ?? Shared;
        return $"{Adjectives[r.Next(Adjectives.Length)]}-{Nouns[r.Next(Nouns.Length)]}-{r.Next(10, 1000)}";
    }
}
