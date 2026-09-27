using System;
using Godot;

namespace Fluitown.GodotApp.Rendering;

/// <summary>
/// Turns the engine-free float lanes of one compiled bake tile into Godot meshes.
///
/// Attribute mapping: colour and every custom lane travel in float
/// <c>CUSTOMn</c> channels because Godot stores <c>ARRAY_COLOR</c> as 8-bit, while the original keeps all
/// of them in float32. Triangles are re-wound from three's counter-clockwise to Godot's clockwise front.
/// </summary>
public static class TerrainLaneMeshes
{
    private static Mesh.ArrayFormat CustomFormat(int channel, Mesh.ArrayCustomFormat format)
    {
        int shift = channel switch
        {
            0 => (int)Mesh.ArrayFormat.FormatCustom0Shift,
            1 => (int)Mesh.ArrayFormat.FormatCustom1Shift,
            2 => (int)Mesh.ArrayFormat.FormatCustom2Shift,
            _ => (int)Mesh.ArrayFormat.FormatCustom3Shift,
        };
        return (Mesh.ArrayFormat)((long)format << shift);
    }

    // Every lane reaches Godot as a Variant holding a native packed array: Variant.CreateFrom(span) copies the span
    // into a new PackedVector3Array/PackedVector2Array/PackedFloat32Array/PackedInt32Array — exactly what the implicit
    // conversion of a managed array did (it forwards to the same span overload). So no managed copy is made at all:
    // Godot's Vector2/Vector3 are sequential float structs (single-precision build), the payload lanes are
    // reinterpreted in place, and interleaved/re-wound lanes are written into ArrayPool scratch that is returned as
    // soon as the Variant exists. The spans are exact-length, so every mesh keeps its size. These run on the tile-prep
    // tasks (GodotTileSink.Prepare); a tile's lane copies used to be several MB of large-object garbage each.
    private static Variant Vec3(float[] data) =>
        Variant.CreateFrom(System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vector3>(data.AsSpan(0, data.Length / 3 * 3)));

    private static Variant Vec2(float[] data) =>
        Variant.CreateFrom(System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vector2>(data.AsSpan(0, data.Length / 2 * 2)));

    /// <summary>One complete per-vertex lane into one RGBA float custom channel.</summary>
    private static Variant Rgba(int vertices, float[]? data, int stride, float fill = 1f)
    {
        // A single complete RGBA lane is already in channel layout.
        if (stride == 4 && data is { } whole && whole.Length == vertices * 4) return Variant.CreateFrom(whole.AsSpan());
        return Rgba(vertices, data, stride, null, 0, fill);
    }

    /// <summary>Interleave one or two per-vertex float lanes into one RGBA float custom channel.</summary>
    private static Variant Rgba(int vertices, float[]? first, int firstStride, float[]? second, int secondStride, float fill = 1f)
    {
        int length = vertices * 4;
        float[] result = System.Buffers.ArrayPool<float>.Shared.Rent(Math.Max(1, length));
        try
        {
            for (int v = 0; v < vertices; v++)
            {
                int slot = 0;
                for (int c = 0; c < firstStride; c++)
                {
                    result[v * 4 + slot] = first != null && first.Length >= (v + 1) * firstStride ? first[v * firstStride + c] : 0f;
                    slot++;
                }
                for (int c = 0; c < secondStride; c++)
                {
                    result[v * 4 + slot] = second != null && second.Length >= (v + 1) * secondStride ? second[v * secondStride + c] : 0f;
                    slot++;
                }
                for (; slot < 4; slot++) result[v * 4 + slot] = fill;
            }
            return Variant.CreateFrom(result.AsSpan(0, length));
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(result);
        }
    }

    /// <summary>
    /// three treats counter-clockwise as front, Godot clockwise: every triangle's second and third index swap, so
    /// Godot's cull_back keeps exactly three's front faces and FRONT_FACING equals gl_FrontFacing.
    /// </summary>
    private static Variant ThreeWinding(uint[] index)
    {
        int length = index.Length;
        int[] result = System.Buffers.ArrayPool<int>.Shared.Rent(Math.Max(1, length));
        try
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<uint, int>(index.AsSpan()).CopyTo(result);
            for (int i = 0; i + 2 < length; i += 3) (result[i + 1], result[i + 2]) = (result[i + 2], result[i + 1]);
            return Variant.CreateFrom(result.AsSpan(0, length));
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(result);
        }
    }

    private static Godot.Collections.Array NewArrays()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        return arrays;
    }

    // The array takes its own native Variant reference. Release the temporary packed-array wrapper immediately;
    // otherwise even a disposed surface array leaves its large lane stores waiting for managed finalization.
    private static void Put(Godot.Collections.Array arrays, Mesh.ArrayType lane, Variant value)
    {
        using (value) arrays[(int)lane] = value;
    }

    private static ArrayMesh Commit(Godot.Collections.Array arrays, Mesh.ArrayFormat flags)
    {
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
        return mesh;
    }

    /// <summary>Surface lane: VERTEX, NORMAL, UV = aSurf, CUSTOM0 = (colour.rgb, aEmissive), CUSTOM1 = aGround.</summary>
    public static ArrayMesh? Surface(float[] position, float[] normal, float[] color, float[] surf, float[] emissive, float[] ground, uint[] index)
    {
        int n = position.Length / 3;
        if (n == 0 || index.Length == 0) return null;
        using var arrays = NewArrays();
        Put(arrays, Mesh.ArrayType.Vertex, Vec3(position));
        Put(arrays, Mesh.ArrayType.Normal, Vec3(normal));
        Put(arrays, Mesh.ArrayType.TexUV, Vec2(surf));
        Put(arrays, Mesh.ArrayType.Custom0, Rgba(n, color, 3, emissive, 1));
        Put(arrays, Mesh.ArrayType.Custom1, Rgba(n, ground, 4));
        Put(arrays, Mesh.ArrayType.Index, ThreeWinding(index));
        return Commit(arrays, CustomFormat(0, Mesh.ArrayCustomFormat.RgbaFloat) | CustomFormat(1, Mesh.ArrayCustomFormat.RgbaFloat));
    }

    /// <summary>Water lane: VERTEX, NORMAL, UV = fold, CUSTOM0 = (colour.rgb, 1), CUSTOM1 = aWater, CUSTOM2 = (reflection, 0).</summary>
    public static ArrayMesh? Water(float[] position, float[] normal, float[] color, float[] water, float[] fold, float[] reflection, uint[] index)
    {
        int n = position.Length / 3;
        if (n == 0 || index.Length == 0) return null;
        using var arrays = NewArrays();
        Put(arrays, Mesh.ArrayType.Vertex, Vec3(position));
        Put(arrays, Mesh.ArrayType.Normal, Vec3(normal));
        Put(arrays, Mesh.ArrayType.TexUV, Vec2(fold));
        Put(arrays, Mesh.ArrayType.Custom0, Rgba(n, color, 3));
        Put(arrays, Mesh.ArrayType.Custom1, Rgba(n, water, 4));
        Put(arrays, Mesh.ArrayType.Custom2, Rgba(n, reflection, 3, 0f));
        Put(arrays, Mesh.ArrayType.Index, ThreeWinding(index));
        return Commit(arrays, CustomFormat(0, Mesh.ArrayCustomFormat.RgbaFloat) | CustomFormat(1, Mesh.ArrayCustomFormat.RgbaFloat) | CustomFormat(2, Mesh.ArrayCustomFormat.RgbaFloat));
    }

    /// <summary>Mist lane: VERTEX, CUSTOM0 = colour rgba, CUSTOM1 = aMist.</summary>
    public static ArrayMesh? Mist(float[] position, float[] color, float[] mist, uint[] index)
    {
        int n = position.Length / 3;
        if (n == 0 || index.Length == 0) return null;
        using var arrays = NewArrays();
        Put(arrays, Mesh.ArrayType.Vertex, Vec3(position));
        Put(arrays, Mesh.ArrayType.Custom0, Rgba(n, color, 4));
        Put(arrays, Mesh.ArrayType.Custom1, Rgba(n, mist, 4));
        Put(arrays, Mesh.ArrayType.Index, ThreeWinding(index));
        return Commit(arrays, CustomFormat(0, Mesh.ArrayCustomFormat.RgbaFloat) | CustomFormat(1, Mesh.ArrayCustomFormat.RgbaFloat));
    }

    /// <summary>Overlay lane: VERTEX, CUSTOM0 = colour rgba.</summary>
    public static ArrayMesh? Overlay(float[] position, float[] color, uint[] index)
    {
        int n = position.Length / 3;
        if (n == 0 || index.Length == 0) return null;
        using var arrays = NewArrays();
        Put(arrays, Mesh.ArrayType.Vertex, Vec3(position));
        Put(arrays, Mesh.ArrayType.Custom0, Rgba(n, color, 4));
        Put(arrays, Mesh.ArrayType.Index, ThreeWinding(index));
        return Commit(arrays, CustomFormat(0, Mesh.ArrayCustomFormat.RgbaFloat));
    }

    /// <summary>Position-only lane (actor wall / shadow casters).</summary>
    public static ArrayMesh? PositionOnly(float[] position, uint[] index)
    {
        int n = position.Length / 3;
        if (n == 0 || index.Length == 0) return null;
        using var arrays = NewArrays();
        Put(arrays, Mesh.ArrayType.Vertex, Vec3(position));
        Put(arrays, Mesh.ArrayType.Index, ThreeWinding(index));
        return Commit(arrays, 0);
    }
}
