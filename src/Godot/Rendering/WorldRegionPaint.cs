using System;
using System.Threading.Tasks;
using Fluitown.GodotApp.Runtime;
using Fluitown.Render;
using Godot;

namespace Fluitown.GodotApp.Rendering;

/// <summary>
/// The shader side of the regions (Explore's "regions" option): the
/// meadow's splat pigments (lush, dry, mineral, turf) and the rock's growth (moss, cap grass) are global uniforms of the
/// ported terrain shader. Around the eye a small texture holds the weights of the four regions of a 1.6 km window
/// (10 m per texel, rebuilt on a worker once the eye has moved 300 m); the comic surface shader mixes their pigments
/// (fluitownRegionSplat), and slots of the start's own region keep the ported ones. Both perspectives.
/// </summary>
public sealed partial class WorldRegionPaint : Node
{
    // STUDIO: any terrain host (the game used its world node here).
    public ITerrainHost World = null!;

    private const int Texels = 160;
    private const double WindowMetres = 1600;

    private static readonly StringName Regions = "fluitown_regions", Weights = "fluitown_region_weights";
    private static readonly StringName Frame = "fluitown_region_frame", Palette = "fluitown_region_palette", Native = "fluitown_region_native";
    private static readonly StringName Keep = "fluitown_region_keep";

    private Image _image = null!;
    private ImageTexture _texture = null!;
    private Vector3 _centre = new(float.MaxValue, 0, 0);
    private Task<(byte[] weights, int[] slots, double originX, double originZ)>? _building;
    private double _timer;

    public override void _Ready()
    {
        ProcessPriority = 121;
        _image = Image.CreateEmpty(Texels, Texels, false, Image.Format.Rgba8);
        _texture = ImageTexture.CreateFromImage(_image);
        World.Renderer.Surface.SetShaderParameter(Weights, _texture);
    }

    public override void _Process(double delta)
    {
        _timer -= delta;
        if (_timer > 0) return;
        _timer = 0.5;
        if (_building is { IsCompleted: true } done)
        {
            _building = null;
            if (done.IsCompletedSuccessfully) Install(done.Result);
            else GD.PushWarning($"world regions: paint window failed: {done.Exception?.GetBaseException().Message}");
        }
        if (_building != null) return;
        // The bird's-eye camera stands far above its focus: the session's focus is the place in both views.
        var focus = new Vector3((float)(World.Session.focusX / FluitownVegetation.PxPerMetre), 0,
            (float)(World.Session.focusY / FluitownVegetation.PxPerMetre));
        if (new Vector2(focus.X - _centre.X, focus.Z - _centre.Z).Length() < 300) return;
        _centre = focus;
        double cx = focus.X * FluitownVegetation.PxPerMetre, cz = focus.Z * FluitownVegetation.PxPerMetre;
        _building = Task.Run(() => Build(cx, cz));
    }

    // The build's stores, reused (one build at a time; the next starts after this one's install): garbage on a worker
    // still pauses the main thread.
    private readonly int[] _regions = new int[Texels * Texels * 4];
    private readonly double[] _weights = new double[Texels * Texels * 4];
    private readonly byte[] _bytes = new byte[Texels * Texels * 4];
    private readonly double[] _total = new double[WorldRegions.Regions.Length];
    private readonly int[] _slotOf = new int[WorldRegions.Regions.Length];

    private (byte[] weights, int[] slots, double originX, double originZ) Build(double centreX, double centreZ)
    {
        double size = WindowMetres * FluitownVegetation.PxPerMetre, texel = size / Texels;
        double originX = centreX - size / 2, originZ = centreZ - size / 2;
        int[] regions = _regions;
        double[] weights = _weights, total = _total;
        Array.Clear(total);
        Span<int> r = stackalloc int[4];
        Span<double> w = stackalloc double[4];
        for (int y = 0; y < Texels; y++)
            for (int x = 0; x < Texels; x++)
            {
                WorldRegions.Sample(originX + (x + 0.5) * texel, originZ + (y + 0.5) * texel, r, w);
                int at = (y * Texels + x) * 4;
                // Near the eye counts most: what it stands in must get a slot, a far corner of the window may not.
                double dx = (x + 0.5) / Texels - 0.5, dz = (y + 0.5) / Texels - 0.5;
                double near = Math.Exp(-(dx * dx + dz * dz) * (WindowMetres * WindowMetres) / (400.0 * 400.0));
                for (int k = 0; k < 4; k++)
                {
                    regions[at + k] = r[k];
                    weights[at + k] = w[k];
                    if (w[k] > 0) total[r[k]] += w[k] * near;
                }
            }
        // The four regions with most (nearness-weighted) weight get the slots; a region left without one (only far from
        // the eye) paints as the slot whose turf is most like its own.
        var slots = new int[] { -1, -1, -1, -1 };
        for (int s = 0; s < 4; s++)
        {
            int best = -1;
            for (int region = 0; region < total.Length; region++)
                if (total[region] > 0 && Array.IndexOf(slots, region) < 0 && (best < 0 || total[region] > total[best])) best = region;
            slots[s] = best;
        }
        int[] slotOf = _slotOf;
        for (int region = 0; region < slotOf.Length; region++)
        {
            slotOf[region] = Array.IndexOf(slots, region);
            if (slotOf[region] >= 0) continue;
            double closest = double.MaxValue;
            for (int s = 0; s < 4; s++)
            {
                if (slots[s] < 0) continue;
                float[] a = WorldRegions.Regions[region].Turf, b = WorldRegions.Regions[slots[s]].Turf;
                double d = (a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]);
                if (d < closest) { closest = d; slotOf[region] = s; }
            }
            if (slotOf[region] < 0) slotOf[region] = 0;
        }
        byte[] bytes = _bytes;
        Array.Clear(bytes);
        for (int t = 0; t < Texels * Texels; t++)
            for (int k = 0; k < 4; k++)
            {
                double weight = weights[t * 4 + k];
                if (weight <= 0) continue;
                int slot = slotOf[regions[t * 4 + k]];
                bytes[t * 4 + slot] = (byte)Math.Min(255, bytes[t * 4 + slot] + (int)Math.Round(weight * 255));
            }
        return (bytes, slots, originX, originZ);
    }

    private void Install((byte[] weights, int[] slots, double originX, double originZ) window)
    {
        _image.SetData(Texels, Texels, false, Image.Format.Rgba8, window.weights);
        _texture.Update(_image);
        var palette = new Vector3[24];
        var native = new Vector4();
        for (int slot = 0; slot < 4; slot++)
        {
            var region = WorldRegions.Regions[Math.Max(0, window.slots[slot])];
            bool flui = WorldRegions.FluiPalette;
            palette[0 * 4 + slot] = V(region.Lush);
            palette[1 * 4 + slot] = V(flui && region.FluiDry != null ? region.FluiDry : region.Dry);
            palette[2 * 4 + slot] = V(flui && region.FluiMineral != null ? region.FluiMineral : region.Mineral);
            palette[3 * 4 + slot] = V(region.Turf);
            palette[4 * 4 + slot] = V(flui && region.FluiMoss != null ? region.FluiMoss : region.Moss);
            palette[5 * 4 + slot] = V(flui && region.FluiCapGrass != null ? region.FluiCapGrass : region.CapGrass);
            native[slot] = region.Native && window.slots[slot] >= 0 ? 1 : 0;
        }
        var surface = World.Renderer.Surface;
        surface.SetShaderParameter(Palette, palette);
        surface.SetShaderParameter(Native, native);
        // Under godot-flui's pipeline the start's own slot paints its dry and mineral patches (keeps lush and turf).
        surface.SetShaderParameter(Keep, WorldRegions.FluiPalette ? new Vector4(1, 0, 0, 1) : Vector4.One);
        double size = WindowMetres * FluitownVegetation.PxPerMetre;
        surface.SetShaderParameter(Frame, new Vector4((float)window.originX, (float)window.originZ, (float)(1 / size), 0));
        surface.SetShaderParameter(Regions, true);
        GD.Print($"world regions: paint window at {window.originX:F0},{window.originZ:F0} px, slots {string.Join(",", window.slots)}");
    }

    private static Vector3 V(float[] c) => new(c[0], c[1], c[2]);
}
