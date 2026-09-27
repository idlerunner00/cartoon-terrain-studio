using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Fluitown.GodotApp.Rendering;
using Fluitown.GodotApp.Replay;
using Fluitown.Render;
using Godot;

namespace Fluitown.GodotApp.Runtime;

/// <summary>
/// Binds the live uniform sets of the engine-free presentation state (<c>TerrainPresentationState.materialUniformSets()</c>)
/// to the ported Godot shaders. The mapping is the replay binder's (validated against the browser
/// dumps), applied to the port's own values instead of JSON: three light structs → parallel arrays, standard material
/// uniforms → <c>three_*</c>, textures are the Godot layer's own (shadow viewport, DFG LUT, procedural paper).
/// Values are only pushed to Godot when they changed. Runs every frame without allocating: one typed
/// <see cref="UniformBinding"/> per (material, uniform holder), created once.
/// </summary>
public sealed class LiveUniformBinder
{
    /// <summary>
    /// Enhancement profile: the sun shadow map is <see cref="TerrainVisualQuality.ShadowMapScale"/> times finer, and
    /// its PCF radius (in texels of that map) covers <see cref="TerrainVisualQuality.ShadowSoftness"/> of the original's
    /// penumbra. Set it through <see cref="SetQuality"/>, which forgets the pushed values so everything is bound again.
    /// </summary>
    public TerrainVisualQuality Quality { get; private set; } = TerrainVisualQuality.Original;

    /// <summary>The bindings of one uniform set on one material, aligned with the set's key order.</summary>
    private sealed class SetBinding
    {
        public required ShaderMaterial Material;
        public required ThreeUniformSet Set;
        public UniformBinding?[] Bindings = Array.Empty<UniformBinding?>();
        /// <summary>Key index of each light uniform (−1 when absent).</summary>
        public readonly int[] LightIndex = new int[LightNames.Length];
        public LightParameters? Lights;
        public int Count = -1;
    }

    private readonly List<SetBinding> _sets = new();
    private readonly List<(string family, ThreeMaterial material, ThreeUniformSet? uniforms)> _families = new();

    public void SetQuality(TerrainVisualQuality quality)
    {
        Quality = quality;
        foreach (var set in _sets)
        {
            foreach (var binding in set.Bindings) binding?.Invalidate();
            set.Lights?.Invalidate();
        }
    }

    /// <summary>Binds every terrain family to its Godot material(s).</summary>
    public void BindTerrain(TerrainPresentationState presentation, TerrainSceneRenderer renderer)
    {
        presentation.materialUniformSets(_families);
        foreach (var (family, _, uniforms) in _families)
        {
            if (uniforms == null) continue;
            switch (family)
            {
                case "surface": BindSet(renderer.Surface, uniforms, lit: true); break;
                case "water": BindSet(renderer.Water, uniforms, lit: true); break;
                case "mist":
                    BindSet(renderer.MistBack, uniforms, lit: false);
                    BindSet(renderer.MistFront, uniforms, lit: false);
                    break;
                case "overlay": BindSet(renderer.Overlay, uniforms, lit: false); break;
                case "backdrop": BindSet(renderer.Backdrop, uniforms, lit: false); break;
            }
        }
    }

    private SetBinding Find(ShaderMaterial material, ThreeUniformSet set)
    {
        foreach (var entry in _sets)
            if (ReferenceEquals(entry.Material, material) && ReferenceEquals(entry.Set, set)) return entry;
        var created = new SetBinding { Material = material, Set = set };
        _sets.Add(created);
        return created;
    }

    private void BindSet(ShaderMaterial material, ThreeUniformSet uniforms, bool lit)
    {
        var entry = Find(material, uniforms);
        int count = uniforms.Count;
        if (entry.Count != count)
        {
            // New keys: rebuild the name layout (bindings are recreated lazily below).
            entry.Count = count;
            entry.Bindings = new UniformBinding?[count];
            Array.Fill(entry.LightIndex, -1);
            for (int i = 0; i < count; i++)
            {
                int light = Array.IndexOf(LightNames, uniforms.keyAt(i));
                if (light >= 0) entry.LightIndex[light] = i;
            }
            if (lit) entry.Lights ??= new LightParameters();
            entry.Lights?.Invalidate();
        }
        for (int i = 0; i < count; i++)
        {
            object holder = uniforms.valueAt(i);
            var binding = entry.Bindings[i];
            if (binding == null || !ReferenceEquals(binding.Holder, holder))
            {
                string name = uniforms.keyAt(i);
                if (ThreeUniformBinder.LightUniformNames.Contains(name)) continue;
                entry.Bindings[i] = binding = UniformBinding.Create(holder, material,
                    ThreeUniformBinder.RenamedMaterialUniforms.Contains(name) ? "three_" + name : name);
            }
            binding.Push();
        }
        if (lit) entry.Lights!.Push(material, uniforms, entry.LightIndex, Quality);
    }

    // ── lights ──────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] LightNames =
    {
        "ambientLightColor", "directionalLights", "pointLights", "hemisphereLights", "directionalShadowMatrix", "directionalLightShadows",
    };

    /// <summary>three's light struct arrays → the parallel arrays the ported shaders declare, change-tracked.</summary>
    private sealed class LightParameters
    {
        private readonly Parameter<Vector3> _ambient = new("three_ambientLightColor");
        private readonly ArrayParameter<Vector3> _dirDirection = new("u_dir_light_direction", 2), _dirColor = new("u_dir_light_color", 2);
        private readonly ArrayParameter<Vector3> _pointPosition = new("u_point_light_position", 3), _pointColor = new("u_point_light_color", 3);
        private readonly ArrayParameter<float> _pointDistance = new("u_point_light_distance", 3), _pointDecay = new("u_point_light_decay", 3);
        private readonly Parameter<Vector3> _hemiDirection = new("u_hemi_direction"), _hemiSky = new("u_hemi_sky"), _hemiGround = new("u_hemi_ground");
        private readonly Parameter<Projection> _shadowMatrix = new("directionalShadowMatrix");
        private readonly Parameter<float> _shadowIntensity = new("u_shadow_intensity"), _shadowBias = new("u_shadow_bias"),
            _shadowNormalBias = new("u_shadow_normal_bias"), _shadowRadius = new("u_shadow_radius");
        private readonly Parameter<Vector2> _shadowMapSize = new("u_shadow_map_size");

        public void Invalidate()
        {
            _ambient.Invalidate(); _dirDirection.Invalidate(); _dirColor.Invalidate();
            _pointPosition.Invalidate(); _pointColor.Invalidate(); _pointDistance.Invalidate(); _pointDecay.Invalidate();
            _hemiDirection.Invalidate(); _hemiSky.Invalidate(); _hemiGround.Invalidate(); _shadowMatrix.Invalidate();
            _shadowIntensity.Invalidate(); _shadowBias.Invalidate(); _shadowNormalBias.Invalidate(); _shadowRadius.Invalidate();
            _shadowMapSize.Invalidate();
        }

        private static object? Value(ThreeUniformSet uniforms, int index) =>
            index >= 0 ? ThreeUniformValues.valueOf(uniforms.valueAt(index)) : null;

        public void Push(ShaderMaterial material, ThreeUniformSet uniforms, int[] index, TerrainVisualQuality quality)
        {
            if (Value(uniforms, index[0]) is double[] { Length: >= 3 } ambient)
                _ambient.Push(material, new Vector3((float)ambient[0], (float)ambient[1], (float)ambient[2]));

            if (index[1] >= 0)
            {
                object? lights = Value(uniforms, index[1]);
                for (int i = 0; i < 2; i++)
                {
                    var light = UniformLists.At<ThreeDirectionalLightUniforms>(lights, i);
                    _dirDirection.Values[i] = light != null ? V3(light.direction) : default;
                    _dirColor.Values[i] = light != null ? C3(light.color) : default;
                }
                _dirDirection.Push(material);
                _dirColor.Push(material);
            }

            if (index[2] >= 0)
            {
                object? lights = Value(uniforms, index[2]);
                for (int i = 0; i < 3; i++)
                {
                    // Absent light (three would compile fewer): black and far away, so it adds exactly nothing.
                    var light = UniformLists.At<ThreePointLightUniforms>(lights, i);
                    _pointPosition.Values[i] = light != null ? V3(light.position) : new Vector3(0, 1e7f, 0);
                    _pointColor.Values[i] = light != null ? C3(light.color) : default;
                    _pointDistance.Values[i] = light != null ? (float)light.distance : 0;
                    _pointDecay.Values[i] = light != null ? (float)light.decay : 0;
                }
                _pointPosition.Push(material);
                _pointColor.Push(material);
                _pointDistance.Push(material);
                _pointDecay.Push(material);
            }

            if (UniformLists.At<ThreeHemisphereLightUniforms>(Value(uniforms, index[3]), 0) is { } hemi)
            {
                _hemiDirection.Push(material, V3(hemi.direction));
                _hemiSky.Push(material, C3(hemi.skyColor));
                _hemiGround.Push(material, C3(hemi.groundColor));
            }

            if (UniformLists.At<ThreeMatrix4>(Value(uniforms, index[4]), 0) is { } matrix)
                _shadowMatrix.Push(material, ThreeCameraMath.ToProjection(matrix.elements));

            if (UniformLists.At<ThreeDirectionalLightShadowUniforms>(Value(uniforms, index[5]), 0) is { } s)
            {
                _shadowIntensity.Push(material, (float)s.shadowIntensity);
                _shadowBias.Push(material, (float)s.shadowBias);
                _shadowNormalBias.Push(material, (float)s.shadowNormalBias);
                int mapScale = quality.ShadowMapScale;
                float radius = quality.IsOriginal ? (float)s.shadowRadius : (float)s.shadowRadius * mapScale * quality.ShadowSoftness;
                _shadowRadius.Push(material, radius);
                _shadowMapSize.Push(material, new Vector2((float)s.shadowMapSize.x, (float)s.shadowMapSize.y) * mapScale);
            }
        }
    }

    /// <summary>One change-tracked shader parameter.</summary>
    private sealed class Parameter<[MustBeVariant] T>(string name) where T : struct, IEquatable<T>
    {
        private readonly StringName _name = name;
        private T _last;
        private bool _pushed;
        public void Invalidate() => _pushed = false;
        public void Push(ShaderMaterial material, T value)
        {
            if (_pushed && value.Equals(_last)) return;
            _last = value;
            _pushed = true;
            material.SetShaderParameter(_name, Variant.From(value));
        }
    }

    private static Vector3 V3(ThreeVector3 v) => new((float)v.x, (float)v.y, (float)v.z);
    private static Vector3 C3(ThreeColor c) => new((float)c.r, (float)c.g, (float)c.b);

    /// <summary>A port uniform value → a Godot value (the replay binder's shapes);
    /// the allocating fallback of <see cref="UniformBinding"/> for rare holder types.</summary>
    internal static object? Convert(object? value)
    {
        switch (value)
        {
            case null: return null;
            case double d: return (float)d;
            case int i: return i;
            case bool b: return b;
            case ThreeColor c: return C3(c);
            case ThreeVector2 v2: return new Vector2((float)v2.x, (float)v2.y);
            case ThreeVector3 v3: return V3(v3);
            case ThreeVector4 v4: return new Vector4((float)v4.x, (float)v4.y, (float)v4.z, (float)v4.w);
            case ThreeMatrix4 m4: return ThreeCameraMath.ToProjection(m4.elements);
            case ThreeMatrix3 m3:
            {
                var e = m3.elements;
                return new Basis(
                    new Vector3((float)e[0], (float)e[1], (float)e[2]),
                    new Vector3((float)e[3], (float)e[4], (float)e[5]),
                    new Vector3((float)e[6], (float)e[7], (float)e[8]));
            }
            case double[] a:
                return a.Length switch
                {
                    2 => new Vector2((float)a[0], (float)a[1]),
                    3 => new Vector3((float)a[0], (float)a[1], (float)a[2]),
                    4 => new Vector4((float)a[0], (float)a[1], (float)a[2], (float)a[3]),
                    _ => a.Select(x => (float)x).ToArray(),
                };
            case string or ThreeTexture: return null;
            case IEnumerable list:
            {
                var items = list.Cast<object?>().ToList();
                if (items.Count > 0 && items.All(x => x is ThreeVector4))
                    return items.Select(x => (Vector4)Convert(x)!).ToArray();
                if (items.Count > 0 && items.All(x => x is ThreeVector3))
                    return items.Select(x => (Vector3)Convert(x)!).ToArray();
                if (items.Count > 0 && items.All(x => x is double))
                    return items.Select(x => (float)(double)x!).ToArray();
                return null;
            }
            default: return null;
        }
    }

    internal static bool Same(object a, object b) => (a, b) switch
    {
        (Vector3[] x, Vector3[] y) => x.AsSpan().SequenceEqual(y),
        (Vector4[] x, Vector4[] y) => x.AsSpan().SequenceEqual(y),
        (float[] x, float[] y) => x.AsSpan().SequenceEqual(y),
        _ => a.Equals(b),
    };

    internal static Variant ToVariant(object value) => value switch
    {
        float f => f,
        int i => i,
        bool b => b,
        Vector2 v => v,
        Vector3 v => v,
        Vector4 v => v,
        Basis m => m,
        Projection p => p,
        Vector3[] a => a,
        Vector4[] a => a,
        float[] a => a,
        _ => throw new InvalidOperationException($"unsupported uniform value {value.GetType()}"),
    };
}
