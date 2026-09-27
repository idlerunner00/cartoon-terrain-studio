using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Fluitown.GodotApp.Rendering;
using Fluitown.Render;
using Godot;

namespace Fluitown.GodotApp.Runtime;

/// <summary>
/// Allocation-free uniform bindings for <see cref="LiveUniformBinder"/> (nothing on the per-frame
/// path may allocate — every managed allocation brings the next multi-millisecond GC pause closer). One binding per
/// (material, uniform holder) is created once; each frame it reads the holder's typed value, converts it without boxing
/// and pushes it to the material only when it changed. Holders of rare value types fall back to
/// <see cref="GenericBinding"/>, which converts through <see cref="LiveUniformBinder.Convert"/> like before.
/// </summary>
internal abstract class UniformBinding
{
    public readonly object Holder;
    protected readonly ShaderMaterial Material;
    protected readonly StringName Name;
    protected bool Pushed;

    protected UniformBinding(object holder, ShaderMaterial material, StringName name)
    {
        Holder = holder;
        Material = material;
        Name = name;
    }

    /// <summary>Pushes the value if it changed since the last push (or was never pushed).</summary>
    public abstract void Push();

    /// <summary>Forgets the pushed value (a quality switch re-binds everything).</summary>
    public void Invalidate() => Pushed = false;

    /// <summary>The binding for one uniform holder (a <c>ThreeUniform&lt;T&gt;</c>).</summary>
    public static UniformBinding Create(object holder, ShaderMaterial material, StringName name) => holder switch
    {
        ThreeUniform<double> h => new DoubleBinding(h, material, name),
        ThreeUniform<int> h => new IntBinding(h, material, name),
        ThreeUniform<bool> h => new BoolBinding(h, material, name),
        ThreeUniform<ThreeColor> h => new ColorBinding(h, material, name),
        ThreeUniform<ThreeVector2> h => new Vector2Binding(h, material, name),
        ThreeUniform<ThreeVector3> h => new Vector3Binding(h, material, name),
        ThreeUniform<ThreeVector4> h => new Vector4Binding(h, material, name),
        ThreeUniform<ThreeMatrix4> h => new Matrix4Binding(h, material, name),
        ThreeUniform<double[]> h => new DoubleArrayBinding(h, material, name),
        ThreeUniform<ThreeVector4[]> h => new Vector4ArrayBinding(h, material, name),
        ThreeUniform<ThreeMatrix3> h => new Matrix3Binding(h, material, name),
        // Textures are the Godot layer's own (shadow viewport, DFG LUT, paper): nothing to bind.
        ThreeUniform<ThreeTexture> or ThreeUniform<string> => new NoBinding(holder, material, name),
        // three's light probe (nine SH coefficients, never empty): the generic path allocated ≈1 KB per lit material per frame.
        ThreeUniform<List<ThreeVector3>> h => new Vector3ListBinding(h, material, name),
        // three's light lists this port's shaders do not declare (spot, rect-area) stay empty, and holders without a value
        // (clippingPlanes) push nothing: skip them cheaply.
        _ when ThreeUniformValues.valueOf(holder) is null or IList => new ListBinding(holder, material, name),
        _ => new GenericBinding(holder, material, name),
    };

    private sealed class DoubleBinding(ThreeUniform<double> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private float _last;
        public override void Push()
        {
            float v = (float)h.value;
            if (Pushed && v.Equals(_last)) return;
            _last = v; Pushed = true;
            Material.SetShaderParameter(Name, v);
        }
    }

    private sealed class IntBinding(ThreeUniform<int> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private int _last;
        public override void Push()
        {
            int v = h.value;
            if (Pushed && v == _last) return;
            _last = v; Pushed = true;
            Material.SetShaderParameter(Name, v);
        }
    }

    private sealed class BoolBinding(ThreeUniform<bool> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private bool _last;
        public override void Push()
        {
            bool v = h.value;
            if (Pushed && v == _last) return;
            _last = v; Pushed = true;
            Material.SetShaderParameter(Name, v);
        }
    }

    private sealed class ColorBinding(ThreeUniform<ThreeColor> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private Vector3 _last;
        public override void Push()
        {
            if (h.value is not { } c) return;
            var v = new Vector3((float)c.r, (float)c.g, (float)c.b);
            if (Pushed && v.Equals(_last)) return;
            _last = v; Pushed = true;
            Material.SetShaderParameter(Name, v);
        }
    }

    private sealed class Vector2Binding(ThreeUniform<ThreeVector2> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private Vector2 _last;
        public override void Push()
        {
            if (h.value is not { } c) return;
            var v = new Vector2((float)c.x, (float)c.y);
            if (Pushed && v.Equals(_last)) return;
            _last = v; Pushed = true;
            Material.SetShaderParameter(Name, v);
        }
    }

    private sealed class Vector3Binding(ThreeUniform<ThreeVector3> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private Vector3 _last;
        public override void Push()
        {
            if (h.value is not { } c) return;
            var v = new Vector3((float)c.x, (float)c.y, (float)c.z);
            if (Pushed && v.Equals(_last)) return;
            _last = v; Pushed = true;
            Material.SetShaderParameter(Name, v);
        }
    }

    private sealed class Vector4Binding(ThreeUniform<ThreeVector4> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private Vector4 _last;
        public override void Push()
        {
            if (h.value is not { } c) return;
            var v = new Vector4((float)c.x, (float)c.y, (float)c.z, (float)c.w);
            if (Pushed && v.Equals(_last)) return;
            _last = v; Pushed = true;
            Material.SetShaderParameter(Name, v);
        }
    }

    private sealed class Matrix4Binding(ThreeUniform<ThreeMatrix4> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private Projection _last;
        public override void Push()
        {
            if (h.value is not { } c) return;
            var v = ThreeCameraMath.ToProjection(c.elements);
            if (Pushed && v.Equals(_last)) return;
            _last = v; Pushed = true;
            Material.SetShaderParameter(Name, v);
        }
    }

    /// <summary>`double[]` of length 2–4 → a vector, any other length → a float array (like the former generic path).</summary>
    private sealed class DoubleArrayBinding(ThreeUniform<double[]> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private double[] _last = Array.Empty<double>();
        public override void Push()
        {
            if (h.value is not { } a) return;
            if (Pushed && a.AsSpan().SequenceEqual(_last)) return;
            if (_last.Length != a.Length) _last = new double[a.Length];
            a.AsSpan().CopyTo(_last);
            Pushed = true;
            switch (a.Length)
            {
                case 2: Material.SetShaderParameter(Name, new Vector2((float)a[0], (float)a[1])); break;
                case 3: Material.SetShaderParameter(Name, new Vector3((float)a[0], (float)a[1], (float)a[2])); break;
                case 4: Material.SetShaderParameter(Name, new Vector4((float)a[0], (float)a[1], (float)a[2], (float)a[3])); break;
                default:
                {
                    var f = new float[a.Length];
                    for (int i = 0; i < a.Length; i++) f[i] = (float)a[i];
                    Material.SetShaderParameter(Name, f);
                    break;
                }
            }
        }
    }

    /// <summary>`vec4[]` uniforms (the interior cutaway banks): a reused Godot array, pushed when an element changed.</summary>
    private sealed class Vector4ArrayBinding(ThreeUniform<ThreeVector4[]> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private Vector4[] _values = Array.Empty<Vector4>(), _last = Array.Empty<Vector4>();
        public override void Push()
        {
            if (h.value is not { } a) return;
            if (_values.Length != a.Length) { _values = new Vector4[a.Length]; _last = new Vector4[a.Length]; Pushed = false; }
            for (int i = 0; i < a.Length; i++)
                _values[i] = a[i] is { } v ? new Vector4((float)v.x, (float)v.y, (float)v.z, (float)v.w) : default;
            if (Pushed && _values.AsSpan().SequenceEqual(_last)) return;
            _values.AsSpan().CopyTo(_last);
            Pushed = true;
            Material.SetShaderParameter(Name, _values);
        }
    }

    private sealed class Matrix3Binding(ThreeUniform<ThreeMatrix3> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private Basis _last;
        public override void Push()
        {
            if (h.value is not { } c) return;
            var e = c.elements;
            var v = new Basis(
                new Vector3((float)e[0], (float)e[1], (float)e[2]),
                new Vector3((float)e[3], (float)e[4], (float)e[5]),
                new Vector3((float)e[6], (float)e[7], (float)e[8]));
            if (Pushed && v.Equals(_last)) return;
            _last = v; Pushed = true;
            Material.SetShaderParameter(Name, v);
        }
    }

    /// <summary>`vec3[]` from a list of vectors: a reused array, pushed when an element changed. Same values and push
    /// rule as the generic path (an empty list or a missing element converts to nothing there).</summary>
    private sealed class Vector3ListBinding(ThreeUniform<List<ThreeVector3>> h, ShaderMaterial m, StringName n) : UniformBinding(h, m, n)
    {
        private Vector3[] _values = Array.Empty<Vector3>(), _last = Array.Empty<Vector3>();
        public override void Push()
        {
            if (h.value is not { Count: > 0 } list) return;
            if (_values.Length != list.Count) { _values = new Vector3[list.Count]; _last = new Vector3[list.Count]; Pushed = false; }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is not { } v) return;
                _values[i] = new Vector3((float)v.x, (float)v.y, (float)v.z);
            }
            if (Pushed && _values.AsSpan().SequenceEqual(_last)) return;
            _values.AsSpan().CopyTo(_last);
            Pushed = true;
            Material.SetShaderParameter(Name, _values);
        }
    }

    /// <summary>A list-valued (or still empty) uniform: nothing to do while it is empty or null (the usual case), the
    /// generic path otherwise.</summary>
    private sealed class ListBinding(object holder, ShaderMaterial m, StringName n) : UniformBinding(holder, m, n)
    {
        private GenericBinding? _generic;
        public override void Push()
        {
            if (ThreeUniformValues.valueOf(Holder) is null or IList { Count: 0 }) return;
            (_generic ??= new GenericBinding(Holder, Material, Name)).Push();
        }
    }

    private sealed class NoBinding(object holder, ShaderMaterial m, StringName n) : UniformBinding(holder, m, n)
    {
        public override void Push() { }
    }

    /// <summary>Any other holder: the former generic path (reflection, boxing).</summary>
    private sealed class GenericBinding(object holder, ShaderMaterial m, StringName n) : UniformBinding(holder, m, n)
    {
        private object? _last;
        public override void Push()
        {
            if (LiveUniformBinder.Convert(ThreeUniformValues.valueOf(Holder)) is not { } value) return;
            if (Pushed && _last != null && LiveUniformBinder.Same(_last, value)) return;
            _last = value; Pushed = true;
            Material.SetShaderParameter(Name, LiveUniformBinder.ToVariant(value));
        }
    }
}

/// <summary>A reusable, change-tracked float/Vector3 array parameter (the light struct arrays).</summary>
internal sealed class ArrayParameter<T> where T : struct, IEquatable<T>
{
    private readonly StringName _name;
    public readonly T[] Values;
    private readonly T[] _last;
    private bool _pushed;

    public ArrayParameter(string name, int length)
    {
        _name = name;
        Values = new T[length];
        _last = new T[length];
    }

    public void Invalidate() => _pushed = false;

    public void Push(ShaderMaterial material)
    {
        if (_pushed && Values.AsSpan().SequenceEqual(_last)) return;
        Values.AsSpan().CopyTo(_last);
        _pushed = true;
        material.SetShaderParameter(_name, Variant.From(Values));
    }
}

internal static class UniformLists
{
    public static T? At<T>(object? value, int index) where T : class => value is IList list && index < list.Count ? list[index] as T : null;
}
