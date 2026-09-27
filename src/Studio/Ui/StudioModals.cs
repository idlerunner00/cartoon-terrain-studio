using System;
using Godot;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio.Ui;

/// <summary>The studio's cards: welcome, settings and the shortcut sheet.</summary>
public static class StudioModals
{
    // ── welcome ─────────────────────────────────────────────────────────────────────────────────────────────

    public static Modal Welcome(Control host, bool showAtStart, Action<StudioMode> chosen, Action<bool> showChanged)
    {
        var modal = new Modal("", 780);
        var logo = new TextureRect
        {
            Texture = Icons.Get(IconKind.Mountain, 48, StudioTheme.Accent),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            CustomMinimumSize = new Vector2(0, 52),
        };
        var title = W.Label("Cartoon Terrain Studio", 26, StudioTheme.Text);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var subtitle = W.Label(T("Create hand-inked cartoon landscapes. How would you like to start?"), 14, StudioTheme.TextMuted);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        modal.Body.AddChild(logo);
        modal.Body.AddChild(title);
        modal.Body.AddChild(subtitle);
        modal.Body.AddChild(W.Spacer(6));

        var cards = new HBoxContainer();
        cards.AddThemeConstantOverride("separation", 12);
        void Card(StudioMode mode, IconKind icon, string name, string text)
        {
            var card = ModeCard(icon, name, text);
            card.Pressed += () => { modal.Close(); chosen(mode); };
            cards.AddChild(card);
        }
        Card(StudioMode.Paint, IconKind.Brush, T("Paint"), T("Draw your own map with brushes: ground, rock, water, bridges and trees."));
        Card(StudioMode.Generate, IconKind.Sparkle, T("Generate"), T("Let the studio compose a complete map for you — then change what you like."));
        Card(StudioMode.World, IconKind.Globe, T("Explore"), T("Wander endless worlds that grow from a seed, and discover new ones."));
        modal.Body.AddChild(cards);

        var tip = W.Label(T("Tip: “Style” at the top right changes colours, season and weather at any time."), 12, StudioTheme.TextFaint, wrap: true);
        tip.HorizontalAlignment = HorizontalAlignment.Center;
        modal.Body.AddChild(tip);
        var show = W.Toggle(T("Show this at start"), showAtStart, showChanged);
        show.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        modal.Body.AddChild(show);
        return modal.Open(host);
    }

    private static Button ModeCard(IconKind icon, string name, string text)
    {
        var card = new Button
        {
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 200),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        card.AddThemeStyleboxOverride("normal", StudioTheme.Box(StudioTheme.Raised, StudioTheme.Line, 16, 1, 18, 18));
        card.AddThemeStyleboxOverride("hover", StudioTheme.Box(StudioTheme.AccentTint, StudioTheme.Accent, 16, 2, 18, 18));
        card.AddThemeStyleboxOverride("pressed", StudioTheme.Box(StudioTheme.AccentTint, StudioTheme.Accent, 16, 2, 18, 18));
        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 18);
        var column = W.Column(10);
        column.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.AddChild(new TextureRect
        {
            Texture = Icons.Get(icon, 36, StudioTheme.Accent),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            CustomMinimumSize = new Vector2(0, 44),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        var heading = W.Label(name, 18, StudioTheme.Text);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(heading);
        var body = W.Label(text, 13, StudioTheme.TextMuted, wrap: true);
        body.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(body);
        margin.AddChild(column);
        card.AddChild(margin);
        return card;
    }

    // ── settings ────────────────────────────────────────────────────────────────────────────────────────────

    public sealed class SettingsHandlers
    {
        public required string Quality;
        public required bool Animate, ShowWelcome;
        public required Action<string> QualityChanged;
        public required Action<bool> AnimateChanged, ShowWelcomeChanged;
        public required Action ShowHelp;
    }

    public static Modal Settings(Control host, SettingsHandlers h)
    {
        var modal = new Modal(T("Settings"), 500);
        var quality = new Segmented();
        quality.Add("auto", T("Auto"), null, T("Chosen for your graphics card"));
        quality.Add("low", T("Low"), null, T("For weak graphics cards"));
        quality.Add("medium", T("Medium"));
        quality.Add("high", T("High"));
        quality.Add("ultra", T("Ultra"), null, T("Twice the resolution inside (sharpest, slowest)"));
        quality.Set(h.Quality);
        quality.Selected += h.QualityChanged;
        modal.Body.AddChild(W.Group(T("Graphics quality"), quality));

        modal.Body.AddChild(W.Group(T("Behaviour"),
            W.Toggle(T("Animate water, wind and clouds"), h.Animate, h.AnimateChanged),
            W.Toggle(T("Show the welcome screen at start"), h.ShowWelcome, h.ShowWelcomeChanged)));

        modal.AddButton(T("Keyboard shortcuts"), () => { modal.Close(); h.ShowHelp(); }, icon: IconKind.Help);
        modal.AddButton(T("Done"), modal.Close, primary: true);
        return modal.Open(host);
    }

    // ── help ────────────────────────────────────────────────────────────────────────────────────────────────

    public static Modal Help(Control host)
    {
        var modal = new Modal(T("Help & shortcuts"), 820, T("Hover over any button to see what it does. The line at the bottom always explains the current tool."));
        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 28);
        var left = W.Column(16);
        var right = W.Column(16);
        left.SizeFlagsHorizontal = right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        columns.AddChild(left);
        columns.AddChild(right);

        left.AddChild(Keys(T("Moving around"), new[]
        {
            ("Right drag", T("move the view")), ("Wheel", T("zoom")), ("W A S D", T("move (Shift: faster)")),
            ("Home", T("show the whole map")), ("V", T("3D view on / off")), ("Tab", T("hide the panels")),
        }));
        left.AddChild(Keys(T("Everywhere"), new[]
        {
            ("1  2  3", T("Paint · Generate · Explore")), ("Ctrl+Z / Ctrl+Y", T("undo / redo")), ("Ctrl+S", T("save the map")),
            ("Ctrl+O", T("open a map")), ("Ctrl+N", T("new map")), ("L", T("shuffle the look")), (",  .", T("time of day")),
            ("F12", T("screenshot")), ("F1", T("this help")),
        }));
        right.AddChild(Keys(T("Paint"), new[]
        {
            ("B R T H P M", T("Terrain · Sculpt · Nature · Build · Theme · Select")), ("Ctrl+click", T("take material and height")),
            ("F", T("brush / fill")), ("[  ]", T("brush size")), ("Shift+wheel", T("height")),
            ("Ctrl+C / X / V", T("copy / cut / paste")), ("Delete", T("clear the selection")), ("Arrows", T("move the selection")),
        }));
        right.AddChild(Keys(T("Generate and Explore"), new[]
        {
            ("Enter", T("generate a new map")), ("Space", T("skip the build animation")), ("R", T("new random world (Explore)")),
        }));
        modal.Body.AddChild(columns);
        modal.AddButton(T("Close"), modal.Close, primary: true);
        return modal.Open(host);
    }

    private static Control Keys(string title, (string key, string what)[] rows)
    {
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 7);
        foreach (var (key, what) in rows)
        {
            var cap = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            cap.AddThemeStyleboxOverride("panel", StudioTheme.Box(new Color("ffffff10"), StudioTheme.LineStrong, 6, 1, 8, 2));
            cap.AddChild(W.Label(T(key), 12, StudioTheme.Text));
            cap.CustomMinimumSize = new Vector2(118, 0);
            grid.AddChild(cap);
            grid.AddChild(W.Label(what, 13, StudioTheme.TextMuted, wrap: true));
            grid.GetChild<Label>(grid.GetChildCount() - 1).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        return W.Group(title, grid);
    }
}
