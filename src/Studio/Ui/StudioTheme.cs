using Godot;

namespace TerrainStudio.Ui;

/// <summary>
/// The studio's visual language: quiet dark glass surfaces floating over the terrain, one warm accent used only for
/// the current choice and the main action, generous spacing and no decoration that does not carry meaning.
/// </summary>
public static class StudioTheme
{
    public static readonly Color Surface = new("14171df0");
    public static readonly Color Raised = new("ffffff0d");
    public static readonly Color Hover = new("ffffff17");
    public static readonly Color Line = new("ffffff12");
    public static readonly Color LineStrong = new("ffffff26");
    public static readonly Color Field = new("0c0e12");
    public static readonly Color Text = new("eeeae3");
    public static readonly Color TextMuted = new("a2a8b4");
    public static readonly Color TextFaint = new("6e7482");
    public static readonly Color Accent = new("ffb547");
    public static readonly Color AccentDeep = new("d9922f");
    public static readonly Color AccentTint = new("ffb54724");
    public static readonly Color Ink = new("1a1206");
    public static readonly Color Mint = new("63d6b9");
    public static readonly Color Danger = new("ff6470");
    public static readonly Color Warning = new("ffd166");
    public static readonly Color Ok = new("84e39a");

    public const int Radius = 14;
    public const int FontSize = 13;

    private static Theme? _theme;

    public static Theme Build()
    {
        if (_theme != null) return _theme;
        var t = new Theme { DefaultFontSize = FontSize };

        t.SetStylebox("panel", "PanelContainer", Glass(Radius, 14));
        t.SetStylebox("panel", "Panel", Glass(Radius, 0));
        t.SetColor("font_color", "Label", Text);

        // ── buttons: flat by default; the choice made is tinted in the accent ──
        var normal = Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 9, 0, 10, 7);
        var hover = Box(Hover, new Color(0, 0, 0, 0), 9, 0, 10, 7);
        var pressed = Box(AccentTint, new Color(0, 0, 0, 0), 9, 0, 10, 7);
        var disabled = Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 9, 0, 10, 7);
        var focus = new StyleBoxEmpty();
        foreach (var type in new[] { "Button", "MenuButton", "OptionButton" })
        {
            t.SetStylebox("normal", type, type == "OptionButton" ? Box(Raised, new Color(0, 0, 0, 0), 9, 0, 10, 7) : normal);
            t.SetStylebox("hover", type, hover);
            t.SetStylebox("pressed", type, pressed);
            t.SetStylebox("hover_pressed", type, pressed);
            t.SetStylebox("disabled", type, disabled);
            t.SetStylebox("focus", type, focus);
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, new Color("ffffff"));
            t.SetColor("font_pressed_color", type, Accent);
            t.SetColor("font_hover_pressed_color", type, Accent);
            t.SetColor("font_focus_color", type, Text);
            t.SetColor("font_disabled_color", type, TextFaint);
            t.SetColor("icon_normal_color", type, Text);
            t.SetColor("icon_hover_color", type, new Color("ffffff"));
            t.SetColor("icon_pressed_color", type, Accent);
            t.SetColor("icon_hover_pressed_color", type, Accent);
            t.SetColor("icon_disabled_color", type, TextFaint);
            t.SetConstant("h_separation", type, 8);
        }
        t.SetIcon("arrow", "OptionButton", Icons.Get(IconKind.ChevronDown, 14, TextMuted));
        foreach (var type in new[] { "CheckButton", "CheckBox" })
        {
            t.SetStylebox("normal", type, Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 8, 0, 2, 5));
            t.SetStylebox("hover", type, Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 8, 0, 2, 5));
            t.SetStylebox("pressed", type, Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 8, 0, 2, 5));
            t.SetStylebox("hover_pressed", type, Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 8, 0, 2, 5));
            t.SetStylebox("focus", type, focus);
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, new Color("ffffff"));
            t.SetColor("font_pressed_color", type, Text);
            t.SetColor("font_hover_pressed_color", type, new Color("ffffff"));
        }
        t.SetIcon("checked", "CheckButton", Icons.Toggle(true));
        t.SetIcon("unchecked", "CheckButton", Icons.Toggle(false));
        t.SetIcon("checked", "CheckBox", Icons.Check(true));
        t.SetIcon("unchecked", "CheckBox", Icons.Check(false));

        // ── popups, tooltips, dialogs ──
        t.SetStylebox("panel", "PopupMenu", Glass(12, 6, solid: true));
        t.SetStylebox("hover", "PopupMenu", Box(Hover, new Color(0, 0, 0, 0), 7, 0, 8, 5));
        t.SetStylebox("separator", "PopupMenu", new StyleBoxLine { Color = Line, Thickness = 1, GrowBegin = -6, GrowEnd = -6 });
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", new Color("ffffff"));
        t.SetColor("font_disabled_color", "PopupMenu", TextFaint);
        t.SetColor("font_accelerator_color", "PopupMenu", TextFaint);
        t.SetColor("font_separator_color", "PopupMenu", TextMuted);
        t.SetConstant("v_separation", "PopupMenu", 8);
        t.SetConstant("item_start_padding", "PopupMenu", 10);
        t.SetConstant("item_end_padding", "PopupMenu", 12);
        t.SetStylebox("panel", "TooltipPanel", Box(new Color("0b0d11f7"), LineStrong, 8, 1, 10, 7));
        t.SetColor("font_color", "TooltipLabel", Text);
        t.SetFontSize("font_size", "TooltipLabel", 12);
        t.SetStylebox("panel", "PopupPanel", Glass(14, 14, solid: true));
        t.SetStylebox("panel", "AcceptDialog", Glass(14, 16, solid: true));
        t.SetStylebox("embedded_border", "Window", Glass(14, 8, solid: true, titleSpace: 34));
        t.SetStylebox("embedded_unfocused_border", "Window", Glass(14, 8, solid: true, titleSpace: 34));
        t.SetColor("title_color", "Window", Text);
        t.SetFontSize("title_font_size", "Window", 14);

        // ── inputs ──
        var field = Box(Field, Line, 9, 1, 10, 7);
        var fieldFocus = Box(Field, new Color(Accent, 0.8f), 9, 1, 10, 7);
        foreach (var type in new[] { "LineEdit", "SpinBox", "TextEdit" })
        {
            t.SetStylebox("normal", type, field);
            t.SetStylebox("focus", type, fieldFocus);
            t.SetStylebox("read_only", type, field);
            t.SetColor("font_color", type, Text);
            t.SetColor("font_placeholder_color", type, TextFaint);
            t.SetColor("caret_color", type, Accent);
            t.SetColor("selection_color", type, new Color(Accent, .35f));
        }

        // ── sliders ──
        var track = Box(new Color("ffffff14"), new Color(0, 0, 0, 0), 3, 0, 0, 2);
        track.ContentMarginTop = track.ContentMarginBottom = 2;
        var fill = Box(AccentDeep, new Color(0, 0, 0, 0), 3, 0, 0, 2);
        fill.ContentMarginTop = fill.ContentMarginBottom = 2;
        var fillHover = Box(Accent, new Color(0, 0, 0, 0), 3, 0, 0, 2);
        fillHover.ContentMarginTop = fillHover.ContentMarginBottom = 2;
        t.SetStylebox("slider", "HSlider", track);
        t.SetStylebox("grabber_area", "HSlider", fill);
        t.SetStylebox("grabber_area_highlight", "HSlider", fillHover);
        t.SetIcon("grabber", "HSlider", Icons.Grabber(false));
        t.SetIcon("grabber_highlight", "HSlider", Icons.Grabber(true));
        t.SetIcon("grabber_disabled", "HSlider", Icons.Grabber(false));

        // ── scrolling ──
        var bar = new StyleBoxEmpty();
        var grab = Box(new Color("ffffff1c"), new Color(0, 0, 0, 0), 3, 0, 0, 0);
        var grabHover = Box(new Color("ffffff33"), new Color(0, 0, 0, 0), 3, 0, 0, 0);
        grab.ContentMarginLeft = grab.ContentMarginRight = 2;
        t.SetStylebox("scroll", "VScrollBar", bar);
        t.SetStylebox("grabber", "VScrollBar", grab);
        t.SetStylebox("grabber_highlight", "VScrollBar", grabHover);
        t.SetStylebox("grabber_pressed", "VScrollBar", grabHover);
        t.SetStylebox("scroll", "HScrollBar", bar);
        t.SetStylebox("grabber", "HScrollBar", grab);

        t.SetStylebox("background", "ProgressBar", Box(new Color("ffffff12"), new Color(0, 0, 0, 0), 4, 0, 0, 0));
        t.SetStylebox("fill", "ProgressBar", Box(Accent, new Color(0, 0, 0, 0), 4, 0, 0, 0));

        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Line, Thickness = 1 });
        t.SetConstant("separation", "HSeparator", 14);
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Line, Thickness = 1, Vertical = true });
        t.SetConstant("separation", "VSeparator", 8);
        t.SetConstant("separation", "VBoxContainer", 8);
        t.SetConstant("separation", "HBoxContainer", 8);
        t.SetConstant("h_separation", "GridContainer", 8);
        t.SetConstant("v_separation", "GridContainer", 8);
        t.SetConstant("h_separation", "HFlowContainer", 6);
        t.SetConstant("v_separation", "HFlowContainer", 6);
        _theme = t;
        return t;
    }

    /// <summary>A floating glass surface with a soft shadow.</summary>
    public static StyleBoxFlat Glass(int radius, int pad, bool solid = false, int titleSpace = 0)
    {
        var box = Box(solid ? new Color("171a21fa") : Surface, Line, radius, 1, pad);
        box.ShadowColor = new Color(0, 0, 0, 0.35f);
        box.ShadowSize = 14;
        box.ShadowOffset = new Vector2(0, 4);
        if (titleSpace > 0) box.ContentMarginTop = titleSpace;
        return box;
    }

    public static StyleBoxFlat Box(Color background, Color border, int radius, int borderWidth, int padX, int padY = -1)
    {
        var box = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            CornerDetail = 8,
            AntiAliasing = true,
        };
        box.SetCornerRadiusAll(radius);
        box.SetBorderWidthAll(borderWidth);
        box.ContentMarginLeft = box.ContentMarginRight = padX;
        box.ContentMarginTop = box.ContentMarginBottom = padY < 0 ? padX : padY;
        return box;
    }
}
