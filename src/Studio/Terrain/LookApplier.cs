using System;
using System.Collections.Generic;
using System.Linq;
using Flui;
using Fluitown.GodotApp.Rendering;
using Fluitown.Render;
using Godot;
using TerrainStudio.Core;

namespace TerrainStudio.Terrain;

/// <summary>
/// Writes a <see cref="LookSettings"/> into the terrain's materials, lights and ink every frame, right after the uniform
/// binder pushed the port's own values. Studio uniforms are set absolutely; the port's uniforms are scaled from their
/// latest bound value (the binder only pushes on change, so every scaled uniform remembers its base and re-derives from
/// it whenever the binder writes a new one).
/// </summary>
public sealed class LookApplier
{
    public LookSettings Look { get; set; } = new();

    private readonly Dictionary<StringName, (Variant based, Variant written)> _scaled = new();
    private int _appliedRevision = -1;
    private WorldOutline? _outline;
    private ulong _outlineSearch;

    private static readonly StringName GradeGlobal = "studio_grade_global", GradeFloor = "studio_grade_floor", GradeCap = "studio_grade_cap",
        GradeWall = "studio_grade_wall", GradeFoliage = "studio_grade_foliage", GradeTone = "studio_grade_tone", GradeWater = "studio_grade_water",
        GroundMix = "studio_ground_mix", GroundDetail = "studio_ground_detail", GroundScale = "studio_ground_scale",
        RockDetail = "studio_rock_detail", RockScale = "studio_rock_scale", RockGrowth = "studio_rock_growth",
        Moss = "fluitown_moss", CapGrass = "fluitown_cap_grass", GroundPaint = "fluitown_ground_paint", Mountain = "fluitown_mountain",
        Style = "uMmoratStyle", MaterialB = "uMmoratTerrainMaterialB", DriftA = "uMmoratDriftA", DriftB = "uMmoratDriftB",
        InkStructure = "uMmoratInkStructure", Frequency = "uMmoratTerrainMaterialFrequency", Wind = "uMmoratTerrainWind",
        WeatherSurface = "uMmoratWeatherSurface", WeatherSnow = "uMmoratWeatherSnow",
        Shallow = "uMmoratShallow", Deep = "uMmoratDeep", WaterStyle = "uMmoratWaterStyle";

    /// <summary>Called by <see cref="TerrainView.LookOverrides"/> once per frame.</summary>
    public void Apply(TerrainSceneRenderer renderer)
    {
        var look = Look;
        var surface = renderer.Surface;
        var water = renderer.Water;
        double L(string id) => look[id];

        // Absolute studio uniforms: only when the look changed (the binder never touches them).
        if (look.Revision != _appliedRevision)
        {
            _appliedRevision = look.Revision;
            surface.SetShaderParameter(GradeGlobal, V4(L("color.hue"), L("color.sat"), L("color.value"), L("color.warmth")));
            surface.SetShaderParameter(GradeFloor, V4(L("floor.hue"), L("floor.sat"), L("floor.value"), L("floor.warmth")));
            surface.SetShaderParameter(GradeCap, V4(L("cap.hue"), L("cap.sat"), L("cap.value"), L("rock.warmth")));
            surface.SetShaderParameter(GradeWall, V4(L("wall.hue"), L("wall.sat"), L("wall.value"), L("rock.warmth")));
            surface.SetShaderParameter(GradeFoliage, V4(L("foliage.hue"), L("foliage.sat"), L("foliage.value"), L("foliage.warmth")));
            surface.SetShaderParameter(GradeTone, V4(L("color.contrast"), 0, 0, 0));
            double grain = L("floor.grain");
            surface.SetShaderParameter(GroundMix, V4(0.16 * L("floor.dry"), 0.5 * L("floor.turf"), 0.075 * L("floor.wear"), 0.22 * L("floor.broad")));
            surface.SetShaderParameter(GroundDetail, V4(0.07 * grain, 0.065 * grain, L("floor.stones"), 0.11 * grain));
            surface.SetShaderParameter(GroundScale, V4(L("pattern.broad"), L("pattern.brush"), L("pattern.grain"), L("pattern.stones")));
            double texture = L("rock.texture"), strata = L("rock.strata");
            surface.SetShaderParameter(RockDetail, V4(0.16 * texture, 0.13 * texture, 0.34 * strata, 0.22 * strata));
            surface.SetShaderParameter(RockScale, V4(L("pattern.rock"), L("pattern.mineral"), L("pattern.beds"), L("pattern.growth")));
            surface.SetShaderParameter(RockGrowth, V4(L("rock.growth"), 1.38, 0, 0));
            surface.SetShaderParameter(Moss, new Vector4(0.15f, 0.19f, 0.07f, (float)L("rock.moss")));
            surface.SetShaderParameter(CapGrass, new Vector4(0.21f, 0.26f, 0.085f, (float)L("rock.grass")));
            surface.SetShaderParameter(GroundPaint, look.Flag("floor.paint"));
            surface.SetShaderParameter(Mountain, look.Flag("rock.mountain") && TerrainOrganicForm.Enabled);
            water.SetShaderParameter(GradeWater, V4(L("water.hue"), L("water.sat"), L("water.value"), 1));
            water.SetShaderParameter(GradeGlobal, V4(L("color.hue"), L("color.sat"), L("color.value"), L("color.warmth")));
            _outline?.SetWidth((float)L("ink.width"));
        }

        // Weather uniforms are also bound by the port (zero without weather): re-assert them whenever they differ.
        SetIfDifferent(surface, WeatherSnow, (float)L("weather.snow"));
        SetIfDifferent(surface, WeatherSurface, V4(L("weather.wet"), L("weather.rain"), L("weather.rain"), 0.5 * L("wind.strength")));

        // The port's uniforms, scaled from their bound base.
        Scale(surface, Style, b => { var v = b.AsVector4(); return new Vector4(v.X * (float)L("floor.grain"), v.Y * (float)L("rock.texture"), v.Z * (float)L("rock.texture"), v.W); });
        Scale(surface, MaterialB, b => { var v = b.AsVector4(); return new Vector4(v.X * (float)L("floor.lush"), v.Y * (float)L("floor.dry"), v.Z, v.W); });
        float drift = (float)L("floor.drift");
        Scale(surface, DriftA, b => Vector3.One + (b.AsVector3() - Vector3.One) * drift);
        Scale(surface, DriftB, b => Vector3.One + (b.AsVector3() - Vector3.One) * drift);
        Scale(surface, InkStructure, b => { var v = b.AsVector4(); return new Vector4(v.X * (float)L("floor.grain"), v.Y, v.Z * (float)L("rock.strata"), v.W); });
        Scale(surface, Frequency, b => { var v = b.AsVector3(); return new Vector3(v.X / (float)L("pattern.rock"), v.Y / (float)L("pattern.mineral"), v.Z / (float)L("pattern.grain")); });
        Scale(surface, Wind, b => { var v = b.AsVector4(); return new Vector4(v.X, v.Y * (float)L("wind.strength"), v.Z * (float)L("wind.tempo"), v.W * (float)L("wind.gusts")); });
        float depth = (float)L("water.depth");
        Scale(water, WaterStyle, b => { var v = b.AsVector4(); return new Vector4(v.X * (float)L("water.waves"), v.Y * (float)L("water.foam"), v.Z * (float)L("water.glint"), v.W * (float)L("water.caustics")); });
        var shallow = Scale(water, Shallow, b => b.AsVector3());
        Scale(water, Deep, b => shallow.VariantType == Variant.Type.Vector3 ? shallow.AsVector3().Lerp(b.AsVector3(), depth) : b.AsVector3());

        // Light: SyncLights assigned the sun and ambient afresh this frame.
        if (renderer.Sun != null)
        {
            renderer.Sun.LightEnergy *= (float)L("light.sun");
            renderer.Sun.ShadowBlur = 1.2f * (float)L("light.softness");
        }
        renderer.SceneEnvironment.AmbientLightEnergy *= (float)L("light.ambient");

        // The world ink appears a few frames after the camera (ComicRendering creates it).
        if (_outline == null && Engine.GetProcessFrames() >= _outlineSearch)
        {
            _outlineSearch = Engine.GetProcessFrames() + 20;
            _outline = renderer.SceneViewport.FindChildren("*", "MeshInstance3D", true, false).OfType<WorldOutline>().FirstOrDefault()
                ?? renderer.GetTree().Root.FindChildren("*", "MeshInstance3D", true, false).OfType<WorldOutline>().FirstOrDefault();
            _outline?.SetWidth((float)L("ink.width"));
        }
    }

    /// <summary>Forces every scaled uniform to be re-derived (after a new session or quality change).</summary>
    public void Invalidate()
    {
        _scaled.Clear();
        _appliedRevision = -1;
        _outline = null;
        _outlineSearch = 0;
    }

    /// <summary>Scales a port uniform from its base; returns the base.</summary>
    private Variant Scale(ShaderMaterial material, StringName name, Func<Variant, Variant> transform)
    {
        var current = material.GetShaderParameter(name);
        if (current.VariantType == Variant.Type.Nil) return current;
        if (!_scaled.TryGetValue(name, out var entry) || !Same(current, entry.written))
            entry = (current, default);
        var written = transform(entry.based);
        if (!Same(written, current)) material.SetShaderParameter(name, written);
        _scaled[name] = (entry.based, written);
        return entry.based;
    }

    private static void SetIfDifferent(ShaderMaterial material, StringName name, Variant value)
    {
        if (!Same(material.GetShaderParameter(name), value)) material.SetShaderParameter(name, value);
    }

    private static bool Same(Variant a, Variant b)
    {
        if (a.VariantType != b.VariantType) return false;
        return a.VariantType switch
        {
            Variant.Type.Vector4 => a.AsVector4() == b.AsVector4(),
            Variant.Type.Vector3 => a.AsVector3() == b.AsVector3(),
            Variant.Type.Float => a.AsSingle() == b.AsSingle(),
            _ => false,
        };
    }

    private static Vector4 V4(double x, double y, double z, double w) => new((float)x, (float)y, (float)z, (float)w);
}
