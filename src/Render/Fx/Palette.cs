// Port of packages/client/src/render/fx/palette.ts — keep in lockstep with the original.
using System.Runtime.CompilerServices;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// Tiny color helpers for procedural FX. Colors are packed `0xRRGGBB` integers — the same format the
/// shared `SkillFxDescriptor` uses — so the renderer can blend toward white (hot cores), darken, or
/// cross-fade a skill's primary/secondary palette without any assets.
/// </summary>
/// <remarks>
/// Colour arguments are JS numbers read through bitwise operators, i.e. ECMAScript ToInt32. The <c>int</c>
/// overloads are the fast path for already-packed colours; the <c>double</c> overloads apply ToInt32 first so a
/// caller holding a colour in a <c>double</c> (or a non-integral / NaN value) gets exactly the JS result.
/// </remarks>
public static partial class Palette
{
    /// <summary>Linear blend from `a` to `b` (t = 0 → a, t = 1 → b) in straight RGB.</summary>
    public static int mix(int a, int b, double t)
    {
        int ar = (a >> 16) & 0xff;
        int ag = (a >> 8) & 0xff;
        int ab = a & 0xff;
        int br = (b >> 16) & 0xff;
        int bg = (b >> 8) & 0xff;
        int bb = b & 0xff;
        double r = Math.round(ar + (br - ar) * t);
        double g = Math.round(ag + (bg - ag) * t);
        double bl = Math.round(ab + (bb - ab) * t);
        return (Js.ToInt32(r) << 16) | (Js.ToInt32(g) << 8) | Js.ToInt32(bl);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int mix(double a, double b, double t) => mix(Js.ToInt32(a), Js.ToInt32(b), t);

    /// <summary>Push a color toward white (for hot cores / high-rank flashes).</summary>
    public static int lighten(double c, double t)
    {
        return mix(c, 0xffffff, t);
    }

    /// <summary>Push a color toward black.</summary>
    public static int darken(double c, double t)
    {
        return mix(c, 0x000000, t);
    }

    /// <summary>
    /// Decompose a packed `0xRRGGBB` color into HSL — hue in `[0,360)`, saturation &amp; lightness in `[0,1]`.
    /// Pairs with <see cref="hslToRgb"/> so callers can lift a color's LIGHTNESS while holding its hue/saturation —
    /// the only way to brighten a tinted material without washing the hue toward grey (which straight RGB
    /// `lighten` toward white does). Used by the terrain palette to derive bright-but-biome-hued rock.
    /// </summary>
    public static (double h, double s, double l) rgbToHsl(double c)
    {
        int ci = Js.ToInt32(c);
        double r = (double)((ci >> 16) & 0xff) / 255;
        double g = (double)((ci >> 8) & 0xff) / 255;
        double b = (double)(ci & 0xff) / 255;
        double mx = Math.max(r, g, b);
        double mn = Math.min(r, g, b);
        double l = (mx + mn) / 2;
        if (mx == mn) return (0, 0, l);
        double d = mx - mn;
        double s = d / (1 - Math.abs(2 * l - 1));
        double h;
        if (mx == r) h = ((g - b) / d) % 6;
        else if (mx == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return ((h * 60 + 360) % 360, s, l);
    }

    /// <summary>
    /// Compose a packed `0xRRGGBB` color from HSL (hue in degrees, saturation &amp; lightness in `[0,1]`). Inverse
    /// of <see cref="rgbToHsl"/>; clamps each channel to byte range.
    /// </summary>
    public static int hslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.abs(2 * l - 1)) * s;
        double hh = (((h % 360) + 360) % 360) / 60;
        double x = c * (1 - Math.abs((hh % 2) - 1));
        double m = l - c / 2;
        double r = 0;
        double g = 0;
        double b = 0;
        if (hh < 1) (r, g, b) = (c, x, 0);
        else if (hh < 2) (r, g, b) = (x, c, 0);
        else if (hh < 3) (r, g, b) = (0, c, x);
        else if (hh < 4) (r, g, b) = (0, x, c);
        else if (hh < 5) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        double to(double v) => Math.max(0, Math.min(255, Math.round((v + m) * 255)));
        return (Js.ToInt32(to(r)) << 16) | (Js.ToInt32(to(g)) << 8) | Js.ToInt32(to(b));
    }
}
