// Fluitown extension — NOT a port of the original. The comic look's foliage (set in TerrainSceneRenderer.Initialize).
using System.Collections.Generic;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.WorldPropPrimitives;
using Math = Fluitown.Runtime.JsMath;
using PropTileset = Fluitown.Render.TerrainMaterialTileset;

namespace Fluitown.Render;

/// <summary>
/// Foliage for the comic look: every foliage mass the original builds with <see cref="WorldPropPrimitives.propClump"/>
/// (tree crowns, bushes, shrubs) becomes one smooth, closed puff instead of three turned, flat-shaded frustum bands.
///
/// Why: the original's clumps are drawn for its fixed oblique camera and its paper-and-hatching shading — faceted,
/// camera-culled (only the south half exists) and with band rings turned against each other. Under the comic look's
/// light ramp and world ink the facets read as crystals, the turned rings leave small ledges that the ink draws as
/// dashes on every clump, and the Flui perspective looks into the missing back halves. A puff with smooth normals
/// gets the ramp's clean toon bands (lit cap, shaded belly), a single silhouette for the ink, and it is closed for the
/// perspective. Its pigment runs from the original's side colour at the belly to its top colour at the cap, and its
/// wind weights rise towards the cap like the original bands'.
///
/// The tree's shadow proxy follows the crown: one slightly smaller puff per outer mass instead of the original's slab,
/// so the ground gets the crown's round, broken silhouette. The proxies stay inside the visible puffs, so a mass never
/// shadows itself (the original's reason for the slab), only the masses below and behind it.
/// </summary>
public static class FluitownSoftFoliage
{
    /// <summary>Set before the first bake (TerrainSceneRenderer.Initialize); bakes run on worker threads.</summary>
    public static volatile bool Enabled;

    private static readonly double SURF_FLOOR = TERRAIN_SURFACE_PATTERN.floor;

    /// <summary>
    /// One foliage mass as a closed puff: widest a little below its middle, a slightly flattened belly, a rounded
    /// cap. Occupies the original clump's footprint (base <paramref name="cy"/>, height, waist radius).
    /// </summary>
    public static void propSoftClump(
        PropSurfaceBuilder builder,
        double cx,
        double cy,
        double cz,
        double radius,
        double height,
        int sides,
        double rot,
        int topHex,
        int sideHex,
        double wind = 0,
        double squash = 1)
    {
        if (radius <= 0.01 || height <= 0.01) return;
        // Richer pigment: the original's foliage is authored for its paper wash, which the comic ramp does not add.
        topHex = saturate(topHex, 1.2);
        sideHex = saturate(sideHex, 1.3);
        // Blossom accents (5 sides) are a few pixels wide; crown and bush masses get a round silhouette.
        int around = sides <= 5 ? 9 : Math.max(12, sides + 5);
        const int bands = 5;
        double centreY = cy + height * 0.46;
        double capRise = height * 0.54;
        double bellyDrop = height * 0.46 * (0.84 + (squash - 1) * 0.1);
        double rx = radius * 1.04;

        // Ring vertices, bottom pole (k = 0) to top pole (k = bands).
        var points = new P3[bands + 1][];
        var normals = new double[bands + 1][];
        var colours = new int[bands + 1];
        var winds = new double[bands + 1];
        for (int k = 0; k <= bands; k++)
        {
            double phi = -Math.PI / 2 + Math.PI * k / bands;
            double s = Math.sin(phi);
            double c = Math.cos(phi);
            double ry = s >= 0 ? capRise : bellyDrop;
            double y = centreY + s * ry;
            points[k] = new P3[around];
            normals[k] = new double[around * 3];
            for (int i = 0; i < around; i++)
            {
                double theta = rot + (i / (double)around) * PROP_TAU;
                double ct = Math.cos(theta);
                double st = Math.sin(theta);
                points[k][i] = new P3(cx + ct * c * rx, y, cz + st * c * rx);
                // Ellipsoid normal (x/a², y/b², z/c²), normalised.
                double nx = ct * c / rx;
                double ny = s / ry;
                double nz = st * c / rx;
                double len = Math.hypot(nx, ny, nz);
                normals[k][i * 3] = nx / len;
                normals[k][i * 3 + 1] = ny / len;
                normals[k][i * 3 + 2] = nz / len;
            }
            // Belly in the side pigment, cap in the top pigment; the ramp does the rest.
            double t = (s + 1) * 0.5;
            colours[k] = mix(sideHex, topHex, t * t * (3 - 2 * t));
            winds[k] = wind * (0.7 + 0.3 * t);
        }

        void Face(int k0, int i0, int k1, int i1, int k2, int i2, int k3 = -1, int i3 = -1)
        {
            int count = k3 < 0 ? 3 : 4;
            var poly = new P3[count];
            var nx = new double[count];
            var ny = new double[count];
            var nz = new double[count];
            var hex = new double[count];
            var w = new double[count];
            void Put(int slot, int k, int i)
            {
                poly[slot] = points[k][i];
                nx[slot] = normals[k][i * 3];
                ny[slot] = normals[k][i * 3 + 1];
                nz[slot] = normals[k][i * 3 + 2];
                hex[slot] = colours[k];
                w[slot] = winds[k];
            }
            Put(0, k0, i0);
            Put(1, k1, i1);
            Put(2, k2, i2);
            if (count == 4) Put(3, k3, i3);
            builder.addSurface(poly, nx, ny, nz, hex, SURF_FLOOR, 0.05, null, wind > 0 ? new NumberOrArray(w) : (NumberOrArray?)null);
        }

        for (int i = 0; i < around; i++)
        {
            int j = (i + 1) % around;
            // Bottom and top caps as fans around the poles (the pole ring collapses to one point).
            Face(1, i, 0, i, 1, j);
            Face(bands, i, bands - 1, i, bands - 1, j);
            for (int k = 1; k < bands - 1; k++) Face(k + 1, i, k + 1, j, k, j, k, i);
        }
    }

    /// <summary>Scales a colour's chroma around its luminance (1 = unchanged), in the pigment's own sRGB encoding.</summary>
    public static int saturate(int hex, double amount)
    {
        double r = (hex >> 16) & 0xff;
        double g = (hex >> 8) & 0xff;
        double b = hex & 0xff;
        double l = r * 0.2126 + g * 0.7152 + b * 0.0722;
        int Channel(double v) => (int)Math.max(0, Math.min(255, Math.round(l + (v - l) * amount)));
        return (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
    }

    /// <summary>
    /// A flower / fruit / lantern accent of a crown in the comic look. The original punctuates crowns with small
    /// masses in the profile's bloom channel, some of which resolve near ink; under the comic ramp those read as
    /// holes. Dark accents become a light blossom of the crown's own pigment instead; coloured ones stay.
    /// </summary>
    public static int softBloomHex(int bloomHex, int crownHex, PropTileset tileset)
    {
        double r = ((bloomHex >> 16) & 0xff) / 255.0;
        double g = ((bloomHex >> 8) & 0xff) / 255.0;
        double b = (bloomHex & 0xff) / 255.0;
        double luminance = r * 0.2126 + g * 0.7152 + b * 0.0722;
        return luminance < 0.2 ? mix(crownHex, tileset.peakTint, 0.55) : bloomHex;
    }

    /// <summary>
    /// The tree's sun-shadow proxy for the comic look: the bole as in the original, and one puff at 82 % of each outer
    /// crown mass (rank 0), so the crown throws its own round, broken shadow and shades the masses below it.
    /// </summary>
    public static void addSoftTreeShadowCaster(
        PropGeometryBuilder builder,
        TerrainTreeLike tree,
        TreeSkeleton skeleton,
        double cx,
        double y0,
        double cz)
    {
        double crownLow = double.PositiveInfinity;
        foreach (TreeCrownMass mass in skeleton.masses) crownLow = Math.min(crownLow, mass.y);
        if (double.IsPositiveInfinity(crownLow)) return;
        double boleTop = y0 + Math.max(skeleton.height * 0.12, crownLow * 0.9);
        propShadowVolume(
            builder,
            cx,
            cz,
            y0,
            boleTop,
            skeleton.baseRadius * 1.1,
            skeleton.baseRadius * 0.7,
            4,
            tree.phase * PROP_TAU);
        foreach (TreeCrownMass mass in skeleton.masses)
        {
            if (mass.rank != 0) continue;
            addPuffCaster(builder, cx + mass.x, y0 + mass.y, cz + mass.z, mass.r * 0.82, mass.h * 0.82);
        }
    }

    private static void addPuffCaster(PropGeometryBuilder builder, double cx, double cy, double cz, double radius, double height)
    {
        if (radius < 0.05 || height < 0.05) return;
        const int around = 10;
        double centreY = cy + height * 0.5;
        double half = height * 0.5;
        List<P3> Ring(double y, double r)
        {
            var ring = new List<P3>(around);
            for (int i = 0; i < around; i++)
            {
                double theta = (i / (double)around) * PROP_TAU;
                ring.Add(new P3(cx + Math.cos(theta) * r, y, cz + Math.sin(theta) * r));
            }
            return ring;
        }
        List<P3> low = Ring(centreY - half * 0.6, radius * 0.75);
        List<P3> mid = Ring(centreY, radius);
        List<P3> high = Ring(centreY + half * 0.6, radius * 0.75);
        builder.addShadowCaster(low);
        builder.addShadowCaster(high);
        var quad = new P3[4];
        for (int i = 0; i < around; i++)
        {
            int j = (i + 1) % around;
            quad[0] = mid[i]; quad[1] = mid[j]; quad[2] = low[j]; quad[3] = low[i];
            builder.addShadowCaster(quad);
            quad[0] = high[i]; quad[1] = high[j]; quad[2] = mid[j]; quad[3] = mid[i];
            builder.addShadowCaster(quad);
        }
    }
}
