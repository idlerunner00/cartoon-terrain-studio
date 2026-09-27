// Port of packages/shared/src/domain/dungeon/endlessSetPiece.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.EndlessCoordinates;
using static Fluitown.Domain.MapSetPieces;
using static Fluitown.Domain.MapSetPieceStamps;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * **Set pieces for the endless world** — the wonders a run is allowed to build.
 *
 * ## What this is
 *
 * The landform generator ships bedrock, a route network and scattered props. A **set piece** is the one thing
 * in a run that is unmistakably deliberate: a wonder, carved into real terrain by the shared stamp machine
 * (stampMapWonder) from the shared catalog (MAP_WONDERS). What this module owns is siting — where, how often,
 * and on what ground.
 *
 * Everything a set piece writes is terrain: the world is **landscape**, and wonders, landmarks, relief, water
 * and dressing compose the places worth walking to.
 *
 * ## The rule
 *
 * A set piece is placed by the chunk that **wholly contains it**, and by no other. That single constraint is
 * what makes this safe on an infinitely streamed world: two chunks meeting at an immutable seam never see each
 * other's raster, so a structure straddling that seam would have to be agreed on by two independent
 * generators. Here it never straddles one — a site whose footprint plus margin does not fit inside the chunk
 * that owns its lattice cell is simply not built, and the next block's site is.
 */

public sealed class EndlessSetPieceResult
{
    public List<TerrainDecorationPlacement> decorations = new();
    /// <summary>Cells a set piece owns. The ordinary dressing pass leaves them alone.</summary>
    public byte[] claim = Array.Empty<byte>();
    public string? wonderKey;
}

public static partial class EndlessSetPiece
{
    /// <summary>
    /// Side of one wonder block, in CHUNKS.
    ///
    /// At two, a run states a wonder about every four chunks — roughly every seventeen screens, which is a rhythm
    /// a player can notice. The lattice is expressed in chunks rather than tiles on purpose: a block designates one
    /// of its chunks as the owner and that chunk places the wonder inside its own interior, so a site can never
    /// fail to fit. A tile-space lattice was measured first and it produced **zero wonders in a hundred chunks** —
    /// a site landed where it landed, and the "must fit whole" rule then rejected five sixths of them.
    /// </summary>
    public const int ENDLESS_WONDER_BLOCK_CHUNKS = 2;
    /// <summary>
    /// Largest wonder footprint radius an endless chunk may build, in tiles.
    ///
    /// A chunk is 32 tiles across and a set piece must fit inside it whole, with a margin the later topology
    /// repairs can work in. Seven is the largest radius that always leaves that margin — a 15-tile structure,
    /// which at 62.5 units per tile is about 938 world units: comfortably more than half a screen.
    /// </summary>
    public const int ENDLESS_WONDER_MAX_RADIUS = 7;
    /// <summary>Margin, in tiles, kept clear between a set piece and the chunk seam.</summary>
    private const int SEAM_MARGIN = 3;
    /// <summary>Shoulder, in tiles, kept clear between a set piece and the run's authored escape artery.</summary>
    private const int ARTERY_CLEARANCE = 2;
    /// <summary>Shoulder, in tiles, kept clear between a set piece and any bridge deck.</summary>
    private const int BRIDGE_CLEARANCE = 3;
    /// <summary>
    /// How far a set piece may stand from the nearest water or void, in tiles.
    ///
    /// Twelve is half a camera view, and it is the same number the perceptual-variety contract uses to define rock
    /// that "reads as an undifferentiated grey slab". A set piece raises real rock, so it obeys the same rule the
    /// landform does: it stands where the land already has a feature to be composed against, rather than dropping
    /// a mass into the middle of dry country.
    /// </summary>
    private const int FEATURE_REACH = 12;
    private const int WONDER_SALT = 0x6d2b79f5;

    /// <summary>
    /// The wonders that fit an endless chunk, resolved once.
    ///
    /// `radius` in the catalog is a fraction of the map's shorter axis, which for a 32-tile chunk would round most
    /// entries down to nothing. The endless world therefore reads the band as a fraction of its own chunk and
    /// keeps the entries that can still build something worth calling a wonder at that size.
    /// </summary>
    private static readonly IReadOnlyList<MapWonderDef> ENDLESS_WONDERS =
        MAP_WONDERS.filter(
            (wonder) => wonder.radius.Item1 * ENDLESS_CHUNK_TILES <= ENDLESS_WONDER_MAX_RADIUS);

    private static double hash(double seed, int salt, double x, double y)
    {
        return latticeHash(Js.ToUint32(Js.ToInt32(seed) ^ salt), x, y);
    }

    /// <summary>The wonder a block states, and which of its chunks builds it (TS inline return type).</summary>
    private sealed class WonderBlockPlan
    {
        public int ownerCx;
        public int ownerCy;
        public MapWonderDef wonder = null!;
        public int radius;
    }

    /// <summary>The wonder this chunk owns, seated inside its own interior (TS inline return type).</summary>
    private sealed class WonderPlacement
    {
        public int tx;
        public int ty;
        public MapWonderDef wonder = null!;
        public int radius;
    }

    /// <summary>The wonder a block states, and which of its chunks builds it.</summary>
    private static WonderBlockPlan? wonderBlockPlan(double seed, int blockX, int blockY)
    {
        if (ENDLESS_WONDERS.Count == 0) return null;
        // Not every block builds one. A wonder that appears on a fixed grid stops reading as a discovery.
        if (hash(seed, WONDER_SALT, blockX, blockY) > 0.78) return null;
        int ownerCx =
            blockX * ENDLESS_WONDER_BLOCK_CHUNKS +
            (int)Math.floor(hash(seed, WONDER_SALT ^ unchecked((int)0x9e3779b9), blockX, blockY) * ENDLESS_WONDER_BLOCK_CHUNKS);
        int ownerCy =
            blockY * ENDLESS_WONDER_BLOCK_CHUNKS +
            (int)Math.floor(hash(seed, WONDER_SALT ^ unchecked((int)0x85ebca6b), blockX, blockY) * ENDLESS_WONDER_BLOCK_CHUNKS);
        double total = 0;
        foreach (MapWonderDef wonder in ENDLESS_WONDERS) total += wonder.weight;
        double pick = hash(seed, WONDER_SALT ^ unchecked((int)0xc2b2ae35), blockX, blockY) * total;
        MapWonderDef chosen = ENDLESS_WONDERS[0];
        foreach (MapWonderDef wonder in ENDLESS_WONDERS)
        {
            pick -= wonder.weight;
            if (pick <= 0)
            {
                chosen = wonder;
                break;
            }
        }
        var (minFraction, maxFraction) = chosen.radius;
        int low = (int)Math.max(3, Math.round(minFraction * ENDLESS_CHUNK_TILES));
        int high = (int)Math.max(
            low,
            Math.min(ENDLESS_WONDER_MAX_RADIUS, Math.round(maxFraction * ENDLESS_CHUNK_TILES)));
        int radius =
            low + (int)Math.floor(hash(seed, WONDER_SALT ^ 0x27d4eb2f, blockX, blockY) * (high - low + 1));
        return new WonderBlockPlan { ownerCx = ownerCx, ownerCy = ownerCy, wonder = chosen, radius = Math.min(radius, ENDLESS_WONDER_MAX_RADIUS) };
    }

    /// <summary>
    /// Build every set piece this chunk wholly contains.
    ///
    /// `tiles` and `elevation` are edited in place — a wonder carves real terrain, which is the difference between
    /// a set piece and a decal. Everything written stays inside the chunk by construction.
    /// </summary>
    public static EndlessSetPieceResult buildEndlessSetPieces(
        byte[] tiles,
        sbyte[] elevation,
        byte[] primaryRouteMask,
        double seed,
        string biomeKey,
        int baseTx,
        int baseTy)
    {
        int width = ENDLESS_CHUNK_TILES;
        var claim = new byte[tiles.Length];
        // The artery, DILATED. Refusing only to write on it is not enough: a wonder carved hard against the escape
        // route can leave its cells standing on a shelf nothing else reaches, and the artifact validates that route
        // as one connected component across the whole world. A two-cell shoulder is what keeps the guarantee local.
        var arteryGuard = new byte[tiles.Length];
        byte[] featureReach = reachOfWaterAndVoid(tiles, width);
        // Decks and their banks. A crossing is the narrowest, most fragile thing in the world: it must touch water
        // or void to be a crossing at all and it must hold a minimum width, so a stamp that fills the channel under
        // one or raises rock against its bank turns it into a stray plank — measured, that dropped whole chunks to
        // the sparse-route fallback. Set pieces simply do not go near a deck.
        var bridgeGuard = new byte[tiles.Length];
        for (int ty = 0; ty < width; ty++)
            for (int tx = 0; tx < width; tx++)
            {
                if (tiles[ty * width + tx] != TileType.Bridge) continue;
                for (int dy = -BRIDGE_CLEARANCE; dy <= BRIDGE_CLEARANCE; dy++)
                    for (int dx = -BRIDGE_CLEARANCE; dx <= BRIDGE_CLEARANCE; dx++)
                    {
                        int x = tx + dx;
                        int y = ty + dy;
                        if (x < 0 || y < 0 || x >= width || y >= width) continue;
                        bridgeGuard[y * width + x] = 1;
                    }
            }
        for (int ty = 0; ty < width; ty++)
            for (int tx = 0; tx < width; tx++)
            {
                if (primaryRouteMask[ty * width + tx] == 0) continue;
                for (int dy = -ARTERY_CLEARANCE; dy <= ARTERY_CLEARANCE; dy++)
                    for (int dx = -ARTERY_CLEARANCE; dx <= ARTERY_CLEARANCE; dx++)
                    {
                        int x = tx + dx;
                        int y = ty + dy;
                        if (x < 0 || y < 0 || x >= width || y >= width) continue;
                        arteryGuard[y * width + x] = 1;
                    }
            }
        var decorations = new List<TerrainDecorationPlacement>();
        string? wonderKey = null;

        // The ceiling a set piece may build to. A world states its own vertical range — run 5 is a deliberately low
        // trench run whose contract is that NO walkable cell reaches the top of the ladder — and a ziggurat's summit
        // plaza is walkable. A set piece composes with the land it stands on; it does not out-climb it.
        int walkableCeiling = MIN_ELEVATION;
        for (int index = 0; index < tiles.Length; index++)
            if (isWalkable(tiles[index]) && elevation[index] > walkableCeiling)
                walkableCeiling = elevation[index];

        WonderPlacement? wonder = wonderFor(
            seed,
            (int)Math.floor((double)baseTx / width),
            (int)Math.floor((double)baseTy / width),
            width);
        if (wonder != null)
        {
            bool oriented = hash(seed, WONDER_SALT ^ 0x2545f491, baseTx, baseTy) < 0.5;
            var site = new WonderSite
            {
                tx = wonder.tx,
                ty = wonder.ty,
                radius = wonder.radius,
                dx = oriented ? 1 : 0,
                dy = oriented ? 0 : 1,
                span = wonder.radius,
            };
            var ground = new sbyte[tiles.Length];
            for (int index = 0; index < tiles.Length; index++) ground[index] = elevation[index];
            var canvas = new WonderCanvas
            {
                width = width,
                height = width,
                tiles = tiles,
                elevation = elevation,
                ground = ground,
                protect = new byte[tiles.Length],
                claim = claim,
                maxLevel = MAX_ELEVATION,
                minLevel = MIN_ELEVATION,
                seed = seed,
                // The seam belt and the escape artery are the two things a set piece may never write over: one is
                // shared with a chunk this generator cannot see, the other is the run's guaranteed way out.
                canWrite = (tx, ty) =>
                    tx >= SEAM_MARGIN &&
                    ty >= SEAM_MARGIN &&
                    tx < width - SEAM_MARGIN &&
                    ty < width - SEAM_MARGIN &&
                    arteryGuard[ty * width + tx] == 0 &&
                    bridgeGuard[ty * width + tx] == 0 &&
                    featureReach[ty * width + tx] != 0,
            };
            WonderStamp stamp = stampMapWonder(
                canvas,
                wonder.wonder,
                site,
                new Rng($"{Js.Str(seed)}:wonder:{Js.Str(baseTx + wonder.tx)}:{Js.Str(baseTy + wonder.ty)}"));
            if (stamp.built)
            {
                for (int index = 0; index < tiles.Length; index++)
                    if (
                        claim[index] != 0 &&
                        isWalkable(tiles[index]) &&
                        elevation[index] > walkableCeiling)
                        elevation[index] = (sbyte)walkableCeiling;
                wonderKey = wonder.wonder.key;
                decorations.AddRange(stamp.decorations);
                // A stamp's dressing can land on a cell its carving never wrote — a monolith on the apron outside a
                // ring, for instance. Those cells belong to the set piece too, or the artifact publishes a wonder's own
                // props as ordinary scatter and every contract about the scatter has to explain them.
                foreach (TerrainDecorationPlacement decoration in stamp.decorations)
                    claim[(int)(decoration.ty * width + decoration.tx)] = 1;
            }
        }

        // The claim mask is returned even when nothing was built. A zero-length stand-in would make every lookup
        // read `undefined`, and a caller comparing that against 0 would refuse the whole chunk — which is exactly
        // what a shorter version of this function did, and it emptied the world.
        return new EndlessSetPieceResult { decorations = decorations, claim = claim, wonderKey = wonderKey };
    }

    /// <summary>The wonder this chunk owns, if any, seated inside its own interior.</summary>
    private static WonderPlacement? wonderFor(double seed, int cx, int cy, int width)
    {
        int blockX = (int)Math.floor((double)cx / ENDLESS_WONDER_BLOCK_CHUNKS);
        int blockY = (int)Math.floor((double)cy / ENDLESS_WONDER_BLOCK_CHUNKS);
        WonderBlockPlan? plan = wonderBlockPlan(seed, blockX, blockY);
        if (plan == null || plan.ownerCx != cx || plan.ownerCy != cy) return null;
        // Jitter inside the interior the margin leaves, so a wonder is not always dead centre in its chunk.
        int margin = plan.radius + SEAM_MARGIN;
        int span = Math.max(1, width - margin * 2);
        int tx = margin + (int)Math.floor(hash(seed, WONDER_SALT ^ 0x165667b1, cx, cy) * span);
        int ty = margin + (int)Math.floor(hash(seed, WONDER_SALT ^ 0x1f83d9ab, cx, cy) * span);
        return new WonderPlacement { tx = tx, ty = ty, wonder = plan.wonder, radius = plan.radius };
    }

    /// <summary>
    /// Cells within <see cref="FEATURE_REACH"/> of any water or void, as a 1/0 mask.
    ///
    /// A chunk that carries neither is entirely eligible: refusing to build anything there would mean a dry inland
    /// Country never gets a set piece at all, which is a bigger composition failure than the one this guards.
    /// </summary>
    private static byte[] reachOfWaterAndVoid(byte[] tiles, int width)
    {
        var reach = new byte[tiles.Length];
        var queue = new int[tiles.Length];
        var distance = new short[tiles.Length].fill((short)-1);
        int tail = 0;
        for (int index = 0; index < tiles.Length; index++)
        {
            int tile = tiles[index];
            if (tile != TileType.Water && tile != TileType.Chasm) continue;
            distance[index] = 0;
            queue[tail++] = index;
        }
        if (tail == 0) return reach.fill((byte)1);
        for (int head = 0; head < tail; head++)
        {
            int index = queue[head];
            int next = distance[index] + 1;
            if (next > FEATURE_REACH) continue;
            int x = index % width;
            int y = index / width;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= width) continue;
                    int neighbour = ny * width + nx;
                    if (distance[neighbour] != -1) continue;
                    distance[neighbour] = (short)next;
                    queue[tail++] = neighbour;
                }
        }
        for (int index = 0; index < reach.Length; index++)
            reach[index] = (byte)(distance[index] != -1 && distance[index] <= FEATURE_REACH ? 1 : 0);
        return reach;
    }
}
