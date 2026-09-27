using System;
using System.Collections.Generic;
using Flui;
using Fluitown.Render;
using Godot;
using static Fluitown.Render.FluitownVegetation;

namespace Fluitown.GodotApp.Rendering.Vegetation;

/// <summary>
/// The comic look's vegetation of one bake tile, built off the main thread from the payload's vegetation lane
/// (<see cref="FluitownVegetation"/>): one flat-shaded mesh with every tree and bush of the tile
/// (<see cref="PlantGrowth"/>, <see cref="PlantMesher"/>) and the meadow as MultiMesh chunks of godot-flui tufts.
/// </summary>
public static class TerrainVegetation
{
    /// <summary>Side of one grass chunk (m): the visibility-range unit in the Flui perspective.</summary>
    public const float GrassChunk = 16f;
    /// <summary>Side of one chunk of wildflowers and wall plants (m): their visibility-range unit in the Flui perspective
    /// (large: they are sparse, and every chunk is a draw call in each pass).</summary>
    public const float HerbChunk = 40f;
    /// <summary>Side of one chunk of leaf shells (m): their visibility-range unit in the Flui perspective.</summary>
    public const float ShellChunk = 32f;
    /// <summary>Floats per tuft in the MultiMesh buffer: 3×4 transform + custom data (no instance colour).</summary>
    public const int TuftStride = 16;

    public sealed class Prepared : IDisposable
    {
        public ArrayMesh? Plants;
        public readonly List<GrassChunkData> Grass = new();
        /// <summary>Lily pads, reeds and water grass (<see cref="InstancedPlants"/>).</summary>
        public InstancedPlants.Prepared? Water;
        /// <summary>Wildflowers and wall plants (<see cref="FlowerMesher"/>, <see cref="WallPlantMesher"/>), smooth and
        /// two-sided: the Flui perspective's full plants per chunk, and one mesh per tile for the bird's-eye view (the
        /// flowers' stand-ins and the wall plants).</summary>
        public List<HerbChunk>? Herbs;
        public ArrayMesh? HerbsFar;
        /// <summary>Leaves over the bushes' crowns (<see cref="LeafShell"/>), drawn in the Flui perspective.</summary>
        public List<HerbChunk>? Shells;

        public void Dispose()
        {
            Plants?.Dispose(); HerbsFar?.Dispose();
            Plants = HerbsFar = null;
            if (Herbs != null) foreach (var chunk in Herbs) chunk.Mesh.Dispose();
            if (Shells != null) foreach (var chunk in Shells) chunk.Mesh.Dispose();
            Herbs = Shells = null;
        }
    }

    /// <summary>
    /// Shuffles a chunk's tufts once (worker thread, deterministic per chunk), so that any prefix of the MultiMesh is an
    /// even sample of the whole chunk: a quality profile thins the meadow with <c>VisibleInstanceCount</c>
    /// (<see cref="Fluitown.GodotApp.Rendering.TerrainVisualQuality.MeadowDensity"/>) instead of rebuilding the buffer,
    /// and no corner of the chunk stays bare. The tufts' own order carries no meaning (the shader varies them by their
    /// custom data).
    /// </summary>
    private static void ShuffleTufts(float[] buffer, int count, int seed)
    {
        uint state = (uint)seed | 1u;
        for (int i = count - 1; i > 0; i--)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            int j = (int)(state % (uint)(i + 1));
            if (j == i) continue;
            for (int f = 0; f < TuftStride; f++)
                (buffer[i * TuftStride + f], buffer[j * TuftStride + f]) = (buffer[j * TuftStride + f], buffer[i * TuftStride + f]);
        }
    }

    public sealed class GrassChunkData
    {
        public required Vector3 Origin;
        public required float[] Buffer;
        public required int Count;
        public required Aabb Bounds;
    }

    private static Color Hex(float value)
    {
        int hex = (int)MathF.Round(value);
        return new Color(((hex >> 16) & 0xff) / 255f, ((hex >> 8) & 0xff) / 255f, (hex & 0xff) / 255f);
    }

    private static uint SeedOf(float seed, float x, float z) =>
        unchecked((uint)(seed * 16777215f) * 2654435761u ^ (uint)(int)MathF.Round(x) * 40503u ^ (uint)(int)MathF.Round(z) * 69069u);

    /// <summary>godot-flui's five crown tints (<c>UnderstoryPlant.Grow</c>): the leaf, two lifts towards ivory, two steps
    /// towards its shade — here the terrain world's own leaf and shade pigments.</summary>
    private static Color[] CrownTints(Color leaf, Color shade, float lift = 1)
    {
        var ivory = new Color(0.78f, 0.80f, 0.71f);
        return new[] { leaf, leaf.Lerp(ivory, .12f * lift), leaf.Lerp(ivory, .23f * lift), leaf.Lerp(shade, .36f), leaf.Lerp(shade, .18f) };
    }

    /// <summary>The growth plan and pigments of the tree whose lane record starts at <paramref name="i"/>
    /// (<paramref name="scale"/>: metres per compile px). Shared by the tile mesh and a felled tree's own mesh.</summary>
    public static PlantPlan TreePlan(float[] records, int i, float scale, out PlantPalette palette, out uint plantSeed)
    {
        float height = records[i + Height] * scale, radius = records[i + Radius] * scale;
        plantSeed = SeedOf(records[i + Seed], records[i + X], records[i + Z]);
        var plan = PlantGrowth.Tree(height, radius, (int)records[i + Form], records[i + Lean], records[i + Rotation], plantSeed);
        var leaf = Hex(records[i + ColourLeaf]);
        palette = new PlantPalette(CrownTints(leaf, Hex(records[i + ColourShade])), Hex(records[i + ColourWood]),
            Hex(records[i + ColourAccent]), records[i + Bloom] > 0 ? records[i + Bloom] * .3f : 0);
        return plan;
    }

    /// <summary>Wind weight scale of a tree of this height (m).</summary>
    public static float TreeWind(float height) => .045f + height * .012f;

    /// <summary>Thread-safe: no scene-tree access.</summary>
    public static Prepared? Prepare(TerrainVegetationLane? lane, Transform3D compileToWorld)
    {
        if (lane == null || lane.Count == 0) return null;
        float scale = compileToWorld.Basis.X.Length();
        var prepared = new Prepared();
        var plants = PlantMeshBuffers.Rent();
        HerbMeshBuffers? herbsFar = null;
        HerbChunks? herbs = null;
        var shells = new HerbChunks(ShellChunk);
        var chunks = new Dictionary<(int, int), List<float>>();
        var records = lane.records;
        for (int i = 0; i + Stride <= records.Length; i += Stride)
        {
            int kind = (int)MathF.Round(records[i + Kind]);
            var foot = compileToWorld * new Vector3(records[i + X], records[i + Y], records[i + Z]);
            float height = records[i + Height] * scale, radius = records[i + Radius] * scale;
            float seed = records[i + Seed];
            switch (kind)
            {
                case KindTree:
                {
                    var plan = TreePlan(records, i, scale, out var palette, out uint plantSeed);
                    PlantMesher.Emit(plants, plan, foot, palette, seed, TreeWind(height), plantSeed);
                    break;
                }
                case KindBush:
                {
                    uint plantSeed = SeedOf(seed, records[i + X], records[i + Z]);
                    var leaf = Hex(records[i + ColourLeaf]);
                    var accent = Hex(records[i + ColourAccent]);
                    // godot-flui's heath: a flowering cushion in the world's accent hue; about a third of the thickets.
                    bool heath = (plantSeed % 100) < 35;
                    // Undergrowth sits deeper and richer in the world's leaf than the canopy above it: half the ivory
                    // lift, a step towards the shade, more chroma — a pale, even bush reads as a boulder.
                    var deep = leaf.Lerp(Hex(records[i + ColourShade]), .22f);
                    deep = Color.FromHsv(deep.H, Math.Min(1, deep.S * 1.35f), deep.V);
                    var crown = heath ? leaf.Lerp(accent, .55f) : deep;
                    var plan = PlantGrowth.Bush(height, radius * .8f, (int)records[i + Density], heath, plantSeed);
                    var palette = new PlantPalette(CrownTints(crown, Hex(records[i + ColourShade]), .5f), Hex(records[i + ColourWood]),
                        accent, heath ? .25f : 0);
                    if (FluitownFlowers.Enabled)
                    {
                        // A deeper inner mass under a shell of single leaves (LeafShell), which the perspective view draws.
                        PlantMesher.Emit(plants, LeafShell.Core(plan, .9f), foot, LeafShell.Shaded(palette, Hex(records[i + ColourShade]), .25f), seed, .035f, plantSeed, 34);
                        LeafShell.Emit(shells.At(foot), plan, foot, palette, seed, .035f, plantSeed, Basis.Identity);
                    }
                    else
                        PlantMesher.Emit(plants, plan, foot, palette, seed, .035f, plantSeed, 34);
                    break;
                }
                case KindGrass:
                {
                    var key = ((int)MathF.Floor(foot.X / GrassChunk), (int)MathF.Floor(foot.Z / GrassChunk));
                    if (!chunks.TryGetValue(key, out var list)) chunks[key] = list = new List<float>(4096);
                    // godot-flui's tuft (MeadowGrass.BladeMesh) is 0.32–0.48 m tall and 0.19 m wide at its roots.
                    float up = height / .42f;
                    float wide = Math.Clamp(radius / .17f, .65f, 1.45f);
                    var basis = new Basis(Vector3.Up, records[i + Rotation]).Scaled(new Vector3(wide, up, wide));
                    var origin = new Vector3(key.Item1 * GrassChunk, 0, key.Item2 * GrassChunk);
                    var p = foot - origin - Vector3.Up * .02f;
                    list.Add(basis.X.X); list.Add(basis.Y.X); list.Add(basis.Z.X); list.Add(p.X);
                    list.Add(basis.X.Y); list.Add(basis.Y.Y); list.Add(basis.Z.Y); list.Add(p.Y);
                    list.Add(basis.X.Z); list.Add(basis.Y.Z); list.Add(basis.Z.Z); list.Add(p.Z);
                    list.Add(records[i + ColourLeaf]);
                    list.Add(records[i + Lean]);
                    list.Add(records[i + ColourShade]);
                    float variation = MathF.Floor(Math.Clamp(seed, 0, 1) * 255);
                    list.Add(variation + (seed * 7.31f - MathF.Floor(seed * 7.31f)) * .999f);
                    break;
                }
                case FluitownFlowers.KindFlower:
                {
                    FlowerMesher.Emit((herbs ??= new HerbChunks(HerbChunk)).At(foot), herbsFar ??= new HerbMeshBuffers(), records, i, compileToWorld);
                    break;
                }
                case FluitownWallPlants.KindWallPlant:
                {
                    WallPlantMesher.Emit((herbs ??= new HerbChunks(HerbChunk)).At(foot), herbsFar ??= new HerbMeshBuffers(), plants, shells.At(foot),
                        records, i, compileToWorld);
                    break;
                }
                case FluitownWallPlants.KindWallTangent:
                case FluitownWallPlants.KindWallFoot:
                    break;
                default:
                    InstancedPlants.Collect(ref prepared.Water, records, i, foot, scale);
                    break;
            }
        }
        prepared.Water?.Finish();
        prepared.Plants = plants.Commit();
        plants.Return();
        prepared.Herbs = herbs?.Commit();
        prepared.HerbsFar = herbsFar?.Commit();
        prepared.Shells = shells.Commit();
        if (prepared.Shells.Count == 0) prepared.Shells = null;
        foreach (var ((cx, cz), list) in chunks)
        {
            int count = list.Count / TuftStride;
            var buffer = list.ToArray();
            ShuffleTufts(buffer, count, cx * 73856093 ^ cz * 19349663);
            var bounds = new Aabb();
            for (int t = 0; t < count; t++)
            {
                var p = new Vector3(buffer[t * TuftStride + 3], buffer[t * TuftStride + 7], buffer[t * TuftStride + 11]);
                bounds = t == 0 ? new Aabb(p, Vector3.Zero) : bounds.Expand(p);
            }
            // Blade height, wind, footprints and the contact bend.
            prepared.Grass.Add(new GrassChunkData { Origin = new Vector3(cx * GrassChunk, 0, cz * GrassChunk), Buffer = buffer, Count = count, Bounds = bounds.Grow(1.5f) });
        }
        return prepared;
    }
}

/// <summary>
/// The comic look's vegetation layer (a child of the terrain's scene viewport): the plant and meadow materials, one node
/// group per bake tile (shown and freed with the tile), and the per-frame state — the terrain's wind, haze and
/// compile-space placement copied from the terrain surface material, godot-flui's footprints and contact bend for the
/// Flui and its companion, and the draw range per view.
/// </summary>
public sealed partial class TerrainVegetationLayer : Node3D
{
    public TerrainSceneRenderer Renderer = null!;
    /// <summary>Bodies that part the grass (the Flui first, then its companion); null entries are skipped.</summary>
    public Func<(Vector3? Flui, bool FluiGrounded, Vector3? Companion, bool CompanionGrounded)>? Walkers;

    public ShaderMaterial PlantMaterial { get; private set; } = null!;
    public ShaderMaterial MeadowMaterial { get; private set; } = null!;
    /// <summary>Lily pads, reeds and water grass (<see cref="InstancedPlants"/>).</summary>
    public ShaderMaterial SmallPlantMaterial { get; private set; } = null!;
    /// <summary>Wildflowers and wall plants (<see cref="HerbMeshBuffers"/>).</summary>
    public ShaderMaterial HerbMaterial { get; private set; } = null!;
    private ArrayMesh _blade = null!;
    private ArrayMesh _bladeLod = null!;
    private ShaderMaterial[]? _materials;
    private readonly Dictionary<Node3D, TerrainVegetation.Prepared> _prepared = new();
    private readonly List<MultiMeshInstance3D> _grass = new();
    private readonly List<MeshInstance3D> _shells = new(), _nearHerbs = new(), _farHerbs = new();
    private readonly Vector4[] _steps = new Vector4[16];
    private readonly Vector4[] _stepForces = new Vector4[16];
    private int _step;
    private float _clock;
    private Vector3 _lastFluiStep = new(0, -10000, 0), _lastCompanionStep = new(0, -10000, 0);
    private bool _perspective;

    /// <summary>Flui perspective: meadow drawn out to this distance (godot-flui's high quality: 46 m, faded over 12 m).</summary>
    public const float PerspectiveGrassDistance = 52f;
    /// <summary>Flui perspective: wildflowers and wall plants shrink into their foot before this distance (m).</summary>
    public const float PerspectiveHerbDistance = 52f;
    /// <summary>Flui perspective: the bushes' leaf shells shrink away before this distance (m); the crowns stay.</summary>
    public const float PerspectiveShellDistance = 26f;

    // StringNames, not strings: a string argument allocates a StringName on every call, every frame.
    private static readonly StringName[] SharedUniforms =
    {
        "uMmoratHaze", "uMmoratHazeCfg", "uMmoratHazeBody", "uMmoratTerrainWind", "uMmoratTerrainWindDirection", "uMmoratTerrainTime",
        // STUDIO: the live colour grade (shaders/studio/studio_grade.gdshaderinc) follows the terrain's.
        "studio_grade_global", "studio_grade_floor", "studio_grade_cap", "studio_grade_wall", "studio_grade_foliage", "studio_grade_tone",
    };
    private static readonly StringName CompileOffset = "fluitown_compile_offset", CompileScale = "fluitown_compile_scale",
        Footsteps = "footsteps", Explorer = "explorer", Companion = "companion", BladeFade = "blade_fade",
        MeadowClumps = "meadow_clumps", FootstepBounds = "footstep_bounds";

    public override void _Ready()
    {
        PlantMaterial = new ShaderMaterial { Shader = ComicShaders.Load("res://shaders/vegetation/terrain_plant.gdshader") };
        // The central light() adds its foliage back-scatter for role 2: crowns glow a little against the sun.
        PlantMaterial.SetShaderParameter("comic_role", 2);
        MeadowMaterial = new ShaderMaterial { Shader = ComicShaders.Load("res://shaders/vegetation/terrain_meadow.gdshader") };
        MeadowMaterial.SetShaderParameter(MeadowClumps, _meadowClumps);
        SmallPlantMaterial = new ShaderMaterial { Shader = ComicShaders.Load("res://shaders/vegetation/terrain_small_plant.gdshader") };
        HerbMaterial = new ShaderMaterial { Shader = ComicShaders.Load("res://shaders/vegetation/terrain_herb.gdshader") };
        _blade = BladeMesh();
        _bladeLod = MeadowMesh.Create();
        for (int i = 0; i < _steps.Length; i++) _steps[i] = new(0, -10000, 0, -10000);
        SetPerspective(false);
    }

    public override void _ExitTree()
    {
        // Tile children release their MultiMeshes before the layer exits. These two prototypes belong to the layer.
        _blade?.Dispose();
        _bladeLod?.Dispose();
    }

    /// <summary>Five broad, curved ribbons with two segments: 15 triangles instead of 35.
    /// UV carries side and height so the shader can widen unresolved blades without changing their roots.</summary>
    public static ArrayMesh BladeMesh()
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        for (int blade = 0; blade < 5; blade++)
        {
            float a = blade * 2.399963f;
            var right = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var forward = new Vector3(-right.Z, 0, right.X);
            var root = right * (blade * .032f);
            float height = .32f + (blade % 3) * .08f;
            Vector3 P(float t, float side) => root + Vector3.Up * (t * height) + forward * (t * t * .18f) + right * (side * .045f * (1 - t));
            void V(float t, float side)
            {
                tool.SetNormal(forward);
                tool.SetUV(new Vector2(side * .5f + .5f, t));
                tool.SetColor(Colors.White);
                tool.AddVertex(P(t, side));
            }
            for (int ring = 0; ring < 2; ring++)
            {
                float lo = ring / 2f, hi = (ring + 1) / 2f;
                V(lo, -1); V(lo, 1); V(hi, -1);
                if (ring < 1) { V(lo, 1); V(hi, 1); V(hi, -1); }
            }
        }
        tool.Index();
        return tool.Commit();
    }

    /// <summary>Instantiates one tile's vegetation (main thread). The returned group is shown and freed with the tile.</summary>
    public Node3D? Create(TerrainVegetation.Prepared? prepared, bool visible)
    {
        if (prepared == null) return null;
        var group = new Node3D { Name = "TileVegetation", Visible = visible };
        if (prepared.Plants != null)
        {
            group.AddChild(new MeshInstance3D
            {
                Name = "Plants",
                Mesh = prepared.Plants,
                MaterialOverride = PlantMaterial,
                // Trees and bushes throw their own sun shadow (the original's caster proxies are not in the comic bake).
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            });
        }
        var chunks = new List<MultiMeshInstance3D>(prepared.Grass.Count);
        foreach (var chunk in prepared.Grass)
        {
            var multi = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,
                Mesh = _meadowClumps ? _bladeLod : _blade,
                InstanceCount = chunk.Count,
            };
            multi.CustomAabb = chunk.Bounds;
            if (chunk.Count > 0) multi.Buffer = chunk.Buffer;
            ApplyDensity(multi);
            var node = new MultiMeshInstance3D
            {
                Name = "Meadow",
                Position = chunk.Origin,
                Multimesh = multi,
                MaterialOverride = MeadowMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            ApplyRange(node);
            group.AddChild(node);
            chunks.Add(node);
        }
        _grass.AddRange(chunks);
        InstancedPlants.Create(group, prepared.Water, SmallPlantMaterial);
        var near = new List<MeshInstance3D>();
        var shellNodes = new List<MeshInstance3D>();
        MeshInstance3D? far = null;
        // Full flowers and wall plants (with their sun shadow) and leaf shells in the Flui perspective, per chunk within
        // their draw range; the flowers' stand-ins and the wall plants in the bird's-eye view, one mesh per tile.
        if (prepared.Herbs != null)
            foreach (var chunk in prepared.Herbs)
            {
                var node = new MeshInstance3D { Name = "Herbs", Mesh = chunk.Mesh, MaterialOverride = HerbMaterial, Position = chunk.Origin,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
                ApplyHerbRange(node, false);
                group.AddChild(node);
                near.Add(node);
            }
        if (prepared.HerbsFar != null)
        {
            far = new MeshInstance3D { Name = "HerbsFar", Mesh = prepared.HerbsFar, MaterialOverride = HerbMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = !_perspective };
            group.AddChild(far);
            _farHerbs.Add(far);
        }
        if (prepared.Shells != null)
            foreach (var chunk in prepared.Shells)
            {
                var node = new MeshInstance3D { Name = "LeafShell", Mesh = chunk.Mesh, MaterialOverride = HerbMaterial, Position = chunk.Origin,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                ApplyHerbRange(node, true);
                group.AddChild(node);
                shellNodes.Add(node);
            }
        _nearHerbs.AddRange(near);
        _shells.AddRange(shellNodes);
        AddChild(group);
        _prepared[group] = prepared;
        group.TreeExiting += () =>
        {
            _prepared.Remove(group);
            foreach (var node in chunks) _grass.Remove(node);
            foreach (var node in shellNodes) _shells.Remove(node);
            foreach (var node in near) _nearHerbs.Remove(node);
            if (far != null) _farHerbs.Remove(far);
            for (int i = 0; i < group.GetChildCount(); i++)
                if (group.GetChild(i) is MultiMeshInstance3D instance && instance.Multimesh is { } owned)
                {
                    instance.Multimesh = null;
                    owned.Dispose(); // the shared blade/pad/reed prototype stays owned by the vegetation layer
                }
            prepared.Dispose();
        };
        return group;
    }

    /// <summary>How many of a chunk's shuffled tufts are drawn (<see cref="TerrainVegetation.ShuffleTufts"/>): the
    /// quality profile's share, thinned by what the bird's-eye view still resolves (<see cref="ApplyMeadowScreenFade"/>).</summary>
    private void ApplyDensity(MultiMesh multi)
    {
        float density = _density * _meadowShare;
        multi.VisibleInstanceCount = density >= 1 ? -1 : Math.Max(multi.InstanceCount > 0 ? 1 : 0, (int)(multi.InstanceCount * density));
    }

    private void ApplyRange(MultiMeshInstance3D node)
    {
        // Bird's-eye view: the orthographic camera is far away, every chunk in the frame is drawn — until the blades
        // lie in the ground (<see cref="ApplyMeadowScreenFade"/>), where the chunk is nothing but cost. Flui
        // perspective: godot-flui's draw distance per chunk (the shader fades the blades into the ground before it).
        node.Visible = _perspective || _meadowShare > 0;
        node.VisibilityRangeEnd = _perspective ? PerspectiveGrassDistance * _reach + TerrainVegetation.GrassChunk : 0;
        node.VisibilityRangeEndMargin = _perspective ? 4 : 0;
    }

    /// <summary>A chunk of herbs or leaf shells: drawn only in the Flui perspective, within its draw range (measured to the
    /// chunk's centre, so a chunk's half diagonal is added).</summary>
    private void ApplyHerbRange(MeshInstance3D node, bool shell)
    {
        node.Visible = _perspective && (!shell || _leafShells);
        float range = shell ? PerspectiveShellDistance : PerspectiveHerbDistance * _reach;
        node.VisibilityRangeEnd = range + (shell ? TerrainVegetation.ShellChunk : TerrainVegetation.HerbChunk) * .71f;
        node.VisibilityRangeEndMargin = 4;
    }

    private float _reach = 1;
    private float _density = 1;
    private bool _leafShells = true;
    private float _meadowFade = 1, _meadowShare = 1;
    private bool _meadowClumps = true;

    public void SetClumps(bool enabled)
    {
        if (enabled == _meadowClumps) return;
        _meadowClumps = enabled;
        MeadowMaterial?.SetShaderParameter(MeadowClumps, enabled);
        if (_bladeLod != null)
            foreach (var node in _grass)
                if (node.Multimesh is { } multi) multi.Mesh = enabled ? _bladeLod : _blade;
        if (Renderer != null && MeadowMaterial != null) ApplyMeadowScreenFade();
    }

    /// <summary>godot-flui's tuft height (m); the instances scale their blades by it (<see cref="TerrainVegetation"/>).</summary>
    private const float TuftHeight = .42f;
    /// <summary>Bird's-eye view (<see cref="ApplyMeadowScreenFade"/>): the pixels of the buffer the world is rendered
    /// into that one tuft's height covers at which the whole meadow is drawn, below which the last blades sink into the
    /// ground, and the band they sink over.</summary>
    // The shader consolidates thin blades into broad ribbons before they become subpixel.
    // Only genuinely unresolved whole tufts give way to the ground's filtered meadow material.
    private const float MeadowFullPixels = 14f, MeadowGonePixels = .7f, MeadowSinkBand = 1.5f;

    /// <summary>Whether the bushes' leaf shells are drawn in the Flui perspective (<see cref="TerrainVisualQuality.LeafShells"/>).</summary>
    public void SetLeafShells(bool on)
    {
        if (on == _leafShells) return;
        _leafShells = on;
        SetPerspective(_perspective);
    }
    /// <summary>The share of each chunk's tufts that is drawn (quality profile,
    /// <see cref="Fluitown.GodotApp.Rendering.TerrainVisualQuality.MeadowDensity"/>). The tufts are shuffled per chunk,
    /// so a smaller share thins the meadow evenly; the blades that stay keep their place, size and colour.</summary>
    public void SetDensity(float density)
    {
        density = Math.Clamp(density, .1f, 1);
        if (density == _density) return;
        _density = density;
        foreach (var node in _grass) if (node.Multimesh is { } multi) ApplyDensity(multi);
    }

    /// <summary>Flui perspective: the meadow's draw distance as a share of <see cref="PerspectiveGrassDistance"/>
    /// (quality profile, <see cref="TerrainVisualQuality.MeadowReach"/>).</summary>
    public void SetReach(float reach)
    {
        reach = Math.Clamp(reach, .2f, 1);
        if (reach == _reach) return;
        _reach = reach;
        SetPerspective(_perspective);
    }

    /// <summary>
    /// Bird's-eye view: how much of the meadow is drawn, measured in the pixels one tuft covers in the buffer the world
    /// is rendered into (the render scale is part of the measure, because that is the buffer that is sampled). Zoomed
    /// out, a 0.42 m tuft falls below a pixel; the upscaler — FSR under a render scale below 1 — then reads the
    /// sub-pixel carpet as structure and smears it into a coarse ripple over the whole ground, worse the further out
    /// the camera stands. Blades consolidate into wider, calmer ribbons in the shader, while the CPU thins the meadow:
    /// a tuft's footprint grows with the square of its
    /// screen size, so drawing that share of the (shuffled) tufts keeps the tufts per screen pixel where they are at
    /// <see cref="MeadowFullPixels"/> — the ground keeps its grass at every zoom, and never a carpet below the pixels
    /// that carry it. The last blades sink into the ground over <see cref="MeadowSinkBand"/> through the same
    /// <c>reach</c> as the shader's distance fade, and the chunks stop being drawn once they are down. Both views
    /// additionally filter each tuft's projected size in the shader; this CPU rule saves submissions in the
    /// orthographic view, where every chunk has the same projected scale.
    /// </summary>
    private void ApplyMeadowScreenFade()
    {
        float fade = 1, share = 1;
        var camera = Renderer.Camera;
        if (!_perspective && camera != null && camera.Size > .001f)
        {
            float pixels = Renderer.SceneViewport.Size.Y * Math.Min(Renderer.RenderScale, 1f) * TuftHeight / camera.Size;
            float seen = Math.Clamp(pixels / (_meadowClumps ? MeadowFullPixels : 18f), 0, 1);
            // In sixteenths: the share may not follow every pixel of a zoom, or every chunk is rewritten every frame.
            share = MathF.Ceiling(seen * seen * 16) / 16;
            float standing = Math.Clamp((pixels - (_meadowClumps ? MeadowGonePixels : 5.5f)) / (_meadowClumps ? MeadowSinkBand : 5f), 0, 1);
            fade = standing * standing * (3 - 2 * standing);
            if (fade <= 0) share = 0;
        }
        bool sank = MathF.Abs(fade - _meadowFade) > .002f, thinned = share != _meadowShare;
        if (!sank && !thinned) return;
        bool wasDrawn = _meadowShare > 0;
        _meadowFade = fade;
        _meadowShare = share;
        if (sank) MeadowMaterial.SetShaderParameter(BladeFade, fade);
        // The blades sink through the material alone; only a new share rewrites the chunks.
        if (!thinned) return;
        _grass.RemoveAll(node => !IsInstanceValid(node) || node.IsQueuedForDeletion());
        foreach (var node in _grass)
        {
            if (wasDrawn != share > 0) ApplyRange(node);
            if (share > 0 && node.Multimesh is { } multi) ApplyDensity(multi);
        }
    }

    public void SetPerspective(bool fluiPerspective)
    {
        _perspective = fluiPerspective;
        MeadowMaterial?.SetShaderParameter("draw_distance", fluiPerspective ? PerspectiveGrassDistance * _reach : 1.0e7f);
        // Camera changes affect visibility and detail, never the shared vegetation ink.
        HerbMaterial?.SetShaderParameter("draw_distance", fluiPerspective ? PerspectiveHerbDistance * _reach : 1.0e7f);
        HerbMaterial?.SetShaderParameter("shell_distance", fluiPerspective ? PerspectiveShellDistance : 1.0e7f);
        _grass.RemoveAll(node => !IsInstanceValid(node) || node.IsQueuedForDeletion());
        foreach (var node in _grass) ApplyRange(node);
        _shells.RemoveAll(node => !IsInstanceValid(node) || node.IsQueuedForDeletion());
        foreach (var node in _shells) ApplyHerbRange(node, true);
        _nearHerbs.RemoveAll(node => !IsInstanceValid(node) || node.IsQueuedForDeletion());
        _farHerbs.RemoveAll(node => !IsInstanceValid(node) || node.IsQueuedForDeletion());
        foreach (var node in _nearHerbs) ApplyHerbRange(node, false);
        foreach (var node in _farHerbs) node.Visible = !fluiPerspective;
        // The blades stand up again in the Flui perspective without waiting for the next frame's measure.
        if (Renderer != null) ApplyMeadowScreenFade();
    }

    public override void _Process(double delta)
    {
        if (Renderer == null || PlantMaterial == null) return;
        _clock += (float)delta;
        var surface = Renderer.Surface;
        var toWorld = Renderer.CompileToWorld;
        float scale = toWorld.Basis.X.Length();
        var offset = toWorld.Origin;
        foreach (var material in _materials ??= new[] { PlantMaterial, MeadowMaterial, SmallPlantMaterial, HerbMaterial })
        {
            foreach (var name in SharedUniforms) material.SetShaderParameter(name, surface.GetShaderParameter(name));
            material.SetShaderParameter(CompileOffset, offset);
            material.SetShaderParameter(CompileScale, scale > 0 ? 1f / scale : 25f);
        }
        ApplyMeadowScreenFade();

        // godot-flui's footprints (MeadowGrass.Touch / _Process): a ring of 16 steps whose force decays with exp(−0.55 t).
        var walkers = Walkers?.Invoke() ?? default;
        void Touch(Vector3? point, bool grounded, ref Vector3 last)
        {
            if (point is not { } p || !grounded) return;
            if (p.DistanceSquaredTo(last) < .20f) return;
            last = p;
            _steps[_step] = new Vector4(p.X, p.Y, p.Z, _clock);
            _step = (_step + 1) % _steps.Length;
        }
        Touch(walkers.Flui, walkers.FluiGrounded, ref _lastFluiStep);
        Touch(walkers.Companion, walkers.CompanionGrounded, ref _lastCompanionStep);
        var stepBounds = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        for (int i = 0; i < _steps.Length; i++)
        {
            var step = _steps[i];
            step.W = step.W < -1000 ? 0 : MathF.Exp(-MathF.Max(0, _clock - step.W) * .55f);
            _stepForces[i] = step;
            if (step.W > 0)
            {
                stepBounds.X = MathF.Min(stepBounds.X, step.X - 1.5f);
                stepBounds.Y = MathF.Min(stepBounds.Y, step.Z - 1.5f);
                stepBounds.Z = MathF.Max(stepBounds.Z, step.X + 1.5f);
                stepBounds.W = MathF.Max(stepBounds.W, step.Z + 1.5f);
            }
        }
        MeadowMaterial.SetShaderParameter(Footsteps, _stepForces);
        MeadowMaterial.SetShaderParameter(FootstepBounds, stepBounds);
        MeadowMaterial.SetShaderParameter(Explorer, walkers.Flui ?? new Vector3(0, -10000, 0));
        MeadowMaterial.SetShaderParameter(Companion, walkers.Companion ?? new Vector3(0, -10000, 0));
        SmallPlantMaterial.SetShaderParameter(Explorer, walkers.Flui ?? new Vector3(0, -10000, 0));
        SmallPlantMaterial.SetShaderParameter(Companion, walkers.Companion ?? new Vector3(0, -10000, 0));
        HerbMaterial.SetShaderParameter(Explorer, walkers.Flui ?? new Vector3(0, -10000, 0));
        HerbMaterial.SetShaderParameter(Companion, walkers.Companion ?? new Vector3(0, -10000, 0));
    }
}
