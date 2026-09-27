// Port of packages/client/src/render/environment/terrainLighting.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

// `export type TerrainEdgeDirection = 'north' | 'south' | 'west' | 'east';` → string. No C# type is declared:
// the name is also the (different, 'n'|'e'|'s'|'w') Fluitown.Domain.TerrainEdgeDirection literal class.
// `export type TerrainShadowBand = 'outer' | 'inner';` → string.

public sealed class TerrainEdgeLighting
{
    /// <summary>TerrainEdgeDirection of this module: "north" | "south" | "west" | "east".</summary>
    public string direction;
    /// <summary>0 = away from key light, 1 = facing the north-west key light.</summary>
    public double light;
    /// <summary>Receiving-surface cast shadow strength.</summary>
    public double cast;
    /// <summary>Tight contact darkening where a higher cell meets lower ground.</summary>
    public double contact;
    /// <summary>Vertical side-face darkness.</summary>
    public double face;
    /// <summary>Thin lit crest strength on light-facing edges.</summary>
    public double rim;
}

public static partial class TerrainLighting
{
    /// <summary>`Record&lt;TerrainEdgeDirection, { x; y; z }&gt;`; lookup only.</summary>
    private static readonly Dictionary<string, (double x, double y, double z)> EDGE_NORMAL = new()
    {
        ["north"] = (0, -0.82, 0.58),
        ["west"] = (-0.82, 0, 0.5),
        ["south"] = (0, 0.86, 0.28),
        ["east"] = (0.86, 0, 0.22),
    };

    private static readonly (double x, double y, double z) KEY_LIGHT =
        normalize(RenderWorldLighting.LIGHT_DIR.x, RenderWorldLighting.LIGHT_DIR.y, 0.38);

    private static readonly Dictionary<string, double> CAST_BIAS = new()
    {
        ["north"] = 0.05,
        ["west"] = 0.16,
        ["south"] = 0.82,
        ["east"] = 0.62,
    };

    private static readonly Dictionary<string, double> CONTACT_BIAS = new()
    {
        ["north"] = 0.34,
        ["west"] = 0.38,
        ["south"] = 0.7,
        ["east"] = 0.62,
    };

    private static readonly Dictionary<string, double> RIM_BIAS = new()
    {
        ["north"] = 0.78,
        ["west"] = 0.66,
        ["south"] = 0.2,
        ["east"] = 0.12,
    };

    private static (double x, double y, double z) normalize(double x, double y, double z)
    {
        double h = Math.hypot(x, y, z);
        double l = Js.Truthy(h) ? h : 1;
        return (x / l, y / l, z / l);
    }

    private static double clamp01(double v)
    {
        return v <= 0 ? 0 : v >= 1 ? 1 : v;
    }

    private static double dropWeight(double drop)
    {
        return clamp01((drop - 0.35) / 5.65);
    }

    /// <summary>
    /// Standard fake-3D terrain lighting contract.
    ///
    /// Direction is from the high cell toward the lower receiving side. The model is deliberately pure data:
    /// tile/elevation/connection rules decide whether an edge exists; this helper only says how that physical edge
    /// is lit by the global north-west key light.
    /// </summary>
    /// <param name="direction">TerrainEdgeDirection: "north" | "south" | "west" | "east".</param>
    public static TerrainEdgeLighting terrainEdgeLighting(string direction, double drop, bool climbable = false)
    {
        var n = EDGE_NORMAL[direction];
        double dot = n.x * KEY_LIGHT.x + n.y * KEY_LIGHT.y + n.z * KEY_LIGHT.z;
        double light = clamp01(0.45 + dot * 0.62);
        double shade = 1 - light;
        double height = dropWeight(drop);
        double climbScale = climbable ? 0.42 : 1;
        double cast = clamp01((CAST_BIAS[direction] * (0.36 + height * 0.74) + shade * 0.12) * climbScale);
        double contact = clamp01((CONTACT_BIAS[direction] * (0.42 + height * 0.58) + shade * 0.14) * (climbable ? 0.52 : 1));
        double face = clamp01((shade * 0.72 + height * 0.34 + (direction == "east" || direction == "south" ? 0.18 : 0)) * (climbable ? 0.72 : 1));
        double rim = clamp01(RIM_BIAS[direction] * (0.64 + light * 0.5) * (climbable ? 0.72 : 1));
        return new TerrainEdgeLighting
        {
            direction = direction,
            light = light,
            cast = cast,
            contact = contact,
            face = face,
            rim = rim,
        };
    }

    /// <param name="direction">TerrainEdgeDirection: "north" | "south" | "west" | "east".</param>
    public static double terrainContactShadowWidthPx(string direction, double drop, double tileSize, bool climbable)
    {
        var lighting = terrainEdgeLighting(direction, drop, climbable);
        if (lighting.contact < 0.08) return 0;
        return Math.min(tileSize * 0.18, tileSize * (0.035 + lighting.contact * 0.16));
    }

    /// <param name="direction">TerrainEdgeDirection: "north" | "south" | "west" | "east".</param>
    public static double terrainContactShadowAlpha(string direction, double drop, bool climbable)
    {
        var lighting = terrainEdgeLighting(direction, drop, climbable);
        return Math.min(0.14, lighting.contact * 0.11);
    }
}
