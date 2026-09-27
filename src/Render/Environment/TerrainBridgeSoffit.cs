// Port of packages/client/src/render/environment/terrainBridgeSoffit.ts — keep in lockstep with the original.
//
// The plank bed under a Bridge deck — the one surface that makes a crossing a CLOSED cell.
//
// Every other terrain cell caps a volume that closes itself: a Floor's faces run down to its neighbours, a
// Solid is a column, Water owns a basin. A deck is the exception. It is a thin suspended slab over Water, a
// shaft or (fail-closed) ordinary ground, and its cap is laid as REAL timber — separate boards with a real
// geometric gap between them (`buildBridgePlanks`). Without a bed behind those boards every gap was a slit
// straight through the world, and the camera looking under the deck found nothing at all: the grey backdrop,
// reported as "grey background showing through the bridge".
//
// The bed therefore sits at the modelled deck underside — `TerrainCell.baseZ`, the same datum the deck fascia
// already closes the sides down to — and is emitted for every span, because "what is under this deck" may
// never answer "nothing". It is deliberately a separate module from the geometry compiler: the closure is a
// property of decks, not of one compiler pass, and the compiler is long past its size budget.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Render.Palette;

namespace Fluitown.Render;

public sealed class BridgeSoffitInput
{
    public SuspensionSurfaceBuilder builder;
    public double x0;
    public double x1;
    public double z0;
    public double z1;
    /// <summary>Exact deck-cap footprint. Omitted only for the ordinary square fast path.</summary>
    public IReadOnlyList<SuspensionPoint>? footprint;
    /// <summary>World-px height of the deck underside — `TerrainCell.baseZ` in elevation pixels.</summary>
    public double undersideY;
    /// <summary>World-px height of the visible boards. A near-coplanar substrate closes their decorative seams.</summary>
    public double deckY;
    public SuspensionBridgeMaterial material;
    public int surfaceKind;
}

public static partial class TerrainBridgeSoffit
{
    /// <summary>Timber seen edge-on in shadow: darker than the boards above it, still plainly the same wood.</summary>
    private const double BED_SIDE_BLEND = 0.26;
    /// <summary>The bed is shaded like an underside, never like a lit cap; slightly deeper toward the far edge.</summary>
    private static readonly double[] BED_SHADE = { 0.6, 0.6, 0.54, 0.54 };
    /// <summary>Dark timber immediately below the boards: visible only through their narrow authored joints.</summary>
    private static readonly double[] SEAM_BED_SHADE = { 0.76, 0.76, 0.72, 0.72 };

    // Mutable module scratch → one instance per compiler thread (see RENDER_AGENT_BRIEF "Thread safety").
    [ThreadStatic] private static SuspensionPoint[]? _SOFFIT_POINTS;
    private static SuspensionPoint[] SOFFIT_POINTS => _SOFFIT_POINTS ??= createSoffitPoints();
    [ThreadStatic] private static List<SuspensionPoint>? _SOFFIT_POLYGON;
    private static List<SuspensionPoint> SOFFIT_POLYGON => _SOFFIT_POLYGON ??= new List<SuspensionPoint>();

    private static SuspensionPoint[] createSoffitPoints()
    {
        var points = new SuspensionPoint[20];
        for (int index = 0; index < points.Length; index++) points[index] = new SuspensionPoint { x = 0, y = 0, z = 0 };
        return points;
    }

    public static void addBridgeDeckSoffit(BridgeSoffitInput input)
    {
        SuspensionSurfaceBuilder builder = input.builder;
        double x0 = input.x0;
        double x1 = input.x1;
        double z0 = input.z0;
        double z1 = input.z1;
        IReadOnlyList<SuspensionPoint>? footprint = input.footprint;
        double undersideY = input.undersideY;
        double deckY = input.deckY;
        SuspensionBridgeMaterial material = input.material;
        int surfaceKind = input.surfaceKind;
        void addBed(double y, int color, double[] shade)
        {
            IReadOnlyList<SuspensionPoint> points;
            if (footprint != null && footprint.Count >= 3)
            {
                List<SuspensionPoint> polygon = SOFFIT_POLYGON;
                SuspensionPoint[] storage = SOFFIT_POINTS;
                polygon.Clear();
                for (int index = 0; index < footprint.Count; index++)
                {
                    SuspensionPoint source = footprint[index];
                    SuspensionPoint target = storage[index];
                    target.x = source.x;
                    target.y = y;
                    target.z = source.z;
                    polygon.push(target);
                }
                points = polygon;
            }
            else
            {
                points = new[]
                {
                    new SuspensionPoint { x = x0, y = y, z = z0 },
                    new SuspensionPoint { x = x1, y = y, z = z0 },
                    new SuspensionPoint { x = x1, y = y, z = z1 },
                    new SuspensionPoint { x = x0, y = y, z = z1 },
                };
            }
            builder.addSurface(points, 0, 1, 0, color, surfaceKind, 0.1, shade);
        }

        // Plank gaps are inked carpentry joints, not holes through the deck. At gameplay pitch an underside at the
        // physical slab bottom projects away from a top-plane slit and leaves a large black rectangle. This complete
        // near-coplanar substrate keeps every decorative gap a thin dark seam.
        addBed(deckY - 0.035, material.edgeDark, SEAM_BED_SHADE);
        addBed(undersideY, mix(material.edgeDark, material.side, BED_SIDE_BLEND), BED_SHADE);
    }
}
