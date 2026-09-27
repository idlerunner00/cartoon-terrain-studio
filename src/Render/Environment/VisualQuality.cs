// Port of packages/client/src/render/environment/visualQuality.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `currentSearch()` reads `globalThis.location?.search ?? ''`. The engine-free port has no browser location, so it
//   is always the empty query and every feature resolves to its shipping default (exactly what the original does in
//   any context without `location`). `new URLSearchParams(search).get(name)` is reproduced inline (same helper as
//   LookPreset.cs).
// * `Object.freeze` → a class whose instances are never mutated after construction.
using System;

namespace Fluitown.Render;

/// <summary>
/// Premium rendering features added on top of the stable HANDINK baseline.
///
/// The master query switch is intentionally public and coarse: `?visualquality=0` restores the previous
/// lighting/material/output behaviour after a reload without reverting source files. Individual audit switches
/// make regressions bisectable (`worldlights=0`, `materialfinish=0`, `filmfinish=0`, `previewparity=0`,
/// `groundform=0`, `focalframe=0`, `reliefaa=0`, `microaa=0`). The experimental broad terrain-shadow kernel remains
/// opt-in (`hqshadows=1`): its screen-space footprint leaks across vertical cliff silhouettes at distant zoom
/// and shimmers while the snapped shadow projection moves.
/// </summary>
public sealed class VisualQualityFeatures
{
    public bool enabled;
    public bool worldLights;
    public bool semanticMaterials;
    public bool filmFinish;
    public bool previewParity;
    public bool highQualityShadows;
    /// <summary>Actors/pets/monsters/statues projecting their real sun shadow onto the terrain (`actorgroundshadows=0`
    ///  falls back to the pooled ink contact disc, which is the A/B this feature replaced).</summary>
    public bool actorGroundShadows;
    public bool visualGrounding;
    /// <summary>
    /// Per-pixel ground/wall aggregate grain. `?groundgrain=0` removes it in one reload.
    ///
    /// It exists because it is opt-OUT that a detail layer this camera-sensitive needs: the same grain that
    /// reads as soil at gameplay zoom became a full-screen noise blanket in the map generator and from a
    /// lifted balloon, and a bisectable switch is what turns that from an outage into a question.
    /// </summary>
    public bool groundGrain;
    /// <summary>
    /// Fade the derivative relief bump (and its tone) out on the shared world footprint. `?reliefaa=0` restores
    /// the un-faded term in one reload.
    ///
    /// It is opt-OUT for the same reason `groundGrain` is: the layer is camera-sensitive, and this is the switch
    /// that makes the difference measurable rather than arguable. With it off, a fragment that covers more world
    /// than the height field's finest octave gets a random lighting normal — i.e. a random SUNLIT pixel — which
    /// is the zoomed-out white speckle on trees, bushes and trunks.
    /// </summary>
    public bool reliefAntialias;
    /// <summary>Object-space LOD for animated facets that project below one pixel; prevents temporal blossom/moss glitter.</summary>
    public bool microGeometryAntialias;
    /// <summary>Player-centred exposure/chroma pool in the final grade — the "brightest point is the hero" rule.</summary>
    public bool focalFrame;
}

public static partial class VisualQuality
{
    public static VisualQualityFeatures resolveVisualQualityFeatures(string? search = null)
    {
        search ??= currentSearch();
        bool enabled = urlSearchParamsGet(search, "visualquality") != "0";
        bool feature(string name) => enabled && urlSearchParamsGet(search, name) != "0";
        bool optInFeature(string name) => enabled && urlSearchParamsGet(search, name) == "1";
        return new VisualQualityFeatures
        {
            enabled = enabled,
            worldLights = feature("worldlights"),
            semanticMaterials = feature("materialfinish"),
            filmFinish = feature("filmfinish"),
            previewParity = feature("previewparity"),
            highQualityShadows = optInFeature("hqshadows"),
            actorGroundShadows = feature("actorgroundshadows"),
            visualGrounding = feature("groundform"),
            groundGrain = feature("groundgrain"),
            reliefAntialias = feature("reliefaa"),
            microGeometryAntialias = feature("microaa"),
            focalFrame = feature("focalframe"),
        };
    }

    /// <summary>`globalThis.location?.search ?? ''` — no browser location in the engine-free port.</summary>
    private static string currentSearch()
    {
        return "";
    }

    /// <summary>
    /// `new URLSearchParams(search).get(name)`: strip one leading "?", split on "&amp;", split each pair at its first
    /// "=", decode "+" as space and percent escapes, and return the first value whose decoded name matches (or null).
    /// </summary>
    private static string? urlSearchParamsGet(string search, string name)
    {
        string input = search.StartsWith("?", StringComparison.Ordinal) ? search.Substring(1) : search;
        foreach (string sequence in input.Split('&'))
        {
            if (sequence.Length == 0) continue;
            int eq = sequence.IndexOf('=');
            string rawName = eq >= 0 ? sequence.Substring(0, eq) : sequence;
            string rawValue = eq >= 0 ? sequence.Substring(eq + 1) : "";
            if (urlFormDecode(rawName) == name) return urlFormDecode(rawValue);
        }
        return null;
    }

    private static string urlFormDecode(string value)
    {
        return Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    public static readonly VisualQualityFeatures VISUAL_QUALITY = resolveVisualQualityFeatures();
}
