using System;
using Godot;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio.Ui;

/// <summary>The three modes of the studio.</summary>
public enum StudioMode { Paint = 0, Generate = 1, World = 2 }

/// <summary>
/// The studio shell. The terrain fills the window; everything else floats over it as quiet glass:
/// <list type="bullet">
/// <item>top left: the File menu and, while painting, the map's name and its check status;</item>
/// <item>top centre: the mode switch (Paint · Generate · Explore);</item>
/// <item>top right: undo/redo (painting), the Style drawer, settings and help;</item>
/// <item>left: the mode's tool rail (painting) and its context panel, as tall as its content;</item>
/// <item>right: the Style drawer (colours, light, season, weather);</item>
/// <item>bottom: a one-line hint for the current tool, the view controls and toasts.</item>
/// </list>
/// The area not covered by a panel is <see cref="Viewport"/>, which receives the pointer.
/// </summary>
public sealed partial class StudioUi : Control
{
    public const float Margin = 12, BarHeight = 46, PanelWidth = 300, StyleWidth = 328, Gap = 8;

    public readonly ViewportInput Viewport = new() { Name = "ViewportInput" };
    public readonly Toasts Toasts = new();
    public readonly CinematicCard Cinematic = new();
    public readonly SegmentedTabs Modes = new();
    public readonly MenuButton FileMenu = new();
    public readonly LineEdit MapName = new();
    public readonly Button ChecksChip = new();
    public readonly FloatingPanel Left = new(), Right = new();
    public Button Undo { get; private set; } = null!;
    public Button Redo { get; private set; } = null!;
    public Button StyleButton { get; private set; } = null!;
    public Button SettingsButton { get; private set; } = null!;
    public Button HelpButton { get; private set; } = null!;
    public Button ZoomIn { get; private set; } = null!;
    public Button ZoomOut { get; private set; } = null!;
    public Button FitButton { get; private set; } = null!;
    public Button OrbitButton { get; private set; } = null!;

    private readonly PanelContainer _leftBar = new(), _modeBar = new(), _rightBar = new(), _viewBar = new(), _hintBar = new();
    private readonly Button _showUi = new();
    private readonly Label _dirty = W.Label("●", 12, StudioTheme.Accent);
    private readonly HBoxContainer _document = new();
    private readonly Label _hint = W.Label("", 12, StudioTheme.TextMuted);
    private readonly Label _cursor = W.Label("", 12, StudioTheme.TextFaint);
    private readonly HBoxContainer _busy = new();
    private readonly Label _busyDot = W.Label("●", 11, StudioTheme.Mint);
    private readonly Control?[] _panels = new Control?[3];
    private readonly Control?[] _rails = new Control?[3];
    private bool _panelsVisible = true, _styleOpen, _busyShown;
    private double _clock;

    public event Action<StudioMode>? ModeSelected;
    public event Action<bool>? StyleToggled;

    public StudioMode Mode { get; private set; } = StudioMode.Paint;
    /// <summary>Whether the bottom hint line may show (scripted recordings turn it off).</summary>
    public bool HintVisible { get; set; } = true;

    public override void _Ready()
    {
        Theme = StudioTheme.Build();
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        Viewport.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(Viewport);

        BuildLeftBar();
        BuildModeBar();
        BuildRightBar();
        BuildViewBar();
        BuildHintBar();

        Left.Name = "ContextPanel";
        AddChild(Left);
        Right.Name = "StyleDrawer";
        Right.Visible = false;
        AddChild(Right);

        AddChild(Cinematic);
        AddChild(Toasts);

        _showUi.Text = T("Show interface");
        _showUi.Icon = Icons.Get(IconKind.Eye, 16);
        _showUi.TooltipText = T("Show the panels again (Tab)");
        _showUi.FocusMode = FocusModeEnum.None;
        _showUi.AddThemeStyleboxOverride("normal", StudioTheme.Glass(12, 10));
        _showUi.AddThemeStyleboxOverride("hover", StudioTheme.Glass(12, 10));
        _showUi.Pressed += () => PanelsVisible = true;
        _showUi.Visible = false;
        AddChild(_showUi);
    }

    private static PanelContainer Bar(PanelContainer bar, string name, int padX = 6)
    {
        bar.Name = name;
        var box = StudioTheme.Glass(13, padX);
        box.ContentMarginTop = box.ContentMarginBottom = 5;
        bar.AddThemeStyleboxOverride("panel", box);
        bar.CustomMinimumSize = new Vector2(0, BarHeight);
        bar.MouseFilter = MouseFilterEnum.Stop;
        return bar;
    }

    private void BuildLeftBar()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        Bar(_leftBar, "LeftBar").AddChild(row);
        AddChild(_leftBar);

        var logo = new TextureRect
        {
            Texture = Icons.Get(IconKind.Mountain, 22, StudioTheme.Accent),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            CustomMinimumSize = new Vector2(30, 0),
            TooltipText = "Cartoon Terrain Studio",
            MouseFilter = MouseFilterEnum.Pass,
        };
        row.AddChild(logo);

        FileMenu.Text = T("File");
        FileMenu.Icon = Icons.Get(IconKind.ChevronDown, 14, StudioTheme.TextMuted);
        FileMenu.IconAlignment = HorizontalAlignment.Right;
        FileMenu.FocusMode = FocusModeEnum.None;
        FileMenu.Flat = false;
        FileMenu.SwitchOnHover = true;
        FileMenu.MouseDefaultCursorShape = CursorShape.PointingHand;
        row.AddChild(FileMenu);

        _document.AddThemeConstantOverride("separation", 6);
        _document.AddChild(new VSeparator());
        MapName.Flat = true;
        MapName.CustomMinimumSize = new Vector2(150, 0);
        MapName.ExpandToTextLength = true;
        MapName.TooltipText = T("Name of the map — click to rename");
        MapName.AddThemeFontSizeOverride("font_size", 14);
        MapName.AddThemeStyleboxOverride("normal", StudioTheme.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 8, 0, 6, 4));
        MapName.AddThemeStyleboxOverride("focus", StudioTheme.Box(StudioTheme.Field, new Color(StudioTheme.Accent, 0.7f), 8, 1, 6, 4));
        _document.AddChild(MapName);
        _dirty.TooltipText = T("Unsaved changes");
        _dirty.MouseFilter = MouseFilterEnum.Pass;
        _dirty.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _document.AddChild(_dirty);
        ChecksChip.FocusMode = FocusModeEnum.None;
        ChecksChip.MouseDefaultCursorShape = CursorShape.PointingHand;
        ChecksChip.AddThemeFontSizeOverride("font_size", 12);
        _document.AddChild(ChecksChip);
        row.AddChild(_document);
        SetChecks(0, 0, checking: true);
    }

    private void BuildModeBar()
    {
        Bar(_modeBar, "ModeBar", 4).AddChild(Modes);
        Modes.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        Modes.Add(T("Paint"), IconKind.Brush, T("Paint your own map with brushes (1)"));
        Modes.Add(T("Generate"), IconKind.Sparkle, T("Let the studio compose a complete map (2)"));
        Modes.Add(T("Explore"), IconKind.Globe, T("Wander endless worlds grown from a seed (3)"));
        Modes.Selected += index => ModeSelected?.Invoke((StudioMode)index);
        Modes.Set(0);
        AddChild(_modeBar);
    }

    private void BuildRightBar()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 2);
        Bar(_rightBar, "RightBar").AddChild(row);
        AddChild(_rightBar);
        Undo = W.IconButton(IconKind.Undo, T("Undo (Ctrl+Z)"), () => { }, 36, 18);
        Redo = W.IconButton(IconKind.Redo, T("Redo (Ctrl+Y)"), () => { }, 36, 18);
        row.AddChild(Undo);
        row.AddChild(Redo);
        row.AddChild(new VSeparator());
        StyleButton = W.Button(T("Style"), IconKind.Palette, () => StyleOpen = !StyleOpen, T("Colours, light, season and weather of the terrain"), filled: false);
        StyleButton.ToggleMode = true;
        row.AddChild(StyleButton);
        SettingsButton = W.IconButton(IconKind.Gear, T("Settings"), () => { }, 36, 18);
        HelpButton = W.IconButton(IconKind.Help, T("Help and shortcuts (F1)"), () => { }, 36, 18);
        row.AddChild(SettingsButton);
        row.AddChild(HelpButton);
    }

    private void BuildViewBar()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 2);
        Bar(_viewBar, "ViewBar").AddChild(row);
        AddChild(_viewBar);
        ZoomOut = W.IconButton(IconKind.Minus, T("Zoom out (−)"), () => { }, 34, 16);
        ZoomIn = W.IconButton(IconKind.Plus, T("Zoom in (+)"), () => { }, 34, 16);
        FitButton = W.IconButton(IconKind.Fit, T("Show the whole map (Home)"), () => { }, 34, 16);
        OrbitButton = W.IconButton(IconKind.Cube, T("3D view: orbit around the terrain (V)"), () => { }, 34, 16);
        OrbitButton.ToggleMode = true;
        row.AddChild(ZoomOut);
        row.AddChild(ZoomIn);
        row.AddChild(FitButton);
        row.AddChild(new VSeparator());
        row.AddChild(OrbitButton);
    }

    private void BuildHintBar()
    {
        var box = StudioTheme.Glass(11, 12);
        box.ContentMarginTop = box.ContentMarginBottom = 6;
        box.BgColor = new Color("14171dd8");
        _hintBar.AddThemeStyleboxOverride("panel", box);
        _hintBar.MouseFilter = MouseFilterEnum.Ignore;
        _hintBar.Name = "HintBar";
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        _busy.AddThemeConstantOverride("separation", 6);
        _busy.AddChild(_busyDot);
        _busy.AddChild(W.Label(T("Building terrain…"), 12, StudioTheme.Text));
        _busy.Visible = false;
        row.AddChild(_busy);
        row.AddChild(_hint);
        row.AddChild(_cursor);
        _hintBar.AddChild(row);
        AddChild(_hintBar);
    }

    // ── content ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Installs a mode's context panel and, optionally, its tool rail.</summary>
    public void SetModePanel(StudioMode mode, Control panel, Control? rail = null)
    {
        _panels[(int)mode] = panel;
        _rails[(int)mode] = rail;
        if (rail != null)
        {
            rail.Visible = false;
            AddChild(rail);
            MoveChild(rail, Left.GetIndex());
        }
    }

    /// <summary>Installs the Style drawer's content.</summary>
    public void SetStylePanel(Control panel) => Right.Content = panel;

    public void ShowMode(StudioMode mode)
    {
        Mode = mode;
        Modes.Set((int)mode);
        Left.Content = _panels[(int)mode];
        bool paint = mode == StudioMode.Paint;
        _document.Visible = paint;
        Undo.Visible = paint;
        Redo.Visible = paint;
        FitButton.Visible = mode != StudioMode.World;
        ApplyVisibility();
    }

    /// <summary>Whether the Style drawer is open.</summary>
    public bool StyleOpen
    {
        get => _styleOpen;
        set
        {
            if (_styleOpen == value) { StyleButton.SetPressedNoSignal(value); return; }
            _styleOpen = value;
            StyleButton.SetPressedNoSignal(value);
            ApplyVisibility();
            StyleToggled?.Invoke(value);
        }
    }

    /// <summary>Whether the panels are shown (Tab hides them for a clean view).</summary>
    public bool PanelsVisible
    {
        get => _panelsVisible;
        set
        {
            _panelsVisible = value;
            ApplyVisibility();
        }
    }

    private void ApplyVisibility()
    {
        bool on = _panelsVisible;
        foreach (var control in new Control[] { _leftBar, _modeBar, _rightBar, _viewBar, _hintBar, Left }) control.Visible = on;
        Right.Visible = on && _styleOpen;
        for (int i = 0; i < _rails.Length; i++)
            if (_rails[i] != null) _rails[i]!.Visible = on && i == (int)Mode;
        _showUi.Visible = !on;
    }

    /// <summary>Whether a modal card is open (the studio's keys stay off).</summary>
    public bool ModalOpen
    {
        get
        {
            foreach (var child in GetChildren())
                if (child is Modal) return true;
            return false;
        }
    }

    // ── document header (painting) ──────────────────────────────────────────────────────────────────────────

    public void SetDocument(string name, bool dirty)
    {
        if (!MapName.HasFocus() && MapName.Text != name) MapName.Text = name;
        _dirty.Visible = dirty;
    }

    public void SetChecks(int errors, int warnings, bool checking = false)
    {
        if (checking)
        {
            ChecksChip.Text = T("Checking…");
            ChecksChip.Icon = Icons.Get(IconKind.Clock, 14, StudioTheme.TextMuted);
            ChecksChip.TooltipText = T("The map is being checked");
            ChecksChip.AddThemeColorOverride("font_color", StudioTheme.TextMuted);
            return;
        }
        if (errors > 0)
        {
            ChecksChip.Text = errors == 1 ? T("1 problem") : F("{0} problems", errors);
            ChecksChip.Icon = Icons.Get(IconKind.Warning, 14, StudioTheme.Danger);
            ChecksChip.AddThemeColorOverride("font_color", new Color("ff9aa4"));
        }
        else
        {
            ChecksChip.Text = T("Map OK");
            ChecksChip.Icon = Icons.Get(IconKind.Check, 14, StudioTheme.Ok);
            ChecksChip.AddThemeColorOverride("font_color", StudioTheme.TextMuted);
        }
        ChecksChip.TooltipText = T("Check the map: problems, hints and map overlays")
            + (errors == 0 && warnings > 0 ? "\n" + (warnings == 1 ? T("1 hint") : F("{0} hints", warnings)) : "");
    }

    // ── status ──────────────────────────────────────────────────────────────────────────────────────────────

    public void SetHint(string text) => _hint.Text = text;
    public void SetCursor(string text) => _cursor.Text = text;
    public void SetBusy(bool busy) => _busyShown = busy;

    // ── layout ──────────────────────────────────────────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        _clock += delta;
        var size = Size;
        foreach (var bar in new Control[] { _leftBar, _modeBar, _rightBar, _viewBar, _hintBar }) bar.ResetSize();

        _leftBar.Position = new Vector2(Margin, Margin);
        _rightBar.Position = new Vector2(size.X - Margin - _rightBar.Size.X, Margin);
        float modeX = Math.Max((size.X - _modeBar.Size.X) / 2, _leftBar.Position.X + _leftBar.Size.X + Gap);
        modeX = Math.Min(modeX, _rightBar.Position.X - Gap - _modeBar.Size.X);
        _modeBar.Position = new Vector2(Math.Max(Margin, modeX), Margin);

        float top = Margin + BarHeight + 10;
        float x = Margin;
        var rail = _rails[(int)Mode];
        if (rail is { Visible: true })
        {
            rail.ResetSize();
            rail.Position = new Vector2(Margin, top);
            x += rail.Size.X + Gap;
        }
        Left.Fit(new Vector2(x, top), PanelWidth, size.Y - top - Margin);

        _viewBar.Position = new Vector2(size.X - Margin - _viewBar.Size.X, size.Y - Margin - _viewBar.Size.Y);
        if (Right.Visible) Right.Fit(new Vector2(size.X - Margin - StyleWidth, top), StyleWidth, _viewBar.Position.Y - Gap - top);

        float freeLeft = Left.Visible ? Left.Position.X + Left.Size.X + Gap : Margin;
        float freeRight = _viewBar.Visible ? _viewBar.Position.X - Gap : size.X - Margin;
        _busy.Visible = _busyShown;
        if (_busyShown) _busyDot.Modulate = new Color(1, 1, 1, 0.45f + 0.55f * (float)Math.Abs(Math.Sin(_clock * 3)));
        _hintBar.Visible = _panelsVisible && HintVisible && (_hint.Text.Length > 0 || _busyShown || _cursor.Text.Length > 0);
        float hintWidth = Math.Min(_hintBar.Size.X, Math.Max(120, freeRight - freeLeft));
        float hintX = Math.Clamp((freeLeft + freeRight - hintWidth) / 2, freeLeft, Math.Max(freeLeft, freeRight - hintWidth));
        _hintBar.Position = new Vector2(hintX, size.Y - Margin - _hintBar.Size.Y);
        float above = _hintBar.Visible ? _hintBar.Position.Y - Gap : size.Y - Margin;

        Cinematic.ResetSize();
        Cinematic.Position = new Vector2((freeLeft + freeRight - Cinematic.Size.X) / 2, above - Cinematic.Size.Y - 6);
        Toasts.Size = new Vector2(560, 220);
        Toasts.Position = new Vector2((freeLeft + freeRight - 560) / 2, above - 220 - (Cinematic.Visible ? Cinematic.Size.Y + 14 : 0));

        _showUi.ResetSize();
        _showUi.Position = new Vector2(size.X - Margin - _showUi.Size.X, Margin);
    }
}

/// <summary>
/// The transparent control over the terrain: it owns the pointer there, forwards the gestures to the active tool and
/// draws the tools' 2D overlays (brush outline, selection, markers).
/// </summary>
public sealed partial class ViewportInput : Control
{
    public event Action<InputEvent>? Input;
    public event Action<ViewportInput>? Drawing;

    public ViewportInput()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.Click;
    }

    public override void _GuiInput(InputEvent @event)
    {
        Input?.Invoke(@event);
        AcceptEvent();
    }

    public override void _Draw() => Drawing?.Invoke(this);

    public override void _Process(double delta) => QueueRedraw();
}
