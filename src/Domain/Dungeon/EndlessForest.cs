// Port of packages/shared/src/domain/dungeon/endlessForest.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * World-space forest composition for Endless runs.
 *
 * A tree is not a decoration dot. Trees grow as communities: broad woodland bodies with broken edges and
 * internal glades, compact copses, wind-shaped belts, and the occasional deliberate solitary tree. The field
 * is evaluated in absolute tile coordinates, so a formation keeps its shape while 32x32 streaming chunks are
 * loaded independently.
 */

public static class EndlessForestFormation
{
    public const string Woodland = "woodland";
    public const string Copse = "copse";
    public const string Windbreak = "windbreak";
    public const string Solitary = "solitary";
}

public sealed class EndlessForestSample
{
    public string formation = "";
    /// <summary>Stable identity shared by every tile that belongs to this formation (a `>>> 0` value).</summary>
    public double id;
    /// <summary>0 at the broken outer fringe, 1 in the formation's densest interior.</summary>
    public double strength;
    /// <summary>Natural canopy spacing in tiles before the biome's own crown-spacing rule is applied.</summary>
    public double spacing;

    public EndlessForestSample Clone() => (EndlessForestSample)MemberwiseClone();
}

public static partial class EndlessForest
{
    // Salts are the ToInt32 images of the original hex literals, which is what `^` sees in JS.
    private static class SALT
    {
        public const int woodland = 0x6a09e667;
        public const int copse = unchecked((int)0xbb67ae85);
        public const int windbreak = 0x3c6ef372;
        public const int solitary = unchecked((int)0xa54ff53a);
        public const int shape = 0x510e527f;
        public const int clearing = unchecked((int)0x9b05688c);
    }

    private static double hash(double seed, int salt, double x, double y)
    {
        return latticeHash(Js.ToUint32(Js.ToInt32(seed) ^ salt), x, y);
    }

    private static double smooth01(double value)
    {
        double t = value <= 0 ? 0 : value >= 1 ? 1 : value;
        return t * t * (3 - 2 * t);
    }

    private static double formationId(double seed, int salt, double gx, double gy)
    {
        return (uint)(
            Math.imul(Js.ToUint32(hash(seed, salt ^ SALT.shape, gx, gy) * 0x1_0000_0000L), unchecked((int)0x9e3779b1)) ^
            Math.imul(Js.ToInt32(gx), unchecked((int)0x85ebca6b)) ^
            Math.imul(Js.ToInt32(gy), unchecked((int)0xc2b2ae35)) ^
            salt);
    }

    private static double distanceToSegment(double px, double py, double halfLength)
    {
        double along = Math.max(-halfLength, Math.min(halfLength, px));
        return Math.hypot(px - along, py);
    }

    /// <summary>Resolve the strongest authored forest structure at one world tile.</summary>
    public static EndlessForestSample? endlessForestAt(double seed, double x, double y)
    {
        EndlessForestSample? best = null;
        void offer(string formation, double id, double rawStrength, double spacing)
        {
            double strength = smooth01(rawStrength);
            if (strength <= 0.015 || (best != null && strength <= best.strength)) return;
            best = new EndlessForestSample { formation = formation, id = id, strength = strength, spacing = spacing };
        }

        // Large irregular bodies exceed a streamed chunk, so a vista can contain a genuine forest mass.
        const double woodlandCell = 56;
        double woodlandGx = Math.floor(x / woodlandCell);
        double woodlandGy = Math.floor(y / woodlandCell);
        for (double gy = woodlandGy - 1; gy <= woodlandGy + 1; gy++)
        {
            for (double gx = woodlandGx - 1; gx <= woodlandGx + 1; gx++)
            {
                if (hash(seed, SALT.woodland, gx, gy) >= 0.55) continue;
                double cx =
                    (gx + 0.5) * woodlandCell + (hash(seed, SALT.woodland ^ 0x243f6a88, gx, gy) - 0.5) * 21;
                double cy =
                    (gy + 0.5) * woodlandCell + (hash(seed, SALT.woodland ^ unchecked((int)0x85a308d3), gx, gy) - 0.5) * 21;
                double angle = hash(seed, SALT.shape, gx, gy) * Math.PI;
                double cos = Math.cos(angle);
                double sin = Math.sin(angle);
                double dx = x - cx;
                double dy = y - cy;
                double u = dx * cos + dy * sin;
                double v = -dx * sin + dy * cos;
                double major = 19 + hash(seed, SALT.shape ^ 0x13198a2e, gx, gy) * 12;
                double minor = 13 + hash(seed, SALT.shape ^ 0x03707344, gx, gy) * 9;
                double theta = Math.atan2(v / minor, u / major);
                double edgeWobble =
                    1 +
                    Math.sin(
                        theta * (3 + Math.floor(hash(seed, SALT.shape ^ unchecked((int)0xa4093822), gx, gy) * 3)) + angle) *
                    0.11 +
                    (valueNoise(Js.ToUint32(Js.ToInt32(seed) ^ SALT.shape), x, y, 6.5) - 0.5) * 0.24;
                double normalized = Math.hypot(u / major, v / minor) / edgeWobble;
                double strength = (1.08 - normalized) / 0.34;

                // Most large woods contain one off-centre glade, framed by a deliberate ring of trees.
                if (hash(seed, SALT.clearing, gx, gy) < 0.72)
                {
                    double gladeU = (hash(seed, SALT.clearing ^ 0x299f31d0, gx, gy) - 0.5) * major * 0.75;
                    double gladeV = (hash(seed, SALT.clearing ^ 0x082efa98, gx, gy) - 0.5) * minor * 0.7;
                    double gladeRadius = 2.8 + hash(seed, SALT.clearing ^ unchecked((int)0xec4e6c89), gx, gy) * 3.8;
                    double gladeDistance = Math.hypot(u - gladeU, v - gladeV);
                    strength *= smooth01((gladeDistance - gladeRadius * 0.55) / (gladeRadius * 0.7));
                }
                offer(
                    EndlessForestFormation.Woodland,
                    formationId(seed, SALT.woodland, gx, gy),
                    strength,
                    // A woodland core needs a connected crown mass. The former 2.9..4.35 spacing made diagonal anchors
                    // read as unrelated specimen trees at gameplay scale even though the field called them one wood.
                    1 + (1 - smooth01(strength)) * 0.55);
            }
        }

        // Compact tree islands contrast with the broad woodland masses without peppering every open field.
        const double copseCell = 18;
        double copseGx = Math.floor(x / copseCell);
        double copseGy = Math.floor(y / copseCell);
        for (double gy = copseGy - 1; gy <= copseGy + 1; gy++)
        {
            for (double gx = copseGx - 1; gx <= copseGx + 1; gx++)
            {
                if (hash(seed, SALT.copse, gx, gy) >= 0.52) continue;
                double cx = (gx + 0.5) * copseCell + (hash(seed, SALT.copse ^ 0x452821e6, gx, gy) - 0.5) * 7;
                double cy = (gy + 0.5) * copseCell + (hash(seed, SALT.copse ^ 0x38d01377, gx, gy) - 0.5) * 7;
                double radius = 4 + hash(seed, SALT.shape ^ unchecked((int)0xbe5466cf), gx, gy) * 2.6;
                double angle = Math.atan2(y - cy, x - cx);
                double wobble = 1 + Math.sin(angle * 3 + radius) * 0.13;
                double normalized = Math.hypot(x - cx, y - cy) / (radius * wobble);
                double strength = (1.1 - normalized) / 0.42;
                offer(
                    EndlessForestFormation.Copse,
                    formationId(seed, SALT.copse, gx, gy),
                    strength,
                    1 + (1 - smooth01(strength)) * 0.42);
            }
        }

        // Broken, linear windbreaks state wind, banks and field boundaries without becoming walls.
        const double beltCell = 52;
        double beltGx = Math.floor(x / beltCell);
        double beltGy = Math.floor(y / beltCell);
        for (double gy = beltGy - 1; gy <= beltGy + 1; gy++)
        {
            for (double gx = beltGx - 1; gx <= beltGx + 1; gx++)
            {
                if (hash(seed, SALT.windbreak, gx, gy) >= 0.3) continue;
                double cx =
                    (gx + 0.5) * beltCell + (hash(seed, SALT.windbreak ^ unchecked((int)0xc0ac29b7), gx, gy) - 0.5) * 19;
                double cy =
                    (gy + 0.5) * beltCell + (hash(seed, SALT.windbreak ^ unchecked((int)0xc97c50dd), gx, gy) - 0.5) * 19;
                double angle = hash(seed, SALT.shape ^ 0x3f84d5b5, gx, gy) * Math.PI;
                double cos = Math.cos(angle);
                double sin = Math.sin(angle);
                double dx = x - cx;
                double dy = y - cy;
                double u = dx * cos + dy * sin;
                double v = -dx * sin + dy * cos;
                double halfLength = 13 + hash(seed, SALT.shape ^ unchecked((int)0xb5470917), gx, gy) * 11;
                double halfWidth = 1.7 + hash(seed, SALT.shape ^ unchecked((int)0x9216d5d9), gx, gy) * 1.8;
                double distance = distanceToSegment(u, v, halfLength);
                double broken =
                    0.72 + valueNoise(Js.ToUint32(Js.ToInt32(seed) ^ SALT.windbreak), u + gx * 19, gy * 23, 5) * 0.46;
                double strength = (halfWidth * broken - distance) / Math.max(1.4, halfWidth * 0.72) + 0.35;
                offer(
                    EndlessForestFormation.Windbreak,
                    formationId(seed, SALT.windbreak, gx, gy),
                    strength,
                    1 + (1 - smooth01(strength)) * 0.5);
            }
        }

        // A jittered low-frequency lattice authors occasional solitary trees in the negative space.
        const double solitaryCell = 40;
        double solitaryGx = Math.floor(x / solitaryCell);
        double solitaryGy = Math.floor(y / solitaryCell);
        for (double gy = solitaryGy - 1; gy <= solitaryGy + 1; gy++)
        {
            for (double gx = solitaryGx - 1; gx <= solitaryGx + 1; gx++)
            {
                if (hash(seed, SALT.solitary, gx, gy) >= 0.5) continue;
                double cx =
                    (gx + 0.5) * solitaryCell + (hash(seed, SALT.solitary ^ unchecked((int)0x8979fb1b), gx, gy) - 0.5) * 16;
                double cy =
                    (gy + 0.5) * solitaryCell + (hash(seed, SALT.solitary ^ unchecked((int)0xd1310ba6), gx, gy) - 0.5) * 16;
                double strength = (1.45 - Math.hypot(x - cx, y - cy)) / 0.95;
                offer(
                    EndlessForestFormation.Solitary,
                    formationId(seed, SALT.solitary, gx, gy),
                    strength * 0.82,
                    8);
            }
        }

        return best;
    }
}
