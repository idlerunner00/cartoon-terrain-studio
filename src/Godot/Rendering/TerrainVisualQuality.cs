using System;
using Godot;

namespace Fluitown.GodotApp.Rendering;

/// <summary>
/// Visual enhancements of the Godot terrain beyond the browser original. <see cref="Original"/> is the browser-exact
/// pipeline; the live
/// world starts with <see cref="High"/>. Every enhancement leaves the ported material math untouched and works on
/// top of it: more samples per pixel, a finer sun shadow map, occlusion and silhouette ink as a separate multiply
/// pass, sky reflection on the water in the Flui perspective, and output dithering.
/// The live profiles are bound by the frame budget: <see cref="High"/> must hold 180 FPS on the
/// reference machine, <see cref="Ultra"/> (supersampling) is an explicit exception for pictures. Weaker devices get
/// <see cref="Medium"/> or <see cref="Low"/> (<see cref="ForDevice"/>, `auto`): the same comic look and world ink with
/// native pixels, cheaper shadows and occlusion and a shorter meadow reach.
/// </summary>
public sealed class TerrainVisualQuality
{
    public string Name { get; private set; } = "original";
    /// <summary>The HUD's name of the profile.</summary>
    public string Label { get; private set; } = "Basis";

    /// <summary>Supersampling: internal 3D pixels per output pixel along each axis (1 = none).</summary>
    public float RenderScale { get; private set; } = 1;
    /// <summary>
    /// Pixel budget of the internal 3D resolution (megapixels, 0 = none): large windows get less supersampling and,
    /// below 1×, use FSR. The legibility floor takes precedence over this budget in large windows.
    /// </summary>
    public float MaxInternalMegapixels { get; private set; } = 0;
    /// <summary>Minimum resolution per axis. Live profiles keep native pixels; reduction is an explicit measurement override.</summary>
    public float MinimumRenderScale { get; private set; } = 1f;
    /// <summary>Fallback antialiasing after tonemapping. Multisampled world ink needs no additional face-blurring FXAA.</summary>
    public Viewport.ScreenSpaceAAEnum ScreenSpaceAA { get; private set; } = Viewport.ScreenSpaceAAEnum.Disabled;
    /// <summary>MSAA of the HDR world target (the original: 2 samples, postfx.ts worldHdrMsaaSamples).</summary>
    public Viewport.Msaa Msaa { get; private set; } = Viewport.Msaa.Msaa2X;

    /// <summary>Sun shadow map resolution as a multiple of the original's (1024² per side).</summary>
    public int ShadowMapScale { get; private set; } = 1;
    /// <summary>PCF taps of the sun shadow (0 = three's 5-tap disk).</summary>
    public int ShadowTaps { get; private set; }
    /// <summary>Penumbra radius as a share of the original's (whose radius is one texel of its coarse map).</summary>
    public float ShadowSoftness { get; private set; } = 1;

    /// <summary>
    /// The world sun's Godot shadow atlas (per side) and soft-shadow filter. `original` keeps the engine defaults
    /// (project settings).
    /// </summary>
    public int SunShadowAtlas { get; private set; } = 4096;
    public RenderingServer.ShadowQuality SunShadowFilter { get; private set; } = RenderingServer.ShadowQuality.SoftLow;

    /// <summary>
    /// Ambient occlusion strength (0 = off) and radius in compile px (1 px ≈ 4 cm): Godot's SSAO on the comic surfaces
    /// (they are lit by Godot, like godot-flui's world).
    /// </summary>
    public float AmbientOcclusion { get; private set; }
    /// <summary>Comic look: Godot's SSAO at half resolution (cheaper; the supersampled frame hides the difference).</summary>
    public bool AmbientOcclusionHalfSize { get; private set; }
    /// <summary>Sky reflection and sun glint on the water in the Flui perspective (0 = off).</summary>
    public float WaterViewReflection { get; private set; }
    /// <summary>Output dither in 8-bit levels (0 = off).</summary>
    public float Dither { get; private set; }

    /// <summary>Comic look: contact-hardening soft sun shadows (PCSS through the sun's angular size); off = plain PCF
    /// filtering with the same blur, which skips the blocker search.</summary>
    public bool SunSoftShadows { get; private set; } = true;
    /// <summary>Comic look: the Flui perspective splits the sun's shadow in two (finer near the Flui); off = one map
    /// over the whole shadow range (every caster is drawn once instead of per split).</summary>
    public bool SunShadowSplits { get; private set; } = true;
    /// <summary>Comic look: reach of the sun's shadow in the Flui perspective as a share of godot-flui's 60 m.</summary>
    public float SunShadowRange { get; private set; } = 1;
    /// <summary>Comic look: Godot's SSAO on terrain and Flui (with <see cref="AmbientOcclusion"/> above 0); the terrain-only
    /// occlusion pass keeps running without it.</summary>
    public bool Ssao { get; private set; } = true;
    /// <summary>Comic look: how far the meadow is drawn in the Flui perspective, as a share of the full reach (the
    /// wildflowers and wall plants follow it).</summary>
    public float MeadowReach { get; private set; } = 1;
    /// <summary>Comic look: single leaves over the bushes' crowns in the Flui perspective (LeafShell).</summary>
    public bool LeafShells { get; private set; } = true;
    /// <summary>Share of the meadow's tufts that is drawn (1 = every one). The tufts of a chunk are shuffled when it is
    /// built, so a smaller share thins the meadow evenly instead of leaving bare patches; it is the only lever that
    /// takes geometry (not pixels) off a weak GPU without changing the look of a single blade.</summary>
    public float MeadowDensity { get; private set; } = 1;
    /// <summary>Consolidate distant grass into wider ribbons instead of losing all blades below a pixel.</summary>
    public bool MeadowClumps { get; private set; }
    /// <summary>Comic look: the terrain surface's relief normal (bump shading from its material noise). Off, the surface
    /// keeps its geometric normal and the depth pre-pass evaluates no noise: 0.16-0.19 ms GPU per frame on the reference
    /// machine at 1.5 MP; pigment, patterns and relief tone stay.</summary>
    public bool SurfaceRelief { get; private set; } = true;

    // World look: Fluitown extension, not in the original.
    /// <summary>Footprint-filtered mineral chips and meadow fibres on the ground.</summary>
    public bool GroundPaint { get; private set; }
    /// <summary>Continuous ground layers sharing vegetation cover, wear, weather and the central comic lighting.</summary>
    public bool GroundLayers { get; private set; }
    /// <summary>Hardware-filtered, mipmapped material field in place of repeated four-gradient evaluations.</summary>
    public bool FastMaterialNoise { get; private set; } = true;
    /// <summary>One coherent rock material across crowns and walls, replacing stacked legacy patterns.</summary>
    public bool RockLayers { get; private set; }
    /// <summary>Strata, foot moss and crest grass on the mountain-form cliffs.</summary>
    public bool RockDetail { get; private set; }
    /// <summary>Comic look with godot-flui's image pipeline (TerrainSceneRenderer.FluiLook): the Environment's glow on
    /// highlights above HDR 1.6. Only <see cref="Ultra"/>: in this world's light almost nothing reaches the threshold
    /// (glow on/off captures differ only by animation), and it cost ≈ 0.15 ms GPU.</summary>
    public bool Glow { get; private set; }

    // STUDIO: the crowd-detail hook (ApplyToCrowds) of the character runtime is not part of the terrain studio.
    public bool IsOriginal => Name == "original";


    /// <summary>The browser-exact pipeline, as validated against the reference captures.</summary>
    public static readonly TerrainVisualQuality Original = new();

    /// <summary>
    /// The live default: native resolution, 2× MSAA for fine geometry and FXAA for ink and shading edges.
    /// The adaptive governor reduces effects and geometry without reconstructing small faces from fewer pixels.
    /// The 180 FPS reference remains 1600×900; higher output resolutions cost more GPU time.
    /// Sun shadows: a 4096 atlas with the soft-medium filter looked the same as 8192 with soft-high in both views and gave
    /// the GPU budget back 0.25–0.35 ms (it was exceeded after the organic terrain).
    /// </summary>
    public static readonly TerrainVisualQuality High = new()
    {
        Name = "high",
        RockLayers = true,
        MeadowClumps = true,
        GroundLayers = true,
        Label = "High",
        SunShadowAtlas = 4096,
        SunShadowFilter = RenderingServer.ShadowQuality.SoftMedium,
        // Filtered PCF retains soft edges without the noisy PCSS blocker search.
        SunSoftShadows = false,
        AmbientOcclusionHalfSize = true,
        RenderScale = 1,
        MaxInternalMegapixels = 4.0f,
        // Current-frame coverage for tiny silhouettes, without FXAA/TAA blur.
        Msaa = Viewport.Msaa.Msaa8X,
        ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Fxaa,
        ShadowMapScale = 4,
        ShadowTaps = 16,
        ShadowSoftness = 0.55f,
        AmbientOcclusion = 1f,
        WaterViewReflection = 0.6f,
        Dither = 1,
        GroundPaint = true,
        RockDetail = true,
    };

    /// <summary>
    /// Mid-range and older discrete GPUs: up to 2.5 MP, 2× MSAA and FSR where needed,
    /// a 4096 sun atlas with plain PCF instead of PCSS, occlusion only in the terrain pass (no Godot SSAO), 80 % of the
    /// meadow reach and 60 % of the crowd's fully animated Fluis.
    /// </summary>
    public static readonly TerrainVisualQuality Medium = new()
    {
        Name = "medium",
        RockLayers = true,
        MeadowClumps = true,
        GroundLayers = true,
        Label = "Medium",
        SunShadowAtlas = 4096,
        SunShadowFilter = RenderingServer.ShadowQuality.SoftLow,
        SunSoftShadows = false,
        SunShadowRange = .85f,
        AmbientOcclusionHalfSize = true,
        Ssao = false,
        RenderScale = 1,
        MaxInternalMegapixels = 2.5f,
        Msaa = Viewport.Msaa.Msaa2X,
        ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Fxaa,
        ShadowMapScale = 4,
        ShadowTaps = 16,
        ShadowSoftness = 0.55f,
        AmbientOcclusion = 1f,
        WaterViewReflection = 0.6f,
        Dither = 1,
        MeadowReach = .8f,
        RockDetail = true,
    };

    /// <summary>
    /// Integrated graphics and weak GPUs: native resolution, FXAA and 2× MSAA, one 2048 sun map over 70 % of the
    /// shadow range with plain PCF, no occlusion, 60 % of the meadow reach and a third of the crowd's fully animated
    /// Fluis. Comic shading and world ink stay exactly as in <see cref="High"/>.
    /// </summary>
    public static readonly TerrainVisualQuality Low = new()
    {
        Name = "low",
        RockLayers = true,
        MeadowClumps = true,
        GroundLayers = true,
        Label = "Low",
        SunShadowAtlas = 2048,
        SunShadowFilter = RenderingServer.ShadowQuality.SoftLow,
        SunSoftShadows = false,
        SunShadowSplits = false,
        SunShadowRange = .7f,
        AmbientOcclusionHalfSize = true,
        Ssao = false,
        RenderScale = 1,
        MaxInternalMegapixels = 1.5f,
        Msaa = Viewport.Msaa.Msaa2X,
        ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Fxaa,
        ShadowMapScale = 4,
        ShadowTaps = 16,
        ShadowSoftness = 0.55f,
        AmbientOcclusion = 0,
        WaterViewReflection = 0.6f,
        Dither = 1,
        MeadowReach = .6f,
        // A third of the tufts, evenly thinned (the chunks are shuffled when they are built): −0.13 ms GPU at 0.6 MP on
        // the reference machine, the only lever that takes geometry rather than pixels off a weak GPU.
        MeadowDensity = .35f,
        LeafShells = false,
        // The relief's bump shading (pigment, patterns and ink stay): −0.15–0.23 ms at 1.44 MP on the reference, far more
        // on a fill-rate-bound GPU.
        SurfaceRelief = false,
    };

    /// <summary>
    /// Pictures, not play: 2× supersampling (up to 6.5 MP) — the former default. About 2.4× the GPU time of
    /// <see cref="High"/>; outside the frame budget.
    /// </summary>
    public static readonly TerrainVisualQuality Ultra = new()
    {
        Name = "ultra",
        RockLayers = true,
        MeadowClumps = true,
        GroundLayers = true,
        Label = "Ultra, above the 180 FPS budget",
        Glow = true,
        SunShadowAtlas = 8192,
        SunShadowFilter = RenderingServer.ShadowQuality.SoftHigh,
        AmbientOcclusionHalfSize = true,
        RenderScale = 2,
        MaxInternalMegapixels = 6.5f,
        Msaa = Viewport.Msaa.Msaa2X,
        ShadowMapScale = 4,
        ShadowTaps = 16,
        ShadowSoftness = 0.55f,
        AmbientOcclusion = 1f,
        WaterViewReflection = 0.6f,
        Dither = 1,
        GroundPaint = true,
        RockDetail = true,
    };

    /// <summary>Whether a `--quality` name asks for the device's profile (`auto`, also empty); any other name fixes the
    /// profile.</summary>
    public static bool IsAuto(string? name) => (name ?? "").Trim().ToLowerInvariant() is "" or "auto";

    /// <summary>
    /// The starting profile for this device: <see cref="High"/> on a discrete GPU, <see cref="Low"/> on integrated
    /// graphics, a software rasteriser or a virtual GPU, <see cref="Medium"/> when the driver does not say.
    /// </summary>
    public static TerrainVisualQuality ForDevice(out string reason)
    {
        var type = RenderingServer.GetVideoAdapterType();
        string adapter = RenderingServer.GetVideoAdapterName();
        reason = $"{adapter} ({type}), {OS.GetProcessorCount()} threads";
        return type switch
        {
            RenderingDevice.DeviceType.DiscreteGpu => High,
            RenderingDevice.DeviceType.IntegratedGpu or RenderingDevice.DeviceType.Cpu or RenderingDevice.DeviceType.VirtualGpu => Low,
            _ => Medium,
        };
    }

    /// <summary>
    /// `original` (also `off`), `low`, `medium`, `high`, `ultra`, `auto` (the
    /// device's profile, <see cref="ForDevice"/>); a profile may be followed by overrides for measurements, e.g.
    /// `high+scale=1+msaa=4+ao=0+atlas=4096+filter=2`.
    /// </summary>
    public static TerrainVisualQuality Parse(string? name)
    {
        var parts = (name ?? "").Trim().ToLowerInvariant().Split('+');
        var profile = parts[0] switch
        {
            "" or "auto" => ForDevice(out _),
            "high" => High,
            "medium" => Medium,
            "low" => Low,
            "original" or "off" or "browser" => Original,
            "ultra" => Ultra,
            _ => throw new ArgumentException($"unknown terrain quality '{name}' (auto, low, medium, high, ultra, original)"),
        };
        for (int i = 1; i < parts.Length; i++)
        {
            var kv = parts[i].Split('=');
            if (kv.Length != 2) throw new ArgumentException($"quality override '{parts[i]}' is not key=value");
            profile = profile.With(kv[0], kv[1]);
        }
        return profile;
    }

    /// <summary>A copy with one setting overridden (measurements; see <see cref="Parse"/>).</summary>
    public TerrainVisualQuality With(string key, string value)
    {
        float f = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var q = (TerrainVisualQuality)MemberwiseClone();
        q.Name = $"{Name}+{key}={value}";
        switch (key)
        {
            case "scale": q.RenderScale = f; break;
            case "minscale": q.MinimumRenderScale = Math.Clamp(f, .5f, 1); break;
            case "msaa": q.Msaa = f switch { <= 1 => Viewport.Msaa.Disabled, 2 => Viewport.Msaa.Msaa2X, 4 => Viewport.Msaa.Msaa4X, _ => Viewport.Msaa.Msaa8X }; break;
            case "atlas": q.SunShadowAtlas = (int)f; break;
            case "filter": q.SunShadowFilter = (RenderingServer.ShadowQuality)(int)f; break;
            case "ao": q.AmbientOcclusion = f; break;
            case "aohalf": q.AmbientOcclusionHalfSize = f > 0; break;
            case "saa": q.ScreenSpaceAA = (Viewport.ScreenSpaceAAEnum)(int)f; break;
            case "mp": q.MaxInternalMegapixels = f; break;
            case "soft": q.SunSoftShadows = f > 0; break;
            case "splits": q.SunShadowSplits = f > 0; break;
            case "shadowrange": q.SunShadowRange = f; break;
            case "ssao": q.Ssao = f > 0; break;
            case "meadow": q.MeadowReach = f; break;
            case "density": q.MeadowDensity = f; break;
            case "clumps": q.MeadowClumps = f > 0; break;
            case "shells": q.LeafShells = f > 0; break;
            case "groundpaint": q.GroundPaint = f > 0; break;
            case "fastnoise": q.FastMaterialNoise = f > 0; break;
            case "groundlayers": q.GroundLayers = f > 0; break;
            case "rocklayers": q.RockLayers = f > 0; break;
            case "rock": q.RockDetail = f > 0; break;
            case "glow": q.Glow = f > 0; break;
            case "relief": q.SurfaceRelief = f > 0; break;
            default: throw new ArgumentException($"unknown quality override '{key}' (scale, minscale, msaa, atlas, filter, ao, aohalf, saa, mp, soft, splits, shadowrange, ssao, meadow, density, clumps, shells, crowd, sky, landmarks, groundpaint, groundlayers, rocklayers, rock, glow, relief)");
        }
        return q;
    }

    /// <summary>
    /// Internal 3D pixels per output pixel for a window of the given size: <see cref="RenderScale"/>, capped by the
    /// <see cref="MaxInternalMegapixels"/> budget times <paramref name="pixelShare"/> — above 1 in quarter steps (bilinear downsampling is an exact
    /// box filter at 2×), below 1 in steps of 1/20 down to <see cref="MinimumRenderScale"/> (FSR upscaling).
    /// </summary>
    public float EffectiveRenderScale(int width, int height, float pixelShare = 1)
    {
        if (width <= 0 || height <= 0) return 1;
        float scale = MathF.Max(1, RenderScale);
        if (MaxInternalMegapixels > 0)
            scale = MathF.Min(scale, MathF.Sqrt(MaxInternalMegapixels * pixelShare * 1e6f / (width * (float)height)));
        if (scale >= 1) return MathF.Floor(scale * 4) / 4;
        // Half resolution destroys the one-pixel ink and small faces before FSR
        // sees them. Keep a legibility floor, also when the adaptive governor
        // lowers its pixel budget. Explicit diagnostic overrides can test below it.
        return MathF.Max(MinimumRenderScale, MathF.Floor(scale * 20) / 20);
    }
}
