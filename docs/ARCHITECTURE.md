# Architecture

## Origin

The terrain was first built in TypeScript/three.js for a prototype of a [flui.gg](https://flui.gg) project. It was then
ported to Godot and C# with Claude Code and Codex, reworked, and boiled down into this standalone studio: the terrain
is the most interesting part of that prototype, so it is the only part kept.

The port stays close to its source. Most files are line-by-line ports whose header names the TypeScript original, and
namespaces and identifiers still say *Fluitown*. Changes made for the studio are marked `STUDIO` in the code. The
ported map composer, editor and analysis were checked against the TypeScript original and give byte-identical results.

## Folders

| Folder | Content | Engine |
| --- | --- | --- |
| `src/Domain` | tile model, generators (endless worlds, map composer), editor functions, validation, analysis | none |
| `src/Render` | tile manager, bake workers, geometry compiler, materials, camera, lights, day/night | none |
| `src/Runtime` | the JavaScript number and collection semantics the port relies on | none |
| `src/Godot` | the Godot side of the renderer: scene renderer, meshes, vegetation, uniform binding | Godot |
| `src/Comic` | the comic pipeline: material admission, world ink outline | Godot |
| `src/Studio` | the studio: modes, UI, tools, look, 3D view | Godot |

The engine-free folders compile without Godot, so the generator can be reused or ported on its own.

## How a frame is made

```
StudioMain                          one controller per mode: Paint, Generate, Explore
└─ TerrainView
   ├─ TerrainWorldSession           engine-free
   │   ├─ ClientDungeon             the map: a painted/generated layout or endless chunks
   │   ├─ TerrainTileManager        28×28-cell tiles, baked into mesh data on worker threads
   │   └─ TerrainPresentationState  camera, lights, day/night, material values
   ├─ StudioTileSink                mesh data → Godot meshes and vegetation
   ├─ TerrainSceneRenderer          scene viewport, comic materials, shadows, ink outline
   └─ LookApplier                   Style values → materials, light and ink
```

**Edits.** An edit changes the layout in place. Every tile hashes the cells it samples, so only tiles whose cells
changed are baked again; the old tile stays on screen until its replacement is ready. A brush stroke typically re-bakes
a few tiles in about a second.

**Endless worlds.** A world id `theme:<biome>:<seed>` streams 32×32-cell chunks, generated on worker threads. With
"Any theme" the seed also picks the theme, so a seed alone identifies a world.

## The studio

* **Shell.** `StudioMain`, the root of `scenes/Studio.tscn`, owns the terrain view, the UI and the mode controllers.
  The UI is built in code from a small design system: `StudioTheme` (colours, spacing, the Godot theme), `Widgets.cs`
  (the few components everything uses) and `Icons.cs` (vector icons).
* **Paint.** `PaintState` holds the six tools, `PaintPanel` their settings, and `PaintController` edits the map with the
  ported editor functions. Raise, lower and flatten are studio additions.
* **Generate.** `GenerateController` runs the map composer (`MapSimulation`) on a worker and plays the build animation.
  Every later setting composes the same seed again, and only the tiles that changed re-bake. The dials scale the
  composer's features from 0% (none) to 200%.
* **Style.** `LookSettings` holds every look value and preset; season and weather are macro controls over them.
  `LookApplier` writes them into the materials without rebuilding the terrain.
* **Explore.** `WorldController` opens worlds; `WorldPreview` draws the *Discover* pictures and the minimap from the
  same chunk generator.
* **Texts.** The interface is English only; visible strings pass through `Tr.T` / `Tr.F`.

## Changes from the original

* Only the terrain remains: characters, gameplay, networking, finite dungeons, the original non-comic look, post
  effects and camera effects are gone.
* The comic look is the only look. A colour grade per material family (`shaders/studio/studio_grade.gdshaderinc`) and
  material constants turned into uniforms drive the Style controls.
* New for the studio: painted and generated maps inside the world session, sculpting, free seed text in world ids, and
  composer dials that do exactly what they say while keeping the rest of the map.
* `Math.sin`/`cos` use V8's fdlibm routines instead of glibc's, whose LGPL licence does not fit this project. Every
  seed still grows the same world.
* The terrain shaders were once generated from the prototype's three.js programs; here they are ordinary source files.

## Known limitations

* A map ends at its border; bake tiles that overlap the border show a strip of rock there.
* Parts of the C# still read like TypeScript (camelCase members, JavaScript number semantics), which keeps the port
  comparable with its original.
