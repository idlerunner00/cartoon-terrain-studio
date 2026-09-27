using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fluitown.GodotApp.Rendering;
using Godot;
using TerrainStudio.Core;
using TerrainStudio.Terrain;
using TerrainStudio.Ui;
using static TerrainStudio.Ui.Tr;

namespace TerrainStudio;

/// <summary>
/// Entry node of the studio: owns the terrain view, the UI shell and the three mode controllers (Paint, Generate,
/// Explore), and routes the pointer and keyboard to the active one.
///
/// The exported properties below can be changed in the Godot inspector (select the Studio node in
/// <c>scenes/Studio.tscn</c>); command-line user arguments (after <c>--</c>) override them:
/// <c>--mode paint|generate|world</c>, <c>--seed text</c>, <c>--theme key</c>, <c>--map file.json</c>,
/// <c>--quality auto|low|medium|high|ultra</c>, <c>--user-dir folder</c>; tests: <c>--capture out.png</c>,
/// <c>--frames n</c>, <c>--look preset</c>, <c>--day d</c>, <c>--no-ui</c>, <c>--orbit</c>,
/// <c>--selftest scenario folder</c>.
/// </summary>
public partial class StudioMain : Node
{
	/// <summary>The mode to open when the studio starts ("" = the one used last).</summary>
	[Export(PropertyHint.Enum, ",paint,generate,world")] public string StartMode { get; set; } = "";
	/// <summary>Graphics quality ("" = the user's setting, else auto, low, medium, high or ultra).</summary>
	[Export(PropertyHint.Enum, ",auto,low,medium,high,ultra")] public string StartQuality { get; set; } = "";
	/// <summary>Whether the welcome card may open on start (it also follows the user's setting).</summary>
	[Export] public bool AllowWelcome { get; set; } = true;

	public TerrainView View { get; private set; } = null!;
	public StudioUi Ui { get; private set; } = null!;
	public StudioSettings Settings { get; private set; } = null!;
	public readonly LookSettings Look = new();
	public readonly LookApplier Applier = new();
	public StudioMode Mode { get; private set; } = StudioMode.Paint;

	private readonly IModeController?[] _controllers = new IModeController?[3];
	private StylePanel _style = null!;
	private PopupMenu _recentMenu = null!;
	private string _qualityKey = "auto";
	private string? _capturePath;
	private int _captureFrames = 30, _readySince = -1, _frame;
	private bool _panning, _orbitDrag, _scripted;
	private double _saveTimer;
	private string? _startMap;
	private string? _selfTest, _selfTestOut;
	private bool _startOrbit;

	private enum FileItem { New, Open, Save, SaveAs, ExportHeight, Screenshot, LoadStyle, SaveStyle }

	public override void _Ready()
	{
		// The studio is English only: engine control texts, number formats ("1.6", not "1,6") and messages stay
		// English on any system language, on this thread and on every worker thread.
		TranslationServer.SetLocale("en");
		var english = CultureInfo.GetCultureInfo("en-US");
		CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = english;
		CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = english;
		var args = OS.GetCmdlineUserArgs();
		// --user-dir keeps the settings and the autosave in a folder of their own instead of the Godot user folder,
		// so a scripted run (a test, a recording) neither depends on nor changes the user's.
		int userDir = Array.IndexOf(args, "--user-dir");
		StudioSettings.Folder = userDir >= 0 && userDir + 1 < args.Length
			? Directory.CreateDirectory(Path.GetFullPath(args[userDir + 1])).FullName
			: ProjectSettings.GlobalizePath("user://");
		Settings = StudioSettings.Load();
		string mode = StartMode.Length > 0 ? StartMode : Settings.Mode;
		string? worldSeed = null, worldTheme = null, lookPreset = null;
		bool noUi = false;
		double? day = null;
		if (StartQuality.Length > 0) _qualityKey = StartQuality;
		for (int i = 0; i < args.Length; i++)
		{
			string next = i + 1 < args.Length ? args[i + 1] : "";
			switch (args[i])
			{
				case "--mode": mode = next; i++; _scripted = true; break;
				case "--seed": worldSeed = next; mode = "world"; i++; _scripted = true; break;
				case "--theme": worldTheme = next; i++; break;
				case "--map": _startMap = next; mode = "paint"; i++; _scripted = true; break;
				case "--quality": _qualityKey = next; i++; break;
				case "--capture": _capturePath = next; i++; break;
				case "--frames": _captureFrames = int.Parse(next, CultureInfo.InvariantCulture); i++; break;
				case "--look": lookPreset = next; i++; break;
				case "--day": day = double.Parse(next, CultureInfo.InvariantCulture); i++; break;
				case "--no-ui": noUi = true; break;
				case "--orbit": _startOrbit = true; break;
				case "--user-dir": i++; break;
				case "--selftest":
					_selfTest = next;
					_scripted = true;
					_selfTestOut = i + 2 < args.Length && !args[i + 2].StartsWith("--") ? args[i + 2] : "build/selftest";
					i += i + 2 < args.Length && !args[i + 2].StartsWith("--") ? 2 : 1;
					break;
			}
		}
		if (_qualityKey == "auto" && Settings.Quality != "auto" && StartQuality.Length == 0) _qualityKey = Settings.Quality;
		if (Settings.Look != null) Look.CopyFrom(LookSettings.FromJson(Settings.Look));
		if (lookPreset != null && LookSettings.Presets.FirstOrDefault(p => p.Key == lookPreset) is { } preset) Look.ApplyPreset(preset);
		if (worldSeed != null) Settings.WorldSeed = worldSeed;
		if (worldTheme != null) { Settings.WorldTheme = worldTheme; Settings.WorldThemeChoice = worldTheme; }

		GetWindow().Title = "Cartoon Terrain Studio";
		GetWindow().MinSize = new Vector2I(1200, 720);

		View = new TerrainView { Name = "TerrainView" };
		AddChild(View);
		View.DayPhase = day ?? Settings.Day;
		View.Animate = Settings.Animate;
		ApplyQuality(_qualityKey);
		View.ApplyForm(Look);
		Applier.Look = Look;
		View.LookOverrides = Applier.Apply;
		View.SessionChanged += () => Applier.Invalidate();

		var layer = new CanvasLayer { Name = "UiLayer", Layer = 10 };
		AddChild(layer);
		Ui = new StudioUi { Name = "StudioUi" };
		layer.AddChild(Ui);
		Ui.Visible = !noUi;
		BuildStyle();
		WireShell();

		BuildControllers();
		Ui.Viewport.Input += OnViewportInput;
		Ui.Viewport.Drawing += canvas => Controller?.Draw(canvas);
		Ui.ModeSelected += m => SetMode(m);
		View.Presented += () => Controller?.AfterPresent();

		SetMode(mode switch { "generate" => StudioMode.Generate, "world" => StudioMode.World, _ => StudioMode.Paint }, initial: true);
		Ui.StyleOpen = Settings.StyleOpen && !_scripted;
		GD.Print($"studio: mode {Mode}, quality {View.Quality.Name}");
		if (_startOrbit) ToggleOrbit();
		if (_selfTest != null) AddChild(new SelfTest { Main = this, Scenario = _selfTest, Output = Path.GetFullPath(_selfTestOut!) });
		else if (!_scripted && AllowWelcome && Settings.ShowWelcome) CallDeferred(nameof(ShowWelcome));
	}

	// ── controllers ─────────────────────────────────────────────────────────────────────────────────────────

	private IModeController? Controller => _controllers[(int)Mode];

	private void BuildControllers()
	{
		_controllers[(int)StudioMode.World] = new WorldController(this);
		_controllers[(int)StudioMode.Paint] = new Painting.PaintController(this);
		_controllers[(int)StudioMode.Generate] = new Generate.GenerateController(this);
		foreach (var controller in _controllers)
			if (controller != null) Ui.SetModePanel(controller.Mode, controller.Panel, controller.Rail);
	}

	public IModeController? ControllerOf(StudioMode mode) => _controllers[(int)mode];

	public void SetMode(StudioMode mode, bool initial = false)
	{
		if (!initial && mode == Mode) { Ui.ShowMode(mode); return; }
		var previous = initial ? null : Controller;
		previous?.Deactivate();
		Mode = mode;
		Ui.ShowMode(mode);
		Settings.Mode = mode switch { StudioMode.Generate => "generate", StudioMode.World => "world", _ => "paint" };
		Controller?.Activate(initial ? _startMap : null);
		_startMap = null;
	}

	// ── shell ───────────────────────────────────────────────────────────────────────────────────────────────

	private void WireShell()
	{
		Ui.Undo.Pressed += () => Controller?.Undo();
		Ui.Redo.Pressed += () => Controller?.Redo();
		Ui.SettingsButton.Pressed += ShowSettings;
		Ui.HelpButton.Pressed += ShowHelp;
		Ui.ZoomIn.Pressed += () => ZoomBy(1.2);
		Ui.ZoomOut.Pressed += () => ZoomBy(1 / 1.2);
		Ui.FitButton.Pressed += () => View.FitView();
		Ui.OrbitButton.Pressed += ToggleOrbit;
		Ui.StyleToggled += open => Settings.StyleOpen = open;

		var menu = Ui.FileMenu.GetPopup();
		Ui.FileMenu.SetDisableShortcuts(true);
		menu.AddItem(T("New map…"), (int)FileItem.New, Accel(Key.N, ctrl: true));
		menu.AddItem(T("Open map…"), (int)FileItem.Open, Accel(Key.O, ctrl: true));
		_recentMenu = new PopupMenu { Name = "Recent" };
		_recentMenu.IdPressed += id =>
		{
			int index = (int)id;
			if (index < Settings.RecentFiles.Count && ControllerOf(StudioMode.Paint) is Painting.PaintController paint)
			{
				SetMode(StudioMode.Paint);
				paint.OpenRecent(Settings.RecentFiles[index]);
			}
		};
		menu.AddSubmenuNodeItem(T("Open recent"), _recentMenu);
		menu.AddSeparator();
		menu.AddItem(T("Save"), (int)FileItem.Save, Accel(Key.S, ctrl: true));
		menu.AddItem(T("Save as…"), (int)FileItem.SaveAs, Accel(Key.S, ctrl: true, shift: true));
		menu.AddSeparator();
		menu.AddItem(T("Export height map…"), (int)FileItem.ExportHeight);
		menu.AddItem(T("Save screenshot"), (int)FileItem.Screenshot, Key.F12);
		menu.AddSeparator();
		menu.AddItem(T("Load style…"), (int)FileItem.LoadStyle);
		menu.AddItem(T("Save style…"), (int)FileItem.SaveStyle);
		menu.IdPressed += id => OnFileItem((FileItem)(int)id);
		menu.AboutToPopup += RefreshFileMenu;
		RefreshFileMenu();
	}

	private static Key Accel(Key key, bool ctrl = false, bool shift = false) =>
		(Key)((long)key | (ctrl ? (long)KeyModifierMask.MaskCmdOrCtrl : 0) | (shift ? (long)KeyModifierMask.MaskShift : 0));

	/// <summary>Updates the File menu: what can be saved in this mode, and the recent files.</summary>
	public void RefreshFileMenu()
	{
		var menu = Ui.FileMenu.GetPopup();
		bool canSave = Mode != StudioMode.World;
		menu.SetItemDisabled(menu.GetItemIndex((int)FileItem.Save), !canSave);
		menu.SetItemDisabled(menu.GetItemIndex((int)FileItem.SaveAs), !canSave);
		menu.SetItemDisabled(menu.GetItemIndex((int)FileItem.ExportHeight), Mode != StudioMode.Paint);
		_recentMenu.Clear();
		for (int i = 0; i < Settings.RecentFiles.Count; i++) _recentMenu.AddItem(Path.GetFileName(Settings.RecentFiles[i]), i);
		if (Settings.RecentFiles.Count == 0)
		{
			_recentMenu.AddItem(T("No recent maps"), 999);
			_recentMenu.SetItemDisabled(0, true);
		}
	}

	private void OnFileItem(FileItem item)
	{
		var paint = ControllerOf(StudioMode.Paint) as Painting.PaintController;
		switch (item)
		{
			case FileItem.New:
				SetMode(StudioMode.Paint);
				paint?.ShowNewMap();
				break;
			case FileItem.Open:
				SetMode(StudioMode.Paint);
				paint?.Open();
				break;
			case FileItem.Save: SaveMapFile(saveAs: false); break;
			case FileItem.SaveAs: SaveMapFile(saveAs: true); break;
			case FileItem.ExportHeight: paint?.ExportHeightmap(); break;
			case FileItem.Screenshot: Screenshot(); break;
			case FileItem.LoadStyle: LoadLookFile(); break;
			case FileItem.SaveStyle: SaveLookFile(); break;
		}
	}

	private void BuildStyle()
	{
		_style = new StylePanel(Look, View.DayPhase);
		_style.LookChanged += OnLookChanged;
		_style.FormChanged += () =>
		{
			if (View.ApplyForm(Look)) { View.Rebuild(); Toast(T("Rebuilding the terrain with the new shape")); }
			OnLookChanged();
		};
		_style.DayChanged += d => { View.DayPhase = d; Settings.Day = d; };
		_style.SaveRequested += SaveLookFile;
		_style.LoadRequested += LoadLookFile;
		_style.CloseRequested += () => Ui.StyleOpen = false;
		Ui.SetStylePanel(_style);
	}

	private void ShowWelcome()
	{
		StudioModals.Welcome(Ui, Settings.ShowWelcome, mode => SetMode(mode), on => { Settings.ShowWelcome = on; Settings.Save(); });
	}

	private void ShowSettings()
	{
		StudioModals.Settings(Ui, new StudioModals.SettingsHandlers
		{
			Quality = Settings.Quality,
			Animate = Settings.Animate,
			ShowWelcome = Settings.ShowWelcome,
			QualityChanged = key =>
			{
				ApplyQuality(key);
				Settings.Quality = key;
				Toast(F("Graphics: {0}", View.Quality.Label));
			},
			AnimateChanged = on => { View.Animate = on; Settings.Animate = on; },
			ShowWelcomeChanged = on => Settings.ShowWelcome = on,
			ShowHelp = ShowHelp,
		});
	}

	private void ShowHelp()
	{
		if (!Ui.ModalOpen) StudioModals.Help(Ui);
	}

	// ── frame ───────────────────────────────────────────────────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		_frame++;
		WorldPreview.Poll();
		HandleKeyboardMotion(delta);
		Controller?.Tick(delta);
		if (_frame % 6 == 0)
		{
			Ui.SetHint(Controller?.Hint ?? "");
			Ui.SetCursor(Controller?.Cursor ?? "");
			Ui.SetBusy(!View.ViewReady);
			Ui.OrbitButton.SetPressedNoSignal(View.Orbit);
		}
		_saveTimer += delta;
		if (_saveTimer > 5)
		{
			_saveTimer = 0;
			Settings.Look = Look.ToJson();
			Settings.Save();
		}
		if (_capturePath == null) return;
		if (View.ViewReady && (Controller?.Busy != true) && _readySince < 0) _readySince = _frame;
		if (_readySince >= 0 && _frame - _readySince >= _captureFrames)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_capturePath))!);
			GetViewport().GetTexture().GetImage().SavePng(_capturePath);
			GD.Print($"studio: wrote {_capturePath} after {_frame} frames");
			GetTree().Quit();
		}
		else if (_frame > 5000)
		{
			GD.PushError("studio: view never became ready");
			GetTree().Quit(1);
		}
	}

	// ── input ───────────────────────────────────────────────────────────────────────────────────────────────

	private bool TextHasFocus => GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit or SpinBox;

	private void OnViewportInput(InputEvent e)
	{
		if (e is InputEventMouseButton button)
		{
			if (button.Pressed && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
			{
				if (Controller?.Wheel(button) == true) return;
				View.ZoomAt(button.Position, button.ButtonIndex == MouseButton.WheelUp ? 1.12 : 1 / 1.12);
				return;
			}
			bool spacePan = button.ButtonIndex == MouseButton.Left && Input.IsKeyPressed(Key.Space) && Mode != StudioMode.Generate;
			if (button.ButtonIndex is MouseButton.Middle or MouseButton.Right || spacePan || (!button.Pressed && _panning))
			{
				_panning = button.Pressed;
				_orbitDrag = View.Orbit && button.ButtonIndex == MouseButton.Right;
				return;
			}
		}
		if (e is InputEventMouseMotion motion && _panning)
		{
			if (_orbitDrag) View.OrbitBy(-motion.Relative.X * 0.006f, motion.Relative.Y * 0.005f);
			else View.PanScreen(-motion.Relative.X, -motion.Relative.Y);
			return;
		}
		if (e is InputEventMagnifyGesture magnify) { View.ZoomAt(magnify.Position, magnify.Factor); return; }
		if (e is InputEventPanGesture pan) { View.PanScreen(pan.Delta.X * 12, pan.Delta.Y * 12); return; }
		Controller?.Pointer(e);
	}

	private void ZoomBy(double factor) => View.ZoomAt(GetViewport().GetVisibleRect().Size / 2, factor);

	private void HandleKeyboardMotion(double delta)
	{
		if (TextHasFocus || Ui.ModalOpen || Controller?.OwnsKeyboardMotion == true) return;
		double speed = 900 * delta * (Input.IsKeyPressed(Key.Shift) ? 3 : 1);
		double sx = 0, sy = 0;
		if (!Input.IsKeyPressed(Key.Ctrl))
		{
			if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) sx -= speed;
			if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) sx += speed;
			if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) sy -= speed;
			if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) sy += speed;
		}
		if (View.Orbit)
		{
			float metres = (float)(View.OrbitDistance * 0.9 * delta * (Input.IsKeyPressed(Key.Shift) ? 3 : 1));
			float right = (sx > 0 ? 1 : sx < 0 ? -1 : 0) * metres, forward = (sy < 0 ? 1 : sy > 0 ? -1 : 0) * metres;
			if (right != 0 || forward != 0) View.OrbitMove(right, forward);
		}
		else if (sx != 0 || sy != 0) View.PanScreen(sx, sy);
		double turn = 0;
		if (Input.IsKeyPressed(Key.Comma)) turn -= 0.08 * delta;
		if (Input.IsKeyPressed(Key.Period)) turn += 0.08 * delta;
		if (turn != 0)
		{
			View.DayPhase += turn;
			_style.SetDay(View.DayPhase);
			Settings.Day = View.DayPhase;
		}
	}

	public override void _UnhandledKeyInput(InputEvent e)
	{
		if (e is not InputEventKey { Pressed: true } key || Ui.ModalOpen) return;
		bool ctrl = key.CtrlPressed || key.MetaPressed;
		if (ctrl && key.Keycode == Key.Z && !key.ShiftPressed) { Controller?.Undo(); Accept(); return; }
		if (ctrl && (key.Keycode == Key.Y || key.Keycode == Key.Z && key.ShiftPressed)) { Controller?.Redo(); Accept(); return; }
		if (TextHasFocus) return;
		if (ctrl && key.Keycode == Key.S) { SaveMapFile(saveAs: key.ShiftPressed); Accept(); return; }
		if (ctrl && key.Keycode == Key.O) { OnFileItem(FileItem.Open); Accept(); return; }
		if (ctrl && key.Keycode == Key.N) { OnFileItem(FileItem.New); Accept(); return; }
		if (key.Echo && key.Keycode is not (Key.Bracketleft or Key.Bracketright)) return;
		if (Controller?.Key(key) == true) { Accept(); return; }
		switch (key.Keycode)
		{
			case Key.Key1 when !ctrl: SetMode(StudioMode.Paint); break;
			case Key.Key2 when !ctrl: SetMode(StudioMode.Generate); break;
			case Key.Key3 when !ctrl: SetMode(StudioMode.World); break;
			case Key.F12: Screenshot(); break;
			case Key.F1: ShowHelp(); break;
			case Key.Tab: Ui.PanelsVisible = !Ui.PanelsVisible; break;
			case Key.L when !ctrl: _style.Randomize(); Toast(T("New look — press L again for another"), StudioTheme.Mint, 1.6); break;
			case Key.Plus or Key.KpAdd or Key.Equal: ZoomBy(1.15); break;
			case Key.Minus or Key.KpSubtract: ZoomBy(1 / 1.15); break;
			case Key.Home: View.FitView(); break;
			case Key.V when !ctrl: ToggleOrbit(); break;
			default: return;
		}
		Accept();
	}

	private void Accept() => GetViewport().SetInputAsHandled();

	public void ToggleOrbit()
	{
		View.SetOrbit(!View.Orbit);
		Ui.OrbitButton.SetPressedNoSignal(View.Orbit);
		Toast(View.Orbit ? T("3D view — right mouse turns, wheel zooms, WASD moves (V returns)") : T("Map view"), StudioTheme.Mint, 2);
	}

	// ── look & quality ──────────────────────────────────────────────────────────────────────────────────────

	private void OnLookChanged() => Settings.Look = Look.ToJson();

	public void ApplyQuality(string key)
	{
		_qualityKey = key;
		var quality = TerrainVisualQuality.IsAuto(key) ? TerrainVisualQuality.ForDevice(out _) : TerrainVisualQuality.Parse(key);
		View.SetQuality(quality);
		Applier.Invalidate();
	}

	private void SaveLookFile()
	{
		Dialogs.SaveFile(this, T("Save style"), "style.json", new[] { "*.json ; " + T("Style files") }, path =>
		{
			File.WriteAllText(path, Look.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			Toast(F("Style saved: {0}", Path.GetFileName(path)), StudioTheme.Ok);
		});
	}

	private void LoadLookFile()
	{
		Dialogs.OpenFile(this, T("Load style"), new[] { "*.json ; " + T("Style files") }, path =>
		{
			try
			{
				var node = JsonNode.Parse(File.ReadAllText(path));
				// A map document may carry its look too.
				var look = LookSettings.FromJson(node?["look"] ?? node);
				Look.CopyFrom(look);
				SyncLookPanel();
				if (View.ApplyForm(Look)) View.Rebuild();
				OnLookChanged();
				Toast(F("Style loaded: {0}", Path.GetFileName(path)), StudioTheme.Ok);
			}
			catch (Exception ex) { Toast(F("Could not read the style: {0}", ex.Message), StudioTheme.Danger); }
		});
	}

	public void SyncLookPanel()
	{
		_style.SyncFromLook();
		_style.MarkCustom();
	}

	// ── files ───────────────────────────────────────────────────────────────────────────────────────────────

	private void SaveMapFile(bool saveAs)
	{
		switch (Controller)
		{
			case Painting.PaintController paint:
				if (saveAs) paint.SaveAs(); else paint.Save();
				break;
			case Generate.GenerateController generate:
				generate.SaveMap();
				break;
			default:
				Toast(T("In Explore, open an area in Paint to save it as a map"), StudioTheme.TextMuted);
				break;
		}
	}

	public void Screenshot()
	{
		string folder = Path.Combine(OS.GetSystemDir(OS.SystemDir.Pictures), "Cartoon Terrain Studio");
		Directory.CreateDirectory(folder);
		Ui.Visible = false;
		RenderingServer.FramePostDraw += Capture;
		void Capture()
		{
			RenderingServer.FramePostDraw -= Capture;
			string path = Path.Combine(folder, $"terrain-{DateTime.Now:yyyyMMdd-HHmmss}.png");
			GetViewport().GetTexture().GetImage().SavePng(path);
			CallDeferred(nameof(RestoreUi));
			CallDeferred(nameof(ToastDeferred), F("Screenshot saved in Pictures › Cartoon Terrain Studio ({0})", Path.GetFileName(path)));
		}
	}

	private void RestoreUi() => Ui.Visible = true;
	private void ToastDeferred(string text) => Toast(text, StudioTheme.Ok);

	public void Toast(string text, Color? accent = null, double seconds = 3.2) => Ui.Toasts.Show(text, accent, seconds);

	private void PersistAll()
	{
		Settings.Look = Look.ToJson();
		foreach (var controller in _controllers) controller?.Persist(Settings);
		Settings.Save();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest) PersistAll();
	}
}

/// <summary>A mode of the studio: its panel and tool rail, its pointer tool and its hint line.</summary>
public interface IModeController
{
	StudioMode Mode { get; }
	Control Panel { get; }
	/// <summary>The mode's tool rail left of the panel (painting), or null.</summary>
	Control? Rail { get; }
	/// <summary>One line explaining what the pointer does right now.</summary>
	string Hint { get; }
	/// <summary>What lies under the pointer, or "".</summary>
	string Cursor { get; }
	/// <summary>Whether the mode is still preparing its terrain (captures wait for it).</summary>
	bool Busy { get; }
	/// <summary>Whether WASD belongs to the mode (the studio's camera keys stay off).</summary>
	bool OwnsKeyboardMotion { get; }
	void Activate(string? startFile);
	void Deactivate();
	void Tick(double delta);
	void AfterPresent();
	void Pointer(InputEvent e);
	bool Wheel(InputEventMouseButton e);
	bool Key(InputEventKey e);
	void Draw(ViewportInput canvas);
	void Undo();
	void Redo();
	void Persist(StudioSettings settings);
}

/// <summary>A mode that edits a map document (open / save).</summary>
public interface IMapDocumentHost
{
}
