using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Fluitown.Domain;
using Fluitown.Render;
using Godot;
using TerrainStudio.Core;
using TerrainStudio.Terrain;
using TerrainStudio.Ui;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio.Painting;

/// <summary>
/// The Paint mode. The document is a <see cref="TerrainArtifact"/> edited with the ported editor functions
/// (<see cref="TerrainEditor"/>). Every gesture is shown at once twice over: the edited cells are copied into the live
/// layout (the renderer re-bakes exactly the touched tiles) and drawn as a translucent preview until their new tiles are
/// on screen. When the gesture ends the artifact is compiled and validated on a worker (auto-cliffs, bridge and chasm
/// rules), and the compiled layout, the validation and the check overlays replace the draft.
/// </summary>
public sealed partial class PaintController : IModeController, IMapDocumentHost, IArtifactReceiver
{
    private readonly StudioMain _main;
    private readonly PaintState _state = new();
    private readonly PaintPanel _panel;
    private StudioDocument? _doc;
    private DungeonLayout? _layout;
    private TerrainValidationResult? _validation;
    private TerrainAnalysis? _analysis;
    private bool _compiled;

    // Compile pipeline (worker).
    private sealed record CompileOutcome(int Revision, DungeonLayout Layout, TerrainValidationResult Validation, bool Compiled, TerrainAnalysis Analysis);
    private Task<CompileOutcome>? _compile;
    private bool _compileAgain, _replacePending;
    private Task<(TerrainArtifact artifact, string name)>? _newMap;

    // Stroke state.
    private bool _drawing, _moving, _strokeChanged;
    private string _lastApplyKey = "";
    private readonly HashSet<int> _strokeCells = new();
    private int? _flattenLevel;
    private readonly HashSet<int> _preview = new();
    private bool _previewDirty, _clearPreviewWhenReady;
    private double _invalidateCooldown;
    private bool _invalidatePending;
    private int _hoverX = -1, _hoverY = -1;
    private bool _hoverValid;
    private Vector2 _pointer;
    private string _toolBeforePick = PaintTool.Paint;

    // Selection.
    private readonly List<int> _selection = new();
    private TerrainStamp? _clipboard;
    private int _moveStartX, _moveStartY, _moveDx, _moveDy;
    private bool _selectionDirty;

    // Overlays (3D).
    private CellOverlay? _checks, _editPreview, _selectionOverlay;
    private double _autosave = -1;

    public bool EmbedLook { get; set; } = true;
    /// <summary>Whether cells under a stroke are tinted until the terrain has caught up (recordings show the terrain itself).</summary>
    public bool ShowStrokePreview { get; set; } = true;

    public PaintController(StudioMain main)
    {
        _main = main;
        _panel = new PaintPanel(this, _state);
        var ui = main.Ui;
        ui.MapName.TextSubmitted += text => { Rename(text); ui.MapName.ReleaseFocus(); };
        ui.MapName.FocusExited += () => Rename(ui.MapName.Text);
        ui.ChecksChip.Pressed += () => _panel.ShowChecks(ui.ChecksChip);
        ui.Viewport.MouseExited += PointerLeft;
    }

    public StudioMode Mode => StudioMode.Paint;
    public Control Panel => _panel;
    public Control? Rail => _panel.Rail;
    public bool Busy => _compile != null || _newMap != null || !_main.View.ViewReady;
    public bool OwnsKeyboardMotion => _selection.Count > 0;

    private static string AutosavePath => Path.Combine(StudioSettings.Folder, "autosave.terrain.json");

    public string Hint => _state.Tool switch
    {
        PaintTool.Paint => T("Drag to paint · Ctrl+click picks · Shift+wheel: height · [ ]: brush size"),
        PaintTool.Fill => T("Click an area to fill it · Ctrl+click picks"),
        PaintTool.Picker => T("Click the map to take its material and height"),
        PaintTool.Raise => T("Drag to raise the land · [ ]: brush size"),
        PaintTool.Lower => T("Drag to lower the land · [ ]: brush size"),
        PaintTool.Flatten => T("Start on the height you want, then drag"),
        PaintTool.Smooth => T("Drag over ground to smooth it into terraces"),
        PaintTool.Decorate => T("Drag to plant · [ ]: brush size"),
        PaintTool.DecorationErase => T("Drag to remove plants"),
        PaintTool.Structure => T("Click to build · green fits, red does not"),
        PaintTool.Theme => T("Drag to paint the theme · [ ]: brush size"),
        PaintTool.Select => T("Click to select · Shift adds · Alt removes · drag to move"),
        _ => "",
    };

    /// <summary>What lies under the pointer (for the hint line).</summary>
    public string Cursor
    {
        get
        {
            if (_doc == null || !_hoverValid) return "";
            var a = _doc.Artifact;
            int i = _hoverY * a.width + _hoverX;
            if ((uint)i >= (uint)a.baseTiles.Length) return "";
            int tile = a.baseTiles[i];
            int level = TerrainEditor.terrainEditorHeightFromStored(tile, a.elevation?[i] ?? 0);
            return $"{BlockName(tile)} {level:+0;−0;0} · {ThemeCatalog.Name(TerrainEditor.terrainThemeKeyAt(a, i))}";
        }
    }

    // ── activation ──────────────────────────────────────────────────────────────────────────────────────────

    public void Activate(string? startFile)
    {
        EnsureOverlays();
        if (_doc == null)
        {
            if (startFile != null && TryLoad(startFile, out _)) { }
            else if (File.Exists(AutosavePath) && TryLoad(AutosavePath, out _, autosave: true)) { }
            else StarterMap();
            return;
        }
        if (_layout != null && (_main.View.Source != TerrainSource.Authored || _main.View.Layout != _layout))
        {
            _main.View.OpenAuthored(_layout, _cameraX, _cameraY, _cameraZoom);
            _checksDirty = true;
        }
        SetOverlaysVisible(true);
    }

    private double? _cameraX, _cameraY, _cameraZoom;

    public void Deactivate()
    {
        if (_main.View.Source == TerrainSource.Authored && _main.View.Layout == _layout)
        {
            _cameraX = _main.View.FocusX; _cameraY = _main.View.FocusY; _cameraZoom = _main.View.Zoom;
        }
        _drawing = _moving = false;
        SetOverlaysVisible(false);
    }

    private void EnsureOverlays()
    {
        if (_checks != null) return;
        var root = _main.View.Renderer.SceneViewport;
        _checks = new CellOverlay(100) { Name = "PaintChecks" };
        _editPreview = new CellOverlay(101) { Name = "PaintPreview" };
        _selectionOverlay = new CellOverlay(102) { Name = "PaintSelection", Pulse = 1 };
        root.AddChild(_checks);
        root.AddChild(_editPreview);
        root.AddChild(_selectionOverlay);
    }

    private void SetOverlaysVisible(bool visible)
    {
        if (_checks == null) return;
        _checks.Visible = visible;
        _editPreview!.Visible = visible;
        _selectionOverlay!.Visible = visible;
    }

    // ── documents ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The first map a new user sees: a small composed world to paint over, rather than an empty field.</summary>
    private void StarterMap()
    {
        try
        {
            var map = MapSimulation.simulateMap(new MapSimulationOptions
            {
                seed = "first-dawn", width = 64, height = 48, themeKey = GeneratorArtifacts.GENERATOR_DEFAULT_THEME,
                archetypeKey = "terraced_valley", chaos = 0.3,
            });
            SetDocument(map.artifact, "first-dawn", null);
        }
        catch (Exception e)
        {
            GD.PushWarning($"starter map failed, opening a blank map: {e.Message}");
            NewMap(GeneratorArtifacts.BASE_MAP_WIDTH, GeneratorArtifacts.BASE_MAP_HEIGHT, notify: false);
        }
    }

    /// <summary>Asks before a document with unsaved edits is replaced.</summary>
    private void ConfirmDiscard(string action, Action proceed)
    {
        if (_doc == null || !_doc.Dirty) { proceed(); return; }
        Modal.Confirm(_main.Ui, T("Unsaved changes"),
            F("“{0}” has changes that are not saved in a file. Continue anyway? The last state stays in the autosave.", _doc.Name),
            action, proceed);
    }

    /// <summary>The New map card: size, theme, and an empty field or a generated landscape to paint over.</summary>
    public void ShowNewMap()
    {
        var modal = new Modal(T("New map"), 470, T("Start with an empty field, or let the studio grow a landscape you can paint over."));
        var sizes = new Segmented();
        var presets = new Dictionary<string, (int w, int h)> { ["s"] = (64, 48), ["m"] = (96, 72), ["l"] = (128, 96), ["xl"] = (192, 144) };
        sizes.Add("s", T("Small"));
        sizes.Add("m", T("Medium"));
        sizes.Add("l", T("Large"));
        sizes.Add("xl", T("Huge"));
        sizes.Set("m");
        var sizeHint = W.Hint("");
        void UpdateHint()
        {
            var (w, h) = presets[sizes.Current];
            sizeHint.Text = F("{0} × {1} cells · {2} × {3} m", w, h, w * 2.5, h * 2.5);
        }
        sizes.Selected += _ => UpdateHint();
        UpdateHint();
        string theme = _doc?.Artifact.biomeKey ?? GeneratorArtifacts.GENERATOR_DEFAULT_THEME;
        var themes = W.Options(ThemeCatalog.All.Select(e => (e.Name, e.Key)).ToList(), theme, key => theme = key);
        var start = new Segmented();
        start.Add("landscape", T("Landscape"), IconKind.Mountain, T("Hills, water and forests to start from"));
        start.Add("empty", T("Empty"), IconKind.Floor, T("A flat, empty field"));
        start.Set("landscape");
        modal.Body.AddChild(W.Group(T("Size"), sizes, sizeHint));
        modal.Body.AddChild(W.Group(T("Theme"), themes));
        modal.Body.AddChild(W.Group(T("Start with"), start));
        modal.AddButton(T("Cancel"), modal.Close);
        modal.AddButton(T("Create map"), () =>
        {
            modal.Close();
            var (w, h) = presets[sizes.Current];
            bool landscape = start.Current == "landscape";
            ConfirmDiscard(T("New map"), () => NewMap(w, h, theme: theme, landscape: landscape));
        }, primary: true, icon: IconKind.Plus);
        modal.Open(_main.Ui);
    }

    public void NewMap(int width, int height, bool notify = true, string? theme = null, bool landscape = false)
    {
        var (w, h) = GeneratorArtifacts.clampMapSize(width, height);
        theme ??= _doc?.Artifact.biomeKey ?? GeneratorArtifacts.GENERATOR_DEFAULT_THEME;
        _state.ThemeKey = theme;
        if (landscape)
        {
            string seed = Seeds.Random();
            string key = theme;
            _newMap = Task.Run(() =>
            {
                var map = MapSimulation.simulateMap(new MapSimulationOptions { seed = seed, width = w, height = h, themeKey = key, chaos = 0.35 });
                return (map.artifact, seed);
            });
            return;
        }
        var artifact = TerrainEditor.createTerrainArtifact(new CreateTerrainArtifactOptions
        {
            width = w, height = h, biomeKey = theme, withDefaultMarkers = false,
        });
        SetDocument(artifact, T("new-map"), null);
        if (notify) _main.Toast(F("New map · {0} × {1} cells", w, h));
    }

    public void ReceiveArtifact(TerrainArtifact artifact, string name, string notice)
    {
        SetDocument(artifact, name, null);
        _main.Toast(notice);
    }

    private void SetDocument(TerrainArtifact artifact, string name, string? path)
    {
        GeneratorArtifacts.normalizeCoreArtifact(artifact);
        if (_doc == null) _doc = new StudioDocument(artifact, name);
        else _doc.Replace(artifact, name, path);
        _doc.FilePath = path;
        _selection.Clear();
        _selectionDirty = true;
        _preview.Clear();
        _previewDirty = true;
        _cameraX = _cameraY = _cameraZoom = null;
        _validation = null;
        _main.Ui.SetChecks(0, 0, checking: true);
        Republish(replace: true);
        _panel.SyncFromState();
        UpdateSelectionInfo();
    }

    public void Rename(string name)
    {
        if (_doc == null) return;
        name = name.Trim();
        if (name.Length > 0 && name != _doc.Name) _doc.Name = name;
        _main.Ui.SetDocument(_doc.Name, _doc.Dirty);
    }

    /// <summary>Gives every cell of the map the current theme and makes it the map's base theme.</summary>
    public void ApplyThemeToMap()
    {
        if (_doc == null) return;
        var a = _doc.Artifact;
        _doc.BeginEdit();
        a.biomeKey = _state.ThemeKey;
        const int step = 63;
        var brush = new TerrainBrush { size = step, shape = TerrainBrushShape.Square };
        for (int ty = step / 2; ty < a.height + step / 2; ty += step)
            for (int tx = step / 2; tx < a.width + step / 2; tx += step)
                TerrainEditor.applyTerrainThemeBrush(a, new TerrainThemeBrushEdit
                    { tx = Math.Min(tx, a.width - 1), ty = Math.Min(ty, a.height - 1), themeKey = _state.ThemeKey, brush = brush });
        _doc.Touch();
        Republish(replace: true, keepCamera: true);
        _main.Toast(F("The whole map is now “{0}”", ThemeCatalog.Name(_state.ThemeKey)));
    }

    public void FocusCell(int tx, int ty)
    {
        if (_layout == null) return;
        _main.View.FocusX = _layout.originX + (tx + 0.5) * _layout.tileSize;
        _main.View.FocusY = _layout.originY + (ty + 0.5) * _layout.tileSize;
        _main.View.Zoom = Math.Max(_main.View.Zoom, 1.0);
    }

    // ── files ───────────────────────────────────────────────────────────────────────────────────────────────

    public void Open() => ConfirmDiscard(T("Open"), OpenDialog);

    private void OpenDialog()
    {
        Dialogs.OpenFile(_main, T("Open map"), new[] { "*.json ; " + T("Terrain maps") }, path =>
        {
            if (TryLoad(path, out string? error)) _main.Toast(F("Opened {0}", Path.GetFileName(path)));
            else _main.Toast(F("Could not open the map: {0}", error ?? ""), StudioTheme.Danger, 6);
        });
    }

    /// <summary>Opens a file from the recent list.</summary>
    public void OpenRecent(string path) => ConfirmDiscard(T("Open"), () =>
    {
        if (TryLoad(path, out string? error)) _main.Toast(F("Opened {0}", Path.GetFileName(path)));
        else _main.Toast(F("Could not open the map: {0}", error ?? ""), StudioTheme.Danger, 6);
    });

    public void Save()
    {
        if (_doc?.FilePath is { } path && !path.StartsWith(ProjectSettings.GlobalizePath("user://"))) WriteTo(path);
        else SaveAs();
    }

    public void SaveAs()
    {
        if (_doc == null) return;
        string suggested = System.Text.RegularExpressions.Regex.Replace(_doc.Name.ToLowerInvariant(), "[^a-z0-9._-]", "-") + ".json";
        Dialogs.SaveFile(_main, T("Save map"), suggested, new[] { "*.json ; " + T("Terrain maps") }, WriteTo);
    }

    /// <summary>Writes the map to a file without a dialog (self-test, scripted use).</summary>
    public void SaveToPath(string path) => WriteTo(path);

    /// <summary>Opens a map file without a dialog; returns an error text or null.</summary>
    public string? LoadFromPath(string path) => TryLoad(path, out string? error) ? null : error ?? "unknown error";

    /// <summary>A fingerprint of the document (FNV-1a over tiles, heights and themes) for round-trip checks.</summary>
    public uint Fingerprint()
    {
        if (_doc == null) return 0;
        var a = _doc.Artifact;
        uint h = 2166136261;
        void Mix(int v) { h ^= (uint)v; h *= 16777619; }
        foreach (byte b in a.baseTiles) Mix(b);
        if (a.elevation != null) foreach (sbyte e in a.elevation) Mix(e);
        if (a.themeIndex != null) foreach (byte t in a.themeIndex) Mix(t);
        Mix(a.decorations?.Count ?? 0);
        return h;
    }

    /// <summary>The stored height of a cell (self-test).</summary>
    public int StoredHeightAt(int tx, int ty) => _doc?.Artifact.elevation?[ty * _doc.Artifact.width + tx] ?? 0;

    private void WriteTo(string path)
    {
        if (_doc == null) return;
        try
        {
            File.WriteAllText(path, DocumentJson(pretty: true));
            _doc.MarkSaved(path);
            _main.Settings.RememberFile(path);
            _main.RefreshFileMenu();
            _main.Toast(F("Saved {0}", Path.GetFileName(path)), StudioTheme.Ok);
        }
        catch (Exception e) { _main.Toast(F("Could not save: {0}", e.Message), StudioTheme.Danger, 6); }
    }

    /// <summary>
    /// Clamps every stored level into its block's domain before a document is written. The editor functions (like the
    /// original's) can leave e.g. a former chasm's depth on a floor cell, which the importer would then refuse.
    /// </summary>
    private static void SanitiseElevation(TerrainArtifact artifact)
    {
        if (artifact.elevation == null) return;
        for (int i = 0; i < artifact.baseTiles.Length; i++)
        {
            int tile = artifact.baseTiles[i];
            int min = tile == TileType.Chasm ? TerrainModel.CHASM_MIN_DEPTH : TerrainModel.TERRAIN_MIN_GROUND_ELEVATION;
            int max = tile == TileType.Chasm ? TerrainModel.CHASM_MAX_DEPTH : TerrainModel.TERRAIN_MAX_ELEVATION;
            artifact.elevation[i] = (sbyte)Math.Clamp((int)artifact.elevation[i], min, max);
        }
    }

    private string DocumentJson(bool pretty)
    {
        SanitiseElevation(_doc!.Artifact);
        string json = TerrainEditor.terrainEditorDocumentToJsonText(_doc!.Artifact, _doc.Name, _doc.Notes, pretty);
        if (!EmbedLook) return json;
        var node = JsonNode.Parse(json)!.AsObject();
        node["look"] = _main.Look.ToJson();
        node["exportedAt"] = DateTime.UtcNow.ToString("o");
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = pretty });
    }

    private bool TryLoad(string path, out string? error, bool autosave = false)
    {
        error = null;
        try
        {
            string text = File.ReadAllText(path);
            var artifact = TerrainEditor.terrainArtifactFromEditorDocumentText(text, out string? name);
            SetDocument(artifact, name ?? Path.GetFileNameWithoutExtension(path), autosave ? null : path);
            if (!autosave && EmbedLook && JsonNode.Parse(text)?["look"] is JsonNode lookNode)
            {
                _main.Look.CopyFrom(LookSettings.FromJson(lookNode));
                _main.SyncLookPanel();
                if (_main.View.ApplyForm(_main.Look)) Republish(replace: true);
            }
            if (!autosave)
            {
                _main.Settings.RememberFile(path);
                _main.RefreshFileMenu();
            }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    public void ExportHeightmap()
    {
        if (_doc == null) return;
        string suggested = System.Text.RegularExpressions.Regex.Replace(_doc.Name.ToLowerInvariant(), "[^a-z0-9._-]", "-") + "-height.png";
        Dialogs.SaveFile(_main, T("Export height map"), suggested, new[] { "*.png ; " + T("PNG images") }, path =>
        {
            var a = _doc.Artifact;
            var height = Image.CreateEmpty(a.width, a.height, false, Image.Format.L8);
            var blocks = Image.CreateEmpty(a.width, a.height, false, Image.Format.Rgb8);
            for (int ty = 0; ty < a.height; ty++)
                for (int tx = 0; tx < a.width; tx++)
                {
                    int i = ty * a.width + tx, tile = a.baseTiles[i];
                    int level = TerrainEditor.terrainEditorHeightFromStored(tile, a.elevation?[i] ?? 0);
                    float v = Math.Clamp((level + 30) / 55f, 0, 1);
                    height.SetPixel(tx, ty, new Color(v, v, v));
                    blocks.SetPixel(tx, ty, new Color((uint)((OverlayPalette.tileCardColor(tile) << 8) | 0xff)));
                }
            height.SavePng(path);
            blocks.SavePng(Path.ChangeExtension(path, null) + "-blocks.png");
            _main.Toast(F("Exported {0} (and a colour map)", Path.GetFileName(path)), StudioTheme.Ok);
        });
    }

    // ── compile & publish ───────────────────────────────────────────────────────────────────────────────────

    private bool _keepCameraOnReplace;

    /// <summary>
    /// Normalises the document and compiles it on a worker: the compiled layout (auto-cliffs, validated bridges and
    /// chasms), the validation and the analysis for the check overlays. <paramref name="replace"/> installs a new
    /// session (new document, other size or base theme) instead of updating the live layout in place.
    /// </summary>
    private void Republish(bool replace, bool keepCamera = false)
    {
        if (_doc == null) return;
        _replacePending |= replace;
        _keepCameraOnReplace |= keepCamera;
        if (_compile != null) { _compileAgain = true; return; }
        GeneratorArtifacts.normalizeCoreArtifact(_doc.Artifact);
        var snapshot = TerrainEditor.cloneTerrainArtifact(_doc.Artifact);
        int revision = _doc.Revision;
        _compile = Task.Run(() =>
        {
            var result = TerrainArtifactModule.compileTerrainArtifactToDungeonLayout(snapshot, TerrainArtifactModule.TERRAIN_EDITOR_VALIDATION_OPTIONS);
            var layout = result.layout ?? TerrainEditor.draftDungeonLayoutFromTerrainArtifact(snapshot);
            var analysis = OverlayPalette.analyzeForOverlay(layout);
            return new CompileOutcome(revision, layout, result.validation, result.layout != null, analysis);
        });
        _autosave = 2.5;
    }

    private void InstallCompiled(CompileOutcome outcome)
    {
        var view = _main.View;
        var next = outcome.Layout;
        bool replace = _replacePending || _layout == null || _layout.width != next.width || _layout.height != next.height
            || _layout.biomeKey != next.biomeKey || view.Source != TerrainSource.Authored || view.Layout != _layout;
        if (_main.Mode != StudioMode.Paint)
        {
            // Another mode owns the view: keep the compiled map; Activate installs it.
            _layout = next;
        }
        else if (replace)
        {
            bool keep = _keepCameraOnReplace && _layout != null && view.HasSession;
            double fx = view.FocusX, fy = view.FocusY, zoom = view.Zoom;
            _layout = next;
            if (keep) view.OpenAuthored(next, fx, fy, zoom);
            else view.OpenAuthored(next, _cameraX, _cameraY, _cameraZoom);
            _cameraX = _cameraY = _cameraZoom = null;
        }
        else
        {
            // The same layout object keeps its identity (the dungeon holds it): swap in the compiled arrays.
            _layout!.tiles = next.tiles;
            _layout.elevation = next.elevation;
            _layout.terrain = next.terrain;
            _layout.rooms = next.rooms;
            _layout.doors = next.doors;
            view.InvalidateLayout();
            _invalidatePending = false;
        }
        _replacePending = false;
        _keepCameraOnReplace = false;
        _validation = outcome.Validation;
        _analysis = outcome.Analysis;
        _compiled = outcome.Compiled;
        _checksDirty = true;
        _clearPreviewWhenReady = true;
        _ambientTimer = 1.5;
        _panel.SetValidation(_validation, _compiled, next.width);
        int errors = _validation.issues.Count(i => i.severity == "error");
        _main.Ui.SetChecks(errors, _validation.issues.Count - errors);
        if (_doc != null)
        {
            var a = _doc.Artifact;
            _main.Ui.MapName.TooltipText = F("{0} × {1} cells · {2} × {3} m — click to rename", a.width, a.height, a.width * 2.5, a.height * 2.5);
        }
    }

    // ── frame ───────────────────────────────────────────────────────────────────────────────────────────────

    private bool _checksDirty;
    private double _ambientTimer = -1;

    public void Tick(double delta)
    {
        if (_newMap is { IsCompleted: true } created)
        {
            _newMap = null;
            if (created.IsCompletedSuccessfully)
            {
                SetDocument(created.Result.artifact, created.Result.name, null);
                _main.Toast(F("New map “{0}”", created.Result.name));
            }
            else _main.Toast(F("Could not create the map: {0}", created.Exception?.GetBaseException().Message ?? ""), StudioTheme.Danger, 6);
        }
        if (_compile is { IsCompleted: true } done)
        {
            _compile = null;
            if (done.IsCompletedSuccessfully) InstallCompiled(done.Result);
            else _main.Toast(F("The map could not be compiled: {0}", done.Exception?.GetBaseException().Message ?? ""), StudioTheme.Danger, 6);
            if (_compileAgain || (_doc != null && done.IsCompletedSuccessfully && done.Result.Revision != _doc.Revision && !_drawing))
            {
                _compileAgain = false;
                Republish(false);
            }
        }
        // Live draft during a gesture: publish the touched cells a few times a second (each publication re-hashes
        // the whole layout on the main thread, so not on every pointer sample).
        _invalidateCooldown -= delta;
        if (_invalidatePending && _invalidateCooldown <= 0)
        {
            _main.View.InvalidateLayout();
            _invalidatePending = false;
            _invalidateCooldown = _layout != null && _layout.width * _layout.height > 40000 ? 0.35 : 0.15;
        }
        if (_previewDirty) RebuildPreview();
        if (_clearPreviewWhenReady && !_drawing && _compile == null && _main.View.ViewReady && !_invalidatePending)
        {
            _clearPreviewWhenReady = false;
            _preview.Clear();
            _previewDirty = true;
        }
        if (_checksDirty) RebuildChecks();
        if (_ambientTimer > 0 && !_drawing)
        {
            _ambientTimer -= delta;
            if (_ambientTimer <= 0) _main.View.RefreshAmbient();
        }
        if (_selectionDirty) RebuildSelection();
        if (_autosave > 0)
        {
            _autosave -= delta;
            if (_autosave <= 0 && _doc != null)
            {
                try { File.WriteAllText(AutosavePath, DocumentJson(pretty: false)); }
                catch (Exception e) { GD.PushWarning($"autosave failed: {e.Message}"); }
            }
        }
        if (_doc != null)
        {
            _main.Ui.SetDocument(_doc.Name, _doc.Dirty);
            _main.Ui.Undo.Disabled = !_doc.CanUndo;
            _main.Ui.Redo.Disabled = !_doc.CanRedo;
        }
    }

    public void AfterPresent()
    {
        UpdateHover(_pointer);
    }

    /// <summary>The pointer left the map (onto a panel or out of the window): no brush preview until it returns.</summary>
    public void PointerLeft()
    {
        if (_drawing || _moving) return;
        _pointer = new Vector2(-1e6f, -1e6f);
        _hoverValid = false;
    }

    // ── pointer ─────────────────────────────────────────────────────────────────────────────────────────────

    private bool CellAt(Vector2 screen, out int tx, out int ty)
    {
        tx = ty = -1;
        if (_layout == null || !_main.View.TryPickGround(screen, out double wx, out double wy)) return false;
        tx = (int)Math.Floor((wx - _layout.originX) / _layout.tileSize);
        ty = (int)Math.Floor((wy - _layout.originY) / _layout.tileSize);
        return tx >= 0 && ty >= 0 && tx < _layout.width && ty < _layout.height;
    }

    private void UpdateHover(Vector2 screen)
    {
        _hoverValid = CellAt(screen, out _hoverX, out _hoverY);
    }

    public void Pointer(InputEvent e)
    {
        if (_doc == null) return;
        switch (e)
        {
            case InputEventMouseMotion motion:
                _pointer = motion.Position;
                UpdateHover(_pointer);
                if (_moving && _hoverValid)
                {
                    _moveDx = _hoverX - _moveStartX;
                    _moveDy = _hoverY - _moveStartY;
                    _selectionDirty = true;
                }
                else if (_drawing && _hoverValid) ApplyToolAt(_hoverX, _hoverY);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _pointer = button.Position;
                UpdateHover(_pointer);
                if (button.Pressed) PointerDown(button);
                else PointerUp();
                break;
        }
    }

    private void PointerDown(InputEventMouseButton button)
    {
        if (!_hoverValid || _doc == null) return;
        string tool = _state.Tool;
        if (button.CtrlPressed || button.MetaPressed || tool == PaintTool.Picker) { PickAt(_hoverX, _hoverY); return; }
        if (tool == PaintTool.Select)
        {
            int index = _hoverY * _doc.Artifact.width + _hoverX;
            if (!button.ShiftPressed && !button.AltPressed && _selection.Contains(index))
            {
                _moving = true;
                _moveStartX = _hoverX; _moveStartY = _hoverY; _moveDx = _moveDy = 0;
                return;
            }
            var region = TerrainEditor.terrainMagicSelection(_doc.Artifact, _hoverX, _hoverY);
            if (button.ShiftPressed) { foreach (int i in region) if (!_selection.Contains(i)) _selection.Add(i); }
            else if (button.AltPressed) _selection.RemoveAll(region.Contains);
            else { _selection.Clear(); _selection.AddRange(region); }
            _selectionDirty = true;
            UpdateSelectionInfo();
            return;
        }
        BeginStroke();
        _drawing = PaintTool.Drags(tool);
        ApplyToolAt(_hoverX, _hoverY);
        if (!_drawing) EndStroke();
    }

    private void PointerUp()
    {
        if (_moving)
        {
            _moving = false;
            if (_moveDx != 0 || _moveDy != 0) MoveSelectionBy(_moveDx, _moveDy);
            _moveDx = _moveDy = 0;
            _selectionDirty = true;
            return;
        }
        if (!_drawing) return;
        _drawing = false;
        EndStroke();
    }

    private void BeginStroke()
    {
        _doc!.BeginEdit();
        _lastApplyKey = "";
        _strokeChanged = false;
        _strokeCells.Clear();
        _flattenLevel = null;
    }

    private void EndStroke()
    {
        if (!_strokeChanged) _doc?.DiscardEdit();
        else Republish(false);
    }

    /// <summary>
    /// A scripted gesture with the current tool over these cells, exactly as a pointer drag would do it (one undo
    /// snapshot, draft publication per sample, compile at the end). Used by the self-test and by scripted demos.
    /// </summary>
    public void ScriptStroke(IEnumerable<(int tx, int ty)> cells)
    {
        if (_doc == null) return;
        BeginStroke();
        _drawing = true;
        foreach (var (tx, ty) in cells)
            if (tx >= 0 && ty >= 0 && tx < _doc.Artifact.width && ty < _doc.Artifact.height) ApplyToolAt(tx, ty);
        _drawing = false;
        EndStroke();
    }

    public (int width, int height) MapSize => _doc == null ? (0, 0) : (_doc.Artifact.width, _doc.Artifact.height);

    public bool Wheel(InputEventMouseButton e)
    {
        if (!e.ShiftPressed) return false;
        int step = e.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
        SetHeight(_state.Height + step);
        _main.Toast(F("Height {0}", _state.Height.ToString("+0;−0;0")), StudioTheme.Mint, 0.8);
        return true;
    }

    // ── tools ───────────────────────────────────────────────────────────────────────────────────────────────

    private void ApplyToolAt(int tx, int ty)
    {
        var doc = _doc!;
        var a = doc.Artifact;
        string tool = _state.Tool;
        string key = $"{tool}:{_state.Tile}:{_state.Height}:{_state.ThemeKey}:{_state.DecorationKind}:{_state.BrushSize}:{_state.BrushShape}:{_state.BrushSoft}:{_state.BrushStrength}:{_state.SmoothQuantum}:{tx}:{ty}";
        if (key == _lastApplyKey) return;
        _lastApplyKey = key;
        TerrainEditResult result;
        switch (tool)
        {
            case PaintTool.Paint:
                result = TerrainEditor.applyTerrainPaintBrush(a, new TerrainPaintBrushEdit
                    { tx = tx, ty = ty, tile = _state.Tile, height = _state.Height, themeKey = _state.ThemeKey, brush = _state.Brush });
                break;
            case PaintTool.Erase:
                result = TerrainEditor.applyTerrainPaintBrush(a, new TerrainPaintBrushEdit
                    { tx = tx, ty = ty, tile = TileType.Solid, height = 0, themeKey = _state.ThemeKey, brush = _state.Brush });
                break;
            case PaintTool.Theme:
                result = TerrainEditor.applyTerrainThemeBrush(a, new TerrainThemeBrushEdit { tx = tx, ty = ty, themeKey = _state.ThemeKey, brush = _state.Brush });
                break;
            case PaintTool.Fill:
                result = TerrainEditor.floodFillTerrainPaint(a, new TerrainPaintFillEdit
                    { tx = tx, ty = ty, tile = _state.Tile, height = _state.Height, themeKey = _state.ThemeKey });
                break;
            case PaintTool.Smooth:
                result = TerrainEditor.applyTerrainElevationSmooth(a, new TerrainSmoothEdit
                    { tx = tx, ty = ty, brush = _state.Brush, affect = TerrainEditAffect.Walkable, passes = 1, quantum = _state.SmoothQuantum });
                break;
            case PaintTool.Raise:
                result = Sculpt(a, tx, ty, _state.SculptAmount, null);
                break;
            case PaintTool.Lower:
                result = Sculpt(a, tx, ty, -_state.SculptAmount, null);
                break;
            case PaintTool.Flatten:
                if (_flattenLevel == null)
                {
                    TerrainEditor.ensureTerrainEditorLayers(a);
                    int start = ty * a.width + tx;
                    if (a.baseTiles[start] is TileType.Chasm) return;
                    _flattenLevel = a.elevation![start];
                }
                result = Sculpt(a, tx, ty, 0, _flattenLevel);
                break;
            case PaintTool.Decorate:
                result = TerrainEditor.applyTerrainDecorationBrush(a, new TerrainDecorationBrushEdit
                    { tx = tx, ty = ty, kind = _state.DecorationKind, themeKey = _state.ThemeKey, brush = _state.Brush });
                break;
            case PaintTool.DecorationErase:
                result = TerrainEditor.eraseTerrainDecorationBrush(a, new TerrainDecorationEraseEdit { tx = tx, ty = ty, brush = _state.Brush });
                break;
            case PaintTool.Structure:
            {
                var structure = TerrainEditor.applyTerrainDepthStructureBrush(a, new TerrainDepthStructureBrushEdit
                {
                    tx = tx, ty = ty, kind = _state.StructureKind,
                    passageAxis = _state.StructureAxis == "auto" ? null : _state.StructureAxis,
                    span = _state.StructureSpan, themeKey = _state.ThemeKey,
                });
                if (structure.rejection != null) _main.Toast(structure.rejection, StudioTheme.Warning, 4);
                result = structure;
                break;
            }
            default:
                return;
        }
        if (result.changed.Count == 0) return;
        _strokeChanged = true;
        doc.Touch();
        PublishDraft(result.changed);
    }

    /// <summary>
    /// Raise / lower (by <paramref name="delta"/> levels, each cell once per stroke) or flatten (to
    /// <paramref name="target"/>) the stored height under the brush. The material stays; chasms and structures are
    /// left alone.
    /// </summary>
    private TerrainEditResult Sculpt(TerrainArtifact a, int tx, int ty, int delta, int? target)
    {
        TerrainEditor.ensureTerrainEditorLayers(a);
        var elevation = a.elevation!;
        var changed = new List<int>();
        foreach (var cell in TerrainEditor.terrainBrushCells(a.width, a.height, tx, ty, _state.Brush))
        {
            int i = cell.index, tile = a.baseTiles[i];
            if (tile is TileType.Chasm or TileType.Cleft or TileType.Underpass) continue;
            if (target == null && _strokeCells.Contains(i)) continue;
            var range = TerrainEditor.terrainEditorHeightRangeForTile(tile);
            int current = elevation[i];
            double weight = _state.BrushSoft ? cell.weight * _state.BrushStrength : 1;
            int goal = target ?? current + delta;
            int next = Math.Clamp((int)Math.Round(current + (goal - current) * weight), range.min, range.max);
            if (next == current) continue;
            elevation[i] = (sbyte)next;
            _strokeCells.Add(i);
            changed.Add(i);
        }
        return new TerrainEditResult { changed = changed };
    }

    /// <summary>Copies the changed cells of the artifact into the live layout (draft) and marks them for the preview.</summary>
    private void PublishDraft(List<int> changed)
    {
        if (_layout == null || _doc == null) return;
        var a = _doc.Artifact;
        if (_layout.width != a.width || _layout.height != a.height) { Republish(true); return; }
        _layout.elevation ??= new sbyte[a.width * a.height];
        var terrain = _layout.terrain ??= new DungeonTerrainLayers { schemaVersion = DungeonTypes.TERRAIN_ARTIFACT_SCHEMA_VERSION };
        if (a.themeIndex != null && (terrain.themeIndex == null || terrain.themeIndex.Length != a.themeIndex.Length))
            terrain.themeIndex = (byte[])a.themeIndex.Clone();
        terrain.themePalette = a.themePalette != null ? new List<string>(a.themePalette) : null;
        foreach (int i in changed)
        {
            if ((uint)i >= (uint)a.baseTiles.Length) continue;
            _layout.tiles[i] = a.baseTiles[i];
            if (a.elevation != null) _layout.elevation[i] = a.elevation[i];
            if (a.themeIndex != null && terrain.themeIndex != null) terrain.themeIndex[i] = a.themeIndex[i];
            _preview.Add(i);
        }
        terrain.decorations = a.decorations?.Select(d => new TerrainDecorationPlacement { kind = d.kind, tx = d.tx, ty = d.ty, seed = d.seed, themeKey = d.themeKey }).ToList();
        _previewDirty = true;
        _invalidatePending = true;
    }

    private void PickAt(int tx, int ty)
    {
        var a = _doc!.Artifact;
        var sample = TerrainEditor.sampleTerrainBrushAt(a, tx, ty);
        if (sample == null) return;
        string? structure = sample.tile switch
        {
            TileType.Cleft => TerrainDepthStructureKind.Cleft,
            TileType.Underpass => TerrainDepthStructureKind.Underpass,
            _ => null,
        };
        if (structure != null)
        {
            _state.StructureKind = structure;
            _state.Tool = PaintTool.Structure;
            _main.Toast(structure == TerrainDepthStructureKind.Cleft ? T("Picked a cliff pass") : T("Picked a rope bridge"), StudioTheme.Mint, 1.4);
        }
        else
        {
            _state.Tile = sample.tile;
            _state.Height = sample.height;
            string back = _state.Tool == PaintTool.Picker ? _toolBeforePick : _state.Tool;
            _state.Tool = PaintRail.Of(back) == PaintRail.Terrain ? (back == PaintTool.Fill ? PaintTool.Fill : PaintTool.Paint) : back;
            _main.Toast(F("Picked {0} at height {1}", BlockName(sample.tile), _state.Height.ToString("+0;−0;0")), StudioTheme.Mint, 1.4);
        }
        _state.Remember();
        _panel.SyncFromState();
    }

    public void SetRail(string rail)
    {
        _state.Tool = rail switch
        {
            PaintRail.Sculpt => _state.SculptTool,
            PaintRail.Nature => _state.NatureTool,
            PaintRail.Build => _state.BuildTool,
            PaintRail.Theme => PaintTool.Theme,
            PaintRail.Select => PaintTool.Select,
            _ => _state.TerrainTool,
        };
        _panel.SyncFromState();
    }

    public void SetTool(string tool)
    {
        if (tool == PaintTool.Picker && _state.Tool != PaintTool.Picker) _toolBeforePick = _state.Tool;
        _state.Tool = tool;
        _state.Remember();
        _panel.SyncFromState();
    }

    public void ChooseMaterial(int tile)
    {
        _state.Tile = tile;
        _state.Height = _state.Height;
        if (_state.Tool is not (PaintTool.Paint or PaintTool.Fill)) _state.Tool = _state.TerrainTool;
        _panel.SyncFromState();
    }

    public void ChooseNature(string key)
    {
        if (key == "erase") _state.Tool = PaintTool.DecorationErase;
        else { _state.DecorationKind = key; _state.Tool = PaintTool.Decorate; }
        _state.Remember();
        _panel.SyncFromState();
    }

    public void ChooseBuild(string key)
    {
        _state.StructureKind = key;
        _state.Tool = PaintTool.Structure;
        _state.Remember();
        _panel.SyncFromState();
    }

    public void SetHeight(int height)
    {
        _state.Height = height;
        _panel.SyncFromState();
    }

    public void SetSculptAmount(int amount)
    {
        _state.SculptAmount = Math.Clamp(amount, 1, 5);
        _panel.SyncFromState();
    }

    public void SetBrush(int? size = null, string? shape = null, bool? soft = null, double? strength = null)
    {
        if (size is { } s) _state.BrushSize = Math.Clamp(s, 1, 64);
        if (shape != null) _state.BrushShape = shape;
        if (soft is { } f) _state.BrushSoft = f;
        if (strength is { } t) _state.BrushStrength = Math.Clamp(t, 0.1, 1);
        _panel.SyncFromState();
    }

    public void SetTheme(string key)
    {
        _state.ThemeKey = key;
        if (_state.Rail == PaintRail.Theme) _state.Tool = PaintTool.Theme;
        _panel.SyncFromState();
    }

    public void SetStructure(int? span = null, string? axis = null)
    {
        if (span is { } s) _state.StructureSpan = s;
        if (axis != null) _state.StructureAxis = axis;
        _panel.SyncFromState();
    }

    public void SetQuantum(int quantum)
    {
        _state.SmoothQuantum = quantum;
        _panel.SyncFromState();
    }

    public void SetOverlay(OverlayKind kind, bool on)
    {
        _state.Overlays = on ? _state.Overlays | kind : _state.Overlays & ~kind;
        _checksDirty = true;
    }

    // ── selection & clipboard ───────────────────────────────────────────────────────────────────────────────

    private void UpdateSelectionInfo()
    {
        string clip = _clipboard != null ? " · " + F("clipboard {0} × {1}", _clipboard.width, _clipboard.height) : "";
        if (_selection.Count == 0 || _doc == null)
        {
            _panel.SetSelectionInfo(T("Nothing selected") + clip);
            return;
        }
        int w = _doc.Artifact.width;
        int minX = _selection.Min(i => i % w), maxX = _selection.Max(i => i % w), minY = _selection.Min(i => i / w), maxY = _selection.Max(i => i / w);
        _panel.SetSelectionInfo(F("{0} cells selected ({1} × {2})", _selection.Count, maxX - minX + 1, maxY - minY + 1) + clip);
    }

    public void Copy()
    {
        if (_doc == null || _selection.Count == 0) return;
        _clipboard = TerrainEditor.copyTerrainSelectionStamp(_doc.Artifact, _selection);
        UpdateSelectionInfo();
        _main.Toast(F("Copied {0} cells", _selection.Count));
    }

    public void Cut()
    {
        if (_doc == null || _selection.Count == 0) return;
        _clipboard = TerrainEditor.copyTerrainSelectionStamp(_doc.Artifact, _selection);
        ClearSelection();
        _main.Toast(T("Cut to the clipboard"));
    }

    public void ClearSelection()
    {
        if (_doc == null || _selection.Count == 0) return;
        _doc.BeginEdit();
        var result = TerrainEditor.clearTerrainSelection(_doc.Artifact, _selection);
        if (result.changed.Count > 0) { _doc.Touch(); PublishDraft(result.changed); Republish(false); }
        else _doc.DiscardEdit();
        UpdateSelectionInfo();
    }

    public void SelectAll()
    {
        if (_doc == null) return;
        _selection.Clear();
        _selection.AddRange(Enumerable.Range(0, _doc.Artifact.width * _doc.Artifact.height));
        _selectionDirty = true;
        _state.Tool = PaintTool.Select;
        _panel.SyncFromState();
        UpdateSelectionInfo();
    }

    public void Deselect()
    {
        _selection.Clear();
        _selectionDirty = true;
        UpdateSelectionInfo();
    }

    public void Paste()
    {
        if (_doc == null || _clipboard == null) return;
        var a = _doc.Artifact;
        int cx = _hoverValid ? _hoverX : a.width / 2, cy = _hoverValid ? _hoverY : a.height / 2;
        int ox = Math.Clamp(cx - _clipboard.width / 2, 0, Math.Max(0, a.width - _clipboard.width));
        int oy = Math.Clamp(cy - _clipboard.height / 2, 0, Math.Max(0, a.height - _clipboard.height));
        _doc.BeginEdit();
        var result = TerrainEditor.applyTerrainStamp(a, _clipboard, ox, oy);
        _selection.Clear();
        for (int y = 0; y < _clipboard.height; y++)
            for (int x = 0; x < _clipboard.width; x++)
            {
                if (_clipboard.mask != null && _clipboard.mask[y * _clipboard.width + x] == 0) continue;
                int tx = ox + x, ty = oy + y;
                if (tx < a.width && ty < a.height) _selection.Add(ty * a.width + tx);
            }
        _state.Tool = PaintTool.Select;
        _panel.SyncFromState();
        _selectionDirty = true;
        if (result.changed.Count > 0) { _doc.Touch(); PublishDraft(result.changed); Republish(false); }
        else _doc.DiscardEdit();
        UpdateSelectionInfo();
    }

    private void MoveSelectionBy(int dx, int dy)
    {
        if (_doc == null || _selection.Count == 0) return;
        var a = _doc.Artifact;
        int w = a.width;
        int minX = _selection.Min(i => i % w), maxX = _selection.Max(i => i % w), minY = _selection.Min(i => i / w), maxY = _selection.Max(i => i / w);
        dx = Math.Clamp(dx, -minX, a.width - 1 - maxX);
        dy = Math.Clamp(dy, -minY, a.height - 1 - maxY);
        if (dx == 0 && dy == 0) return;
        var stamp = TerrainEditor.copyTerrainSelectionStamp(a, _selection);
        if (stamp == null) return;
        _doc.BeginEdit();
        var cleared = TerrainEditor.clearTerrainSelection(a, _selection);
        var placed = TerrainEditor.applyTerrainStamp(a, stamp, minX + dx, minY + dy);
        var moved = _selection.Select(i => (i / w + dy) * w + i % w + dx).ToList();
        _selection.Clear();
        _selection.AddRange(moved);
        _doc.Touch();
        PublishDraft(cleared.changed.Concat(placed.changed).Distinct().ToList());
        Republish(false);
        _selectionDirty = true;
        UpdateSelectionInfo();
    }

    // ── undo ────────────────────────────────────────────────────────────────────────────────────────────────

    public void Undo()
    {
        if (_doc == null || _drawing) return;
        if (!_doc.Undo()) { _main.Toast(T("Nothing to undo"), StudioTheme.TextMuted, 1.2); return; }
        AfterHistory();
    }

    public void Redo()
    {
        if (_doc == null || _drawing) return;
        if (!_doc.Redo()) { _main.Toast(T("Nothing to redo"), StudioTheme.TextMuted, 1.2); return; }
        AfterHistory();
    }

    private void AfterHistory()
    {
        _selection.Clear();
        _selectionDirty = true;
        bool sameShape = _layout != null && _layout.width == _doc!.Artifact.width && _layout.height == _doc.Artifact.height && _layout.biomeKey == _doc.Artifact.biomeKey;
        Republish(!sameShape, keepCamera: true);
    }

    // ── keys ────────────────────────────────────────────────────────────────────────────────────────────────

    public bool Key(InputEventKey e)
    {
        bool ctrl = e.CtrlPressed || e.MetaPressed;
        if (ctrl)
        {
            switch (e.Keycode)
            {
                case global::Godot.Key.C: Copy(); return true;
                case global::Godot.Key.X: Cut(); return true;
                case global::Godot.Key.V: Paste(); return true;
                case global::Godot.Key.A: SelectAll(); return true;
                case global::Godot.Key.N: ShowNewMap(); return true;
            }
            return false;
        }
        if (_selection.Count > 0)
        {
            int step = e.ShiftPressed ? 5 : 1;
            switch (e.Keycode)
            {
                case global::Godot.Key.Left: MoveSelectionBy(-step, 0); return true;
                case global::Godot.Key.Right: MoveSelectionBy(step, 0); return true;
                case global::Godot.Key.Up: MoveSelectionBy(0, -step); return true;
                case global::Godot.Key.Down: MoveSelectionBy(0, step); return true;
                case global::Godot.Key.Delete or global::Godot.Key.Backspace: ClearSelection(); return true;
                case global::Godot.Key.Escape: Deselect(); return true;
            }
        }
        switch (e.Keycode)
        {
            case global::Godot.Key.B: SetRail(PaintRail.Terrain); return true;
            case global::Godot.Key.R: SetRail(PaintRail.Sculpt); return true;
            case global::Godot.Key.T: SetRail(PaintRail.Nature); return true;
            case global::Godot.Key.H: SetRail(PaintRail.Build); return true;
            case global::Godot.Key.P: SetRail(PaintRail.Theme); return true;
            case global::Godot.Key.M: SetRail(PaintRail.Select); return true;
            case global::Godot.Key.I: SetTool(PaintTool.Picker); return true;
            case global::Godot.Key.F: SetTool(_state.Tool == PaintTool.Fill ? PaintTool.Paint : PaintTool.Fill); return true;
            case global::Godot.Key.G: SetTool(PaintTool.Smooth); return true;
            case global::Godot.Key.Bracketleft: SetBrush(size: _state.BrushSize - (_state.BrushSize > 8 ? 4 : 2)); return true;
            case global::Godot.Key.Bracketright: SetBrush(size: _state.BrushSize + (_state.BrushSize >= 8 ? 4 : 2)); return true;
            case global::Godot.Key.Escape when _drawing: _drawing = false; EndStroke(); return true;
            case global::Godot.Key.Escape when _state.Tool == PaintTool.Picker: SetTool(_toolBeforePick); return true;
        }
        return false;
    }

    // ── overlays ────────────────────────────────────────────────────────────────────────────────────────────

    private double CellHeightPx(int tx, int ty)
    {
        if (_layout == null) return 0;
        return _main.View.SurfaceHeightPx(_layout.originX + (tx + 0.5) * _layout.tileSize, _layout.originY + (ty + 0.5) * _layout.tileSize);
    }

    private void RebuildChecks()
    {
        _checksDirty = false;
        if (_checks == null || _layout == null || _analysis == null) return;
        var kinds = _state.Overlays & ~OverlayKind.Probes;
        if (kinds == OverlayKind.None) { _checks.Clear(); return; }
        var inputs = OverlayInputs.Create(_layout, _analysis, _validation);
        var shapes = new List<CellOverlay.Shape>();
        var marks = new OverlayMark[OverlayPalette.MAX_MARKS_PER_CELL];
        double ts = _layout.tileSize;
        for (int ty = 0; ty < _layout.height; ty++)
            for (int tx = 0; tx < _layout.width; tx++)
            {
                int count = OverlayPalette.cellMarks(kinds, inputs, tx, ty, marks);
                if (count == 0) continue;
                double h = CellHeightPx(tx, ty);
                double x0 = _layout.originX + tx * ts, y0 = _layout.originY + ty * ts;
                for (int k = 0; k < count; k++)
                {
                    var mark = marks[k];
                    var colour = new Color((uint)((mark.color << 8) | 0xff)).SrgbToLinear();
                    colour.A = Math.Clamp(mark.alpha * 1.15f, 0, 0.9f);
                    switch (mark.shape)
                    {
                        case OverlayShape.Circle:
                        {
                            double r = mark.radiusFactor * ts, cx = x0 + ts / 2, cy = y0 + ts / 2;
                            shapes.Add(new CellOverlay.Shape(CellOverlay.ShapeKind.Circle, cx - r, cy - r, cx + r, cy + r, h, colour));
                            break;
                        }
                        case OverlayShape.RectStroke:
                            shapes.Add(new CellOverlay.Shape(CellOverlay.ShapeKind.Stroke, x0 + mark.inset, y0 + mark.inset, x0 + ts - mark.inset, y0 + ts - mark.inset, h, colour, ts * 0.06));
                            break;
                        default:
                            shapes.Add(new CellOverlay.Shape(CellOverlay.ShapeKind.Rect, x0 + mark.inset, y0 + mark.inset, x0 + ts - mark.inset, y0 + ts - mark.inset, h, colour));
                            break;
                    }
                }
            }
        _checks.SetShapes(shapes, _main.View.Renderer.CompileToWorld);
    }

    private void RebuildPreview()
    {
        _previewDirty = false;
        if (_editPreview == null || _doc == null || _layout == null) return;
        if (_preview.Count == 0 || !ShowStrokePreview) { _editPreview.Clear(); return; }
        var a = _doc.Artifact;
        double ts = _layout.tileSize;
        var quads = new List<CellOverlay.Quad>(_preview.Count);
        int limit = 40000;
        foreach (int i in _preview)
        {
            if (--limit < 0) break;
            int tx = i % a.width, ty = i / a.width, tile = a.baseTiles[i];
            int level = TerrainEditor.terrainEditorHeightFromStored(tile, a.elevation?[i] ?? 0);
            var colour = new Color((uint)((OverlayPalette.tileCardColor(tile) << 8) | 0xff)).SrgbToLinear();
            colour.A = 0.5f;
            quads.Add(new CellOverlay.Quad(_layout.originX + tx * ts, _layout.originY + ty * ts, ts, level * TerrainProjection.TERRAIN_ELEVATION_STEP_PX, colour));
        }
        _editPreview.SetQuads(quads, _main.View.Renderer.CompileToWorld, 0.02);
    }

    private void RebuildSelection()
    {
        _selectionDirty = false;
        if (_selectionOverlay == null || _doc == null || _layout == null) return;
        if (_selection.Count == 0) { _selectionOverlay.Clear(); return; }
        int w = _doc.Artifact.width, h = _doc.Artifact.height;
        double ts = _layout.tileSize;
        var colour = new Color(0.35f, 0.85f, 1f, 0.34f);
        var quads = new List<CellOverlay.Quad>(_selection.Count);
        foreach (int i in _selection)
        {
            int tx = i % w + (_moving ? _moveDx : 0), ty = i / w + (_moving ? _moveDy : 0);
            if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
            quads.Add(new CellOverlay.Quad(_layout.originX + tx * ts, _layout.originY + ty * ts, ts, CellHeightPx(i % w, i / w), colour));
        }
        _selectionOverlay.SetQuads(quads, _main.View.Renderer.CompileToWorld, 0.0);
    }

    // ── cursor (2D) ─────────────────────────────────────────────────────────────────────────────────────────

    public void Draw(ViewportInput canvas)
    {
        if (_doc == null || _layout == null || !_hoverValid || _moving) return;
        string tool = _state.Tool;
        var a = _doc.Artifact;
        var cells = new HashSet<int>();
        var roles = new Dictionary<int, Color>();
        Color colour = StudioTheme.Accent;
        switch (tool)
        {
            case PaintTool.Picker:
            case PaintTool.Select:
            case PaintTool.Fill:
                cells.Add(_hoverY * a.width + _hoverX);
                colour = tool == PaintTool.Select ? new Color("5bd9ff") : StudioTheme.Mint;
                break;
            case PaintTool.Smooth:
                foreach (int i in TerrainEditor.terrainSmoothCells(a, new TerrainSmoothEdit
                    { tx = _hoverX, ty = _hoverY, brush = _state.Brush, affect = TerrainEditAffect.Walkable, quantum = _state.SmoothQuantum }))
                    cells.Add(i);
                colour = new Color("9ad0ff");
                break;
            case PaintTool.Structure:
            {
                TerrainEditor.ensureTerrainEditorLayers(a);
                var plan = TerrainDepthStructures.planTerrainDepthStructure(a.baseTiles, a.elevation!, a.width, a.height,
                    _state.StructureKind, _hoverX, _hoverY,
                    new TerrainDepthStructureOptions { passageAxis = _state.StructureAxis == "auto" ? null : _state.StructureAxis, span = _state.StructureSpan });
                if (plan.plan != null)
                    foreach (var cell in plan.plan.cells)
                    {
                        cells.Add(cell.index);
                        roles[cell.index] = cell.role switch
                        {
                            TerrainDepthStructureRole.Feature => new Color("62f1bd"),
                            TerrainDepthStructureRole.Approach => new Color("7dd3fc"),
                            _ => new Color("ffd23f"),
                        };
                    }
                else { cells.Add(_hoverY * a.width + _hoverX); roles[_hoverY * a.width + _hoverX] = new Color("ff2d55"); }
                break;
            }
            default:
                foreach (var cell in TerrainEditor.terrainBrushCells(a.width, a.height, _hoverX, _hoverY, _state.Brush)) cells.Add(cell.index);
                colour = tool switch
                {
                    PaintTool.Erase or PaintTool.DecorationErase => StudioTheme.Danger,
                    PaintTool.Theme => new Color((uint)((Fluitown.Render.Theme.biomeForKey(_state.ThemeKey).groundAccentA << 8) | 0xff)).Lightened(0.2f),
                    PaintTool.Decorate => new Color("7ee08a"),
                    PaintTool.Raise => new Color("8ff0c8"),
                    PaintTool.Lower => new Color("ffab70"),
                    PaintTool.Flatten => new Color("c9d6ff"),
                    _ => new Color((uint)((OverlayPalette.tileCardColor(_state.Tile) << 8) | 0xff)).Lightened(0.15f),
                };
                break;
        }
        DrawCells(canvas, cells, roles, colour, a.width);
    }

    private void DrawCells(ViewportInput canvas, HashSet<int> cells, Dictionary<int, Color> roles, Color colour, int width)
    {
        var view = _main.View;
        double ts = _layout!.tileSize, ox = _layout.originX, oy = _layout.originY;
        bool fill = cells.Count <= 1600;
        var ink = new Color(0, 0, 0, 0.55f);
        var corners = new Vector2[4];
        foreach (int i in cells)
        {
            int tx = i % width, ty = i / width;
            double h = CellHeightPx(tx, ty);
            double x0 = ox + tx * ts, y0 = oy + ty * ts, x1 = x0 + ts, y1 = y0 + ts;
            corners[0] = view.WorldToScreen(x0, y0, h);
            corners[1] = view.WorldToScreen(x1, y0, h);
            corners[2] = view.WorldToScreen(x1, y1, h);
            corners[3] = view.WorldToScreen(x0, y1, h);
            var c = roles.TryGetValue(i, out var role) ? role : colour;
            if (fill) canvas.DrawColoredPolygon(corners, new Color(c, 0.2f));
            bool top = !cells.Contains(i - width) || ty == 0, bottom = !cells.Contains(i + width), left = tx == 0 || !cells.Contains(i - 1), right = tx == width - 1 || !cells.Contains(i + 1);
            void Edge(int from, int to)
            {
                canvas.DrawLine(corners[from], corners[to], ink, 4f, true);
                canvas.DrawLine(corners[from], corners[to], c, 2f, true);
            }
            if (top) Edge(0, 1);
            if (right) Edge(1, 2);
            if (bottom) Edge(2, 3);
            if (left) Edge(3, 0);
        }
    }

    // ── names ───────────────────────────────────────────────────────────────────────────────────────────────

    public static string BlockName(int tile) => tile switch
    {
        TileType.Solid => T("Rock"),
        TileType.Floor => T("Ground"),
        TileType.Water => T("Water"),
        TileType.Bridge => T("Bridge"),
        TileType.Chasm => T("Chasm"),
        TileType.Cleft => T("Cliff pass"),
        TileType.Underpass => T("Rope bridge"),
        _ => "?",
    };

    public void Persist(StudioSettings settings)
    {
        if (_doc == null) return;
        try { File.WriteAllText(AutosavePath, DocumentJson(pretty: false)); }
        catch { /* best effort on exit */ }
    }
}
