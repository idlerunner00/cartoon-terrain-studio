using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace TerrainStudio.Ui;

/// <summary>Small factory helpers shared by every panel.</summary>
public static class W
{
    public static Label Label(string text, int size = StudioTheme.FontSize, Color? color = null, bool wrap = false)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c) label.AddThemeColorOverride("font_color", c);
        if (wrap)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(10, 0);
        }
        return label;
    }

    /// <summary>A panel title.</summary>
    public static Label Title(string text) => Label(text, 16, StudioTheme.Text);

    /// <summary>Quiet explanatory text.</summary>
    public static Label Hint(string text) => Label(text, 12, StudioTheme.TextMuted, wrap: true);

    /// <summary>A small uppercase heading above a group of controls.</summary>
    public static Label Caption(string text) => Label(text.ToUpperInvariant(), 11, StudioTheme.TextFaint);

    public static Button Button(string text, IconKind? icon = null, Action? pressed = null, string? tooltip = null, bool filled = true)
    {
        var button = new Button
        {
            Text = text,
            TooltipText = tooltip ?? "",
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        if (filled)
        {
            button.AddThemeStyleboxOverride("normal", StudioTheme.Box(StudioTheme.Raised, new Color(0, 0, 0, 0), 9, 0, 12, 8));
            button.AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Hover, new Color(0, 0, 0, 0), 9, 0, 12, 8));
        }
        if (icon is { } kind) button.Icon = Icons.Get(kind, 16);
        if (pressed != null) button.Pressed += pressed;
        return button;
    }

    /// <summary>The one main action of a panel, in the accent colour.</summary>
    public static Button Primary(string text, IconKind? icon = null, Action? pressed = null, string? tooltip = null)
    {
        var button = Button(text, null, pressed, tooltip, filled: false);
        if (icon is { } kind) button.Icon = Icons.Get(kind, 18, StudioTheme.Ink);
        button.AddThemeStyleboxOverride("normal", StudioTheme.Box(StudioTheme.Accent, StudioTheme.Accent, 11, 0, 16, 11));
        button.AddThemeStyleboxOverride("hover", StudioTheme.Box(new Color("ffc573"), new Color(0, 0, 0, 0), 11, 0, 16, 11));
        button.AddThemeStyleboxOverride("pressed", StudioTheme.Box(StudioTheme.AccentDeep, new Color(0, 0, 0, 0), 11, 0, 16, 11));
        button.AddThemeStyleboxOverride("hover_pressed", StudioTheme.Box(StudioTheme.AccentDeep, new Color(0, 0, 0, 0), 11, 0, 16, 11));
        button.AddThemeStyleboxOverride("disabled", StudioTheme.Box(new Color("5a4524"), new Color(0, 0, 0, 0), 11, 0, 16, 11));
        foreach (var name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
            button.AddThemeColorOverride(name, StudioTheme.Ink);
        foreach (var name in new[] { "icon_normal_color", "icon_hover_color", "icon_pressed_color", "icon_hover_pressed_color" })
            button.AddThemeColorOverride(name, StudioTheme.Ink);
        button.AddThemeColorOverride("font_disabled_color", new Color("a38a62"));
        button.AddThemeFontSizeOverride("font_size", 14);
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return button;
    }

    /// <summary>A square icon-only button.</summary>
    public static Button IconButton(IconKind icon, string tooltip, Action pressed, int size = 36, int iconSize = 18)
    {
        var button = new Button
        {
            Icon = Icons.Get(icon, iconSize),
            TooltipText = tooltip,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(size, size),
            IconAlignment = HorizontalAlignment.Center,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        button.Pressed += pressed;
        return button;
    }

    public static HBoxContainer Row(params Control[] children)
    {
        var row = new HBoxContainer();
        foreach (var child in children) row.AddChild(child);
        return row;
    }

    public static VBoxContainer Column(int separation = 8)
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", separation);
        return column;
    }

    public static Control Spacer(float height = 4) => new Control { CustomMinimumSize = new Vector2(0, height), MouseFilter = Control.MouseFilterEnum.Ignore };

    public static Control Expand() => new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

    public static OptionButton Options(IReadOnlyList<(string label, string key)> items, string selectedKey, Action<string> changed)
    {
        var options = new OptionButton { FocusMode = Control.FocusModeEnum.None, FitToLongestItem = false, ClipText = true };
        for (int i = 0; i < items.Count; i++)
        {
            options.AddItem(items[i].label, i);
            options.SetItemMetadata(i, items[i].key);
            if (items[i].key == selectedKey) options.Selected = i;
        }
        options.ItemSelected += index => changed(options.GetItemMetadata((int)index).AsString());
        options.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        options.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        return options;
    }

    public static void Select(OptionButton options, string key)
    {
        for (int i = 0; i < options.ItemCount; i++)
            if (options.GetItemMetadata(i).AsString() == key) { options.Selected = i; return; }
    }

    public static CheckButton Toggle(string text, bool value, Action<bool> changed, string? tooltip = null)
    {
        var toggle = new CheckButton
        {
            Text = text,
            ButtonPressed = value,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = tooltip ?? "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        toggle.Toggled += on => changed(on);
        return toggle;
    }

    /// <summary>A labelled field: caption on the left, control filling the rest.</summary>
    public static HBoxContainer Field(string caption, Control control, float captionWidth = 84)
    {
        var label = Label(caption, 13, StudioTheme.TextMuted);
        label.CustomMinimumSize = new Vector2(captionWidth, 0);
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return Row(label, control);
    }

    /// <summary>A titled block of controls (not collapsible).</summary>
    public static VBoxContainer Group(string caption, params Control[] children)
    {
        var group = Column(8);
        if (caption.Length > 0) group.AddChild(Caption(caption));
        foreach (var child in children) group.AddChild(child);
        return group;
    }

    public static Color Rgb(int hex) => new((uint)((hex << 8) | 0xff));

    /// <summary>Lets a button share a row: it fills its part and shortens its text with "…" when space is short.</summary>
    public static Button Fill(Button button)
    {
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return button;
    }
}

/// <summary>An expandable block for options most people never need ("More", "Advanced").</summary>
public sealed partial class Section : VBoxContainer
{
    public readonly VBoxContainer Body;
    private readonly Button _header;
    private bool _open;
    public event Action<bool>? Toggled;

    public Section(string title, bool open = false, IconKind? icon = null)
    {
        AddThemeConstantOverride("separation", 8);
        _header = new Button
        {
            Text = title,
            Alignment = HorizontalAlignment.Left,
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            IconAlignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _header.AddThemeFontSizeOverride("font_size", 13);
        _header.AddThemeColorOverride("font_color", StudioTheme.TextMuted);
        _header.AddThemeColorOverride("font_pressed_color", StudioTheme.TextMuted);
        _header.AddThemeColorOverride("font_hover_color", StudioTheme.Text);
        _header.AddThemeStyleboxOverride("normal", StudioTheme.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 8, 0, 4, 6));
        _header.AddThemeStyleboxOverride("pressed", StudioTheme.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 8, 0, 4, 6));
        _header.AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Raised, new Color(0, 0, 0, 0), 8, 0, 4, 6));
        _header.Pressed += () => { Open = !Open; Toggled?.Invoke(Open); };
        AddChild(_header);
        Body = W.Column(10);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 4);
        margin.AddChild(Body);
        AddChild(margin);
        Open = open;
    }

    public bool Open
    {
        get => _open;
        set
        {
            _open = value;
            Body.GetParent<Control>().Visible = value;
            _header.Icon = Icons.Get(value ? IconKind.ChevronDown : IconKind.ChevronRight, 14, StudioTheme.TextMuted);
        }
    }

    public T Add<T>(T control) where T : Control
    {
        Body.AddChild(control);
        return control;
    }
}

/// <summary>A labelled slider with its value; double-click resets it.</summary>
public sealed partial class SliderRow : VBoxContainer
{
    public readonly HSlider Slider;
    private readonly Label _value;
    private readonly Func<double, string> _format;
    private readonly double _default;
    private bool _silent;
    public event Action<double>? Changed;

    public SliderRow(string label, double min, double max, double step, double value, double @default,
        Func<double, string>? format = null, string? tooltip = null)
    {
        _format = format ?? (v => v.ToString(step >= 1 ? "0" : step >= 0.1 ? "0.0" : "0.00", CultureInfo.InvariantCulture));
        _default = @default;
        AddThemeConstantOverride("separation", 0);
        var caption = W.Label(label, 13, StudioTheme.Text);
        caption.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        caption.CustomMinimumSize = new Vector2(20, 0);
        _value = W.Label("", 12, StudioTheme.TextFaint);
        _value.HorizontalAlignment = HorizontalAlignment.Right;
        AddChild(W.Row(caption, _value));
        Slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, Value = value,
            FocusMode = FocusModeEnum.None,
            TooltipText = tooltip ?? "",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 22),
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        Slider.ValueChanged += v =>
        {
            _value.Text = _format(v);
            if (!_silent) Changed?.Invoke(v);
        };
        Slider.GuiInput += e =>
        {
            if (e is InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left }) Slider.Value = _default;
        };
        AddChild(Slider);
        _value.Text = _format(value);
        if (tooltip != null) caption.TooltipText = tooltip;
    }

    public double Value { get => Slider.Value; set => Slider.Value = value; }

    public void SetSilently(double value)
    {
        _silent = true;
        Slider.Value = value;
        _value.Text = _format(Slider.Value);
        _silent = false;
    }
}

/// <summary>A row of mutually exclusive options in one pill (text, icon or both).</summary>
public sealed partial class Segmented : PanelContainer
{
    private readonly HBoxContainer _row = new();
    private readonly Dictionary<string, Button> _buttons = new();
    public event Action<string>? Selected;
    public string Current { get; private set; } = "";

    public Segmented()
    {
        AddThemeStyleboxOverride("panel", StudioTheme.Box(new Color("ffffff0a"), new Color(0, 0, 0, 0), 10, 0, 3, 3));
        _row.AddThemeConstantOverride("separation", 2);
        AddChild(_row);
    }

    public Button Add(string key, string text, IconKind? icon = null, string? tooltip = null, bool expand = true)
    {
        var button = new Button
        {
            Text = text,
            ToggleMode = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = tooltip ?? "",
            SizeFlagsHorizontal = expand ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            ClipText = true,
        };
        if (icon is { } kind) button.Icon = Icons.Get(kind, 16);
        if (text.Length == 0) button.IconAlignment = HorizontalAlignment.Center;
        button.AddThemeStyleboxOverride("normal", StudioTheme.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 8, 0, 8, 6));
        button.AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Hover, new Color(0, 0, 0, 0), 8, 0, 8, 6));
        button.AddThemeStyleboxOverride("pressed", StudioTheme.Box(new Color("ffffff1f"), new Color(0, 0, 0, 0), 8, 0, 8, 6));
        button.AddThemeStyleboxOverride("hover_pressed", StudioTheme.Box(new Color("ffffff26"), new Color(0, 0, 0, 0), 8, 0, 8, 6));
        button.AddThemeColorOverride("font_pressed_color", new Color("ffffff"));
        button.AddThemeColorOverride("font_hover_pressed_color", new Color("ffffff"));
        button.AddThemeColorOverride("font_color", StudioTheme.TextMuted);
        button.AddThemeColorOverride("icon_pressed_color", StudioTheme.Accent);
        button.AddThemeColorOverride("icon_hover_pressed_color", StudioTheme.Accent);
        button.AddThemeColorOverride("icon_normal_color", StudioTheme.TextMuted);
        button.Pressed += () => { Set(key); Selected?.Invoke(key); };
        _buttons[key] = button;
        _row.AddChild(button);
        return button;
    }

    public void Set(string key)
    {
        Current = key;
        foreach (var (k, b) in _buttons) b.SetPressedNoSignal(k == key);
    }
}

/// <summary>The vertical tool strip of the painter: one icon per tool, the active one lit.</summary>
public sealed partial class IconRail : PanelContainer
{
    private readonly VBoxContainer _column = new();
    private readonly Dictionary<string, Button> _buttons = new();
    public event Action<string>? Selected;

    public IconRail()
    {
        AddThemeStyleboxOverride("panel", StudioTheme.Glass(14, 6));
        _column.AddThemeConstantOverride("separation", 4);
        AddChild(_column);
    }

    public void Add(string key, IconKind icon, string tooltip)
    {
        var button = new Button
        {
            Icon = Icons.Get(icon, 22),
            ToggleMode = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(46, 46),
            IconAlignment = HorizontalAlignment.Center,
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        button.AddThemeStyleboxOverride("normal", StudioTheme.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 11, 0, 0, 0));
        button.AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Hover, new Color(0, 0, 0, 0), 11, 0, 0, 0));
        button.AddThemeStyleboxOverride("pressed", StudioTheme.Box(StudioTheme.AccentTint, new Color(0, 0, 0, 0), 11, 0, 0, 0));
        button.AddThemeStyleboxOverride("hover_pressed", StudioTheme.Box(StudioTheme.AccentTint, new Color(0, 0, 0, 0), 11, 0, 0, 0));
        button.AddThemeColorOverride("icon_normal_color", StudioTheme.TextMuted);
        button.Pressed += () => Selected?.Invoke(key);
        _buttons[key] = button;
        _column.AddChild(button);
    }

    public void AddSeparator() => _column.AddChild(new HSeparator { CustomMinimumSize = new Vector2(0, 4) });

    public void Set(string key)
    {
        foreach (var (k, b) in _buttons) b.SetPressedNoSignal(k == key);
    }
}

/// <summary>A choice shown as a colour card: swatch strip (or an isometric block) above a short name.</summary>
public sealed partial class SwatchCard : Button
{
    private readonly string _caption;
    private readonly Color[] _colours;
    private readonly bool _block;

    public SwatchCard(string caption, Color[] colours, string tooltip, bool block = false, float height = 64)
    {
        _caption = caption;
        _colours = colours;
        _block = block;
        Text = "";
        ToggleMode = true;
        TooltipText = tooltip;
        FocusMode = FocusModeEnum.None;
        CustomMinimumSize = new Vector2(56, height);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        AddThemeStyleboxOverride("normal", StudioTheme.Box(StudioTheme.Raised, new Color(0, 0, 0, 0), 11, 2, 0, 0));
        AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Hover, new Color(0, 0, 0, 0), 11, 2, 0, 0));
        AddThemeStyleboxOverride("pressed", StudioTheme.Box(StudioTheme.AccentTint, StudioTheme.Accent, 11, 2, 0, 0));
        AddThemeStyleboxOverride("hover_pressed", StudioTheme.Box(StudioTheme.AccentTint, StudioTheme.Accent, 11, 2, 0, 0));
    }

    public override void _Draw()
    {
        var size = Size;
        var font = GetThemeDefaultFont();
        var textColour = ButtonPressed ? new Color("ffffff") : IsHovered() ? new Color("ffffff") : StudioTheme.TextMuted;
        int fontSize = font.GetStringSize(_caption, HorizontalAlignment.Left, -1, 11).X <= size.X - 6 ? 11 : 10;
        DrawString(font, new Vector2(3, size.Y - 9), SeedCard.Fit(font, _caption, size.X - 6, fontSize), HorizontalAlignment.Center, size.X - 6, fontSize, textColour);
        float top = 8, bottom = size.Y - 24;
        if (_block) DrawBlock(new Rect2((size.X - Math.Min(size.X - 18, 40)) / 2, top, Math.Min(size.X - 18, 40), bottom - top));
        else
        {
            float x0 = 9, width = size.X - 18;
            float each = width / Math.Max(1, _colours.Length);
            for (int i = 0; i < _colours.Length; i++)
            {
                var rect = new Rect2(x0 + i * each, top, each + 0.5f, bottom - top);
                DrawRect(rect, _colours[i]);
            }
            DrawRect(new Rect2(x0, top, width, bottom - top), new Color(0, 0, 0, 0.35f), false, 1);
        }
    }

    private void DrawBlock(Rect2 rect)
    {
        var swatch = _colours[0];
        var top = new Vector2[] { rect.Position + new Vector2(0, rect.Size.Y * .3f), rect.Position + new Vector2(rect.Size.X * .5f, 0),
            rect.Position + new Vector2(rect.Size.X, rect.Size.Y * .3f), rect.Position + new Vector2(rect.Size.X * .5f, rect.Size.Y * .6f) };
        var depth = new Vector2(0, rect.Size.Y * .38f);
        var front = new Vector2[] { top[0], top[3], top[3] + depth, top[0] + depth };
        var side = new Vector2[] { top[3], top[2], top[2] + depth, top[3] + depth };
        DrawColoredPolygon(front, swatch.Darkened(.28f));
        DrawColoredPolygon(side, swatch.Darkened(.46f));
        DrawColoredPolygon(top, swatch);
        var ink = new Color(0, 0, 0, .5f);
        DrawPolyline(new[] { top[0], top[1], top[2], top[3], top[0] }, ink, 1.2f, true);
        DrawPolyline(new[] { top[0], front[3], front[2], side[2], top[2] }, ink, 1.2f, true);
        DrawLine(top[3], front[2], ink, 1.2f, true);
    }
}

/// <summary>A choice shown as a card: a large icon above a short name.</summary>
public sealed partial class IconCard : Button
{
    public IconCard(string caption, IconKind icon, string tooltip, float height = 66)
    {
        Text = caption;
        Icon = Icons.Get(icon, 24);
        ToggleMode = true;
        TooltipText = tooltip;
        FocusMode = FocusModeEnum.None;
        IconAlignment = HorizontalAlignment.Center;
        VerticalIconAlignment = VerticalAlignment.Top;
        ClipText = true;
        CustomMinimumSize = new Vector2(52, height);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        AddThemeFontSizeOverride("font_size", 11);
        AddThemeConstantOverride("h_separation", 4);
        AddThemeStyleboxOverride("normal", StudioTheme.Box(StudioTheme.Raised, new Color(0, 0, 0, 0), 11, 2, 4, 9));
        AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Hover, new Color(0, 0, 0, 0), 11, 2, 4, 9));
        AddThemeStyleboxOverride("pressed", StudioTheme.Box(StudioTheme.AccentTint, StudioTheme.Accent, 11, 2, 4, 9));
        AddThemeStyleboxOverride("hover_pressed", StudioTheme.Box(StudioTheme.AccentTint, StudioTheme.Accent, 11, 2, 4, 9));
        AddThemeColorOverride("font_color", StudioTheme.TextMuted);
        AddThemeColorOverride("font_pressed_color", new Color("ffffff"));
        AddThemeColorOverride("font_hover_pressed_color", new Color("ffffff"));
        AddThemeColorOverride("icon_normal_color", StudioTheme.TextMuted);
        AddThemeColorOverride("icon_pressed_color", StudioTheme.Accent);
        AddThemeColorOverride("icon_hover_pressed_color", StudioTheme.Accent);
    }
}

/// <summary>
/// A floating glass card that grows with its content and scrolls once it would leave the screen. The shell places it
/// and calls <see cref="Fit"/> every frame.
/// </summary>
public sealed partial class FloatingPanel : PanelContainer
{
    public readonly ScrollContainer Scroll = new()
    {
        HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        FollowFocus = true,
    };
    private readonly MarginContainer _margin = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private Control? _content;

    public FloatingPanel(int pad = 16)
    {
        var box = StudioTheme.Glass(StudioTheme.Radius, 0);
        box.ContentMarginTop = box.ContentMarginBottom = pad - 2;
        box.ContentMarginLeft = pad;
        box.ContentMarginRight = 6;
        AddThemeStyleboxOverride("panel", box);
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(Scroll);
        _margin.AddThemeConstantOverride("margin_right", pad - 6);
        Scroll.AddChild(_margin);
    }

    /// <summary>Replaces the content (the previous content stays alive for the caller).</summary>
    public Control? Content
    {
        get => _content;
        set
        {
            if (_content != null) _margin.RemoveChild(_content);
            _content = value;
            if (value == null) return;
            value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _margin.AddChild(value);
            Scroll.ScrollVertical = 0;
        }
    }

    /// <summary>Places the card at a point with a width, as tall as its content but at most <paramref name="maxHeight"/>.</summary>
    public void Fit(Vector2 position, float width, float maxHeight)
    {
        var style = GetThemeStylebox("panel");
        float content = _content == null ? 0 : _content.GetCombinedMinimumSize().Y;
        float height = Math.Min(content + style.ContentMarginTop + style.ContentMarginBottom, Math.Max(80, maxHeight));
        Position = position;
        Size = new Vector2(width, height);
    }
}

/// <summary>A number with minus/plus buttons and the unit next to it.</summary>
public sealed partial class Stepper : HBoxContainer
{
    private readonly Label _value;
    private readonly int _min, _max;
    private readonly Func<int, string> _format;
    public int Value { get; private set; }
    public event Action<int>? Changed;

    public Stepper(int value, int min, int max, Func<int, string> format, string tooltip)
    {
        _min = min; _max = max; _format = format;
        AddThemeConstantOverride("separation", 4);
        var minus = W.IconButton(IconKind.Minus, tooltip, () => Set(Value - 1, notify: true), 34, 16);
        var plus = W.IconButton(IconKind.Plus, tooltip, () => Set(Value + 1, notify: true), 34, 16);
        foreach (var b in new[] { minus, plus })
        {
            b.AddThemeStyleboxOverride("normal", StudioTheme.Box(StudioTheme.Raised, new Color(0, 0, 0, 0), 9, 0, 0, 0));
            b.AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Hover, new Color(0, 0, 0, 0), 9, 0, 0, 0));
        }
        _value = W.Label("", 18, StudioTheme.Text);
        _value.HorizontalAlignment = HorizontalAlignment.Center;
        _value.CustomMinimumSize = new Vector2(64, 0);
        _value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _value.TooltipText = tooltip;
        _value.MouseFilter = MouseFilterEnum.Pass;
        AddChild(minus);
        AddChild(_value);
        AddChild(plus);
        Set(value, notify: false);
    }

    public void Set(int value, bool notify)
    {
        Value = Math.Clamp(value, _min, _max);
        _value.Text = _format(Value);
        if (notify) Changed?.Invoke(Value);
    }
}

/// <summary>A small toggle pill.</summary>
public sealed partial class Chip : Button
{
    public Chip(string text, string tooltip = "")
    {
        Text = text;
        ToggleMode = true;
        TooltipText = tooltip;
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        AddThemeFontSizeOverride("font_size", 12);
        AddThemeStyleboxOverride("normal", StudioTheme.Box(StudioTheme.Raised, new Color(0, 0, 0, 0), 14, 0, 11, 5));
        AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Hover, new Color(0, 0, 0, 0), 14, 0, 11, 5));
        AddThemeStyleboxOverride("pressed", StudioTheme.Box(StudioTheme.AccentTint, StudioTheme.Accent, 14, 1, 11, 5));
        AddThemeStyleboxOverride("hover_pressed", StudioTheme.Box(StudioTheme.AccentTint, StudioTheme.Accent, 14, 1, 11, 5));
        AddThemeColorOverride("font_color", StudioTheme.TextMuted);
        AddThemeColorOverride("font_pressed_color", new Color("ffffff"));
        AddThemeColorOverride("font_hover_pressed_color", new Color("ffffff"));
    }
}

/// <summary>A quiet raised container for grouped content (result cards, previews).</summary>
public sealed partial class Card : PanelContainer
{
    public readonly VBoxContainer Body = W.Column(6);

    public Card(int pad = 12)
    {
        AddThemeStyleboxOverride("panel", StudioTheme.Box(StudioTheme.Raised, new Color(0, 0, 0, 0), 12, 0, pad));
        AddChild(Body);
    }
}

/// <summary>The three-way mode switch of the top bar.</summary>
public sealed partial class SegmentedTabs : PanelContainer
{
    private readonly HBoxContainer _row = new();
    private readonly List<Button> _buttons = new();
    public event Action<int>? Selected;

    public SegmentedTabs()
    {
        AddThemeStyleboxOverride("panel", StudioTheme.Box(new Color("ffffff0a"), new Color(0, 0, 0, 0), 12, 0, 3, 3));
        _row.AddThemeConstantOverride("separation", 2);
        AddChild(_row);
    }

    public void Add(string caption, IconKind icon, string tooltip)
    {
        int index = _buttons.Count;
        var button = new Button
        {
            Text = caption,
            Icon = Icons.Get(icon, 17),
            ToggleMode = true,
            TooltipText = tooltip,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(128, 36),
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        button.AddThemeFontSizeOverride("font_size", 14);
        button.AddThemeStyleboxOverride("normal", StudioTheme.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 10, 0, 14, 7));
        button.AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.Hover, new Color(0, 0, 0, 0), 10, 0, 14, 7));
        button.AddThemeStyleboxOverride("pressed", StudioTheme.Box(StudioTheme.Accent, new Color(0, 0, 0, 0), 10, 0, 14, 7));
        button.AddThemeStyleboxOverride("hover_pressed", StudioTheme.Box(StudioTheme.Accent, new Color(0, 0, 0, 0), 10, 0, 14, 7));
        button.AddThemeColorOverride("font_color", StudioTheme.TextMuted);
        button.AddThemeColorOverride("font_pressed_color", StudioTheme.Ink);
        button.AddThemeColorOverride("font_hover_pressed_color", StudioTheme.Ink);
        button.AddThemeColorOverride("icon_normal_color", StudioTheme.TextMuted);
        button.AddThemeColorOverride("icon_pressed_color", StudioTheme.Ink);
        button.AddThemeColorOverride("icon_hover_pressed_color", StudioTheme.Ink);
        button.Pressed += () => Selected?.Invoke(index);
        _buttons.Add(button);
        _row.AddChild(button);
    }

    public void Set(int index)
    {
        for (int i = 0; i < _buttons.Count; i++) _buttons[i].SetPressedNoSignal(i == index);
    }
}

/// <summary>Transient notifications above the hint line.</summary>
public sealed partial class Toasts : VBoxContainer
{
    public Toasts()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeConstantOverride("separation", 6);
        Alignment = AlignmentMode.End;
    }

    public void Show(string text, Color? accent = null, double seconds = 2.6)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        var colour = accent ?? StudioTheme.Accent;
        var box = StudioTheme.Glass(12, 12, solid: true);
        box.ContentMarginTop = box.ContentMarginBottom = 8;
        panel.AddThemeStyleboxOverride("panel", box);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var dot = new ColorRect { Color = colour, CustomMinimumSize = new Vector2(6, 6), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(dot);
        row.AddChild(W.Label(text, 13));
        panel.AddChild(row);
        AddChild(panel);
        var tween = panel.CreateTween();
        panel.Modulate = new Color(1, 1, 1, 0);
        tween.TweenProperty(panel, "modulate:a", 1f, .15);
        tween.TweenInterval(seconds);
        tween.TweenProperty(panel, "modulate:a", 0f, .3);
        tween.TweenCallback(Callable.From(panel.QueueFree));
        // Keep at most three: remove the oldest at once (QueueFree alone leaves it in the tree until the frame ends).
        while (GetChildCount() > 3)
        {
            var oldest = GetChild(0);
            RemoveChild(oldest);
            oldest.QueueFree();
        }
    }
}
