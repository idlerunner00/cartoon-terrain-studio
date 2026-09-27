using System.Collections.Generic;
using Godot;

namespace Fluitown.GodotApp.Replay;

/// <summary>
/// Applies uniform values dumped from the running original to a
/// Godot <see cref="ShaderMaterial"/>. Only used by the replay harness, which feeds captured state into the
/// ported shaders to validate them independently of the CPU-side uniform logic.
/// </summary>
public static class ThreeUniformBinder
{
    public static readonly HashSet<string> RenamedMaterialUniforms = new() { "diffuse", "emissive", "roughness", "metalness", "opacity" };

    public static readonly HashSet<string> LightUniformNames = new()
    {
        "ambientLightColor", "directionalLights", "pointLights", "hemisphereLights", "directionalShadowMatrix",
        "directionalLightShadows", "directionalShadowMap", "dfgLUT", "uMmoratActorShadowMap",
    };
}
