using System;
using Godot;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio.Ui;

/// <summary>The caption card of the generator's build animation: which layer is being built, progress and Skip.</summary>
public sealed partial class CinematicCard : PanelContainer
{
    private readonly Label _step, _title, _detail;
    private readonly ProgressBar _progress;
    public event Action? SkipRequested;

    public CinematicCard()
    {
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", StudioTheme.Glass(16, 20, solid: true));
        var column = W.Column(6);
        AddChild(column);
        _step = W.Label("", 11, StudioTheme.Accent);
        var skip = W.Button(T("Skip"), IconKind.ChevronRight, () => SkipRequested?.Invoke(), T("Show the finished map now (Space)"));
        skip.AddThemeFontSizeOverride("font_size", 12);
        column.AddChild(W.Row(_step, W.Expand(), skip));
        _title = W.Label("", 19, StudioTheme.Text);
        column.AddChild(_title);
        _detail = W.Label("", 13, StudioTheme.TextMuted, wrap: true);
        _detail.CustomMinimumSize = new Vector2(440, 0);
        column.AddChild(_detail);
        _progress = new ProgressBar { MinValue = 0, MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 5) };
        column.AddChild(_progress);
        CustomMinimumSize = new Vector2(480, 0);
        Visible = false;
    }

    public void Show(string step, string title, string detail, double progress)
    {
        Visible = true;
        _step.Text = step.ToUpperInvariant();
        _title.Text = title;
        _detail.Text = detail;
        _progress.Value = progress;
    }
}
