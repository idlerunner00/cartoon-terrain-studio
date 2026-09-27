using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace Fluitown.GodotApp.Rendering;

/// <summary>Lossless worker-side render preparation. Collision keeps the original bake.
/// Four independently culled parts replace a whole 70 m tile; identical complete vertices share GPU work.
/// No position quantisation, simplification, new edges or per-frame CPU culling.</summary>
public static class TerrainRenderMesh
{
    public static long InputVertices, OutputVertices, Triangles, Parts;

    private readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector4 Pigment, Vector2 Surface, Vector4 Ground);
    private sealed class Part
    {
        // Retain source indices, not two copies of every 64-byte vertex in a dictionary and list.
        // Hash collisions form short chains and always compare the complete source attributes.
        public readonly Dictionary<int, int> Heads = new();
        public readonly List<int> Sources = new(), SameHash = new();
        public readonly List<int> Indices = new();
        public int Add(int source, Func<int, Vertex> read)
        {
            Vertex vertex = read(source);
            int hash = vertex.GetHashCode();
            int head = Heads.TryGetValue(hash, out int first) ? first : -1;
            for (int candidate = head; candidate >= 0; candidate = SameHash[candidate])
                if (vertex == read(Sources[candidate])) return candidate;
            int index = Sources.Count;
            Sources.Add(source);
            SameHash.Add(head);
            Heads[hash] = index;
            return index;
        }
    }

    public static ArrayMesh[] Surface(float[] position, float[] normal, float[] color, float[] surf,
        float[] emissive, float[] ground, uint[] indices)
    {
        Vertex Read(int i) => new(new(position[i * 3], position[i * 3 + 1], position[i * 3 + 2]),
            new(normal[i * 3], normal[i * 3 + 1], normal[i * 3 + 2]),
            new(color[i * 3], color[i * 3 + 1], color[i * 3 + 2], emissive[i]),
            new(surf[i * 2], surf[i * 2 + 1]),
            new(ground[i * 4], ground[i * 4 + 1], ground[i * 4 + 2], ground[i * 4 + 3]));
        return Build(position, indices, Read, false);
    }

    public static ArrayMesh[] Casters(float[] position, uint[] indices)
    {
        Vertex Read(int i) => new(new(position[i * 3], position[i * 3 + 1], position[i * 3 + 2]), default, default, default, default);
        return Build(position, indices, Read, true);
    }

    private static ArrayMesh[] Build(float[] position, uint[] indices, Func<int, Vertex> read, bool depthOnly)
    {
        if (indices.Length == 0) return Array.Empty<ArrayMesh>();
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < position.Length; i += 3)
        {
            minX = Math.Min(minX, position[i]); maxX = Math.Max(maxX, position[i]);
            minZ = Math.Min(minZ, position[i + 2]); maxZ = Math.Max(maxZ, position[i + 2]);
        }
        float midX = (minX + maxX) * .5f, midZ = (minZ + maxZ) * .5f;
        bool split = indices.Length >= 12000;
        var parts = new Part?[split ? 4 : 1];
        int vertexCount = position.Length / 3;
        int[] remap = System.Buffers.ArrayPool<int>.Shared.Rent(vertexCount * parts.Length);
        Array.Fill(remap, -1, 0, vertexCount * parts.Length);
        try
        {
            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
                float x = (position[a * 3] + position[b * 3] + position[c * 3]) / 3;
                float z = (position[a * 3 + 2] + position[b * 3 + 2] + position[c * 3 + 2]) / 3;
                int bucket = split ? (x >= midX ? 1 : 0) + (z >= midZ ? 2 : 0) : 0;
                var part = parts[bucket] ??= new Part();
                int VertexIndex(int source)
                {
                    ref int target = ref remap[bucket * vertexCount + source];
                    if (target < 0) target = part.Add(source, read);
                    return target;
                }
                // Change only winding: three is counter-clockwise, Godot clockwise.
                part.Indices.Add(VertexIndex(a)); part.Indices.Add(VertexIndex(c)); part.Indices.Add(VertexIndex(b));
            }
        }
        finally { System.Buffers.ArrayPool<int>.Shared.Return(remap); }
        var meshes = new List<ArrayMesh>(parts.Length);
        try
        {
            foreach (var part in parts)
            {
                if (part == null) continue;
                int count = part.Sources.Count;
                var vertices = new Vector3[count];
                var normals = depthOnly ? Array.Empty<Vector3>() : new Vector3[count];
                var uv = depthOnly ? Array.Empty<Vector2>() : new Vector2[count];
                var pigments = depthOnly ? Array.Empty<float>() : new float[count * 4];
                var ground = depthOnly ? Array.Empty<float>() : new float[count * 4];
                for (int i = 0; i < count; i++)
                {
                    var v = read(part.Sources[i]); vertices[i] = v.Position;
                    if (depthOnly) continue;
                    normals[i] = v.Normal; uv[i] = v.Surface;
                    for (int j = 0; j < 4; j++) { pigments[i * 4 + j] = v.Pigment[j]; ground[i * 4 + j] = v.Ground[j]; }
                }
                using var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                Put(arrays, Mesh.ArrayType.Vertex, Variant.CreateFrom(vertices.AsSpan()));
                Put(arrays, Mesh.ArrayType.Index, Variant.CreateFrom(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(part.Indices)));
                Mesh.ArrayFormat flags = 0;
                if (!depthOnly)
                {
                    Put(arrays, Mesh.ArrayType.Normal, Variant.CreateFrom(normals.AsSpan()));
                    Put(arrays, Mesh.ArrayType.TexUV, Variant.CreateFrom(uv.AsSpan()));
                    Put(arrays, Mesh.ArrayType.Custom0, Variant.CreateFrom(pigments.AsSpan()));
                    Put(arrays, Mesh.ArrayType.Custom1, Variant.CreateFrom(ground.AsSpan()));
                    flags = (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
                        | (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift);
                }
                var mesh = new ArrayMesh();
                meshes.Add(mesh);
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: flags);
                Interlocked.Add(ref OutputVertices, count);
            }
            Interlocked.Add(ref InputVertices, position.Length / 3);
            Interlocked.Add(ref Triangles, indices.Length / 3);
            Interlocked.Add(ref Parts, meshes.Count);
            return meshes.ToArray();
        }
        catch { foreach (var mesh in meshes) mesh.Dispose(); throw; }
    }

    private static void Put(Godot.Collections.Array arrays, Mesh.ArrayType lane, Variant value)
    {
        using (value) arrays[(int)lane] = value;
    }
}
