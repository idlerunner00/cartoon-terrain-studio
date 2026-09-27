using Fluitown.GodotApp.Rendering;
using Fluitown.Render;

namespace Fluitown.GodotApp.Runtime;

/// <summary>
/// The node that owns the running terrain: its engine-free session, the Godot renderer that draws it and the active
/// quality profile. World-look layers (region paint) read the terrain through this, so they work in every mode of the
/// studio (painted map, generated map, open world) without knowing which host created them.
/// </summary>
public interface ITerrainHost
{
    TerrainWorldSession Session { get; }
    TerrainSceneRenderer Renderer { get; }
}
