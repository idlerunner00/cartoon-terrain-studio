// Port of packages/client/src/render/environment/terrainGeometryDetail.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

// `export type TerrainGeometryDetail = 'near' | 'overview';` → string ("near" | "overview").

public sealed class TerrainGeometryDetailProfile
{
    public double vegetationBladeCap;
    public double cliffDressingDensity;
}

public static partial class TerrainGeometryDetailModule
{
    /// <summary>Enter overview only after details become sub-pixel; leave it later to prevent rebuild flapping at the edge.</summary>
    private const double OVERVIEW_ENTER_CELL_PX = 20;
    private const double OVERVIEW_EXIT_CELL_PX = 26;

    /// <param name="previous">TerrainGeometryDetail: "near" | "overview".</param>
    /// <returns>TerrainGeometryDetail: "near" | "overview".</returns>
    public static string terrainGeometryDetailForProjectedCell(double projectedCellCssPx, string previous)
    {
        double cellPx = Number.isFinite(projectedCellCssPx) ? Math.max(0, projectedCellCssPx) : 0;
        if (previous == "overview") return cellPx < OVERVIEW_EXIT_CELL_PX ? "overview" : "near";
        return cellPx <= OVERVIEW_ENTER_CELL_PX ? "overview" : "near";
    }

    /// <param name="detail">TerrainGeometryDetail: "near" | "overview".</param>
    public static TerrainGeometryDetailProfile terrainGeometryDetailProfile(
        string detail,
        double vegetationBladeCap,
        double cliffDressingDensity)
    {
        if (detail == "near")
            return new TerrainGeometryDetailProfile
            {
                vegetationBladeCap = vegetationBladeCap,
                cliffDressingDensity = cliffDressingDensity,
            };
        // Four blades retain the complete outer vegetation/tree silhouette while removing internal scatter that is
        // smaller than one screen pixel. The same rule is already the renderer's proven compact hardware tier.
        return new TerrainGeometryDetailProfile
        {
            vegetationBladeCap = Math.min(vegetationBladeCap, 4),
            cliffDressingDensity = Math.min(cliffDressingDensity, 0.3),
        };
    }

    /// <summary>Keep near hashes byte-identical; namespace overview payloads so both immutable variants can be validated.</summary>
    /// <param name="contentHash">A JS number (a uint32 hash).</param>
    /// <param name="detail">TerrainGeometryDetail: "near" | "overview".</param>
    public static double terrainContentHashForDetail(double contentHash, string detail)
    {
        return detail == "near" ? contentHash : (double)Js.ToUint32(Js.ToInt32(contentHash) ^ 0x7f4a7c15);
    }
}
