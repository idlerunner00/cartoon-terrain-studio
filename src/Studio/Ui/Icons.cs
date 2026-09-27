using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace TerrainStudio.Ui;

public enum IconKind
{
    Brush, Bucket, Eraser, Picker, Wand, Hand, Smooth, Palette, Undo, Redo, Save, Folder, Export, Import, Dice, Globe,
    Sparkle, Sun, Moon, Play, Pause, Stop, Plus, Minus, Fit, Camera, Trash, Copy, Cut, Paste, ChevronDown, ChevronRight,
    Reset, Eye, Grid, Tree, Mountain, Drop, Bridge, Chasm, Cleft, Gear, Help, Info, Check, Close, Layers, Map,
    Stump, Bush, Target, Warning, Film, Pin, Arrows, Wall, Floor,
    Raise, Lower, Flatten, Leaf, Star, StarFilled, Share, Snow, Cloud, Rain, Menu, Sculpt, Terrain, ChevronLeft, Clock,
    Cube, Shuffle,
}

/// <summary>
/// Vector icons drawn in code (no image assets): each icon is a few strokes, arcs and filled shapes on a 24-unit grid,
/// rasterised once per size and colour with 4×4 supersampling into a mipmapped texture.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<(IconKind, int, uint), Texture2D> Cache = new();

    // Mini path language on a 24×24 grid, stroke width 2 unless changed:
    //   M x y L x y …   polyline (a new M starts a new line)      Z closes the current polyline
    //   O cx cy r       stroked circle                           A cx cy r from to   stroked arc (degrees, clockwise from +x)
    //   D cx cy r       filled disc                              F x y x y …         filled polygon
    //   W w             stroke width for the following shapes
    private static readonly Dictionary<IconKind, string> Paths = new()
    {
        [IconKind.Brush] = "M 18 3 L 21 6 L 11 16 L 8 13 Z F 8 13 11 16 9 19 4 21 5 16",
        [IconKind.Bucket] = "M 5 11 L 12 4 L 19 11 L 12 18 Z M 12 4 L 9 1 F 19 11 21 15 22 17 20 19 18 17 19 14",
        [IconKind.Eraser] = "M 9 20 L 3 14 L 13 4 L 21 12 L 13 20 Z M 8 9 L 16 17 M 9 20 L 21 20",
        [IconKind.Picker] = "M 14 4 L 20 10 M 16 2 L 22 8 L 19 11 L 13 5 Z M 16 8 L 6 18 L 3 21 M 6 18 L 4 20",
        [IconKind.Wand] = "M 4 20 L 15 9 M 13 7 L 17 11 F 18 2 19 5 22 6 19 7 18 10 17 7 14 6 17 5 D 7 5 1.2 D 20 16 1.2",
        [IconKind.Hand] = "M 7 12 L 7 5 A 8.5 5 1.5 180 360 L 10 11 M 10 4 A 11.5 4 1.5 180 360 L 13 11 M 13 5 A 14.5 5 1.5 180 360 L 16 12 M 16 7 A 17.5 7 1.5 180 360 L 19 15 A 12 15 7 0 90 M 7 12 L 5 10 A 3.8 11.2 1.6 225 45 L 7 18",
        [IconKind.Smooth] = "M 2 8 A 5 8 3 180 360 A 11 8 3 0 180 A 17 8 3 180 360 L 22 8 M 2 16 A 5 16 3 180 360 A 11 16 3 0 180 A 17 16 3 180 360 L 22 16",
        [IconKind.Palette] = "O 12 12 9 D 8 9 1.5 D 12 7 1.5 D 16 9 1.5 D 8 14 1.5 F 13 21 13 16 17 16 17 19",
        [IconKind.Undo] = "M 3 9 L 8 4 M 3 9 L 8 14 M 3 9 L 15 9 A 15 15 6 270 450 L 11 21",
        [IconKind.Redo] = "M 21 9 L 16 4 M 21 9 L 16 14 M 21 9 L 9 9 A 9 15 6 270 90 L 13 21",
        [IconKind.Save] = "M 4 3 L 17 3 L 21 7 L 21 21 L 3 21 L 3 3 M 7 3 L 7 8 L 15 8 L 15 3 M 7 21 L 7 14 L 17 14 L 17 21",
        [IconKind.Folder] = "M 3 6 L 9 6 L 11 8 L 21 8 L 21 19 L 3 19 Z",
        [IconKind.Export] = "M 12 3 L 12 15 M 7 10 L 12 15 L 17 10 M 4 17 L 4 21 L 20 21 L 20 17",
        [IconKind.Import] = "M 12 15 L 12 3 M 7 8 L 12 3 L 17 8 M 4 17 L 4 21 L 20 21 L 20 17",
        [IconKind.Dice] = "M 4 4 L 20 4 L 20 20 L 4 20 Z D 8.5 8.5 1.7 D 15.5 15.5 1.7 D 15.5 8.5 1.7 D 8.5 15.5 1.7 D 12 12 1.7",
        [IconKind.Globe] = "O 12 12 9 M 3 12 L 21 12 A 12 12 4 270 450 A 12 12 4 90 270 M 5 7 L 19 7 M 5 17 L 19 17",
        [IconKind.Sparkle] = "F 12 2 14 10 22 12 14 14 12 22 10 14 2 12 10 10 F 19 2 20 4 22 5 20 6 19 8 18 6 16 5 18 4",
        [IconKind.Sun] = "O 12 12 4 M 12 2 L 12 4 M 12 20 L 12 22 M 2 12 L 4 12 M 20 12 L 22 12 M 5 5 L 6.5 6.5 M 17.5 17.5 L 19 19 M 5 19 L 6.5 17.5 M 17.5 6.5 L 19 5",
        [IconKind.Moon] = "A 12 12 9 60 330 A 16 8 7 330 60",
        [IconKind.Play] = "F 7 4 20 12 7 20",
        [IconKind.Pause] = "F 6 4 10 4 10 20 6 20 F 14 4 18 4 18 20 14 20",
        [IconKind.Stop] = "F 6 6 18 6 18 18 6 18",
        [IconKind.Plus] = "M 12 5 L 12 19 M 5 12 L 19 12",
        [IconKind.Minus] = "M 5 12 L 19 12",
        [IconKind.Fit] = "M 3 8 L 3 3 L 8 3 M 16 3 L 21 3 L 21 8 M 21 16 L 21 21 L 16 21 M 8 21 L 3 21 L 3 16 M 8 8 L 16 8 L 16 16 L 8 16 Z",
        [IconKind.Camera] = "M 3 7 L 7 7 L 9 4 L 15 4 L 17 7 L 21 7 L 21 19 L 3 19 Z O 12 13 4",
        [IconKind.Trash] = "M 3 6 L 21 6 M 8 6 L 8 3 L 16 3 L 16 6 M 5 6 L 6 21 L 18 21 L 19 6 M 10 10 L 10 17 M 14 10 L 14 17",
        [IconKind.Copy] = "M 8 8 L 20 8 L 20 20 L 8 20 Z M 16 8 L 16 4 L 4 4 L 4 16 L 8 16",
        [IconKind.Cut] = "O 6 18 3 O 18 18 3 M 8 16 L 18 3 M 16 16 L 6 3",
        [IconKind.Paste] = "M 8 4 L 5 4 L 5 21 L 19 21 L 19 4 L 16 4 M 8 2 L 16 2 L 16 6 L 8 6 Z",
        [IconKind.ChevronDown] = "M 6 9 L 12 15 L 18 9",
        [IconKind.ChevronRight] = "M 9 6 L 15 12 L 9 18",
        [IconKind.Reset] = "A 12 12 8 200 500 M 4 5 L 4.5 10.5 L 10 10",
        [IconKind.Eye] = "M 2 12 A 12 20 11 240 300 M 2 12 A 12 4 11 60 120 O 12 12 3",
        [IconKind.Grid] = "M 3 3 L 21 3 L 21 21 L 3 21 Z M 9 3 L 9 21 M 15 3 L 15 21 M 3 9 L 21 9 M 3 15 L 21 15",
        [IconKind.Tree] = "F 12 2 19 12 15 12 20 18 4 18 9 12 5 12 M 12 18 L 12 22",
        [IconKind.Mountain] = "F 2 20 9 7 13 13 16 9 22 20 F 7.5 9.8 9 7 10.5 9.8",
        [IconKind.Drop] = "M 12 3 L 17 11 A 12 14 6 330 570 L 12 3",
        [IconKind.Bridge] = "M 2 16 L 22 16 M 2 20 L 22 20 M 4 16 A 12 26 16 240 300 M 6 16 L 6 12 M 12 16 L 12 10 M 18 16 L 18 12",
        [IconKind.Chasm] = "M 2 8 L 8 8 L 10 20 L 14 20 L 16 8 L 22 8 M 11 12 L 13 12",
        [IconKind.Cleft] = "F 3 4 10 4 11 20 3 20 F 14 4 21 4 21 20 13 20",
        [IconKind.Gear] = "O 12 12 3 A 12 12 7.5 0 360 M 12 2 L 12 4.5 M 12 19.5 L 12 22 M 2 12 L 4.5 12 M 19.5 12 L 22 12 M 5 5 L 6.8 6.8 M 17.2 17.2 L 19 19 M 5 19 L 6.8 17.2 M 17.2 6.8 L 19 5",
        [IconKind.Help] = "O 12 12 9.5 A 12 9.5 3 180 405 L 12 14.5 D 12 17.8 1.3",
        [IconKind.Info] = "O 12 12 9.5 M 12 11 L 12 17 D 12 7.5 1.3",
        [IconKind.Check] = "M 5 12 L 10 17 L 19 7",
        [IconKind.Close] = "M 6 6 L 18 18 M 18 6 L 6 18",
        [IconKind.Layers] = "M 12 3 L 22 8 L 12 13 L 2 8 Z M 2 12 L 12 17 L 22 12 M 2 16 L 12 21 L 22 16",
        [IconKind.Map] = "M 3 6 L 9 3 L 15 6 L 21 3 L 21 18 L 15 21 L 9 18 L 3 21 Z M 9 3 L 9 18 M 15 6 L 15 21",
        [IconKind.Stump] = "M 5 9 L 5 18 A 12 18 7 180 0 L 19 9 O 12 9 7 A 12 9 3.5 0 360",
        [IconKind.Bush] = "D 8 14 5 D 16 14 5 D 12 9 5.5 F 4 14 20 14 20 19 4 19",
        [IconKind.Target] = "O 12 12 8 O 12 12 3 M 12 1 L 12 5 M 12 19 L 12 23 M 1 12 L 5 12 M 19 12 L 23 12",
        [IconKind.Warning] = "M 12 3 L 22 20 L 2 20 Z M 12 9 L 12 14 D 12 17 1.2",
        [IconKind.Film] = "M 3 5 L 21 5 L 21 19 L 3 19 Z M 7 5 L 7 19 M 17 5 L 17 19 M 3 9 L 7 9 M 3 15 L 7 15 M 17 9 L 21 9 M 17 15 L 21 15",
        [IconKind.Pin] = "O 12 9 3 M 12 22 L 6 13 A 12 9 7 150 390 L 12 22",
        [IconKind.Arrows] = "M 12 2 L 12 22 M 2 12 L 22 12 M 9 5 L 12 2 L 15 5 M 9 19 L 12 22 L 15 19 M 5 9 L 2 12 L 5 15 M 19 9 L 22 12 L 19 15",
        [IconKind.Wall] = "M 3 5 L 21 5 L 21 19 L 3 19 Z M 3 12 L 21 12 M 9 5 L 9 12 M 15 12 L 15 19",
        [IconKind.Floor] = "M 2 16 L 12 11 L 22 16 L 12 21 Z M 7 13.5 L 17 18.5 M 17 13.5 L 7 18.5",
        [IconKind.Raise] = "M 12 18 L 12 5 M 7 10 L 12 5 L 17 10 M 4 21 L 20 21",
        [IconKind.Lower] = "M 12 3 L 12 16 M 7 11 L 12 16 L 17 11 M 4 21 L 20 21",
        [IconKind.Flatten] = "M 3 12 L 21 12 M 8 4 L 12 8 L 16 4 M 8 20 L 12 16 L 16 20",
        [IconKind.Leaf] = "M 5 19 A 13 11 9 150 270 A 11 13 9 270 390 M 5 19 L 15 9",
        [IconKind.Star] = "M 12 3 L 14.6 9 L 21 9.4 L 16 13.4 L 17.8 20 L 12 16.4 L 6.2 20 L 8 13.4 L 3 9.4 L 9.4 9 Z",
        [IconKind.StarFilled] = "F 12 3 14.6 9 21 9.4 16 13.4 17.8 20 12 16.4 6.2 20 8 13.4 3 9.4 9.4 9",
        [IconKind.Share] = "O 18 5 2.6 O 6 12 2.6 O 18 19 2.6 M 8.3 10.8 L 15.7 6.3 M 8.3 13.2 L 15.7 17.7",
        [IconKind.Snow] = "M 12 3 L 12 21 M 4.2 7.5 L 19.8 16.5 M 4.2 16.5 L 19.8 7.5 M 9.5 4.5 L 12 6.5 L 14.5 4.5 M 9.5 19.5 L 12 17.5 L 14.5 19.5",
        [IconKind.Cloud] = "M 7 18 A 7 14 4 90 270 A 12 10 5 200 340 A 17 14 4 270 450 L 7 18",
        [IconKind.Rain] = "M 7 14 A 7 10 4 90 270 A 12 6 5 200 340 A 17 10 4 270 450 L 7 14 M 8 17 L 7 20 M 12 17 L 11 20 M 16 17 L 15 20",
        [IconKind.Menu] = "M 4 7 L 20 7 M 4 12 L 20 12 M 4 17 L 20 17",
        [IconKind.Sculpt] = "M 2 20 L 9 9 L 13 15 L 16 11 L 22 20 Z M 18 2 L 18 8 M 15.5 4.5 L 18 2 L 20.5 4.5",
        [IconKind.Terrain] = "M 2 16 L 7 11 L 11 14 L 16 8 L 22 14 M 2 20 L 22 20",
        [IconKind.ChevronLeft] = "M 15 6 L 9 12 L 15 18",
        [IconKind.Clock] = "O 12 12 9 M 12 7 L 12 12 L 15.5 14",
        [IconKind.Cube] = "M 12 2 L 21 7 L 21 17 L 12 22 L 3 17 L 3 7 Z M 3 7 L 12 12 L 21 7 M 12 12 L 12 22",
        [IconKind.Shuffle] = "M 3 7 L 8 7 L 16 17 L 21 17 M 3 17 L 8 17 L 16 7 L 21 7 M 18 4 L 21 7 L 18 10 M 18 14 L 21 17 L 18 20",
    };

    public static Texture2D Get(IconKind kind, int size = 18, Color? color = null)
    {
        var c = color ?? StudioTheme.Text;
        var key = (kind, size, c.ToRgba32());
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var texture = ImageTexture.CreateFromImage(Rasterise(Paths[kind], size, c));
        Cache[key] = texture;
        return texture;
    }

    /// <summary>The slider grabber: a light disc with an accent ring.</summary>
    public static Texture2D Grabber(bool highlight)
    {
        int size = 16;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var ring = highlight ? StudioTheme.Accent : StudioTheme.AccentDeep;
        Splat(image, (x, y) =>
        {
            double d = Math.Sqrt((x - 8) * (x - 8) + (y - 8) * (y - 8));
            if (d <= 4.5) return new Color("f5efe4");
            if (d <= 7.2) return ring;
            return new Color(0, 0, 0, 0);
        });
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>A pill switch for CheckButton.</summary>
    public static Texture2D Toggle(bool on)
    {
        int w = 34, h = 18;
        var image = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        var track = on ? StudioTheme.AccentDeep : new Color("2b3240");
        double knob = on ? w - 9 : 9;
        Splat(image, (x, y) =>
        {
            double cx = Math.Clamp(x, 9, w - 9);
            double dTrack = Math.Sqrt((x - cx) * (x - cx) + (y - 9) * (y - 9));
            double dKnob = Math.Sqrt((x - knob) * (x - knob) + (y - 9) * (y - 9));
            if (dKnob <= 6.2) return on ? new Color("fff6e6") : new Color("9aa2b1");
            if (dTrack <= 8.6) return track;
            return new Color(0, 0, 0, 0);
        });
        return ImageTexture.CreateFromImage(image);
    }

    public static Texture2D Check(bool on)
    {
        int size = 18;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        Splat(image, (x, y) =>
        {
            bool inside = x >= 1.5 && x <= 16.5 && y >= 1.5 && y <= 16.5;
            bool border = inside && (x < 3 || x > 15 || y < 3 || y > 15);
            if (!inside) return new Color(0, 0, 0, 0);
            if (on) return StudioTheme.AccentDeep;
            return border ? new Color("4a5468") : new Color("10131a");
        });
        if (on)
        {
            var mark = Rasterise(Paths[IconKind.Check], size, new Color("1a1206"), 3);
            image.BlendRect(mark, new Rect2I(0, 0, size, size), Vector2I.Zero);
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static void Splat(Image image, Func<double, double, Color> shade)
    {
        int w = image.GetWidth(), h = image.GetHeight();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        var c = shade(x + (sx + .5) / 4, y + (sy + .5) / 4);
                        r += c.R * c.A; g += c.G * c.A; b += c.B * c.A; a += c.A;
                    }
                image.SetPixel(x, y, a > 0 ? new Color(r / a, g / a, b / a, a / 16) : new Color(0, 0, 0, 0));
            }
    }

    // ── rasteriser ──────────────────────────────────────────────────────────────────────────────────────────

    private abstract record Shape;
    private sealed record Stroke(List<Vector2> Points, float Width) : Shape;
    private sealed record Fill(Vector2[] Points) : Shape;
    private sealed record Disc(Vector2 Centre, float Radius) : Shape;

    private static List<Shape> Parse(string path, float defaultWidth)
    {
        var shapes = new List<Shape>();
        var tokens = path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float width = defaultWidth;
        List<Vector2>? line = null;
        int i = 0;
        float N() => float.Parse(tokens[i++], CultureInfo.InvariantCulture);
        bool NumberNext() => i < tokens.Length && (char.IsDigit(tokens[i][0]) || tokens[i][0] is '-' or '.');
        void Arc(List<Vector2> into, float cx, float cy, float r, float from, float to)
        {
            int steps = Math.Max(6, (int)(Math.Abs(to - from) / 8));
            for (int s = 0; s <= steps; s++)
            {
                float a = Mathf.DegToRad(from + (to - from) * s / steps);
                into.Add(new Vector2(cx + r * Mathf.Cos(a), cy + r * Mathf.Sin(a)));
            }
        }
        while (i < tokens.Length)
        {
            string op = tokens[i++];
            switch (op)
            {
                case "W": width = N(); break;
                case "M":
                    line = new List<Vector2> { new(N(), N()) };
                    shapes.Add(new Stroke(line, width));
                    break;
                case "L":
                    line ??= new List<Vector2>();
                    line.Add(new Vector2(N(), N()));
                    while (NumberNext()) line.Add(new Vector2(N(), N()));
                    break;
                case "Z":
                    if (line is { Count: > 0 }) line.Add(line[0]);
                    break;
                case "O":
                {
                    float cx = N(), cy = N(), r = N();
                    var circle = new List<Vector2>();
                    Arc(circle, cx, cy, r, 0, 360);
                    shapes.Add(new Stroke(circle, width));
                    line = null;
                    break;
                }
                case "A":
                {
                    float cx = N(), cy = N(), r = N(), from = N(), to = N();
                    if (line == null) { line = new List<Vector2>(); shapes.Add(new Stroke(line, width)); }
                    Arc(line, cx, cy, r, from, to);
                    break;
                }
                case "D": shapes.Add(new Disc(new Vector2(N(), N()), N())); line = null; break;
                case "F":
                {
                    var polygon = new List<Vector2>();
                    while (NumberNext()) polygon.Add(new Vector2(N(), N()));
                    shapes.Add(new Fill(polygon.ToArray()));
                    line = null;
                    break;
                }
            }
        }
        return shapes;
    }

    private static Image Rasterise(string path, int size, Color color, float strokeWidth = 2)
    {
        var shapes = Parse(path, strokeWidth);
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float scale = size / 24f;
        const int ss = 4;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                    {
                        var p = new Vector2((x + (sx + .5f) / ss) / scale, (y + (sy + .5f) / ss) / scale);
                        if (Inside(shapes, p)) hits++;
                    }
                if (hits > 0) image.SetPixel(x, y, new Color(color, color.A * hits / (float)(ss * ss)));
            }
        return image;
    }

    private static bool Inside(List<Shape> shapes, Vector2 p)
    {
        foreach (var shape in shapes)
        {
            switch (shape)
            {
                case Disc disc when p.DistanceTo(disc.Centre) <= disc.Radius:
                    return true;
                case Fill fill when fill.Points.Length >= 3 && Geometry2D.IsPointInPolygon(p, fill.Points):
                    return true;
                case Stroke stroke:
                {
                    float half = stroke.Width / 2;
                    var pts = stroke.Points;
                    if (pts.Count == 1 && p.DistanceTo(pts[0]) <= half) return true;
                    for (int k = 1; k < pts.Count; k++)
                        if (DistanceToSegment(p, pts[k - 1], pts[k]) <= half) return true;
                    break;
                }
            }
        }
        return false;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
        return p.DistanceTo(a + ab * t);
    }
}
