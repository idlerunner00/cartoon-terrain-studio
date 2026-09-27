// Port of packages/shared/src/domain/dungeon/endlessCountryMaze.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using static Fluitown.Domain.EndlessCountryField;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/// <summary>Auditable macro grammars used to interrupt one Endless Country's walkable shelf.</summary>
public static class EndlessCountryMazeKind
{
    public const int BrokenGrid = 0;
    public const int BraidedAisles = 1;
    public const int Switchbacks = 2;
    public const int Honeycomb = 3;
    public const int ConcentricWards = 4;
    public const int FaultedPass = 5;
    public const int CourtyardChain = 6;
    public const int StaggeredGates = 7;
    public const int CrossVaults = 8;
    public const int SpiralWard = 9;
    public const int DeltaForks = 10;
    public const int ShatteredShelf = 11;
}

public sealed class EndlessCountryMazePlan
{
    /// <summary>An <see cref="EndlessCountryMazeKind"/> value.</summary>
    public int kind;
    public double angle;
    public double spacing;
    public double wallWidth;
    public double gateWidth;
    public double warp;
    public double phase;
}

public static class EndlessCountryMaze
{
    public const int ENDLESS_COUNTRY_MAZE_KIND_COUNT = 12;

    private static void multiplyWeight(double[] weights, int kind, double factor)
    {
        weights[kind] = weights[kind] * factor;
    }

    /// <summary>Select one macro grammar from landform traits and the current run's construction identity.</summary>
    public static int endlessCountryMazeKindFor(
        double seed,
        double countryX,
        double countryY,
        string biomeKey,
        EndlessCountryLandscape landscape)
    {
        EndlessLandscapeTraits traits = landscape.traits;
        var weights = new double[ENDLESS_COUNTRY_MAZE_KIND_COUNT];
        weights.fill(0.72);
        multiplyWeight(
            weights,
            EndlessCountryMazeKind.BraidedAisles,
            0.68 + traits.channelComplexity * 2.2);
        multiplyWeight(
            weights,
            EndlessCountryMazeKind.DeltaForks,
            0.58 + (traits.waterBias + traits.lakeStrength) * 1.45);
        multiplyWeight(
            weights,
            EndlessCountryMazeKind.Switchbacks,
            0.64 + traits.escarpmentStrength * 1.9);
        multiplyWeight(weights, EndlessCountryMazeKind.FaultedPass, 0.58 + traits.riftStrength * 2.05);
        multiplyWeight(
            weights,
            EndlessCountryMazeKind.ShatteredShelf,
            0.58 + (traits.chasmStrength + traits.reliefRuggedness) * 1.12);
        multiplyWeight(
            weights,
            EndlessCountryMazeKind.Honeycomb,
            0.7 + (traits.pillarDensity + traits.islandStrength) * 1.18);
        multiplyWeight(
            weights,
            EndlessCountryMazeKind.StaggeredGates,
            0.72 + traits.pathConfinement * 1.52);
        multiplyWeight(weights, EndlessCountryMazeKind.SpiralWard, 0.7 + traits.chasmBasins * 1.66);
        multiplyWeight(
            weights,
            EndlessCountryMazeKind.ConcentricWards,
            0.84 + Math.max(0, traits.opennessBias) * 1.4);

        void favour(double factor, params int[] kinds)
        {
            foreach (int kind in kinds) multiplyWeight(weights, kind, factor);
        }
        switch (biomeKey)
        {
            case "noir_sprawl":
                favour(1.62, EndlessCountryMazeKind.BrokenGrid, EndlessCountryMazeKind.CrossVaults);
                favour(1.34, EndlessCountryMazeKind.CourtyardChain);
                break;
            case "olympian_sky_borough":
                favour(1.58, EndlessCountryMazeKind.CourtyardChain, EndlessCountryMazeKind.ConcentricWards);
                favour(1.3, EndlessCountryMazeKind.CrossVaults);
                break;
            case "sakura_temple_dream":
                favour(1.48, EndlessCountryMazeKind.StaggeredGates, EndlessCountryMazeKind.SpiralWard);
                favour(1.28, EndlessCountryMazeKind.CourtyardChain);
                break;
            case "clockwork_moon_bazaar":
                favour(1.6, EndlessCountryMazeKind.Honeycomb, EndlessCountryMazeKind.BrokenGrid);
                favour(1.36, EndlessCountryMazeKind.SpiralWard);
                break;
            case "sugarstorm_carnival":
                favour(1.58, EndlessCountryMazeKind.SpiralWard, EndlessCountryMazeKind.DeltaForks);
                favour(1.3, EndlessCountryMazeKind.ConcentricWards);
                break;
            case "prismglass_archive":
                favour(1.58, EndlessCountryMazeKind.CrossVaults, EndlessCountryMazeKind.ShatteredShelf);
                favour(1.32, EndlessCountryMazeKind.ConcentricWards);
                break;
            case "starforged_cathedral_endrun":
                favour(1.68, EndlessCountryMazeKind.CrossVaults, EndlessCountryMazeKind.FaultedPass);
                favour(1.4, EndlessCountryMazeKind.ConcentricWards);
                break;
        }

        double total = 0;
        foreach (double weight in weights) total += weight;
        double roll = countryHash(seed, ENDLESS_COUNTRY_SALT.mazeKind, countryX, countryY) * total;
        for (int kind = 0; kind < weights.Length; kind++)
        {
            roll -= weights[kind];
            if (roll <= 0) return kind;
        }
        return EndlessCountryMazeKind.ShatteredShelf;
    }

    public static EndlessCountryMazePlan createEndlessCountryMazePlan(
        double seed,
        double countryX,
        double countryY,
        double shelfAngle,
        double openness,
        string biomeKey,
        EndlessCountryLandscape landscape,
        bool functionalTerrain,
        bool wildTerrain = false)
    {
        double hash(double salt) => countryHash(seed, salt, countryX, countryY);
        int kind = functionalTerrain
            ? endlessCountryMazeKindFor(seed, countryX, countryY, biomeKey, landscape)
            : (int)Math.floor(hash(ENDLESS_COUNTRY_SALT.mazeKind) * ENDLESS_COUNTRY_MAZE_KIND_COUNT);
        double scaleRoll = hash(ENDLESS_COUNTRY_SALT.mazeScale);
        double phaseRoll = hash(ENDLESS_COUNTRY_SALT.mazePhase);
        // V11's country silhouette carries fewer, more decisive mountain bodies. Its internal grammar therefore
        // opens the distance between ribs and narrows the ribs themselves: a mountain remains a landmark instead
        // of every camera view becoming the same dense wall maze. Historical plans retain their exact dimensions.
        double wallRoll = hash(ENDLESS_COUNTRY_SALT.mazeScale ^ 0x9e3779b9);
        double wallWidth = wildTerrain ? 1 + wallRoll * 0.48 : 1.05 + wallRoll * 0.72;
        return new EndlessCountryMazePlan
        {
            kind = kind,
            angle =
                shelfAngle +
                (phaseRoll - 0.5) *
                    (kind == EndlessCountryMazeKind.BrokenGrid ||
                    kind == EndlessCountryMazeKind.CourtyardChain
                        ? 0.42
                        : 1.1),
            spacing = wildTerrain
                ? 12.25 + scaleRoll * 5.35 + openness * 1.45
                : 10.5 + scaleRoll * 4.8 + openness * 1.1,
            wallWidth = wallWidth,
            gateWidth =
                wallWidth +
                (wildTerrain ? 0.72 : 0.52) +
                hash(ENDLESS_COUNTRY_SALT.mazeScale ^ 0x85ebca6b) * (wildTerrain ? 0.72 : 0.58),
            warp = 1.8 + hash(ENDLESS_COUNTRY_SALT.mazeDetail) * 3.6,
            phase = phaseRoll * Math.PI * 2,
        };
    }
}
