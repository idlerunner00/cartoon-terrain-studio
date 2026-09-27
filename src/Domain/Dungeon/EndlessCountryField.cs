// Port of packages/shared/src/domain/dungeon/endlessCountryField.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// The shared FIELD vocabulary of an endless Country: its deterministic salts, its two scalar helpers, and the
// one function that answers "what level is the walkable ground at this world tile".
//
// It exists so the Country composer and the wall-geology passes can both speak it without importing each
// other. Everything here is a pure function of `(seed, world coordinates)` — nothing reads a chunk raster —
// which is what makes every answer identical on both sides of an immutable streaming seam.

/// <summary>
/// Deterministic salts of the endless Country. The values are the unsigned 32-bit literals of the original; every
/// consumer only ever combines them with <c>^</c> (ToInt32 semantics), so <c>uint</c> keeps both the numeric value
/// and the bit pattern. (The TS type alias <c>EndlessCountrySalt = typeof ENDLESS_COUNTRY_SALT</c> is this class.)
/// </summary>
public static class ENDLESS_COUNTRY_SALT
{
    public const uint rootX = 0x243f6a88;
    public const uint rootY = 0x85a308d3;
    public const uint chunkNodeX = 0x13198a2e;
    public const uint chunkNodeY = 0x03707344;
    public const uint internalParent = 0xa4093822;
    public const uint internalLoop = 0x299f31d0;
    public const uint countryParent = 0x082efa98;
    public const uint countryLoop = 0xec4e6c89;
    public const uint externalLane = 0x452821e6;
    public const uint verticalPort = 0x38d01377;
    public const uint horizontalPort = 0xbe5466cf;
    public const uint routeBend = 0x34e90c6c;
    public const uint angle = 0xc0ac29b7;
    public const uint radiusX = 0xc97c50dd;
    public const uint radiusY = 0x3f84d5b5;
    public const uint centreX = 0xb5470917;
    public const uint centreY = 0x9216d5d9;
    public const uint secondary = 0x8979fb1b;
    public const uint edgeNoise = 0xd1310ba6;
    public const uint water = 0x98dfb5ac;
    public const uint waterDetail = 0x2ffd72db;
    public const uint lake = 0xd01adfb7;
    public const uint chasm = 0xb8e1afed;
    public const uint chasmDetail = 0x6a267e96;
    public const uint height = 0xba7c9045;
    public const uint heightDetail = 0xf12c7f99;
    public const uint wall = 0x24a19947;
    public const uint wallDetail = 0x7f4a7c15;
    public const uint wallFeature = 0x94d049bb;
    public const uint wallFeatureDetail = 0xed5ad4bb;
    public const uint room = 0xb3916cf7;
    public const uint roomShape = 0x1f83d9ab;
    public const uint boss = 0x0801f2e2;
    public const uint depthSocket = 0x5be0cd19;
    public const uint dressing = 0x858efc16;
    public const uint dressingDetail = 0x636920d8;
    public const uint landmarkCount = 0x6a09e667;
    public const uint landmarkVariant = 0xbb67ae85;
    public const uint landmarkAngle = 0x3c6ef372;
    public const uint landmarkRadius = 0xa54ff53a;
    public const uint choiceLoop = 0x510e527f;
    public const uint mazeKind = 0x9b05688c;
    public const uint mazeScale = 0x1f83d9ab;
    public const uint mazePhase = 0x5be0cd19;
    public const uint mazeDetail = 0xcbbb9d5d;
    public const uint district = 0x2b7e1516;
    public const uint districtWarp = 0x28aed2a6;
    public const uint districtDetail = 0xabf71588;
}

public static class EndlessCountryField
{
    public static double positiveModulo(double value, double divisor) => ((value % divisor) + divisor) % divisor;

    public static double countryClamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

    public static double countryClamp01(double value) => countryClamp(value, 0, 1);

    /// <summary><c>latticeHash((seed ^ salt) &gt;&gt;&gt; 0, x, y)</c>. <paramref name="salt"/> is only ever read through ToInt32.</summary>
    public static double countryHash(double seed, double salt, double x, double y) =>
        latticeHash(Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(salt)), x, y);

    // A Country's walkable level at one world tile.
    //
    // The composition rule lives in {@link module:endlessRelief}: one low-frequency BODY decides which shelf a
    // cell belongs to and the high-frequency rim octave may only wander that shelf's edge. The former field let
    // all three octaves vote on the level directly, which spent the whole 13-level range on local frequencies and
    // crossed a level about every four tiles — the measured "half the world is a step" fizz.
    //
    // ## This is the seam to `world/terrain`, and why the live path stays analytic
    //
    // Replacing this body with `worldGroundLevelAt(seed, x, y)`, clamped to `MIN_ELEVATION..MAX_ELEVATION`, puts the whole
    // geological pipeline under the live generator. It was done, measured, and reverted: its cold erosion-region
    // solve still costs tens of seconds. The live bridge below therefore composes a cheap analytic macro field
    // with this shelf-safe body. It now uses all 51 signed levels and closes most of the visual gap without
    // changing the streaming quantum from one 32-tile chunk to one 384-tile erosion region.
    //
    // | measured on the shared world  | former shelf field | signed live bridge | `worldGroundLevelAt` |
    // | ----------------------------- | -----------------: | -----------------: | -------------------: |
    // | 16-tile relief (mean)         |               1.46 |           **2.03** |             **3.86** |
    // | 32-tile relief (mean)         |               2.79 |           **4.16** |             **6.78** |
    // | 64-tile relief (mean)         |               6.50 |           **8.25** |            **13.50** |
    // | signed vertical span          |            0..+18  |       **−25..+25** |           **−25..+25** |
    //
    // The analytic field's height contract is regression-tested over six unrelated seeds and chunk seams: no
    // 8-connected neighbour changes by more than one level. The complete geological pipeline remains the future
    // upgrade once its regions are persisted or pre-warmed instead of solved per worker process.

    // The body height at the spawn clearing, memoized per `(seed, biome)`.
    //
    // It is a **constant** of the world, and it used to be recomputed for every tile within 42 of the origin.
    // That was merely wasteful while the landform was a handful of value-noise calls; against a cached
    // lattice field it is actively pathological, because tile (200, 200) and tile (15.5, 15.5) live in different
    // reconstruction blocks and asking for them alternately evicts the one-entry hot cache on every single
    // sample. Measured: the spawn chunk's generation time went from 38 ms to 198 ms on that alone.
    //
    // The map is bounded by construction — one entry per world a process touches — and cleared with the
    // landform cache it shadows.
    //
    // Direct-mapped hot cache for the finalized integer height field. A production chunk asks for the same world
    // coordinate in the floor solve, wall neighbourhoods, geology and repair passes. Re-running the complete
    // analytic relief stack for every one of those consumers dominated generation once richer camera-scale forms
    // were added. Direct mapping keeps lookup allocation-free and bounded; a collision merely recomputes a pure
    // value, so neither determinism nor seams depend on cache state.
    //
    // PORT NOTE: the TS module state lives once per JS worker. Here it is one instance per thread
    // ([ThreadStatic]) so concurrent chunk workers never tear each other's multi-array cache entries.
    private const int COUNTRY_LEVEL_CACHE_SIZE = 16_384;
    private const int COUNTRY_LEVEL_CACHE_MASK = COUNTRY_LEVEL_CACHE_SIZE - 1;

    private sealed class FieldCaches
    {
        public readonly Dictionary<string, double> spawnBodies = new();
        public readonly byte[] countryLevelCacheValid = new byte[COUNTRY_LEVEL_CACHE_SIZE];
        public readonly int[] countryLevelCacheSeed = new int[COUNTRY_LEVEL_CACHE_SIZE];
        public readonly int[] countryLevelCacheBiome = new int[COUNTRY_LEVEL_CACHE_SIZE];
        public readonly int[] countryLevelCacheX = new int[COUNTRY_LEVEL_CACHE_SIZE];
        public readonly int[] countryLevelCacheY = new int[COUNTRY_LEVEL_CACHE_SIZE];
        public readonly sbyte[] countryLevelCacheValue = new sbyte[COUNTRY_LEVEL_CACHE_SIZE];
        public readonly Dictionary<string, int> countryLevelBiomeIds = new();
    }

    [ThreadStatic] private static FieldCaches? cachesStorage;

    private static FieldCaches caches => cachesStorage ??= new FieldCaches();

    private static int countryLevelBiomeId(FieldCaches cache, string biomeKey)
    {
        if (cache.countryLevelBiomeIds.TryGetValue(biomeKey, out int cached)) return cached;
        int next = cache.countryLevelBiomeIds.Count + 1;
        cache.countryLevelBiomeIds[biomeKey] = next;
        return next;
    }

    private static double spawnBodyFor(FieldCaches cache, double seed, string biomeKey)
    {
        string key = $"{Js.Str(seed)}:{biomeKey}";
        if (cache.spawnBodies.TryGetValue(key, out double cached)) return cached;
        ElevationProfile profile = Terrain.terrainProfileFor(biomeKey).elevation;
        double value =
            EndlessRelief.endlessReliefBodyAt(
                Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.height)),
                Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.heightDetail)),
                profile,
                15.5,
                15.5) +
            EndlessRelief.endlessLandformOffsetAt(Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.heightDetail)), 15.5, 15.5) /
                ELEVATION_LEVELS;
        // A world generator process legitimately touches a handful of worlds (the shared world plus theme
        // previews); anything beyond that is a tool sweeping seeds, and it gets a bounded map rather than a leak.
        if (cache.spawnBodies.Count > 64) cache.spawnBodies.Clear();
        cache.spawnBodies[key] = value;
        return value;
    }

    public static double countryElevationLevelAt(double seed, string biomeKey, double x, double y)
    {
        FieldCaches cache = caches;
        bool cacheable =
            Number.isInteger(x) &&
            Number.isInteger(y) &&
            x >= -2_147_483_648 &&
            x <= 2_147_483_647 &&
            y >= -2_147_483_648 &&
            y <= 2_147_483_647;
        int cacheSeed = Js.ToInt32(seed);
        int cacheX = Js.ToInt32(x);
        int cacheY = Js.ToInt32(y);
        int cacheBiome = countryLevelBiomeId(cache, biomeKey);
        int cacheSlot = 0;
        if (cacheable)
        {
            int hash = Math.imul(cacheSeed ^ cacheBiome, 0x45d9f3b);
            hash ^= Math.imul(cacheX, 0x1f123bb5) ^ Math.imul(cacheY, 0x5f356495);
            cacheSlot = (hash ^ (int)((uint)hash >> 16)) & COUNTRY_LEVEL_CACHE_MASK;
            if (
                cache.countryLevelCacheValid[cacheSlot] != 0 &&
                cache.countryLevelCacheSeed[cacheSlot] == cacheSeed &&
                cache.countryLevelCacheBiome[cacheSlot] == cacheBiome &&
                cache.countryLevelCacheX[cacheSlot] == cacheX &&
                cache.countryLevelCacheY[cacheSlot] == cacheY)
                return cache.countryLevelCacheValue[cacheSlot];
        }
        ElevationProfile profile = Terrain.terrainProfileFor(biomeKey).elevation;
        double body = EndlessRelief.endlessReliefBodyAt(
            Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.height)),
            Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.heightDetail)),
            profile,
            x,
            y);
        // The shelf field gives every tile a legal local grade; the analytic macro field gives the world a reason
        // to climb at all. It adds fold belts, plateaus and broad basins in LEVEL units. The body owns the complete
        // signed −25..+25 range; the macro offset decides where it reaches those continental extremes.
        body +=
            EndlessRelief.endlessLandformOffsetAt(Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.heightDetail)), x, y) /
            ELEVATION_LEVELS;
        // The spawn bowl flattens the BODY, so the clearing every run opens on is one shelf rather than a stair.
        double spawnDistance = Math.hypot(x - 15.5, y - 15.5);
        double spawnEase = 1;
        if (spawnDistance < 42)
        {
            spawnEase = Scalar.clamp01((spawnDistance - 13) / 29);
            // Flatten to the landform's own height at the clearing, not to a fixed normalized level. A fixed 0.42
            // worked only while the old field stayed near the middle of its range; beside a new mountain it forced a
            // fifteen-level drop through a ten-tile ring and made the onboarding bowl the world's steepest artifact.
            body = spawnBodyFor(cache, seed, biomeKey) * (1 - spawnEase) + body * spawnEase;
        }
        double ridgeAngle =
            countryHash(seed, ENDLESS_COUNTRY_SALT.height ^ ENDLESS_COUNTRY_SALT.mazePhase, 0, 0) * Math.PI;
        // Macro folds already bend every contour. Retain a restrained fine rim for natural breakup without letting
        // its almost-one-level excursion add to a steep fold and turn a legal diagonal into a two-level step.
        double rim =
            EndlessRelief.endlessReliefRimAt(Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.mazeDetail)), ridgeAngle, x, y) * 0.38;
        // A broad uplift or basin may legitimately overshoot the signed domain, but hard-clamping that overshoot
        // turned complete camera views into one identical +25 or -25 plate. Fold only the out-of-domain excess back
        // into a shallow seven-to-eleven-level summit/basin interior. The mapping is continuous at both bounds, the rim
        // adds only slow sub-step contour movement, and the protected spawn core samples one constant detail value.
        // Thus extreme countries retain real +25/-25 rims while their interiors still contain visible relief.
        if (body < 0 || body > 1)
        {
            bool high = body > 1;
            double overshoot = high ? body - 1 : -body;
            // The old quarter-domain gate opened too slowly: a shallow overshoot remained inside the same endpoint
            // band for thousands of connected cells. Open the fold over seven percent of the body and seat it inward
            // fast enough for a camera-scale basin/summit to contain several readable terraces while preserving the
            // exact signed endpoint at the rim.
            double gateInput = Scalar.clamp01(overshoot / 0.07);
            double gate = gateInput * gateInput * (3 - 2 * gateInput);
            double detail = Scalar.clamp01(0.5 + rim * 2 * spawnEase);
            double inset = Math.min(0.14, overshoot * 0.42) + detail * 0.08 * gate;
            body = high ? 1 - inset : inset;
        }
        double level = MIN_ELEVATION + EndlessRelief.endlessReliefLevelAt(body, rim, ELEVATION_LEVELS);
        if (cacheable)
        {
            cache.countryLevelCacheValid[cacheSlot] = 1;
            cache.countryLevelCacheSeed[cacheSlot] = cacheSeed;
            cache.countryLevelCacheBiome[cacheSlot] = cacheBiome;
            cache.countryLevelCacheX[cacheSlot] = cacheX;
            cache.countryLevelCacheY[cacheSlot] = cacheY;
            cache.countryLevelCacheValue[cacheSlot] = Js.I8(level);
        }
        return level;
    }

    /// <summary>
    /// Water-specific elevation ladder for the streamed Country.
    ///
    /// Letting Water copy the one-step walkable field produced long staircases whose every fall was exactly one
    /// level high. Water is not walkable and should instead pool on broad hydraulic terraces, then cross their
    /// irregular ground contour as a decisive cascade. Five-level bands keep a basin at most four levels below
    /// its local bank, span the complete signed -25..+25 domain, and guarantee a real five-level waterfall wherever
    /// two neighbouring Water cells cross a band. The ground field already supplies the organic, seam-stable contour.
    /// </summary>
    public static double countryWaterElevationLevelAt(double groundLevel)
    {
        double clamped = countryClamp(Math.round(groundLevel), MIN_ELEVATION, MAX_ELEVATION);
        double terrace = MIN_ELEVATION + Math.floor((clamped - MIN_ELEVATION) / 5) * 5;
        return countryClamp(terrace, MIN_ELEVATION, MAX_ELEVATION);
    }
}
