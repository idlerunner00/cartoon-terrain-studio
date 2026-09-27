# Cartoon Terrain Studio

Paint, generate and explore hand-inked 2.5D cartoon terrain — terraces, cliffs, rivers, waterfalls, chasms, bridges
and forests — rendered live in a comic look (one light ramp, hatching, ink outlines, soft shadows).

The terrain comes from a prototype of a [flui.gg](https://flui.gg) project: first built in TypeScript/three.js, then
ported to Godot with Claude Code and Codex, reworked and boiled down into this standalone studio. Everything is code
and shaders; there are no hand-made textures, models or sprites.

The studio has three modes, switched at the top of the window:

| Mode | What it does |
| --- | --- |
| **Paint** | Paint a map by hand with six tools: **Terrain** (ground, rock, water, chasm, bridge at any height; brush or fill), **Sculpt** (raise, lower, flatten, smooth), **Nature** (trees, bushes, stumps), **Build** (rope bridges, cliff passes), **Theme** (give any area another world theme, or the whole map) and **Select** (copy, cut, paste, move). Undo/redo, a map check with jump-to-problem, and overlays for heights, walkable ground and more. |
| **Generate** | One button composes a complete map: mountains, rivers, chasms, wonders, roads and forests. Choose a landscape type, a theme and a size; everything else waits under *Fine-tune* (eight dials, layout, outline). A new map builds itself on screen with a camera move; every setting after that reshapes the map on screen at once, keeping its seed. The dials scale what the landscape type brings: 0% leaves none of it (Mountains 0% is flat, open ground), 100% is the landscape's own amount, 200% twice as much. *Edit in Paint* hands the map to the painter. |
| **Explore** | Every seed grows an endless, chunk-streamed world. *New random world* rolls one; *Discover worlds* shows pictures of four more to pick from. A minimap shows where you are and travels on a click; places can be saved, recent seeds come back with one click, and any area opens in Paint as an editable map. Seeds are easy to copy and share — with "Any theme" the seed alone decides the theme, so a shared seed always grows the same world. |

**Style** (top right) changes how the terrain looks, live and without rebuilding it: nine looks, *Shuffle look* (every press a
clearly different look, rock included) with a variety dial, time of day, season (spring → winter), weather (clear, wet, rain, snow) and the six sliders people use
most. *All settings* holds every material control — colour grade per material family, meadow/soil/path mixing, grain,
stones, rock texture, strata, moss, pattern sizes, wind, water, weather, light and ink — and the *Terrain shape*
switches (organic rock, uneven ground, 3D vegetation, flowers), which rebuild the terrain. Styles can be saved and
loaded.

## Requirements

* Windows 10/11 (Direct3D 12 or Vulkan), a GPU from roughly 2018 onwards (a Linux export preset is included but untested)
* [.NET SDK 8](https://dotnet.microsoft.com/download)
* [Godot 4.7.2 .NET](https://godotengine.org/download) — or let `Start.cmd` download it into `.tools`

## Starting

**Double-click `Start.cmd`.** The first start downloads Godot 4.7.2 .NET, builds the C# project and imports the shaders,
which takes about a minute. A welcome card then offers the three modes.

| Call | Effect |
| --- | --- |
| `Start.cmd` | open the studio in the mode used last |
| `Start.cmd -Mode generate` | start in a mode: `paint`, `generate` or `world` |
| `Start.cmd -Seed amber-vale-7` | open Explore with this seed |
| `Start.cmd -Map C:\maps\island.json` | open a map file in Paint |
| `Start.cmd -Quality ultra` | graphics: `auto`, `low`, `medium`, `high`, `ultra` (2× supersampling) |
| `Start.cmd -Editor` | open the project in the Godot editor |
| `Start.cmd -PrepareOnly` | only download, build and import |

## Working in the Godot editor

The project is an ordinary Godot 4.7 C# project; no scripts or tools beyond the editor are needed.

1. Install **Godot 4.7.2 .NET** (the ".NET" download) and the **.NET SDK 8**.
2. In the Project Manager choose **Import**, select `project.godot` and open it. The first import takes a minute.
3. Press **F5** (or the ▶ button). Godot builds the C# code and runs `scenes/Studio.tscn`.

What you find in the editor:

* **`scenes/Studio.tscn`** is the main scene. Its root node *Studio* (`src/Studio/StudioMain.cs`) has three options in the
  Inspector: *Start Mode* (open in Paint, Generate or Explore instead of the last used mode), *Start Quality* and
  *Allow Welcome*. The rest of the interface is built in code, so it follows one design system.
* **Colours, spacing, fonts** of the interface: `src/Studio/Ui/StudioTheme.cs`. **Icons** are drawn from a few lines of
  path data in `src/Studio/Ui/Icons.cs`. **Texts** are written in the code and pass through `src/Studio/Ui/Tr.cs`.
* **Looks** (the Style presets), their colours and the season/weather keys: `src/Studio/Core/LookSettings.cs`.
* **Shaders**: `shaders/` and `assets/shaders/` — edit them in the editor's shader editor; the running studio picks the
  changes up on the next start.
* **Editing C#**: set your IDE under *Editor → Editor Settings → Dotnet → Editor → External Editor*; double-clicking a
  script then opens it there. The code is indented with four spaces (`.editorconfig`); Godot's built-in script editor
  converts to tabs on save unless *Text Editor → Behavior → Indent → Type* is set to *Spaces*.
* **Debugging**: open `CartoonTerrainStudio.sln` in Visual Studio, Rider or VS Code (C# Dev Kit) and attach to the
  running Godot process, or use Godot's own debugger panel (errors and `GD.Print` output appear there).
* **Command-line options** from the table above go into *Project → Project Settings → General → Editor → Run → Main Run
  Args* after `--`, for example `-- --mode world --seed amber-vale-7`.

### Exporting a game build

1. *Editor → Manage Export Templates → Download and Install* (once per Godot version).
2. *Project → Export*: the presets **Windows Desktop** and **Linux** are ready. Choose one and press *Export Project*.
   The build lands in `build/export/`.

## Controls

**Everywhere**

| Input | Action |
| --- | --- |
| Right or middle mouse drag · Space + drag | move the view (in the 3D view, right mouse turns) |
| Mouse wheel · + / − | zoom |
| W A S D / arrows | move (Shift moves faster) |
| Home | show the whole map |
| V | 3D view on or off |
| 1 / 2 / 3 | Paint / Generate / Explore |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Ctrl+N / Ctrl+O / Ctrl+S | new map / open / save |
| L | shuffle the look |
| , / . | time of day |
| Tab | hide or show the panels |
| F12 | screenshot, saved to *Pictures\Cartoon Terrain Studio* |
| F1 | help and shortcuts |

**Paint**

| Input | Action |
| --- | --- |
| Left mouse | use the tool |
| Ctrl + click | take material and height from the map |
| B · R · T · H · P · M | Terrain · Sculpt · Nature · Build · Theme · Select |
| F | brush / fill |
| [ / ] | brush size |
| Shift + wheel | height ±1 |
| Ctrl+C / Ctrl+X / Ctrl+V | copy / cut / paste the selection (paste lands at the pointer) |
| Delete · Esc · arrows | clear the selection · deselect · move the selection (Shift: 5 cells) |

**Generate:** Enter makes a new map, Space skips the build animation. **Explore:** R rolls a new random world.

The line at the bottom of the window always explains what the current tool does.

## Files

* **Maps** are JSON documents of kind `fluitown.terrain-artifact`, version 1. The studio adds a `look` object, which is
  applied when the map is opened; maps saved from Generate also get a `recipe` object. Tools that don't know these
  fields ignore them.
* **Styles** (`style.json`) contain every Style value that differs from its default.
* **Height maps:** *File → Export height map* writes a grey-scale PNG with one pixel per cell (black −30 … white +25)
  plus a colour PNG of the materials.
* **User data** lives in the Godot user folder (`%APPDATA%\Godot\app_userdata\Cartoon Terrain Studio`):
  * `settings.json` holds the mode, quality, style, Explore seeds and saved places, and the generator recipe.
  * `autosave.terrain.json` holds the last painted map, which is restored on start.

## Project layout

```
project.godot, CartoonTerrainStudio.csproj/.sln, export_presets.cfg, icon.svg, Start.cmd/.ps1
LICENSE, THIRD_PARTY_NOTICES.md
scenes/Studio.tscn            main scene
src/Studio/                   the studio (namespace TerrainStudio)
  StudioMain.cs               modes, menus, keyboard, settings, screenshots, command line
  SelfTest.cs                 scripted checks (--selftest paint|look|world|generate|suite|ui <folder>)
  Terrain/                    TerrainView (renderer + session + camera + picking), orbit camera, overlays, look applier
  Modes/Painting/             painter: tool model, panel, controller (strokes, sculpting, compile worker, selection, files)
  Modes/Generate/             generator panel/controller and the build animation
  Modes/WorldController.cs    Explore: worlds, seed suggestions, minimap, saved places
  Core/                       engine-free studio logic: look settings, document/undo, themes, seeds, playback, overlays
  Ui/                         design system (theme, icons, widgets), shell, Style drawer, Explore panel, world previews,
                              dialogs, texts (Tr)
src/Domain/                   terrain domain (generation, editor, validation, map composer), engine-free
src/Render/                   terrain renderer core (tile manager, bake workers, geometry compiler, materials)
src/Runtime/                  JS-semantics helpers used by the port
src/Godot/                    Godot side of the renderer (scene renderer, lane meshes, vegetation, uniform binder)
src/Comic/                    the comic pipeline (material admission, world ink outline)
shaders/terrain, shaders/vegetation, shaders/studio, assets/shaders   shaders
assets/terrain/               the material noise texture of the terrain shader
docs/ARCHITECTURE.md          how the terrain is built and drawn, and how the studio is put together
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the terrain is built and drawn, and for how the studio is put
together.

## Self-tests

```
.tools\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe --path . -- --user-dir build\selftest\userdata --selftest paint build\selftest\paint
```

Each self-test drives the studio through its controls, waits for the terrain after every step, and writes one
screenshot per step plus `report.txt`. The exit code is 0 when every step passed. `--user-dir` gives the run a settings
folder of its own, so it neither depends on nor changes yours. The scenarios are:

* `paint`: ridge, plateau, lake, forest, bridge, theme brush, sculpting (raise once per stroke, lower), undo/redo, chasm
* `look`: every look, a shuffled look, autumn, winter, rain, dusk and night
* `world`: several themes and seeds, a random world and a far-away place
* `generate`: one composed map with its build animation, then the live dials: Mountains, Water, Chasms and Forests
  at 0% leave none of their feature, and back at their defaults the dials give exactly the first map again
* `suite`: end to end: generate, hand to the painter, paint, save/load round trip, terrain shape switches, 3D view
  and picking, Explore, and an area taken into the painter
* `ui`: a screenshot of every tool page, panel and card

## License

Cartoon Terrain Studio is released under the [MIT License](LICENSE). It contains code derived from three.js, V8/fdlibm and Dave Hoskins' "Hash without Sine"; their notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
