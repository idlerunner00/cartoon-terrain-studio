using System;
using System.Collections.Generic;
using Flui;
using Godot;

namespace TerrainStudio.Terrain;

/// <summary>
/// Translucent per-cell quads laid over the terrain (check overlays, the live edit preview, the selection). One mesh,
/// rebuilt when its content changes; drawn unshaded without depth test above everything, like the original editor's
/// overlay graphics. Exempt from the comic pipeline.
/// </summary>
public sealed partial class CellOverlay : MeshInstance3D
{
    private static Shader? _shader;
    private readonly ShaderMaterial _material;

    public CellOverlay(int renderPriority = 100)
    {
        SetMeta(ComicRendering.ExemptMeta, true);
        _shader ??= new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_mix, shadows_disabled, fog_disabled;
                uniform float opacity = 1.0;
                uniform float pulse = 0.0;
                void fragment() {
                    ALBEDO = COLOR.rgb;
                    ALPHA = COLOR.a * opacity * (1.0 - pulse * 0.45 * (0.5 + 0.5 * sin(TIME * 5.0)));
                }
                """,
        };
        _material = new ShaderMaterial { Shader = _shader, RenderPriority = renderPriority };
        MaterialOverride = _material;
        CastShadow = ShadowCastingSetting.Off;
        IgnoreOcclusionCulling = true;
        ExtraCullMargin = 100000;
    }
    public float Pulse { set => _material.SetShaderParameter("pulse", value); }

    /// <summary>A quad of one cell: world px corner, size, height (px), colour.</summary>
    public readonly struct Quad
    {
        public readonly double X, Y, Size, Height;
        public readonly Color Colour;
        public Quad(double x, double y, double size, double height, Color colour) { X = x; Y = y; Size = size; Height = height; Colour = colour; }
    }

    /// <summary>Replaces the mesh with these quads (world px; <paramref name="toWorld"/> = the renderer's compile-to-world).</summary>
    public void SetQuads(IReadOnlyList<Quad> quads, Transform3D toWorld, double inset = 0.06)
    {
        if (quads.Count == 0) { Mesh = null; return; }
        int n = quads.Count;
        var vertices = new Vector3[n * 4];
        var colours = new Color[n * 4];
        var indices = new int[n * 6];
        for (int i = 0; i < n; i++)
        {
            var q = quads[i];
            double pad = q.Size * inset;
            double x0 = q.X + pad, y0 = q.Y + pad, x1 = q.X + q.Size - pad, y1 = q.Y + q.Size - pad;
            float h = (float)(q.Height + 3);
            vertices[i * 4 + 0] = toWorld * new Vector3((float)x0, h, (float)y0);
            vertices[i * 4 + 1] = toWorld * new Vector3((float)x1, h, (float)y0);
            vertices[i * 4 + 2] = toWorld * new Vector3((float)x1, h, (float)y1);
            vertices[i * 4 + 3] = toWorld * new Vector3((float)x0, h, (float)y1);
            for (int k = 0; k < 4; k++) colours[i * 4 + k] = q.Colour;
            indices[i * 6 + 0] = i * 4; indices[i * 6 + 1] = i * 4 + 1; indices[i * 6 + 2] = i * 4 + 2;
            indices[i * 6 + 3] = i * 4; indices[i * 6 + 4] = i * 4 + 2; indices[i * 6 + 5] = i * 4 + 3;
        }
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colours;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        Mesh = mesh;
    }

    public void Clear() => Mesh = null;

    public enum ShapeKind { Rect, Circle, Stroke }

    /// <summary>A mark on a cell: an inset rectangle, a disc or a rectangular outline (world px).</summary>
    public readonly struct Shape
    {
        public readonly ShapeKind Kind;
        public readonly double X0, Y0, X1, Y1, Height, StrokeWidth;
        public readonly Color Colour;
        public Shape(ShapeKind kind, double x0, double y0, double x1, double y1, double height, Color colour, double strokeWidth = 0)
        {
            Kind = kind; X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; Height = height; Colour = colour; StrokeWidth = strokeWidth;
        }
    }

    /// <summary>Replaces the mesh with these marks.</summary>
    public void SetShapes(IReadOnlyList<Shape> shapes, Transform3D toWorld)
    {
        if (shapes.Count == 0) { Mesh = null; return; }
        var vertices = new List<Vector3>(shapes.Count * 6);
        var colours = new List<Color>(shapes.Count * 6);
        void Tri(Vector3 a, Vector3 b, Vector3 c, Color colour)
        {
            vertices.Add(a); vertices.Add(b); vertices.Add(c);
            colours.Add(colour); colours.Add(colour); colours.Add(colour);
        }
        Vector3 P(double x, double y, double h) => toWorld * new Vector3((float)x, (float)(h + 3), (float)y);
        void Quad(double x0, double y0, double x1, double y1, double h, Color colour)
        {
            var a = P(x0, y0, h); var b = P(x1, y0, h); var c = P(x1, y1, h); var d = P(x0, y1, h);
            Tri(a, b, c, colour); Tri(a, c, d, colour);
        }
        foreach (var s in shapes)
        {
            switch (s.Kind)
            {
                case ShapeKind.Rect:
                    Quad(s.X0, s.Y0, s.X1, s.Y1, s.Height, s.Colour);
                    break;
                case ShapeKind.Circle:
                {
                    double cx = (s.X0 + s.X1) / 2, cy = (s.Y0 + s.Y1) / 2, r = (s.X1 - s.X0) / 2;
                    var centre = P(cx, cy, s.Height);
                    const int segments = 14;
                    for (int k = 0; k < segments; k++)
                    {
                        double a0 = k * Math.Tau / segments, a1 = (k + 1) * Math.Tau / segments;
                        Tri(centre, P(cx + Math.Cos(a0) * r, cy + Math.Sin(a0) * r, s.Height), P(cx + Math.Cos(a1) * r, cy + Math.Sin(a1) * r, s.Height), s.Colour);
                    }
                    break;
                }
                case ShapeKind.Stroke:
                {
                    double w = s.StrokeWidth;
                    Quad(s.X0, s.Y0, s.X1, s.Y0 + w, s.Height, s.Colour);
                    Quad(s.X0, s.Y1 - w, s.X1, s.Y1, s.Height, s.Colour);
                    Quad(s.X0, s.Y0 + w, s.X0 + w, s.Y1 - w, s.Height, s.Colour);
                    Quad(s.X1 - w, s.Y0 + w, s.X1, s.Y1 - w, s.Height, s.Colour);
                    break;
                }
            }
        }
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colours.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        Mesh = mesh;
    }
}
