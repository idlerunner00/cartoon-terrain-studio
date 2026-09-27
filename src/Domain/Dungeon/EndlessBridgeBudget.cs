// Port of packages/shared/src/domain/dungeon/endlessBridgeBudget.ts — keep in lockstep with the original.
using System;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Inside Fluitown.Domain a bare `Js` binds to the namespace Fluitown.Runtime (outer-namespace members are searched
// before compilation-unit usings); this namespace-level alias makes it the runtime class.
using Js = Fluitown.Runtime.Js;

public sealed class EndlessBridgeBudgetOptions
{
    public double seed;
    public double cx;
    public double cy;
    public int startX;
    public int startY;
    public byte[] routeMask = Array.Empty<byte>();
    public double? generationVersion;
}

public sealed class EndlessBridgeBudgetResult
{
    public bool entitled;
    public int candidateCells;
    public int bridgeCells;
    public bool added;
}

public static class EndlessBridgeBudget
{
    /// <summary>
    /// World-generation salt for bridge pacing. It is deliberately independent from hydrology and route salts:
    /// moving a river or a path must not also silently move the regional bridge budget.
    /// </summary>
    private const int ENDLESS_BRIDGE_BUDGET_SALT = 0x4d8f2a61;

    /// <summary>V15 raises the latent opportunity rate from V14's checkerboard to a bounded three-quarter lattice.</summary>
    public const int ENDLESS_BALANCED_BRIDGE_BUDGET_GENERATION_VERSION = 15;

    /// <summary>A Country bridge is always a compact two-cell-wide crossing, never a timber-painted district.</summary>
    public const int ENDLESS_BRIDGE_MAX_SPAN_CELLS = 8;

    /// <summary>
    /// Sparse, seam-stable bridge entitlement for streamed chunks.
    ///
    /// A seed-selected quarter lattice withholds exactly one slot in every 2x2 chunk neighbourhood. Three quarters
    /// retain the opportunity to turn a genuine water crossing into a landmark: this is the deliberate midpoint
    /// between the original ungated painter and V14's too-sparse checkerboard. A fully eligible 2x2 cluster still
    /// cannot occur, while hydrology and functional selection thin the latent budget much further. Signed
    /// coordinates use bit parity directly and therefore cannot disagree at the world origin.
    /// </summary>
    public static bool endlessCountryChunkOwnsBridgeBudget(
        double seed,
        double cx,
        double cy,
        double generationVersion = ENDLESS_BALANCED_BRIDGE_BUDGET_GENERATION_VERSION)
    {
        if (generationVersion < ENDLESS_BALANCED_BRIDGE_BUDGET_GENERATION_VERSION)
        {
            int phase = Elevation.latticeHash(Js.ToUint32(Js.ToInt32(seed) ^ ENDLESS_BRIDGE_BUDGET_SALT), 0, 0) < 0.5 ? 0 : 1;
            return (Js.ToInt32(cx + cy) & 1) == phase;
        }
        int withheldPhase = (int)Math.min(
            3,
            Math.floor(Elevation.latticeHash(Js.ToUint32(Js.ToInt32(seed) ^ ENDLESS_BRIDGE_BUDGET_SALT), 0, 0) * 4));
        int withheldX = withheldPhase & 1;
        int withheldY = (int)((uint)withheldPhase >> 1) & 1;
        return (Js.ToInt32(cx) & 1) != withheldX || (Js.ToInt32(cy) & 1) != withheldY;
    }

    /// <summary>
    /// Collapse every broad hydrology/route overlap into at most one authored crossing.
    ///
    /// The hydrology painter historically changed every route cell touched by selected water into `Bridge`. On a
    /// braided route that made one valid crossing grow sideways through junctions and courts until most of a chunk
    /// became timber. Here those cells first recover their underlying Water on a private candidate raster. The
    /// functional-bridge selector may then retain ONE two-wide, short, bank-to-bank span inside the original deck
    /// footprint. Non-entitled chunks keep their route as an ordinary shallow ford (`Floor`), so pacing never harms
    /// navigation; an entitled chunk only replaces the old footprint with Water after a valid bridge was found.
    /// </summary>
    public static EndlessBridgeBudgetResult applyEndlessBridgeBudget(
        byte[] tiles,
        int width,
        int height,
        EndlessBridgeBudgetOptions options)
    {
        if (width <= 0 || height <= 0 || tiles.Length < width * height)
        {
            return new EndlessBridgeBudgetResult { entitled = false, candidateCells = 0, bridgeCells = 0, added = false };
        }

        var originalDeck = new byte[width * height];
        var candidate = tiles.slice();
        int candidateCells = 0;
        for (int index = 0; index < width * height; index++)
        {
            if (tiles[index] != TileType.Bridge) continue;
            originalDeck[index] = 1;
            candidate[index] = TileType.Water;
            candidateCells++;
        }
        if (candidateCells == 0)
        {
            return new EndlessBridgeBudgetResult
            {
                entitled = endlessCountryChunkOwnsBridgeBudget(
                    options.seed,
                    options.cx,
                    options.cy,
                    options.generationVersion ?? ENDLESS_BALANCED_BRIDGE_BUDGET_GENERATION_VERSION),
                candidateCells = 0,
                bridgeCells = 0,
                added = false,
            };
        }

        bool entitled = endlessCountryChunkOwnsBridgeBudget(
            options.seed,
            options.cx,
            options.cy,
            options.generationVersion ?? ENDLESS_BALANCED_BRIDGE_BUDGET_GENERATION_VERSION);
        if (!entitled)
        {
            for (int index = 0; index < originalDeck.Length; index++)
                if (originalDeck[index] != 0) tiles[index] = TileType.Floor;
            return new EndlessBridgeBudgetResult { entitled = entitled, candidateCells = candidateCells, bridgeCells = 0, added = false };
        }

        var rescued = TerrainBridgeRescue.rescueFunctionalWaterBridge(candidate, width, height, new FunctionalBridgeRescueOptions
        {
            startX = options.startX,
            startY = options.startY,
            seed = options.seed,
            salt = Math.imul(options.cx, 0x27d4eb2f) ^ Math.imul(options.cy, 0x165667b1),
            routeMask = options.routeMask,
            deckMask = originalDeck,
            allowWalkableBanks = true,
            maxSpan = ENDLESS_BRIDGE_MAX_SPAN_CELLS,
            minimumBarrierBodyCells = 24,
            minimumRescuedAreaCells = 18,
            minimumUsefulDetour = 24,
        });

        // Braided legacy platforms are often too irregular to contain one complete rectangular span even though the
        // coherent Water body below them has an excellent crossing a cell or two away. The footprint-constrained
        // pass gets first refusal; an entitled chunk may then choose one unrestricted functional candidate from that
        // SAME recovered carrier. The regional entitlement and one-result selector still enforce the hard budget.
        if (!rescued.added)
            rescued = TerrainBridgeRescue.rescueFunctionalWaterBridge(candidate, width, height, new FunctionalBridgeRescueOptions
            {
                startX = options.startX,
                startY = options.startY,
                seed = options.seed,
                salt = Js.ToUint32(
                    Math.imul(options.cx, 0x27d4eb2f) ^ Math.imul(options.cy, 0x165667b1) ^ 0x51ed270b),
                routeMask = options.routeMask,
                allowWalkableBanks = true,
                maxSpan = ENDLESS_BRIDGE_MAX_SPAN_CELLS,
                minimumBarrierBodyCells = 24,
                minimumRescuedAreaCells = 18,
                minimumUsefulDetour = 24,
            });

        // No honest bank-to-bank span means this is a ford, not a decorative timber platform.
        if (!rescued.added)
        {
            for (int index = 0; index < originalDeck.Length; index++)
                if (originalDeck[index] != 0) tiles[index] = TileType.Floor;
            return new EndlessBridgeBudgetResult { entitled = entitled, candidateCells = candidateCells, bridgeCells = 0, added = false };
        }

        // Publish only the selected compact deck. Its one-cell Water reveal makes the carrier legible and keeps the
        // fascia clear; more distant retired platform cells become traversable fords so a former multi-bank junction
        // cannot strand a walkable island merely because its timber budget was reduced to one crossing.
        var selectedDeck = new byte[width * height];
        int selectedMinX = width;
        int selectedMinY = height;
        int selectedMaxX = -1;
        int selectedMaxY = -1;
        for (int index = 0; index < selectedDeck.Length; index++)
            if (candidate[index] == TileType.Bridge)
            {
                selectedDeck[index] = 1;
                int tx = index % width;
                int ty = index / width; // Math.floor of a non-negative quotient
                selectedMinX = Math.min(selectedMinX, tx);
                selectedMinY = Math.min(selectedMinY, ty);
                selectedMaxX = Math.max(selectedMaxX, tx);
                selectedMaxY = Math.max(selectedMaxY, ty);
            }
        bool travelsHorizontally = selectedMaxX - selectedMinX >= selectedMaxY - selectedMinY;
        for (int index = 0; index < originalDeck.Length; index++)
        {
            if (selectedDeck[index] != 0)
            {
                tiles[index] = TileType.Bridge;
                continue;
            }
            if (originalDeck[index] == 0) continue;
            int tx = index % width;
            int ty = index / width; // Math.floor of a non-negative quotient
            bool revealsCarrier = false;
            for (int dy = -1; dy <= 1 && !revealsCarrier; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    bool isCarrierFlank = travelsHorizontally ? dy != 0 : dx != 0;
                    if (isCarrierFlank && selectedDeck[ny * width + nx] != 0)
                    {
                        revealsCarrier = true;
                        break;
                    }
                }
            }
            tiles[index] = revealsCarrier ? (byte)TileType.Water : (byte)TileType.Floor;
        }
        return new EndlessBridgeBudgetResult
        {
            entitled = entitled,
            candidateCells = candidateCells,
            bridgeCells = rescued.bridgeCells,
            added = true,
        };
    }
}
