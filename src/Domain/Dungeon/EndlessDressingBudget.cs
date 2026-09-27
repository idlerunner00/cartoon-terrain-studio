// Port of packages/shared/src/domain/dungeon/endlessDressingBudget.ts — keep in lockstep with the original.
using System;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * **How much** dressing a chunk gets, and **where it is allowed to stand**.
 *
 * ## What was wrong
 *
 * Two independent defects produced one symptom — an empty combat floor with a treeline around it.
 *
 * **The budget was a constant.** It read `round(density · 1024 / 1000) + 6`, a pure function of the theme, so
 * every chunk in the world received the same number of props: measured over 64 `highland_pass` chunks,
 * `min = 63, median = 63, max = 63, sd = 0.00`. Over the same 64 chunks the terrain itself varied enormously
 * — solid rock from 37 to 663 cells, water from 0 to 437 — so a chunk that was two thirds cliff and a chunk
 * that was one open plain were dressed with exactly the same 63 objects. A world cannot read as a place when
 * its density is a constant: there is no thicket to come out of and no clearing to arrive at, only one
 * uniform sparseness stretched over an infinite plane.
 *
 * **The lane rule had eaten the world.** A prop was refused on any cell of `routeMask` that was not a
 * shoulder. That rule was written for *the trail* — one legible line through the map — but `routeMask` marks
 * the entire carved corridor network, and the corridor network is **77.7 % of all walkable ground**. So three
 * quarters of the floor was structurally undressable, and the only cells that survived were the ones touching
 * rock or water. The measurement is unambiguous: a cell one step from rock carried a prop every **6.5** tiles,
 * open ground every **42.4**, and the combat plate four or more steps out every **46.0**. Nine visual stands
 * on a full desktop screen, of which four stood on unreachable cliff tops.
 *
 * ## The rule
 *
 * Density follows AREA and varies between chunks: the budget is derived from how much dressable ground a chunk
 * actually offers, then multiplied by a low-frequency world field so woodland and clearing are *composed*
 * across chunk boundaries instead of being re-rolled per chunk. And only the authored artery is kept clear —
 * the same `primaryRouteMask` the floor pigment already treats as the one real path. The rest of the network
 * is ordinary ground and is dressed like ordinary ground.
 */

public sealed class EndlessDressableArea
{
    /// <summary>Chebyshev distance to the nearest non-walkable cell, per chunk cell; 0 on the non-walkable cells.</summary>
    public short[] depth = Array.Empty<short>();
    /// <summary>Walkable cells one step from rock, water or void — the bank, the shoulder, the treeline's foot.</summary>
    public int rim;
    /// <summary>Walkable cells at ENDLESS_PLATE_DEPTH or more — the open floor a squad actually fights on.</summary>
    public int plate;
    /// <summary>Solid cells eligible to carry a prop.</summary>
    public int rock;
}

public static partial class EndlessDressingBudget
{
    /// <summary>How far from rock/water/void a walkable cell must be before it counts as the open combat plate.</summary>
    public const int ENDLESS_PLATE_DEPTH = 2;

    /// <summary>
    /// Lattice of the density field, in tiles.
    ///
    /// Roughly three chunks: a stand of woodland is bigger than the chunk it starts in, and a clearing is a place
    /// you cross rather than a tile you step over. Anything near the 32-tile chunk size would put the composition
    /// back on the streaming grid, which is precisely the artefact a world field exists to avoid.
    /// </summary>
    public const double ENDLESS_DENSITY_CELL = 96;
    /// <summary>Multiplier at the emptiest end of the density field, and at the densest.</summary>
    public const double ENDLESS_DENSITY_MIN = 0.52;
    public const double ENDLESS_DENSITY_MAX = 1.74;

    private const double CELLS_PER_MILLE = 1000;

    /// <summary>
    /// A theme's `density` is authored as props per 1000 **terrain** cells — the unit it has always been stated in,
    /// and the unit its registry comment documents. This pass spends it per 1000 **dressable** cells instead, and
    /// dressable ground measures 58 % of a chunk, so the theme's authored intent is restored by dividing by that
    /// fraction. Without it every world would silently lose two fifths of the density its data asks for.
    /// </summary>
    private const double ENDLESS_AREA_NORMALISATION = 1 / 0.584;

    /// <summary>
    /// The density ruling, on top of the theme's restored intent.
    ///
    /// The retired pass put 9.2 visual stands on a full desktop screen and the owner's review of that frame was
    /// that the world reads bare. This is the single number that answers it — kept apart from the normalisation
    /// above so "what the theme asked for" and "how much more the world should carry" never get confused for each
    /// other again.
    ///
    /// It is deliberately modest, because the count was never the main defect. The owner-authored Hub — the
    /// declared quality bar — carries 2510 props in 49152 cells, i.e. **5.1 %** coverage, and the retired endless
    /// pass already sat at 6.2 %. What made the run read bare next to the Hub was WHERE those props went: 44.8 % of
    /// them stood on unreachable rock and the open combat plate received one per 46 tiles. Fixing the distribution
    /// is worth several times what raising the count is, so the count rises by roughly half and the rest of the
    /// work is done by the plate quota and the artery rule.
    /// </summary>
    public const double ENDLESS_DRESSING_APPETITE = 1.6;

    /// <summary>
    /// Ceiling per chunk. It is a compile/render budget, not a composition knob: at the dense end of the density
    /// field a chunk that is one continuous open plain would otherwise ask for close to three hundred records in a
    /// single streamed payload.
    /// </summary>
    public const int ENDLESS_DRESSING_CHUNK_CAP = 230;

    /// <summary>
    /// Rock counts less than floor. A cliff top carries a treeline that reads at a glance and needs only a few
    /// silhouettes to do it, whereas open ground has to be furnished across its whole area — and, measured, 44.8 %
    /// of the entire world budget was being spent on rock the player cannot reach.
    /// </summary>
    private const double ROCK_AREA_WEIGHT = 0.45;

    /// <summary>
    /// Measure a chunk's dressable ground and its openness in one pass.
    ///
    /// The depth field is a multi-source BFS from every non-walkable cell, so it is exact rather than sampled, and
    /// it is computed once and handed to the placement pass — the plate quota, the open-field skeleton and the
    /// per-cell composition all need the same answer and must not each estimate their own.
    /// </summary>
    public static EndlessDressableArea measureEndlessDressableArea(byte[] tiles, int width)
    {
        var depth = new short[tiles.Length];
        var queue = new int[tiles.Length];
        int tail = 0;
        int rock = 0;
        for (int index = 0; index < tiles.Length; index++)
        {
            int tile = tiles[index];
            if (isWalkable(tile))
            {
                depth[index] = -1;
                continue;
            }
            depth[index] = 0;
            queue[tail++] = index;
            if (tile == TileType.Solid) rock++;
        }
        for (int head = 0; head < tail; head++)
        {
            int index = queue[head];
            int x = index % width;
            int y = index / width;
            int next = depth[index] + 1;
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= width) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx;
                    if (nx < 0 || nx >= width) continue;
                    int neighbour = ny * width + nx;
                    if (depth[neighbour] != -1) continue;
                    depth[neighbour] = (short)next;
                    queue[tail++] = neighbour;
                }
            }
        }
        int rim = 0;
        int plate = 0;
        for (int index = 0; index < depth.Length; index++)
        {
            int value = depth[index];
            if (value <= 0) continue;
            if (value >= ENDLESS_PLATE_DEPTH) plate++;
            else rim++;
        }
        return new EndlessDressableArea { depth = depth, rim = rim, plate = plate, rock = rock };
    }

    /// <summary>
    /// The low-frequency density field: how thickly THIS part of the world is dressed, 0.52 .. 1.74.
    ///
    /// Two octaves of the shared value noise, both far larger than a chunk, so the answer a chunk gets agrees with
    /// its neighbours' and a stand of woodland or a bare flat spans several of them. This is the single mechanism
    /// that turns a uniform sparseness into composition, and it is a pure function of world coordinates, so both
    /// sides of every immutable seam derive the same value.
    /// </summary>
    public static double endlessDressingDensityAt(double seed, double worldX, double worldY)
    {
        double body = valueNoise(Js.ToUint32(Js.ToInt32(seed) ^ 0x5bf03635), worldX, worldY, ENDLESS_DENSITY_CELL);
        double drift = valueNoise(
            Js.ToUint32(Js.ToInt32(seed) ^ unchecked((int)0x9e3779b9)),
            worldX + 53,
            worldY - 87,
            ENDLESS_DENSITY_CELL * 2.6);
        // The drift octave is the one that decides whether a whole REGION is forested or open; the body octave
        // breaks that region into stands. Expanding the composed value about its centre restores the tails a sum of
        // two noises otherwise loses, so genuinely bare and genuinely dense country both occur.
        double composed = Math.min(1, Math.max(0, 0.5 + (body * 0.55 + drift * 0.45 - 0.5) * 1.5));
        return ENDLESS_DENSITY_MIN + composed * (ENDLESS_DENSITY_MAX - ENDLESS_DENSITY_MIN);
    }

    /// <summary>
    /// How many props this chunk may place.
    ///
    /// `themeDensity` is the theme's authored props-per-1000-cells, `landscapeScale` the active landscape's own
    /// appetite. The area term is what makes an open chunk denser in absolute terms than a chunk that is mostly
    /// cliff — while the rock weight keeps the cliff from claiming a share proportional to its raw cell count.
    /// </summary>
    public static int endlessDressingBudgetFor(
        double themeDensity,
        double landscapeScale,
        double density,
        EndlessDressableArea area)
    {
        double dressable = area.rim + area.plate + area.rock * ROCK_AREA_WEIGHT;
        double budget = Math.round(
            (themeDensity *
                dressable *
                landscapeScale *
                density *
                ENDLESS_AREA_NORMALISATION *
                ENDLESS_DRESSING_APPETITE) /
            CELLS_PER_MILLE);
        return (int)Math.max(0, Math.min(ENDLESS_DRESSING_CHUNK_CAP, budget));
    }
}
