using System;
using System.Collections.Generic;
using Godot;

namespace Fluitown.GodotApp.Rendering.Vegetation;

/// <summary>Curved, tapered blades with native index LODs. One surface, one draw, no per-frame CPU work.</summary>
public static class MeadowMesh
{
    public const int NearTriangles = 25;
    // Geometric error in metres. Godot projects it for both camera types and selects the index buffer.
    public const float MiddleError = .014f, FarError = .045f;

    public static ArrayMesh Create()
    {
        const int blades = 5, perBlade = 7;
        var vertices = new Vector3[blades * perBlade];
        var normals = new Vector3[vertices.Length];
        var uv = new Vector2[vertices.Length];
        var width = new Vector2[vertices.Length];
        for (int b = 0; b < blades; b++)
        {
            float angle = b * 2.399963f;
            var right = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
            var forward = new Vector3(-right.Z, 0, right.X);
            var root = right * (.025f + (b % 3) * .018f);
            float height = .27f + (b * 3 % 5) * .048f;
            float curl = .12f + (b % 3) * .055f;
            float halfWidth = .034f + (b % 3) * .007f;
            for (int k = 0; k < perBlade; k++)
            {
                float t = k == 6 ? 1 : (k / 2) * .35f;
                float side = k == 6 ? 0 : (k % 2 == 0 ? -1 : 1);
                float taper = (1 - t) * (.6f + 1.25f * t);
                int i = b * perBlade + k;
                vertices[i] = root + Vector3.Up * (t * height)
                    + forward * (t * t * curl) + right * (side * halfWidth * taper + t * t * .025f);
                normals[i] = forward;
                uv[i] = new Vector2(side * .5f + .5f, t);
                width[i] = new Vector2(halfWidth * taper, 0);
            }
        }
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = width;
        arrays[(int)Mesh.ArrayType.Index] = Indices(0);
        using var lods = new Godot.Collections.Dictionary
        {
            [MiddleError] = Indices(1),
            [FarError] = Indices(2),
        };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, lods: lods);
        return mesh;
    }

    /// <summary>All tiers reuse blade roots, shoulders and tips. Only unresolved curvature/inner blades disappear.</summary>
    public static int[] Indices(int tier)
    {
        var indices = new List<int>(NearTriangles * 3);
        for (int b = 0; b < 5; b++)
        {
            if (tier == 2 && (b == 1 || b == 3)) continue;
            int start = b * 7;
            void Quad(int a, int c)
            {
                indices.Add(start + a); indices.Add(start + a + 1); indices.Add(start + c);
                indices.Add(start + a + 1); indices.Add(start + c + 1); indices.Add(start + c);
            }
            if (tier == 0) { Quad(0, 2); Quad(2, 4); }
            else Quad(0, 4);
            indices.Add(start + 4); indices.Add(start + 5); indices.Add(start + 6);
        }
        return indices.ToArray();
    }
}
