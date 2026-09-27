using System;
using Fluitown.Domain;
using TerrainStudio.Core;

namespace TerrainStudio.Painting;

/// <summary>The painter's gestures. The tool rail groups them into six tools (see <see cref="PaintRail"/>).</summary>
public static class PaintTool
{
    public const string Picker = "picker", Paint = "paint", Theme = "theme", Select = "select", Fill = "fill", Erase = "erase",
        Smooth = "smooth", Raise = "raise", Lower = "lower", Flatten = "flatten", Structure = "structure", Decorate = "decorate",
        DecorationErase = "decoration-erase";

    /// <summary>Gestures whose drag continues the stroke; the others act on a single click.</summary>
    public static bool Drags(string tool) =>
        tool is Paint or Theme or Erase or Smooth or Raise or Lower or Flatten or Decorate or DecorationErase;
}

/// <summary>The six tools of the rail.</summary>
public static class PaintRail
{
    public const string Terrain = "terrain", Sculpt = "sculpt", Nature = "nature", Build = "build", Theme = "theme", Select = "select";

    public static string Of(string tool) => tool switch
    {
        PaintTool.Raise or PaintTool.Lower or PaintTool.Flatten or PaintTool.Smooth => Sculpt,
        PaintTool.Decorate or PaintTool.DecorationErase => Nature,
        PaintTool.Structure => Build,
        PaintTool.Theme => Theme,
        PaintTool.Select => Select,
        _ => Terrain,
    };
}

/// <summary>What the painter paints with: tool, material, height, theme, dressing, brush and structure settings.</summary>
public sealed class PaintState
{
    public string Tool = PaintTool.Paint;
    /// <summary>The material (TileType) of the terrain brush and fill: floor, wall, water, bridge or chasm.</summary>
    public int Tile = TileType.Floor;
    /// <summary>Semantic height for ordinary blocks (−25..25) and for chasms (−30..−5).</summary>
    public int GroundHeight;
    public int ChasmHeight = -TerrainModel.CHASM_DEFAULT_DEPTH;
    public string ThemeKey = GeneratorArtifacts.GENERATOR_DEFAULT_THEME;
    public string DecorationKind = TerrainDecorationKind.Tree;
    public int BrushSize = 5;
    public string BrushShape = TerrainBrushShape.Circle;
    public bool BrushSoft;
    public double BrushStrength = 1;
    /// <summary>Levels a Raise or Lower stroke moves the ground.</summary>
    public int SculptAmount = 1;
    public int SmoothQuantum = TerrainTerrace.TERRAIN_TERRACE_QUANTUM;
    /// <summary><see cref="TerrainDepthStructureKind.Cleft"/> or <see cref="TerrainDepthStructureKind.Underpass"/>.</summary>
    public string StructureKind = TerrainDepthStructureKind.Underpass;
    public int StructureSpan = TerrainDepthStructures.UNDERPASS_DEFAULT_SPAN;
    /// <summary>"auto", <see cref="TerrainPassageAxis.Horizontal"/> or <see cref="TerrainPassageAxis.Vertical"/>.</summary>
    public string StructureAxis = "auto";
    public OverlayKind Overlays = OverlayKind.None;

    /// <summary>The last gesture used with each tool of the rail (the rail returns to it).</summary>
    public string TerrainTool = PaintTool.Paint, SculptTool = PaintTool.Raise, NatureTool = PaintTool.Decorate, BuildTool = PaintTool.Structure;

    public string Rail => PaintRail.Of(Tool);

    /// <summary>The height the current material paints with.</summary>
    public int Height
    {
        get => Tile == TileType.Chasm ? ChasmHeight : GroundHeight;
        set
        {
            var range = TerrainEditor.terrainEditorHeightRangeForTile(Tile);
            int clamped = Math.Clamp(value, range.min, range.max);
            if (Tile == TileType.Chasm) ChasmHeight = clamped; else GroundHeight = clamped;
        }
    }

    public TerrainBrush Brush => new() { size = BrushSize, shape = BrushShape, soft = BrushSoft, strength = BrushStrength };

    /// <summary>Remembers the gesture as the rail tool's current one.</summary>
    public void Remember()
    {
        switch (Rail)
        {
            case PaintRail.Terrain when Tool is PaintTool.Paint or PaintTool.Fill: TerrainTool = Tool; break;
            case PaintRail.Sculpt: SculptTool = Tool; break;
            case PaintRail.Nature: NatureTool = Tool; break;
            case PaintRail.Build: BuildTool = Tool; break;
        }
    }
}
