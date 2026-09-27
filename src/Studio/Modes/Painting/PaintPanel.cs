using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Fluitown.Domain;
using Godot;
using TerrainStudio.Core;
using TerrainStudio.Ui;
using static TerrainStudio.Ui.Tr;
using Section = TerrainStudio.Ui.Section;

namespace TerrainStudio.Painting;

/// <summary>
/// The painter's controls: the tool rail (six tools) and one page per tool with only what that tool needs — the
/// choice of what to paint first, then its height, then the brush. Rarely needed options sit under "More".
/// Also owns the map-check popover that opens from the check chip in the top bar.
/// </summary>
public sealed partial class PaintPanel : VBoxContainer
{
    public readonly IconRail Rail = new();
    public readonly PopupPanel Checks = new();

    private readonly PaintController _paint;
    private readonly PaintState _state;
    private readonly Label _title = W.Title("");
    private readonly Label _hint = W.Hint("");
    private readonly Dictionary<string, Control> _pages = new();
    private readonly List<(SliderRow size, Segmented shape)> _brushes = new();

    // Terrain.
    private readonly Dictionary<int, SwatchCard> _materials = new();
    private readonly Segmented _terrainMode = new();
    private Stepper _height = null!;
    private Label _heightHint = null!;
    private CheckButton _soft = null!;
    private SliderRow _strength = null!;

    // Sculpt.
    private readonly Dictionary<string, IconCard> _sculpt = new();
    private SliderRow _amount = null!;
    private readonly Segmented _tread = new();
    private Control _amountBox = null!, _treadBox = null!;
    private Label _sculptHint = null!;

    // Nature, build, theme.
    private readonly Dictionary<string, IconCard> _nature = new();
    private readonly Dictionary<string, IconCard> _build = new();
    private readonly Segmented _axis = new();
    private SliderRow _span = null!;
    private Control _bridgeBox = null!, _axisBox = null!;
    private Label _buildHint = null!;
    private readonly Dictionary<string, SwatchCard> _themes = new();

    // Select.
    private Label _selection = null!;

    // Checks.
    private Label _checksSummary = null!;
    private VBoxContainer _issues = null!;
    private readonly Dictionary<OverlayKind, Chip> _overlays = new();

    public PaintPanel(PaintController paint, PaintState state)
    {
        _paint = paint;
        _state = state;
        AddThemeConstantOverride("separation", 14);
        AddChild(W.Column(4));
        GetChild(0).AddChild(_title);
        GetChild(0).AddChild(_hint);

        Rail.Add(PaintRail.Terrain, IconKind.Brush, T("Terrain — paint ground, rock, water and chasms (B)"));
        Rail.Add(PaintRail.Sculpt, IconKind.Sculpt, T("Sculpt — raise, lower, flatten and smooth the land (R)"));
        Rail.Add(PaintRail.Nature, IconKind.Tree, T("Nature — trees, bushes and stumps (T)"));
        Rail.Add(PaintRail.Build, IconKind.Bridge, T("Build — rope bridges and cliff passes (H)"));
        Rail.Add(PaintRail.Theme, IconKind.Palette, T("Theme — give areas another world theme (P)"));
        Rail.AddSeparator();
        Rail.Add(PaintRail.Select, IconKind.Wand, T("Select — pick, move and copy areas (M)"));
        Rail.Selected += key => _paint.SetRail(key);

        AddPage(PaintRail.Terrain, BuildTerrain());
        AddPage(PaintRail.Sculpt, BuildSculpt());
        AddPage(PaintRail.Nature, BuildNature());
        AddPage(PaintRail.Build, BuildBuild());
        AddPage(PaintRail.Theme, BuildTheme());
        AddPage(PaintRail.Select, BuildSelect());
        BuildChecks();
        SyncFromState();
    }

    private void AddPage(string key, Control page)
    {
        _pages[key] = page;
        page.Visible = false;
        AddChild(page);
    }

    private static VBoxContainer Page() => W.Column(14);

    /// <summary>Brush size and shape; every page has its own copy, all kept in sync.</summary>
    private Control Brush(bool shape = true)
    {
        var size = new SliderRow(T("Brush size"), 1, 64, 1, _state.BrushSize, 5, v => F("{0} cells", v), T("Brush diameter in cells. Keys [ and ]"));
        size.Changed += v => _paint.SetBrush(size: (int)v);
        var shapes = new Segmented();
        shapes.Add(TerrainBrushShape.Circle, T("Round"));
        shapes.Add(TerrainBrushShape.Square, T("Square"));
        shapes.Add(TerrainBrushShape.Diamond, T("Diamond"));
        shapes.Selected += key => _paint.SetBrush(shape: key);
        _brushes.Add((size, shapes));
        var group = W.Group(T("Brush"), size);
        if (shape) group.AddChild(shapes);
        return group;
    }

    // ── pages ───────────────────────────────────────────────────────────────────────────────────────────────

    private Control BuildTerrain()
    {
        var page = Page();
        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", 6);
        void Material(int tile, string caption, string tooltip)
        {
            int colour = tile switch
            {
                TileType.Solid => 0xa8a196, TileType.Water => 0x4aa9dc, TileType.Chasm => 0x3a3147, TileType.Bridge => 0xb58350, _ => 0x8dbb55,
            };
            var card = new SwatchCard(caption, new[] { W.Rgb(colour) }, tooltip, block: true, height: 70);
            card.CustomMinimumSize = new Vector2(44, 70);
            card.Pressed += () => _paint.ChooseMaterial(tile);
            _materials[tile] = card;
            grid.AddChild(card);
        }
        Material(TileType.Floor, T("Ground"), T("Walkable ground: meadows, paths and terraces"));
        Material(TileType.Solid, T("Rock"), T("Solid rock that rises as cliffs and mountains"));
        Material(TileType.Water, T("Water"), T("Lakes and rivers; water between levels falls as waterfalls"));
        Material(TileType.Chasm, T("Chasm"), T("A deep open ravine"));
        Material(TileType.Bridge, T("Bridge"), T("A plank deck across water or a chasm"));
        page.AddChild(W.Group(T("Material"), grid));

        _height = new Stepper(_state.Height, -30, 25, h => h.ToString("+0;−0;0", CultureInfo.InvariantCulture), T("Height in levels (0.6 m each). Shift + wheel changes it"));
        _height.Changed += h => _paint.SetHeight(h);
        var pick = W.IconButton(IconKind.Picker, T("Pick material and height from the map (I, or Ctrl+click)"), () => _paint.SetTool(PaintTool.Picker), 34, 16);
        _heightHint = W.Hint("");
        page.AddChild(W.Group(T("Height"), W.Row(_height, pick), _heightHint));

        page.AddChild(Brush());
        _terrainMode.Add(PaintTool.Paint, T("Brush"), IconKind.Brush, T("Paint where you drag"));
        _terrainMode.Add(PaintTool.Fill, T("Fill"), IconKind.Bucket, T("Fill a connected area of the same material with one click (F)"));
        _terrainMode.Selected += key => _paint.SetTool(key);
        page.AddChild(W.Group(T("Mode"), _terrainMode));

        var more = new Section(T("More"), false);
        _soft = W.Toggle(T("Soft edges"), _state.BrushSoft, on => _paint.SetBrush(soft: on), T("Heights fade out towards the edge of the brush"));
        _strength = new SliderRow(T("Strength"), 0.1, 1, 0.01, _state.BrushStrength, 1, v => $"{v * 100:0}%", T("How strongly soft edges blend"));
        _strength.Changed += v => _paint.SetBrush(strength: v);
        more.Add(_soft);
        more.Add(_strength);
        page.AddChild(more);
        return page;
    }

    private Control BuildSculpt()
    {
        var page = Page();
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 6);
        void Card(string tool, string caption, IconKind icon, string tooltip)
        {
            var card = new IconCard(caption, icon, tooltip);
            card.Pressed += () => _paint.SetTool(tool);
            _sculpt[tool] = card;
            grid.AddChild(card);
        }
        Card(PaintTool.Raise, T("Raise"), IconKind.Raise, T("Lift the land under the brush"));
        Card(PaintTool.Lower, T("Lower"), IconKind.Lower, T("Sink the land under the brush"));
        Card(PaintTool.Flatten, T("Flatten"), IconKind.Flatten, T("Level the land to the height where the stroke starts"));
        Card(PaintTool.Smooth, T("Smooth"), IconKind.Smooth, T("Settle rough ground into clean terraces"));
        page.AddChild(grid);
        _sculptHint = W.Hint("");
        page.AddChild(_sculptHint);

        _amount = new SliderRow(T("Amount"), 1, 5, 1, _state.SculptAmount, 1, v => v == 1 ? T("1 level") : F("{0} levels", v), T("How many levels one stroke raises or lowers"));
        _amount.Changed += v => _paint.SetSculptAmount((int)v);
        _amountBox = _amount;
        page.AddChild(_amount);
        _tread.Add("2", T("Narrow"), null, T("Terraces at least 2 cells wide"));
        _tread.Add("3", T("Medium"), null, T("Terraces at least 3 cells wide"));
        _tread.Add("4", T("Wide"), null, T("Terraces at least 4 cells wide"));
        _tread.Selected += key => _paint.SetQuantum(int.Parse(key, CultureInfo.InvariantCulture));
        _treadBox = W.Group(T("Terrace width"), _tread);
        page.AddChild(_treadBox);
        page.AddChild(Brush());
        return page;
    }

    private Control BuildNature()
    {
        var page = Page();
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 6);
        void Card(string key, string caption, IconKind icon, string tooltip)
        {
            var card = new IconCard(caption, icon, tooltip);
            card.Pressed += () => _paint.ChooseNature(key);
            _nature[key] = card;
            grid.AddChild(card);
        }
        Card(TerrainDecorationKind.Tree, T("Trees"), IconKind.Tree, T("Plant trees on land"));
        Card(TerrainDecorationKind.Thicket, T("Bushes"), IconKind.Bush, T("Plant bushes and thickets"));
        Card(TerrainDecorationKind.Stump, T("Stumps"), IconKind.Stump, T("Place old tree stumps"));
        Card("erase", T("Remove"), IconKind.Eraser, T("Remove trees, bushes and stumps"));
        page.AddChild(grid);
        page.AddChild(W.Hint(T("Plants grow in the theme of the ground below them; water and chasms stay free.")));
        page.AddChild(Brush());
        return page;
    }

    private Control BuildBuild()
    {
        var page = Page();
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 6);
        void Card(string key, string caption, IconKind icon, string tooltip)
        {
            var card = new IconCard(caption, icon, tooltip);
            card.Pressed += () => _paint.ChooseBuild(key);
            _build[key] = card;
            grid.AddChild(card);
        }
        Card(TerrainDepthStructureKind.Underpass, T("Rope bridge"), IconKind.Bridge, T("A hanging bridge between two high banks"));
        Card(TerrainDepthStructureKind.Cleft, T("Cliff pass"), IconKind.Cleft, T("A narrow passage cut through a thin rock wall"));
        page.AddChild(grid);
        _buildHint = W.Hint("");
        page.AddChild(_buildHint);

        _span = new SliderRow(T("Bridge length"), TerrainModel.UNDERPASS_MIN_SPAN, TerrainModel.UNDERPASS_MAX_SPAN, 1, _state.StructureSpan,
            TerrainDepthStructures.UNDERPASS_DEFAULT_SPAN, v => F("{0} cells", v), T("Length of the bridge deck"));
        _span.Changed += v => _paint.SetStructure(span: (int)v);
        _bridgeBox = _span;
        page.AddChild(_span);
        _axis.Add("auto", T("Auto"), null, T("Read the direction from the terrain"));
        _axis.Add(TerrainPassageAxis.Horizontal, "↔", null, T("East – west"));
        _axis.Add(TerrainPassageAxis.Vertical, "↕", null, T("North – south"));
        _axis.Selected += key => _paint.SetStructure(axis: key);
        _axisBox = W.Group(T("Direction"), _axis);
        page.AddChild(_axisBox);
        return page;
    }

    private Control BuildTheme()
    {
        var page = Page();
        var grid = new GridContainer { Columns = 2 };
        foreach (var entry in ThemeCatalog.All)
        {
            var e = entry;
            var card = new SwatchCard(e.Name, e.Colours.Select(c => W.Rgb(c)).ToArray(), e.Name, height: 54);
            card.Pressed += () => _paint.SetTheme(e.Key);
            _themes[e.Key] = card;
            grid.AddChild(card);
        }
        page.AddChild(W.Group(T("Themes"), grid));
        page.AddChild(Brush());
        var whole = W.Button(T("Apply to the whole map"), IconKind.Map, () => _paint.ApplyThemeToMap(), T("Give every cell of the map this theme"));
        W.Fill(whole);
        page.AddChild(whole);
        return page;
    }

    private Control BuildSelect()
    {
        var page = Page();
        _selection = W.Label(T("Nothing selected"), 13, StudioTheme.Text);
        page.AddChild(_selection);
        Button Action(string text, IconKind icon, string tooltip, Action action)
        {
            var b = W.Fill(W.Button(text, icon, action, tooltip));
            b.Alignment = HorizontalAlignment.Left;
            return b;
        }
        page.AddChild(W.Row(Action(T("Copy"), IconKind.Copy, T("Copy (Ctrl+C)"), _paint.Copy), Action(T("Cut"), IconKind.Cut, T("Cut (Ctrl+X)"), _paint.Cut)));
        page.AddChild(W.Row(Action(T("Paste"), IconKind.Paste, T("Paste at the pointer (Ctrl+V)"), _paint.Paste), Action(T("Clear"), IconKind.Trash, T("Turn the selection into rock (Delete)"), _paint.ClearSelection)));
        page.AddChild(W.Row(Action(T("Select all"), IconKind.Grid, T("Select the whole map (Ctrl+A)"), _paint.SelectAll), Action(T("Deselect"), IconKind.Close, T("Deselect (Esc)"), _paint.Deselect)));
        page.AddChild(W.Hint(T("Arrow keys move the selection by one cell (Shift: five).")));
        return page;
    }

    // ── checks popover ──────────────────────────────────────────────────────────────────────────────────────

    private void BuildChecks()
    {
        Checks.AddThemeStyleboxOverride("panel", StudioTheme.Glass(14, 16, solid: true));
        var column = W.Column(12);
        column.CustomMinimumSize = new Vector2(340, 0);
        Checks.AddChild(column);
        column.AddChild(W.Title(T("Map check")));
        _checksSummary = W.Label("", 13, StudioTheme.TextMuted, wrap: true);
        _checksSummary.CustomMinimumSize = new Vector2(328, 0);
        column.AddChild(_checksSummary);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 0) };
        _issues = W.Column(2);
        _issues.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_issues);
        column.AddChild(scroll);
        var chips = new HFlowContainer();
        foreach (var (kind, label, tooltip) in new[]
        {
            (OverlayKind.Problems, "Problems", "Mark the cells with problems"),
            (OverlayKind.Height, "Heights", "Colour the land by height"),
            (OverlayKind.Walk, "Walkable", "Show where one can walk"),
            (OverlayKind.Collision, "Blocking", "Show what blocks movement"),
            (OverlayKind.Bridges, "Bridges", "Show bridges and their landings"),
            (OverlayKind.Connect, "Connections", "Show which areas are connected"),
        })
        {
            var k = kind;
            var chip = new Chip(T(label), T(tooltip));
            chip.Toggled += on => _paint.SetOverlay(k, on);
            _overlays[kind] = chip;
            chips.AddChild(chip);
        }
        column.AddChild(W.Group(T("Show on the map"), chips));
    }

    /// <summary>Opens the check popover below the check chip.</summary>
    public void ShowChecks(Control anchor)
    {
        if (Checks.GetParent() == null) anchor.GetTree().Root.AddChild(Checks);
        Checks.Theme = StudioTheme.Build();
        var rect = anchor.GetGlobalRect();
        Checks.Popup(new Rect2I((int)rect.Position.X, (int)(rect.End.Y + 10), 360, 0));
    }

    public void SetValidation(TerrainValidationResult? validation, bool compiled, int width)
    {
        foreach (var child in _issues.GetChildren()) child.QueueFree();
        if (validation == null) { _checksSummary.Text = T("Checking the map …"); return; }
        int errors = validation.issues.Count(i => i.severity == "error"), warnings = validation.issues.Count - errors;
        string problems = errors == 1 ? T("1 problem") : F("{0} problems", errors);
        string hints = warnings == 1 ? T("1 hint") : F("{0} hints", warnings);
        _checksSummary.Text = errors == 0
            ? warnings == 0 ? T("Everything is fine: the map can be walked, bridges and chasms are valid.") : F("No problems · {0}. Hints do not stop the map from working.", hints)
            : F("{0} · {1}. Click one to jump there.", problems, hints) + (compiled ? "" : " " + T("The map is shown as a draft until the problems are fixed."));
        _checksSummary.AddThemeColorOverride("font_color", errors > 0 ? new Color("ff9aa4") : warnings > 0 ? new Color("ffe29a") : StudioTheme.Ok);
        // One row per kind of issue; clicking it again jumps to the next place with that issue.
        var groups = validation.issues.GroupBy(i => (i.severity, i.message)).OrderBy(g => g.Key.severity == "error" ? 0 : 1).Take(24).ToList();
        var scroll = (ScrollContainer)_issues.GetParent();
        scroll.CustomMinimumSize = new Vector2(0, Math.Min(groups.Count, 7) * 30);
        foreach (var group in groups)
        {
            var places = group.Select(i => OverlayPalette.issueCoord(i, width)).Where(c => c != null).Select(c => c!.Value).ToList();
            int next = 0;
            int count = group.Count();
            string text = count > 1 ? count + "×  " + group.Key.message : group.Key.message;
            var button = W.Button(text, group.Key.severity == "error" ? IconKind.Warning : IconKind.Info, () =>
            {
                if (places.Count == 0) return;
                var c = places[next++ % places.Count];
                _paint.FocusCell(c.tx, c.ty);
            }, group.Key.message + "\n" + (places.Count > 1 ? T("Click again to jump to the next place") : group.First().code), filled: false);
            button.Alignment = HorizontalAlignment.Left;
            button.ClipText = true;
            button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.AddThemeFontSizeOverride("font_size", 12);
            button.AddThemeColorOverride("font_color", group.Key.severity == "error" ? new Color("ff9aa4") : new Color("ffe29a"));
            _issues.AddChild(button);
        }
    }

    // ── sync ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Re-reads every control from the painter's state.</summary>
    public void SyncFromState()
    {
        string rail = _state.Rail;
        Rail.Set(rail);
        foreach (var (key, page) in _pages) page.Visible = key == rail;
        string tool = _state.Tool;
        (_title.Text, _hint.Text) = rail switch
        {
            PaintRail.Sculpt => (T("Sculpt"), T("Shape the height of the land without changing what it is made of.")),
            PaintRail.Nature => (T("Nature"), T("Plant trees, bushes and stumps.")),
            PaintRail.Build => (T("Build"), T("Place rope bridges and cliff passes as complete structures.")),
            PaintRail.Theme => (T("Theme"), T("Every area can have its own world: its colours, rock and plants follow it.")),
            PaintRail.Select => (T("Select"), T("Click an area to select everything connected to it. Shift adds, Alt removes, drag moves it.")),
            _ => (T("Terrain"), T("Paint the land. Ctrl+click takes material and height from the map.")),
        };

        foreach (var (tile, card) in _materials) card.SetPressedNoSignal(tile == _state.Tile);
        var range = TerrainEditor.terrainEditorHeightRangeForTile(_state.Tile);
        _height.Set(_state.Height, notify: false);
        _heightHint.Text = _state.Tile switch
        {
            TileType.Chasm => F("Depth {0} (deepest) to {1}.", range.min, range.max),
            TileType.Water => T("Water on different levels falls as waterfalls."),
            TileType.Solid => T("Rock rises this many levels as a cliff."),
            _ => T("Neighbouring ground may differ by one level; higher steps become cliffs."),
        };
        _terrainMode.Set(tool == PaintTool.Fill ? PaintTool.Fill : PaintTool.Paint);
        _soft.SetPressedNoSignal(_state.BrushSoft);
        _strength.SetSilently(_state.BrushStrength);

        foreach (var (key, card) in _sculpt) card.SetPressedNoSignal(key == tool);
        _amountBox.Visible = tool is PaintTool.Raise or PaintTool.Lower;
        _treadBox.Visible = tool == PaintTool.Smooth;
        _amount.SetSilently(_state.SculptAmount);
        _tread.Set(_state.SmoothQuantum.ToString(CultureInfo.InvariantCulture));
        _sculptHint.Text = tool switch
        {
            PaintTool.Raise => T("Each stroke lifts the land once, however often you pass over it."),
            PaintTool.Lower => T("Each stroke sinks the land once, however often you pass over it."),
            PaintTool.Flatten => T("Start the stroke on the height you want and drag over the land."),
            _ => T("Drag over walkable ground to settle it into terraces."),
        };

        foreach (var (key, card) in _nature) card.SetPressedNoSignal(tool == PaintTool.DecorationErase ? key == "erase" : tool == PaintTool.Decorate && key == _state.DecorationKind);

        string build = _state.StructureKind;
        foreach (var (key, card) in _build) card.SetPressedNoSignal(key == build);
        _bridgeBox.Visible = build == TerrainDepthStructureKind.Underpass;
        _span.SetSilently(_state.StructureSpan);
        _axis.Set(_state.StructureAxis);
        _buildHint.Text = build switch
        {
            TerrainDepthStructureKind.Underpass => T("Click in a gap between two high banks. Green shows where it fits, red where it does not."),
            _ => T("Click a thin rock wall to cut a passage through it."),
        };

        foreach (var (key, card) in _themes) card.SetPressedNoSignal(key == _state.ThemeKey);
        foreach (var (size, shape) in _brushes)
        {
            size.SetSilently(_state.BrushSize);
            shape.Set(_state.BrushShape);
        }
        foreach (var (kind, chip) in _overlays) chip.SetPressedNoSignal((_state.Overlays & kind) != 0);
    }

    public void SetSelectionInfo(string text) => _selection.Text = text;
}
