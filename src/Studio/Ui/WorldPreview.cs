using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Fluitown.Domain;
using Godot;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio.Ui;

/// <summary>
/// Small hill-shaded maps of the endless world, drawn from the same chunk generator the terrain streams (the minimap
/// palettes of the render theme: terrace ramp, stone ramp, water). Chunks are generated on two background threads;
/// finished pictures are turned into textures on the main thread by <see cref="Poll"/>.
/// </summary>
public static class WorldPreview
{
    private sealed class Palette
    {
        public int[] Floor = Array.Empty<int>();
        public int FloorMin;
        public int[] Wall = Array.Empty<int>();
        public int WallMin;
        public int Water, Deep, Rock;
    }

    private sealed class Job
    {
        public required Func<(byte[] rgba, int width, int height)> Render;
        public required Action<ImageTexture> Ready;
        public required Func<bool> Wanted;
        public Action? Dropped;
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<string, Palette> Palettes = new();
    private static readonly LinkedList<Job> Jobs = new();
    private static readonly ConcurrentQueue<(Job job, byte[]? rgba, int width, int height)> Done = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static bool _started;

    /// <summary>World px per chunk (32 cells of 62.5 px).</summary>
    public const double ChunkWorld = EndlessCoordinates.ENDLESS_CHUNK_WORLD;
    public const int ChunkCells = EndlessCoordinates.ENDLESS_CHUNK_TILES;

    /// <summary>The chunk that contains a world point.</summary>
    public static (int cx, int cy) ChunkAt(double x, double y) =>
        ((int)Math.Floor((x + ChunkWorld / 2) / ChunkWorld), (int)Math.Floor((y + ChunkWorld / 2) / ChunkWorld));

    /// <summary>
    /// Queues a picture. The newest request runs first (the minimap asks for what is on screen now); a request that
    /// is no longer <paramref name="wanted"/> when a worker reaches it is dropped (<paramref name="dropped"/> then runs on
    /// the main thread). <paramref name="wanted"/> runs on the workers too: it may only read plain fields.
    /// </summary>
    public static void Request(Func<(byte[] rgba, int width, int height)> render, Action<ImageTexture> ready, Func<bool> wanted, Action? dropped = null)
    {
        Start();
        lock (Lock) Jobs.AddFirst(new Job { Render = render, Ready = ready, Wanted = wanted, Dropped = dropped });
        Signal.Release();
    }

    /// <summary>Hands finished pictures to their owners (main thread, once per frame).</summary>
    public static void Poll()
    {
        int budget = 6;
        while (budget-- > 0 && Done.TryDequeue(out var item))
        {
            if (item.rgba == null || !item.job.Wanted())
            {
                item.job.Dropped?.Invoke();
                continue;
            }
            var image = Image.CreateFromData(item.width, item.height, false, Image.Format.Rgba8, item.rgba);
            item.job.Ready(ImageTexture.CreateFromImage(image));
        }
    }

    private static void Start()
    {
        if (_started) return;
        _started = true;
        for (int i = 0; i < 2; i++)
            new Thread(Work) { IsBackground = true, Name = $"world-preview-{i}", Priority = ThreadPriority.BelowNormal }.Start();
    }

    private static void Work()
    {
        while (true)
        {
            Signal.Wait();
            Job? job;
            lock (Lock)
            {
                job = Jobs.First?.Value;
                if (job != null) Jobs.RemoveFirst();
            }
            if (job == null) continue;
            try
            {
                if (!job.Wanted()) { Done.Enqueue((job, null, 0, 0)); continue; }
                var (rgba, width, height) = job.Render();
                Done.Enqueue((job, rgba, width, height));
            }
            catch (Exception e)
            {
                Done.Enqueue((job, null, 0, 0));
                GD.PushWarning($"world preview failed: {e.Message}");
            }
        }
    }

    // ── pictures ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A square of span × span chunks around a chunk, one pixel per cell.</summary>
    public static (byte[] rgba, int width, int height) RenderArea(DungeonDescriptor descriptor, int cx, int cy, int span)
    {
        int radius = span / 2;
        int size = span * ChunkCells;
        var heights = new float[size * size];
        var colours = new int[size * size];
        for (int sy = 0; sy < span; sy++)
            for (int sx = 0; sx < span; sx++)
            {
                var layout = Descriptor.generateEndlessChunk(descriptor, cx - radius + sx, cy - radius + sy);
                Classify(layout, heights, colours, size, sx * ChunkCells, sy * ChunkCells);
            }
        return (Shade(heights, colours, size, size), size, size);
    }

    /// <summary>One chunk, one pixel per cell.</summary>
    public static (byte[] rgba, int width, int height) RenderChunk(DungeonDescriptor descriptor, int cx, int cy)
    {
        var layout = Descriptor.generateEndlessChunk(descriptor, cx, cy);
        int w = layout.width, h = layout.height;
        var heights = new float[w * h];
        var colours = new int[w * h];
        Classify(layout, heights, colours, w, 0, 0);
        return (Shade(heights, colours, w, h), w, h);
    }

    /// <summary>Writes a height and a base colour per cell of a generated layout into the destination arrays.</summary>
    private static void Classify(DungeonLayout layout, float[] heights, int[] colours, int stride, int ox, int oy)
    {
        var palette = PaletteOf(layout.biomeKey ?? "mountain");
        var themes = layout.terrain?.themeIndex;
        var names = layout.terrain?.themePalette;
        for (int ty = 0; ty < layout.height; ty++)
            for (int tx = 0; tx < layout.width; tx++)
            {
                int i = ty * layout.width + tx;
                int d = (oy + ty) * stride + ox + tx;
                if (d < 0 || d >= heights.Length) continue;
                var p = palette;
                if (themes != null && names != null && i < themes.Length && themes[i] < names.Count) p = PaletteOf(names[themes[i]]);
                int tile = layout.tiles[i];
                int level = layout.elevation != null && i < layout.elevation.Length ? layout.elevation[i] : 0;
                switch (tile)
                {
                    case TileType.Solid:
                        heights[d] = level + 4;
                        colours[d] = p.Rock;
                        break;
                    case TileType.Water:
                        heights[d] = level - 0.4f;
                        colours[d] = p.Water;
                        break;
                    case TileType.Chasm:
                        heights[d] = -9;
                        colours[d] = 0x241c2c;
                        break;
                    case TileType.Bridge or TileType.Underpass:
                        heights[d] = level;
                        colours[d] = 0x9a7650;
                        break;
                    default:
                        heights[d] = level;
                        colours[d] = p.Floor[Math.Clamp(level - p.FloorMin, 0, p.Floor.Length - 1)];
                        break;
                }
            }
    }

    /// <summary>Light from the north-west plus a comic ink line on every step of two levels or more.</summary>
    private static byte[] Shade(float[] heights, int[] colours, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        float H(int x, int y) => heights[Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float here = H(x, y);
                float light = 1 + Math.Clamp((here - H(x - 1, y - 1)) * 0.06f, -0.22f, 0.22f);
                // A cliff face below a higher cell (seen from the south) and a thin ink line on every big step.
                if (H(x, y - 1) - here >= 2) light *= 0.7f;
                else if (Math.Abs(here - H(x - 1, y)) >= 2 || here - H(x, y - 1) >= 2) light *= 0.85f;
                int c = colours[y * w + x];
                int o = (y * w + x) * 4;
                rgba[o] = (byte)Math.Clamp(((c >> 16) & 0xff) * light, 0, 255);
                rgba[o + 1] = (byte)Math.Clamp(((c >> 8) & 0xff) * light, 0, 255);
                rgba[o + 2] = (byte)Math.Clamp((c & 0xff) * light, 0, 255);
                rgba[o + 3] = 255;
            }
        return rgba;
    }

    private static Palette PaletteOf(string key)
    {
        lock (Palettes)
        {
            if (Palettes.TryGetValue(key, out var cached)) return cached;
            var biome = Fluitown.Render.Theme.biomeForKey(key);
            var terrain = Fluitown.Render.Theme.terrainOf(biome);
            var elevation = Fluitown.Render.Theme.elevationOf(biome);
            var flood = Fluitown.Render.Theme.floodColors(biome);
            var palette = new Palette
            {
                Floor = elevation.topFill, FloorMin = elevation.minLevel,
                Wall = terrain.wallFill, WallMin = terrain.minLevel,
                Water = flood.surface, Deep = flood.deep, Rock = terrain.wall,
            };
            Palettes[key] = palette;
            return palette;
        }
    }
}

/// <summary>A suggested world: its picture, seed and theme. Clicking opens it.</summary>
public sealed partial class SeedCard : Button
{
    public readonly string Seed, ThemeKey;
    private readonly string _themeName;
    private Texture2D? _picture;
    private double _clock;

    public SeedCard(string seed, string theme, string themeName)
    {
        Seed = seed;
        ThemeKey = theme;
        _themeName = themeName;
        Text = "";
        FocusMode = FocusModeEnum.None;
        TooltipText = F("Open the world “{0}” ({1})", seed, themeName);
        CustomMinimumSize = new Vector2(0, 132);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        AddThemeStyleboxOverride("normal", StudioTheme.Box(StudioTheme.Raised, new Color(0, 0, 0, 0), 12, 2, 0, 0));
        AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Hover, StudioTheme.Accent, 12, 2, 0, 0));
        AddThemeStyleboxOverride("pressed", StudioTheme.Box(StudioTheme.AccentTint, StudioTheme.Accent, 12, 2, 0, 0));
    }

    public Texture2D? Picture
    {
        get => _picture;
        set { _picture = value; QueueRedraw(); }
    }

    public override void _Process(double delta)
    {
        if (_picture != null) return;
        _clock += delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        var area = new Rect2(6, 6, size.X - 12, size.Y - 44);
        if (_picture != null) DrawTextureRect(_picture, area, false);
        else
        {
            float pulse = 0.06f + 0.04f * (float)Math.Sin(_clock * 4);
            DrawRect(area, new Color(1, 1, 1, pulse));
        }
        DrawRect(area, new Color(0, 0, 0, 0.4f), false, 1);
        var font = GetThemeDefaultFont();
        var bright = IsHovered() ? new Color("ffffff") : StudioTheme.Text;
        DrawString(font, new Vector2(8, size.Y - 22), Fit(font, Seed, size.X - 16, 12), HorizontalAlignment.Left, size.X - 16, 12, bright);
        DrawString(font, new Vector2(8, size.Y - 8), Fit(font, _themeName, size.X - 16, 11), HorizontalAlignment.Left, size.X - 16, 11, StudioTheme.TextFaint);
    }

    /// <summary>Shortens a text with an ellipsis until it fits a width.</summary>
    public static string Fit(Font font, string text, float width, int size)
    {
        if (font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X <= width) return text;
        for (int n = text.Length - 1; n > 1; n--)
        {
            string cut = text[..n].TrimEnd() + "…";
            if (font.GetStringSize(cut, HorizontalAlignment.Left, -1, size).X <= width) return cut;
        }
        return "…";
    }
}

/// <summary>
/// A north-up map of the chunks around the camera with the camera's footprint. Clicking travels there; dragging
/// scrubs. Chunks are drawn by <see cref="WorldPreview"/> and cached per world.
/// </summary>
public sealed partial class WorldMinimap : Control
{
    private const int Radius = 3;
    private readonly Dictionary<(string, int, int), ImageTexture> _cache = new();
    private readonly HashSet<(string, int, int)> _pending = new();
    private readonly LinkedList<(string, int, int)> _order = new();
    private DungeonDescriptor? _descriptor;
    private string _world = "";
    private double _x, _y;
    private Vector2[] _footprint = Array.Empty<Vector2>();
    private bool _dragging;
    public event Action<double, double>? Travel;

    public WorldMinimap()
    {
        CustomMinimumSize = new Vector2(0, 236);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.Cross;
        ClipContents = true;
        TooltipText = T("Click or drag to travel");
    }

    public void SetWorld(string worldId, DungeonDescriptor? descriptor)
    {
        if (worldId == _world) return;
        _world = worldId;
        _descriptor = descriptor;
        QueueRedraw();
    }

    /// <summary>The camera focus and its ground footprint (world px, up to four corners).</summary>
    public void SetView(double x, double y, Vector2[] footprint)
    {
        _x = x; _y = y;
        _footprint = footprint;
        QueueRedraw();
    }

    private float MapScale => Math.Min(Size.X, Size.Y) / (float)((Radius * 2 + 1) * WorldPreview.ChunkWorld);

    private Vector2 ToLocal(double wx, double wy) =>
        Size / 2 + new Vector2((float)(wx - _x), (float)(wy - _y)) * MapScale;

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        DrawRect(rect, new Color("0c0e12"));
        if (_descriptor != null)
        {
            var (ccx, ccy) = WorldPreview.ChunkAt(_x, _y);
            float span = (float)WorldPreview.ChunkWorld * MapScale;
            for (int dy = -Radius - 1; dy <= Radius + 1; dy++)
                for (int dx = -Radius - 1; dx <= Radius + 1; dx++)
                {
                    int cx = ccx + dx, cy = ccy + dy;
                    var corner = ToLocal(EndlessCoordinates.endlessChunkOriginX(cx), EndlessCoordinates.endlessChunkOriginY(cy));
                    var cell = new Rect2(corner, new Vector2(span + 0.5f, span + 0.5f));
                    if (!cell.Intersects(rect)) continue;
                    var key = (_world, cx, cy);
                    if (_cache.TryGetValue(key, out var texture)) DrawTextureRect(texture, cell, false);
                    else
                    {
                        DrawRect(cell.Intersection(rect), new Color(1, 1, 1, 0.04f));
                        RequestChunk(key);
                    }
                }
        }
        if (_footprint.Length >= 3)
        {
            var points = new Vector2[_footprint.Length + 1];
            for (int i = 0; i < _footprint.Length; i++) points[i] = ToLocal(_footprint[i].X, _footprint[i].Y);
            points[^1] = points[0];
            DrawPolyline(points, new Color(0, 0, 0, 0.55f), 3.5f, true);
            DrawPolyline(points, new Color("fff3dc"), 1.6f, true);
        }
        var centre = Size / 2;
        DrawCircle(centre, 5, new Color(0, 0, 0, 0.6f));
        DrawCircle(centre, 3.5f, StudioTheme.Accent);
        DrawRect(rect, StudioTheme.Line, false, 1);
    }

    private void RequestChunk((string world, int cx, int cy) key)
    {
        if (_descriptor == null || !_pending.Add(key)) return;
        var descriptor = _descriptor;
        WorldPreview.Request(
            () => WorldPreview.RenderChunk(descriptor, key.cx, key.cy),
            texture =>
            {
                _pending.Remove(key);
                _cache[key] = texture;
                _order.AddLast(key);
                while (_order.Count > 240)
                {
                    _cache.Remove(_order.First!.Value);
                    _order.RemoveFirst();
                }
                QueueRedraw();
            },
            () =>
            {
                // Still on screen (or close to it) in the same world? Plain field reads only (runs on the workers).
                if (key.world != _world) return false;
                var (ccx, ccy) = WorldPreview.ChunkAt(_x, _y);
                return Math.Abs(key.cx - ccx) <= Radius + 2 && Math.Abs(key.cy - ccy) <= Radius + 2;
            },
            () => _pending.Remove(key));
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _dragging = button.Pressed;
            if (button.Pressed) TravelTo(button.Position);
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion motion && _dragging)
        {
            TravelTo(motion.Position);
            AcceptEvent();
        }
    }

    private void TravelTo(Vector2 local)
    {
        var offset = (local - Size / 2) / MapScale;
        Travel?.Invoke(_x + offset.X, _y + offset.Y);
    }
}
