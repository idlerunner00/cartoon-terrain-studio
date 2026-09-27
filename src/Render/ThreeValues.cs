// Engine-free stand-ins for the three.js value types the ported render modules keep (uniform holders, colours,
// vectors). Ported literally from three@0.185.1 (`src/math/Color.js`, `ColorManagement.js`, `Vector3.js`,
// `Vector4.js`) — only the members the ported modules use, plus the colour-space conversions they depend on.
//
// PORT NOTES
// * three's `ColorManagement` is enabled with the linear-sRGB working space (the client never changes it), so
//   `new Color(hex)` / `setHex(hex)` convert sRGB → linear and `setRGB` stores working-space (linear) values as-is.
// * A three uniform `{ value: X }` is shared by IDENTITY between materials; <see cref="ThreeUniform{T}"/> keeps that
//   (a reference type holding a mutable `value`), so one write reaches every consumer exactly like the original.
// * `getHex` returns a double because three returns a JS number (NaN channels propagate as NaN).
using System;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>A three.js uniform holder: `{ value: T }`, shared by reference between every material that binds it.</summary>
public sealed class ThreeUniform<T>
{
    public T value;

    public ThreeUniform(T value)
    {
        this.value = value;
    }
}

/// <summary>three.js `ColorManagement` restricted to the two colour spaces the client uses.</summary>
public static class ThreeColorManagement
{
    /// <summary>three `SRGBColorSpace`.</summary>
    public const string SRGBColorSpace = "srgb";
    /// <summary>three `LinearSRGBColorSpace`.</summary>
    public const string LinearSRGBColorSpace = "srgb-linear";

    /// <summary>`ColorManagement.enabled` (true in three ≥ r152 and never changed by the client).</summary>
    public const bool enabled = true;

    /// <summary>`ColorManagement.workingColorSpace`.</summary>
    public const string workingColorSpace = LinearSRGBColorSpace;

    /// <summary>
    /// `ColorManagement.convert`. Both defined spaces share the Rec.709 primaries (the same array object in three),
    /// so the XYZ matrix step never runs; only the transfer functions do.
    /// </summary>
    public static ThreeColor convert(ThreeColor color, string sourceColorSpace, string targetColorSpace)
    {
        if (enabled == false || sourceColorSpace == targetColorSpace || string.IsNullOrEmpty(sourceColorSpace) ||
            string.IsNullOrEmpty(targetColorSpace))
        {
            return color;
        }

        if (transferOf(sourceColorSpace) == SRGBTransfer)
        {
            color.r = SRGBToLinear(color.r);
            color.g = SRGBToLinear(color.g);
            color.b = SRGBToLinear(color.b);
        }

        if (transferOf(targetColorSpace) == SRGBTransfer)
        {
            color.r = LinearToSRGB(color.r);
            color.g = LinearToSRGB(color.g);
            color.b = LinearToSRGB(color.b);
        }

        return color;
    }

    public static ThreeColor colorSpaceToWorking(ThreeColor color, string sourceColorSpace) =>
        convert(color, sourceColorSpace, workingColorSpace);

    private const string SRGBTransfer = "srgb";
    private const string LinearTransfer = "linear";

    /// <summary>`this.spaces[colorSpace].transfer`. three throws on an undefined space; so does this.</summary>
    private static string transferOf(string colorSpace) => colorSpace switch
    {
        LinearSRGBColorSpace => LinearTransfer,
        SRGBColorSpace => SRGBTransfer,
        _ => throw new InvalidOperationException($"ColorManagement: undefined color space '{colorSpace}'"),
    };

    public static double SRGBToLinear(double c)
    {
        return (c < 0.04045) ? c * 0.0773993808 : Math.pow(c * 0.9478672986 + 0.0521327014, 2.4);
    }

    public static double LinearToSRGB(double c)
    {
        return (c < 0.0031308) ? c * 12.92 : 1.055 * (Math.pow(c, 0.41666)) - 0.055;
    }
}

/// <summary>three.js `Color` (r, g, b in the linear working space).</summary>
public sealed class ThreeColor
{
    public double r = 1;
    public double g = 1;
    public double b = 1;

    /// <summary>`new Color()` — white.</summary>
    public ThreeColor() { }

    /// <summary>`new Color(hex)` → `set(hex)` → `setHex(hex)` (an sRGB hex, converted to the working space).</summary>
    public ThreeColor(double hex)
    {
        setHex(hex);
    }

    /// <summary>`new Color(r, g, b)` → `setRGB(r, g, b)` (working-space components).</summary>
    public ThreeColor(double r, double g, double b)
    {
        setRGB(r, g, b);
    }

    /// <summary>`set(hex)` with a number.</summary>
    public ThreeColor set(double hex)
    {
        setHex(hex);
        return this;
    }

    /// <summary>`set(color)` with another Color.</summary>
    public ThreeColor set(ThreeColor color)
    {
        copy(color);
        return this;
    }

    public ThreeColor setHex(double hex, string colorSpace = ThreeColorManagement.SRGBColorSpace)
    {
        hex = Math.floor(hex);

        this.r = (double)((Js.ToInt32(hex) >> 16) & 255) / 255;
        this.g = (double)((Js.ToInt32(hex) >> 8) & 255) / 255;
        this.b = (double)(Js.ToInt32(hex) & 255) / 255;

        ThreeColorManagement.colorSpaceToWorking(this, colorSpace);

        return this;
    }

    public ThreeColor setRGB(double r, double g, double b, string colorSpace = ThreeColorManagement.workingColorSpace)
    {
        this.r = r;
        this.g = g;
        this.b = b;

        ThreeColorManagement.colorSpaceToWorking(this, colorSpace);

        return this;
    }

    public ThreeColor copy(ThreeColor color)
    {
        this.r = color.r;
        this.g = color.g;
        this.b = color.b;
        return this;
    }
}

/// <summary>three.js `Vector3` (fields only plus the setters the ported modules use).</summary>
public sealed class ThreeVector3
{
    public double x;
    public double y;
    public double z;

    public ThreeVector3(double x = 0, double y = 0, double z = 0)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public ThreeVector3 set(double x, double y, double z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        return this;
    }

    public ThreeVector3 clone() => new ThreeVector3(this.x, this.y, this.z);

    public ThreeVector3 copy(ThreeVector3 v)
    {
        this.x = v.x;
        this.y = v.y;
        this.z = v.z;
        return this;
    }
}

/// <summary>three.js `Vector4`. NB: three's default `w` is 1.</summary>
public sealed class ThreeVector4
{
    public double x;
    public double y;
    public double z;
    public double w;

    public ThreeVector4(double x = 0, double y = 0, double z = 0, double w = 1)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    public ThreeVector4 set(double x, double y, double z, double w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
        return this;
    }
}
