using System;
using Godot;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio.Ui;

/// <summary>Native open/save dialogs (the platform's own file dialog where available).</summary>
public static class Dialogs
{
    public static void OpenFile(Node owner, string title, string[] filters, Action<string> chosen) =>
        Show(owner, title, FileDialog.FileModeEnum.OpenFile, null, filters, chosen);

    public static void SaveFile(Node owner, string title, string suggestedName, string[] filters, Action<string> chosen) =>
        Show(owner, title, FileDialog.FileModeEnum.SaveFile, suggestedName, filters, chosen);

    private static void Show(Node owner, string title, FileDialog.FileModeEnum mode, string? name, string[] filters, Action<string> chosen)
    {
        var dialog = new FileDialog
        {
            Title = title,
            FileMode = mode,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Filters = filters,
            CurrentDir = System.IO.Path.Combine(OS.GetSystemDir(OS.SystemDir.Documents), "Cartoon Terrain Studio"),
        };
        System.IO.Directory.CreateDirectory(dialog.CurrentDir);
        if (name != null) dialog.CurrentFile = name;
        owner.AddChild(dialog);
        dialog.FileSelected += path =>
        {
            try { chosen(path); }
            catch (Exception e) { GD.PushError($"{title}: {e.Message}"); }
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered(new Vector2I(900, 600));
    }
}

/// <summary>
/// A card in the middle of a dimmed studio: title, content and buttons. Esc or a click beside the card closes it;
/// while it is open the studio's shortcuts are off.
/// </summary>
public sealed partial class Modal : Control
{
    public readonly VBoxContainer Body = W.Column(12);
    public readonly HBoxContainer Buttons = new();
    private readonly PanelContainer _card = new();
    private bool _closed;
    public event Action? Closed;
    /// <summary>Whether Esc and a click beside the card close it.</summary>
    public bool Dismissable { get; set; } = true;

    public Modal(string title, float width = 480, string? subtitle = null)
    {
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Color = new Color(0.02f, 0.025f, 0.035f, 0.62f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        var box = StudioTheme.Glass(18, 24, solid: true);
        box.ContentMarginTop = 20;
        box.ContentMarginBottom = 20;
        box.ShadowSize = 30;
        _card.AddThemeStyleboxOverride("panel", box);
        _card.CustomMinimumSize = new Vector2(width, 0);
        _card.MouseFilter = MouseFilterEnum.Stop;
        centre.AddChild(_card);
        var column = W.Column(16);
        _card.AddChild(column);
        if (title.Length > 0)
        {
            var heading = W.Column(4);
            var close = W.IconButton(IconKind.Close, T("Close (Esc)"), Close, 32, 16);
            heading.AddChild(W.Row(W.Label(title, 20, StudioTheme.Text), W.Expand(), close));
            if (subtitle != null) heading.AddChild(W.Hint(subtitle));
            column.AddChild(heading);
        }
        column.AddChild(Body);
        Buttons.AddThemeConstantOverride("separation", 8);
        Buttons.Alignment = BoxContainer.AlignmentMode.End;
        column.AddChild(Buttons);
    }

    /// <summary>Opens the card over the studio.</summary>
    public Modal Open(Control host)
    {
        host.AddChild(this);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Modulate = new Color(1, 1, 1, 0);
        CreateTween().TweenProperty(this, "modulate:a", 1f, 0.12);
        return this;
    }

    public Button AddButton(string text, Action pressed, bool primary = false, IconKind? icon = null)
    {
        var button = primary ? W.Primary(text, icon, pressed) : W.Button(text, icon, pressed);
        button.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        button.CustomMinimumSize = new Vector2(104, 38);
        Buttons.AddChild(button);
        return button;
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Closed?.Invoke();
        QueueFree();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click && Dismissable
            && !_card.GetGlobalRect().HasPoint(click.GlobalPosition))
            Close();
        AcceptEvent();
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key) return;
        if (key.Keycode == Key.Escape && Dismissable) Close();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>A yes/no question.</summary>
    public static void Confirm(Control host, string title, string text, string ok, Action proceed, bool danger = false)
    {
        var modal = new Modal(title, 440);
        modal.Body.AddChild(W.Label(text, 14, StudioTheme.TextMuted, wrap: true));
        modal.AddButton(T("Cancel"), modal.Close);
        var yes = modal.AddButton(ok, () => { modal.Close(); proceed(); }, primary: true);
        if (danger)
        {
            yes.AddThemeStyleboxOverride("normal", StudioTheme.Box(StudioTheme.Danger, new Color(0, 0, 0, 0), 11, 0, 16, 11));
            yes.AddThemeStyleboxOverride("hover", StudioTheme.Box(new Color("ff8a93"), new Color(0, 0, 0, 0), 11, 0, 16, 11));
        }
        modal.Open(host);
    }
}
