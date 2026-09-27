// Engine-free emulation of the parts of three.js r185's WebGLRenderer that DECIDE UNIFORM VALUES for the terrain:
// material uniform objects (`ShaderLib`/`UniformsLib` via `UniformsUtils.merge`, `material.onBeforeCompile`), the light
// uniform state (`WebGLLights.setup` / `setupView`, including the stable light sort), the per-draw material refresh
// (`WebGLMaterials.refreshMaterialUniforms`, `dfgLUT`), the directional shadow pass bookkeeping (`WebGLShadowMap.render`:
// target allocation + `LightShadow.updateMatrices`) and the render/compile entry points that tie them together.
// Ported literally from three@0.185.1 `src/renderers/{WebGLRenderer.js, webgl/WebGLLights.js, webgl/WebGLMaterials.js,
// webgl/WebGLShadowMap.js, webgl/WebGLRenderStates.js, shaders/UniformsLib.js, shaders/ShaderLib.js,
// shaders/UniformsUtils.js}` and `src/materials/{Material,MeshBasicMaterial,MeshStandardMaterial,MeshDepthMaterial}.js`.
//
// PORT NOTES
// * No GL: programs are identified by `customProgramCacheKey()` only (three's full parameter key can only force a NEW
//   uniform clone for an otherwise identical program — the refreshed/shared values are the same either way).
// * A uniform is a `ThreeUniform<T>` (ThreeValues.cs) held by name in a `ThreeUniformSet`, which keeps three's object-key
//   insertion order (assigning an existing key keeps its slot). Shared objects (haze, ink, time…) are held by identity.
// * Only the terrain's light types are set up (ambient, hemisphere, directional, point); spot/rect-area/probe lists stay
//   empty exactly as three leaves them for a scene without such lights.
// * Frustum culling, render lists/sorting, textures units, uniform upload and the VSM/point shadow paths are omitted:
//   none of them changes a uniform VALUE for the terrain scene.
// * Main-thread-only render state (see ThreeScene.cs).
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Fluitown.Runtime;

namespace Fluitown.Render;

/// <summary>
/// three's `shader.uniforms` / `materialProperties.uniforms`: uniform name → uniform object (`ThreeUniform&lt;T&gt;`),
/// in JS property order. This is the per-material store the Godot layer enumerates to bind every uniform by name.
/// </summary>
public sealed class ThreeUniformSet : IDictionary<string, object>
{
    private readonly List<string> _keys = new List<string>();
    private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);

    public object this[string key]
    {
        get => this._values[key];
        set
        {
            if (!this._values.ContainsKey(key)) this._keys.Add(key);
            this._values[key] = value;
        }
    }

    public ICollection<string> Keys => this._keys.AsReadOnly();
    public ICollection<object> Values
    {
        get
        {
            List<object> values = new List<object>(this._keys.Count);
            foreach (string key in this._keys) values.Add(this._values[key]);
            return values;
        }
    }
    public int Count => this._keys.Count;
    public bool IsReadOnly => false;

    public void Add(string key, object value)
    {
        if (this._values.ContainsKey(key)) throw new ArgumentException($"duplicate uniform '{key}'");
        this[key] = value;
    }

    public void Add(KeyValuePair<string, object> item) => this.Add(item.Key, item.Value);

    public void Clear()
    {
        this._keys.Clear();
        this._values.Clear();
    }

    public bool Contains(KeyValuePair<string, object> item) =>
        this._values.TryGetValue(item.Key, out object? value) && Equals(value, item.Value);

    public bool ContainsKey(string key) => this._values.ContainsKey(key);

    public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
    {
        foreach (KeyValuePair<string, object> entry in this) array[arrayIndex++] = entry;
    }

    public bool Remove(string key)
    {
        if (!this._values.Remove(key)) return false;
        this._keys.Remove(key);
        return true;
    }

    public bool Remove(KeyValuePair<string, object> item) => this.Contains(item) && this.Remove(item.Key);

    public bool TryGetValue(string key, out object value) => this._values.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        foreach (string key in this._keys) yield return new KeyValuePair<string, object>(key, this._values[key]);
    }

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <summary>PORT ADDITION: the name and uniform object at <paramref name="index"/> in JS property order — the Godot
    /// binder walks a set every frame and must not allocate an enumerator.</summary>
    public string keyAt(int index) => this._keys[index];

    /// <summary>PORT ADDITION: see <see cref="keyAt"/>.</summary>
    public object valueAt(int index) => this._values[this._keys[index]];

    /// <summary>The typed holder of the named uniform (throws if the name or type is wrong).</summary>
    public ThreeUniform<T> uniform<T>(string key) => (ThreeUniform<T>)this._values[key];
}

/// <summary>Reads `.value` from any `ThreeUniform&lt;T&gt;` without knowing T (the Godot binder's generic path).</summary>
public static class ThreeUniformValues
{
    private static readonly ConcurrentDictionary<Type, FieldInfo> valueFields = new ConcurrentDictionary<Type, FieldInfo>();

    public static object? valueOf(object uniform)
    {
        Type type = uniform.GetType();
        FieldInfo field = valueFields.GetOrAdd(type, t =>
            t.GetField("value", BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException($"{t} is not a uniform holder"));
        return field.GetValue(uniform);
    }
}

/// <summary>The `shader` argument three hands to `material.onBeforeCompile` (only its uniform map is modelled).</summary>
public sealed class ThreeShaderParameters
{
    public ThreeUniformSet uniforms;

    public ThreeShaderParameters(ThreeUniformSet uniforms)
    {
        this.uniforms = uniforms;
    }
}

/// <summary>three.js `Material` (render-state flags + the program hooks the terrain installs).</summary>
public abstract class ThreeMaterial
{
    public abstract string type { get; }
    public int side = ThreeConstants.FrontSide;
    public bool vertexColors = false;
    public double opacity = 1;
    public bool transparent = false;
    public bool depthTest = true;
    public bool depthWrite = true;
    public int? shadowSide = null;
    public bool colorWrite = true;
    public bool polygonOffset = false;
    public double polygonOffsetFactor = 0;
    public double polygonOffsetUnits = 0;
    public bool alphaToCoverage = false;
    public bool visible = true;
    public double alphaTest = 0;
    public int version = 0;
    public readonly Dictionary<string, object> userData = new Dictionary<string, object>();

    /// <summary>`material.onBeforeCompile(shader, renderer)`; three's default is a no-op.</summary>
    public Action<ThreeShaderParameters> onBeforeCompile = _ => { };

    /// <summary>`material.customProgramCacheKey()`; three's default returns `onBeforeCompile.toString()`.</summary>
    public Func<string> customProgramCacheKey = () => "";

    /// <summary>`needsUpdate = true` → `version++`.</summary>
    public bool needsUpdate
    {
        set
        {
            if (value) this.version++;
        }
    }

    /// <summary>`material.color` (undefined → null on materials without one).</summary>
    public virtual ThreeColor? color => null;
    /// <summary>`material.emissive` (undefined → null on materials without one).</summary>
    public virtual ThreeColor? emissive => null;
    public virtual double emissiveIntensity => 1;

    public virtual bool isMeshBasicMaterial => false;
    public virtual bool isMeshStandardMaterial => false;
    public virtual bool isMeshDepthMaterial => false;
}

/// <summary>three.js `MeshBasicMaterial`.</summary>
public sealed class ThreeMeshBasicMaterial : ThreeMaterial
{
    public override string type => "MeshBasicMaterial";
    public override bool isMeshBasicMaterial => true;

    private readonly ThreeColor _color = new ThreeColor(0xffffff); // diffuse

    public override ThreeColor color => this._color;
}

/// <summary>three.js `MeshStandardMaterial` (`defines = { STANDARD: '' }`).</summary>
public sealed class ThreeMeshStandardMaterial : ThreeMaterial
{
    public override string type => "MeshStandardMaterial";
    public override bool isMeshStandardMaterial => true;

    private readonly ThreeColor _color = new ThreeColor(0xffffff); // diffuse
    private readonly ThreeColor _emissive = new ThreeColor(0x000000);
    public double roughness = 1.0;
    public double metalness = 0.0;
    public double _emissiveIntensity = 1.0;

    public override ThreeColor color => this._color;
    public override ThreeColor emissive => this._emissive;
    public override double emissiveIntensity => this._emissiveIntensity;
}

/// <summary>three.js `MeshDepthMaterial` (`depthPacking = BasicDepthPacking`).</summary>
public sealed class ThreeMeshDepthMaterial : ThreeMaterial
{
    public override string type => "MeshDepthMaterial";
    public override bool isMeshDepthMaterial => true;
}

/* ── WebGLLights uniform structs (`UniformsCache` / `ShadowUniformsCache` records) ─────────────────────────── */

/// <summary>`{ direction: Vector3, color: Color }`.</summary>
public sealed class ThreeDirectionalLightUniforms
{
    public readonly ThreeVector3 direction = new ThreeVector3();
    public readonly ThreeColor color = new ThreeColor();
}

/// <summary>`{ position: Vector3, color: Color, distance: 0, decay: 0 }`.</summary>
public sealed class ThreePointLightUniforms
{
    public readonly ThreeVector3 position = new ThreeVector3();
    public readonly ThreeColor color = new ThreeColor();
    public double distance = 0;
    public double decay = 0;
}

/// <summary>`{ direction: Vector3, skyColor: Color, groundColor: Color }`.</summary>
public sealed class ThreeHemisphereLightUniforms
{
    public readonly ThreeVector3 direction = new ThreeVector3();
    public readonly ThreeColor skyColor = new ThreeColor();
    public readonly ThreeColor groundColor = new ThreeColor();
}

/// <summary>Directional shadow record. `shadowMapSize` is the light shadow's own `mapSize` object (by reference).</summary>
public sealed class ThreeDirectionalLightShadowUniforms
{
    public double shadowIntensity = 1;
    public double shadowBias = 0;
    public double shadowNormalBias = 0;
    public double shadowRadius = 1;
    public ThreeVector2 shadowMapSize = new ThreeVector2();
}

/// <summary>Point shadow record (never populated: the terrain's point lights do not cast).</summary>
public sealed class ThreePointLightShadowUniforms
{
    public double shadowIntensity = 1;
    public double shadowBias = 0;
    public double shadowNormalBias = 0;
    public double shadowRadius = 1;
    public ThreeVector2 shadowMapSize = new ThreeVector2();
    public double shadowCameraNear = 1;
    public double shadowCameraFar = 1000;
}

/// <summary>three.js `UniformsLib` groups + `ShaderLib` uniform sets, merged (= cloned) per program.</summary>
public static class ThreeShaderLib
{
    private static ThreeUniform<T> u<T>(T value) => new ThreeUniform<T>(value);

    private static void common(ThreeUniformSet s)
    {
        s["diffuse"] = u(new ThreeColor(0xffffff));
        s["opacity"] = u(1.0);

        s["map"] = u<ThreeTexture?>(null);
        s["mapTransform"] = u(new ThreeMatrix3());

        s["alphaMap"] = u<ThreeTexture?>(null);
        s["alphaMapTransform"] = u(new ThreeMatrix3());

        s["alphaTest"] = u(0.0);
    }

    private static void specularmap(ThreeUniformSet s)
    {
        s["specularMap"] = u<ThreeTexture?>(null);
        s["specularMapTransform"] = u(new ThreeMatrix3());
    }

    private static void envmap(ThreeUniformSet s)
    {
        s["envMap"] = u<ThreeTexture?>(null);
        s["envMapRotation"] = u(new ThreeMatrix3());
        s["reflectivity"] = u(1.0); // basic, lambert, phong
        s["ior"] = u(1.5); // physical
        s["refractionRatio"] = u(0.98); // basic, lambert, phong
        s["dfgLUT"] = u<ThreeTexture?>(null); // DFG LUT for physically-based rendering
    }

    private static void aomap(ThreeUniformSet s)
    {
        s["aoMap"] = u<ThreeTexture?>(null);
        s["aoMapIntensity"] = u(1.0);
        s["aoMapTransform"] = u(new ThreeMatrix3());
    }

    private static void lightmap(ThreeUniformSet s)
    {
        s["lightMap"] = u<ThreeTexture?>(null);
        s["lightMapIntensity"] = u(1.0);
        s["lightMapTransform"] = u(new ThreeMatrix3());
    }

    private static void bumpmap(ThreeUniformSet s)
    {
        s["bumpMap"] = u<ThreeTexture?>(null);
        s["bumpMapTransform"] = u(new ThreeMatrix3());
        s["bumpScale"] = u(1.0);
    }

    private static void normalmap(ThreeUniformSet s)
    {
        s["normalMap"] = u<ThreeTexture?>(null);
        s["normalMapTransform"] = u(new ThreeMatrix3());
        s["normalScale"] = u(new ThreeVector2(1, 1));
    }

    private static void displacementmap(ThreeUniformSet s)
    {
        s["displacementMap"] = u<ThreeTexture?>(null);
        s["displacementMapTransform"] = u(new ThreeMatrix3());
        s["displacementScale"] = u(1.0);
        s["displacementBias"] = u(0.0);
    }

    private static void emissivemap(ThreeUniformSet s)
    {
        s["emissiveMap"] = u<ThreeTexture?>(null);
        s["emissiveMapTransform"] = u(new ThreeMatrix3());
    }

    private static void metalnessmap(ThreeUniformSet s)
    {
        s["metalnessMap"] = u<ThreeTexture?>(null);
        s["metalnessMapTransform"] = u(new ThreeMatrix3());
    }

    private static void roughnessmap(ThreeUniformSet s)
    {
        s["roughnessMap"] = u<ThreeTexture?>(null);
        s["roughnessMapTransform"] = u(new ThreeMatrix3());
    }

    private static void fog(ThreeUniformSet s)
    {
        s["fogDensity"] = u(0.00025);
        s["fogNear"] = u(1.0);
        s["fogFar"] = u(2000.0);
        s["fogColor"] = u(new ThreeColor(0xffffff));
    }

    /// <summary>`UniformsLib.lights`: the `value: []` placeholders WebGLRenderer later rewires to the light state.</summary>
    private static void lights(ThreeUniformSet s)
    {
        s["ambientLightColor"] = u(Array.Empty<double>());
        s["lightProbe"] = u(new List<ThreeVector3>());
        s["directionalLights"] = u(new List<ThreeDirectionalLightUniforms>());
        s["directionalLightShadows"] = u(new List<ThreeDirectionalLightShadowUniforms>());
        s["directionalShadowMatrix"] = u(new List<ThreeMatrix4>());
        s["spotLights"] = u(new List<object>());
        s["spotLightShadows"] = u(new List<object>());
        s["spotLightMap"] = u(new List<object>());
        s["spotLightMatrix"] = u(new List<ThreeMatrix4>());
        s["pointLights"] = u(new List<ThreePointLightUniforms>());
        s["pointLightShadows"] = u(new List<ThreePointLightShadowUniforms>());
        s["pointShadowMatrix"] = u(new List<ThreeMatrix4>());
        s["hemisphereLights"] = u(new List<ThreeHemisphereLightUniforms>());
        // TODO (abelnation): RectAreaLight BRDF data needs to be moved from example to main src
        s["rectAreaLights"] = u(new List<object>());
        s["ltc_1"] = u<ThreeTexture?>(null);
        s["ltc_2"] = u<ThreeTexture?>(null);
        s["probesSH"] = u<ThreeTexture?>(null);
        s["probesMin"] = u(new ThreeVector3());
        s["probesMax"] = u(new ThreeVector3());
        s["probesResolution"] = u(new ThreeVector3());
    }

    /// <summary>`ShaderLib.basic.uniforms` (MeshBasicMaterial).</summary>
    public static ThreeUniformSet basic()
    {
        ThreeUniformSet s = new ThreeUniformSet();
        common(s);
        specularmap(s);
        envmap(s);
        aomap(s);
        lightmap(s);
        fog(s);
        return s;
    }

    /// <summary>`ShaderLib.standard.uniforms`.</summary>
    public static ThreeUniformSet standard()
    {
        ThreeUniformSet s = new ThreeUniformSet();
        common(s);
        envmap(s);
        aomap(s);
        lightmap(s);
        emissivemap(s);
        bumpmap(s);
        normalmap(s);
        displacementmap(s);
        roughnessmap(s);
        metalnessmap(s);
        fog(s);
        lights(s);
        s["emissive"] = u(new ThreeColor(0x000000));
        s["roughness"] = u(1.0);
        s["metalness"] = u(0.0);
        s["envMapIntensity"] = u(1.0);
        return s;
    }

    /// <summary>`ShaderLib.physical.uniforms` — the program of every MeshStandardMaterial (`shaderIDs`).</summary>
    public static ThreeUniformSet physical()
    {
        ThreeUniformSet s = standard();
        s["clearcoat"] = u(0.0);
        s["clearcoatMap"] = u<ThreeTexture?>(null);
        s["clearcoatMapTransform"] = u(new ThreeMatrix3());
        s["clearcoatNormalMap"] = u<ThreeTexture?>(null);
        s["clearcoatNormalMapTransform"] = u(new ThreeMatrix3());
        s["clearcoatNormalScale"] = u(new ThreeVector2(1, 1));
        s["clearcoatRoughness"] = u(0.0);
        s["clearcoatRoughnessMap"] = u<ThreeTexture?>(null);
        s["clearcoatRoughnessMapTransform"] = u(new ThreeMatrix3());
        s["dispersion"] = u(0.0);
        s["iridescence"] = u(0.0);
        s["iridescenceMap"] = u<ThreeTexture?>(null);
        s["iridescenceMapTransform"] = u(new ThreeMatrix3());
        s["iridescenceIOR"] = u(1.3);
        s["iridescenceThicknessMinimum"] = u(100.0);
        s["iridescenceThicknessMaximum"] = u(400.0);
        s["iridescenceThicknessMap"] = u<ThreeTexture?>(null);
        s["iridescenceThicknessMapTransform"] = u(new ThreeMatrix3());
        s["sheen"] = u(0.0);
        s["sheenColor"] = u(new ThreeColor(0x000000));
        s["sheenColorMap"] = u<ThreeTexture?>(null);
        s["sheenColorMapTransform"] = u(new ThreeMatrix3());
        s["sheenRoughness"] = u(1.0);
        s["sheenRoughnessMap"] = u<ThreeTexture?>(null);
        s["sheenRoughnessMapTransform"] = u(new ThreeMatrix3());
        s["transmission"] = u(0.0);
        s["transmissionMap"] = u<ThreeTexture?>(null);
        s["transmissionMapTransform"] = u(new ThreeMatrix3());
        s["transmissionSamplerSize"] = u(new ThreeVector2());
        s["transmissionSamplerMap"] = u<ThreeTexture?>(null);
        s["thickness"] = u(0.0);
        s["thicknessMap"] = u<ThreeTexture?>(null);
        s["thicknessMapTransform"] = u(new ThreeMatrix3());
        s["attenuationDistance"] = u(0.0);
        s["attenuationColor"] = u(new ThreeColor(0x000000));
        s["specularColor"] = u(new ThreeColor(1, 1, 1));
        s["specularColorMap"] = u<ThreeTexture?>(null);
        s["specularColorMapTransform"] = u(new ThreeMatrix3());
        s["specularIntensity"] = u(1.0);
        s["specularIntensityMap"] = u<ThreeTexture?>(null);
        s["specularIntensityMapTransform"] = u(new ThreeMatrix3());
        s["anisotropyVector"] = u(new ThreeVector2());
        s["anisotropyMap"] = u<ThreeTexture?>(null);
        s["anisotropyMapTransform"] = u(new ThreeMatrix3());
        return s;
    }

    /// <summary>`ShaderLib.depth.uniforms` (MeshDepthMaterial).</summary>
    public static ThreeUniformSet depth()
    {
        ThreeUniformSet s = new ThreeUniformSet();
        common(s);
        displacementmap(s);
        return s;
    }

    /// <summary>`WebGLPrograms.getUniforms(material)`: a fresh clone of the material family's ShaderLib uniforms.</summary>
    public static ThreeUniformSet getUniforms(ThreeMaterial material)
    {
        if (material.isMeshStandardMaterial) return physical();
        if (material.isMeshBasicMaterial) return basic();
        if (material.isMeshDepthMaterial) return depth();
        throw new InvalidOperationException($"no ShaderLib entry for {material.type}");
    }
}

/// <summary>three.js `WebGLLights` (one instance per render state, i.e. per scene).</summary>
public sealed class ThreeWebGLLights
{
    /// <summary>`nextVersion` is module-level in three and shared by every WebGLLights instance.</summary>
    private static int nextVersion = 0;

    public sealed class Hash
    {
        public int directionalLength = -1;
        public int pointLength = -1;
        public int spotLength = -1;
        public int rectAreaLength = -1;
        public int hemiLength = -1;

        public int numDirectionalShadows = -1;
        public int numPointShadows = -1;
        public int numSpotShadows = -1;
        public int numSpotMaps = -1;

        public int numLightProbes = -1;
    }

    public sealed class State
    {
        public int version = 0;
        public readonly Hash hash = new Hash();
        public readonly double[] ambient = { 0, 0, 0 };
        public readonly List<ThreeVector3> probe = new List<ThreeVector3>();
        public readonly List<ThreeDirectionalLightUniforms> directional = new List<ThreeDirectionalLightUniforms>();
        public readonly List<ThreeDirectionalLightShadowUniforms> directionalShadow = new List<ThreeDirectionalLightShadowUniforms>();
        public readonly List<ThreeTexture?> directionalShadowMap = new List<ThreeTexture?>();
        public readonly List<ThreeMatrix4> directionalShadowMatrix = new List<ThreeMatrix4>();
        public readonly List<object> spot = new List<object>();
        public readonly List<object> spotLightMap = new List<object>();
        public readonly List<object> spotShadow = new List<object>();
        public readonly List<ThreeTexture?> spotShadowMap = new List<ThreeTexture?>();
        public readonly List<ThreeMatrix4> spotLightMatrix = new List<ThreeMatrix4>();
        public readonly List<object> rectArea = new List<object>();
        public ThreeTexture? rectAreaLTC1 = null;
        public ThreeTexture? rectAreaLTC2 = null;
        public readonly List<ThreePointLightUniforms> point = new List<ThreePointLightUniforms>();
        public readonly List<ThreePointLightShadowUniforms> pointShadow = new List<ThreePointLightShadowUniforms>();
        public readonly List<ThreeTexture?> pointShadowMap = new List<ThreeTexture?>();
        public readonly List<ThreeMatrix4> pointShadowMatrix = new List<ThreeMatrix4>();
        public readonly List<ThreeHemisphereLightUniforms> hemi = new List<ThreeHemisphereLightUniforms>();
        public int numSpotLightShadowsWithMaps = 0;
        public int numLightProbes = 0;
    }

    // `UniformsCache` / `ShadowUniformsCache`, keyed by `light.id` → keyed by light identity.
    private readonly Dictionary<ThreeLight, object> cache = new Dictionary<ThreeLight, object>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ThreeLight, object> shadowCache = new Dictionary<ThreeLight, object>(ReferenceEqualityComparer.Instance);

    public readonly State state = new State();

    private readonly ThreeVector3 vector3 = new ThreeVector3();

    public ThreeWebGLLights()
    {
        for (int i = 0; i < 9; i++) this.state.probe.Add(new ThreeVector3());
    }

    private T cacheGet<T>(Dictionary<ThreeLight, object> store, ThreeLight light) where T : new()
    {
        if (store.TryGetValue(light, out object? uniforms)) return (T)uniforms;
        T created = new T();
        store[light] = created;
        return created;
    }

    /// <summary>`shadowCastingAndTexturingLightsFirst`.</summary>
    private static double shadowCastingAndTexturingLightsFirst(ThreeLight lightA, ThreeLight lightB)
    {
        return (lightB.castShadow ? 2 : 0) - (lightA.castShadow ? 2 : 0) + (lightB.map != null ? 1 : 0) - (lightA.map != null ? 1 : 0);
    }

    /// <summary>JS `array[index] = value` (grows the array; a gap would hold holes — never happens here).</summary>
    private static void setAt<T>(List<T> list, int index, T value)
    {
        while (list.Count < index) list.Add(default!);
        if (index == list.Count) list.Add(value);
        else list[index] = value;
    }

    /// <summary>JS `array.length = n` (truncate, or extend with holes).</summary>
    private static void setLength<T>(List<T> list, int length)
    {
        if (list.Count > length) list.RemoveRange(length, list.Count - length);
        while (list.Count < length) list.Add(default!);
    }

    public void setup(List<ThreeLight> lights)
    {
        double r = 0, g = 0, b = 0;

        for (int i = 0; i < 9; i++) this.state.probe[i].set(0, 0, 0);

        int directionalLength = 0;
        int pointLength = 0;
        int spotLength = 0;
        int rectAreaLength = 0;
        int hemiLength = 0;

        int numDirectionalShadows = 0;
        int numPointShadows = 0;
        int numSpotShadows = 0;
        int numSpotMaps = 0;
        int numSpotShadowsWithMaps = 0;

        int numLightProbes = 0;

        // ordering : [shadow casting + map texturing, map texturing, shadow casting, none ]
        Js.Sort(lights, shadowCastingAndTexturingLightsFirst);

        for (int i = 0, l = lights.Count; i < l; i++)
        {
            ThreeLight light = lights[i];

            ThreeColor color = light.color;
            double intensity = light.intensity;

            ThreeTexture? shadowMap = null;

            if (light.shadow != null && light.shadow.map != null)
            {
                // Other types (than VSM) use depth texture
                shadowMap = light.shadow.map.depthTexture ?? light.shadow.map.texture;
            }

            if (light is ThreeAmbientLight)
            {
                r += color.r * intensity;
                g += color.g * intensity;
                b += color.b * intensity;
            }
            else if (light is ThreeDirectionalLight directionalLight)
            {
                ThreeDirectionalLightUniforms uniforms = this.cacheGet<ThreeDirectionalLightUniforms>(this.cache, light);

                uniforms.color.copy(light.color).multiplyScalar(light.intensity);

                if (light.castShadow)
                {
                    ThreeLightShadow shadow = light.shadow!;

                    ThreeDirectionalLightShadowUniforms shadowUniforms = this.cacheGet<ThreeDirectionalLightShadowUniforms>(this.shadowCache, light);

                    shadowUniforms.shadowIntensity = shadow.intensity;
                    shadowUniforms.shadowBias = shadow.bias;
                    shadowUniforms.shadowNormalBias = shadow.normalBias;
                    shadowUniforms.shadowRadius = shadow.radius;
                    shadowUniforms.shadowMapSize = shadow.mapSize;

                    setAt(this.state.directionalShadow, directionalLength, shadowUniforms);
                    setAt(this.state.directionalShadowMap, directionalLength, shadowMap);
                    setAt(this.state.directionalShadowMatrix, directionalLength, light.shadow!.matrix);

                    numDirectionalShadows++;
                }

                setAt(this.state.directional, directionalLength, uniforms);

                directionalLength++;
            }
            else if (light is ThreePointLight pointLight)
            {
                ThreePointLightUniforms uniforms = this.cacheGet<ThreePointLightUniforms>(this.cache, light);

                uniforms.color.copy(light.color).multiplyScalar(light.intensity);
                uniforms.distance = pointLight.distance;
                uniforms.decay = pointLight.decay;

                if (light.castShadow)
                {
                    ThreeLightShadow shadow = light.shadow!;

                    ThreePointLightShadowUniforms shadowUniforms = this.cacheGet<ThreePointLightShadowUniforms>(this.shadowCache, light);

                    shadowUniforms.shadowIntensity = shadow.intensity;
                    shadowUniforms.shadowBias = shadow.bias;
                    shadowUniforms.shadowNormalBias = shadow.normalBias;
                    shadowUniforms.shadowRadius = shadow.radius;
                    shadowUniforms.shadowMapSize = shadow.mapSize;
                    shadowUniforms.shadowCameraNear = ((ThreePerspectiveCamera)shadow.camera).near;
                    shadowUniforms.shadowCameraFar = ((ThreePerspectiveCamera)shadow.camera).far;

                    setAt(this.state.pointShadow, pointLength, shadowUniforms);
                    setAt(this.state.pointShadowMap, pointLength, shadowMap);
                    setAt(this.state.pointShadowMatrix, pointLength, light.shadow!.matrix);

                    numPointShadows++;
                }

                setAt(this.state.point, pointLength, uniforms);

                pointLength++;
            }
            else if (light is ThreeHemisphereLight hemisphereLight)
            {
                ThreeHemisphereLightUniforms uniforms = this.cacheGet<ThreeHemisphereLightUniforms>(this.cache, light);

                uniforms.skyColor.copy(light.color).multiplyScalar(intensity);
                uniforms.groundColor.copy(hemisphereLight.groundColor).multiplyScalar(intensity);

                setAt(this.state.hemi, hemiLength, uniforms);

                hemiLength++;
            }
        }

        this.state.ambient[0] = r;
        this.state.ambient[1] = g;
        this.state.ambient[2] = b;

        Hash hash = this.state.hash;

        if (hash.directionalLength != directionalLength ||
            hash.pointLength != pointLength ||
            hash.spotLength != spotLength ||
            hash.rectAreaLength != rectAreaLength ||
            hash.hemiLength != hemiLength ||
            hash.numDirectionalShadows != numDirectionalShadows ||
            hash.numPointShadows != numPointShadows ||
            hash.numSpotShadows != numSpotShadows ||
            hash.numSpotMaps != numSpotMaps ||
            hash.numLightProbes != numLightProbes)
        {
            setLength(this.state.directional, directionalLength);
            setLength(this.state.spot, spotLength);
            setLength(this.state.rectArea, rectAreaLength);
            setLength(this.state.point, pointLength);
            setLength(this.state.hemi, hemiLength);

            setLength(this.state.directionalShadow, numDirectionalShadows);
            setLength(this.state.directionalShadowMap, numDirectionalShadows);
            setLength(this.state.pointShadow, numPointShadows);
            setLength(this.state.pointShadowMap, numPointShadows);
            setLength(this.state.spotShadow, numSpotShadows);
            setLength(this.state.spotShadowMap, numSpotShadows);
            setLength(this.state.directionalShadowMatrix, numDirectionalShadows);
            setLength(this.state.pointShadowMatrix, numPointShadows);
            setLength(this.state.spotLightMatrix, numSpotShadows + numSpotMaps - numSpotShadowsWithMaps);
            setLength(this.state.spotLightMap, numSpotMaps);
            this.state.numSpotLightShadowsWithMaps = numSpotShadowsWithMaps;
            this.state.numLightProbes = numLightProbes;

            hash.directionalLength = directionalLength;
            hash.pointLength = pointLength;
            hash.spotLength = spotLength;
            hash.rectAreaLength = rectAreaLength;
            hash.hemiLength = hemiLength;

            hash.numDirectionalShadows = numDirectionalShadows;
            hash.numPointShadows = numPointShadows;
            hash.numSpotShadows = numSpotShadows;
            hash.numSpotMaps = numSpotMaps;

            hash.numLightProbes = numLightProbes;

            this.state.version = nextVersion++;
        }
    }

    public void setupView(List<ThreeLight> lights, ThreeCamera camera)
    {
        int directionalLength = 0;
        int pointLength = 0;
        int hemiLength = 0;

        ThreeMatrix4 viewMatrix = camera.matrixWorldInverse;

        for (int i = 0, l = lights.Count; i < l; i++)
        {
            ThreeLight light = lights[i];

            if (light is ThreeDirectionalLight directionalLight)
            {
                ThreeDirectionalLightUniforms uniforms = this.state.directional[directionalLength];

                uniforms.direction.setFromMatrixPosition(light.matrixWorld);
                this.vector3.setFromMatrixPosition(directionalLight.target.matrixWorld);
                uniforms.direction.sub(this.vector3);
                uniforms.direction.transformDirection(viewMatrix);

                directionalLength++;
            }
            else if (light is ThreePointLight)
            {
                ThreePointLightUniforms uniforms = this.state.point[pointLength];

                uniforms.position.setFromMatrixPosition(light.matrixWorld);
                uniforms.position.applyMatrix4(viewMatrix);

                pointLength++;
            }
            else if (light is ThreeHemisphereLight)
            {
                ThreeHemisphereLightUniforms uniforms = this.state.hemi[hemiLength];

                uniforms.direction.setFromMatrixPosition(light.matrixWorld);
                uniforms.direction.transformDirection(viewMatrix);

                hemiLength++;
            }
        }
    }
}

/// <summary>three.js `WebGLMaterials.refreshMaterialUniforms` for the material families the terrain uses.</summary>
public static class ThreeWebGLMaterials
{
    public static void refreshMaterialUniforms(ThreeUniformSet uniforms, ThreeMaterial material)
    {
        if (material.isMeshBasicMaterial)
        {
            refreshUniformsCommon(uniforms, material);
        }
        else if (material.isMeshStandardMaterial)
        {
            refreshUniformsCommon(uniforms, material);
            refreshUniformsStandard(uniforms, (ThreeMeshStandardMaterial)material);
        }
        else if (material.isMeshDepthMaterial)
        {
            refreshUniformsCommon(uniforms, material);
        }
    }

    /// <summary>`refreshUniformsCommon`. Every map/env/light/ao branch is skipped: the terrain materials carry none.</summary>
    private static void refreshUniformsCommon(ThreeUniformSet uniforms, ThreeMaterial material)
    {
        uniforms.uniform<double>("opacity").value = material.opacity;

        if (material.color != null)
        {
            uniforms.uniform<ThreeColor>("diffuse").value.copy(material.color);
        }

        if (material.emissive != null)
        {
            uniforms.uniform<ThreeColor>("emissive").value.copy(material.emissive).multiplyScalar(material.emissiveIntensity);
        }

        if (material.alphaTest > 0)
        {
            uniforms.uniform<double>("alphaTest").value = material.alphaTest;
        }
    }

    private static void refreshUniformsStandard(ThreeUniformSet uniforms, ThreeMeshStandardMaterial material)
    {
        uniforms.uniform<double>("metalness").value = material.metalness;

        uniforms.uniform<double>("roughness").value = material.roughness;
    }
}

/// <summary>three.js `WebGLShadowMap` (PCF directional path; target allocation + `updateMatrices`).</summary>
public sealed class ThreeWebGLShadowMap
{
    public bool enabled = false;
    public bool autoUpdate = true;
    public bool needsUpdate = false;
    public int type = ThreeConstants.PCFShadowMap;

    private int _previousType;
    private readonly ThreeVector2 _shadowMapSize = new ThreeVector2();

    public ThreeWebGLShadowMap()
    {
        this._previousType = this.type;
    }

    public void render(List<ThreeLight> lights)
    {
        if (this.enabled == false) return;
        if (this.autoUpdate == false && this.needsUpdate == false) return;

        if (lights.Count == 0) return;

        if (this.type == ThreeConstants.PCFSoftShadowMap)
        {
            JsConsole.warn("WebGLShadowMap: PCFSoftShadowMap has been deprecated. Using PCFShadowMap instead.");
            this.type = ThreeConstants.PCFShadowMap;
        }

        bool typeChanged = this._previousType != this.type;

        for (int i = 0, il = lights.Count; i < il; i++)
        {
            ThreeLight light = lights[i];
            ThreeLightShadow? shadow = light.shadow;

            if (shadow == null)
            {
                JsConsole.warn("WebGLShadowMap: light has no shadow.");
                continue;
            }

            if (shadow.autoUpdate == false && shadow.needsUpdate == false) continue;

            this._shadowMapSize.copy(shadow.mapSize);

            ThreeVector2 shadowFrameExtents = shadow.getFrameExtents();

            this._shadowMapSize.multiply(shadowFrameExtents);

            // `renderer.state.buffers.depth.getReversed()` is false for the terrain renderer.
            shadow.camera._reversedDepth = false;

            if (shadow.map == null || typeChanged == true)
            {
                ThreeShadowMapTarget map = new ThreeShadowMapTarget(
                    (int)this._shadowMapSize.x,
                    (int)this._shadowMapSize.y,
                    new ThreeTexture("", "", (int)this._shadowMapSize.x, (int)this._shadowMapSize.y));
                map.depthTexture = new ThreeTexture(
                    light.name + ".shadowMap",
                    ThreeTexture.RoleTerrainSunShadowDepth,
                    (int)this._shadowMapSize.x,
                    (int)this._shadowMapSize.y);
                shadow.map = map;

                if (shadow.camera is ThreeOrthographicCamera orthographic) orthographic.updateProjectionMatrix();
                else if (shadow.camera is ThreePerspectiveCamera perspective) perspective.updateProjectionMatrix();
            }

            if (light is ThreeDirectionalLight directionalLight)
            {
                shadow.updateMatrices(directionalLight);
            }

            shadow.needsUpdate = false;
        }

        this._previousType = this.type;

        this.needsUpdate = false;
        // PORT (integration): counts executed shadow passes so the Godot layer knows when three would have
        // re-rendered the depth map (the map is a content snapshot, not a per-frame render).
        this.passes++;
    }

    /// <summary>Number of executed shadow-map passes (not part of three; read by the Godot layer).</summary>
    public int passes;
}

/// <summary>Per-material renderer bookkeeping (`renderer.properties.get(material)`).</summary>
public sealed class ThreeMaterialProperties
{
    public ThreeUniformSet? uniforms;
    public string? currentProgram;
    public int lightsStateVersion = -1;
    public bool needsLights;
    public readonly HashSet<string> programs = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>PORT ADDITION: the program cache key of <see cref="keyVersion"/> / <see cref="keySource"/> (see getProgram).</summary>
    public string? programCacheKey;
    public int keyVersion = -1;
    public Func<string>? keySource;
}

/// <summary>
/// The value-producing core of three.js `WebGLRenderer` for the terrain: scene/camera matrix updates, light uniform
/// state, shadow bookkeeping and per-material uniform refresh. No pixels are produced — the Godot layer draws.
/// </summary>
public sealed class ThreeWebGLRenderer
{
    private sealed class RenderState
    {
        public readonly ThreeWebGLLights lights = new ThreeWebGLLights();
        public readonly List<ThreeLight> lightsArray = new List<ThreeLight>();
        public readonly List<ThreeLight> shadowsArray = new List<ThreeLight>();
    }

    private readonly Dictionary<ThreeScene, RenderState> renderStates = new Dictionary<ThreeScene, RenderState>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ThreeMaterial, ThreeMaterialProperties> properties = new Dictionary<ThreeMaterial, ThreeMaterialProperties>(ReferenceEqualityComparer.Instance);
    private RenderState? currentRenderState;

    /// <summary>`WebGLClipping.uniform` — `{ value: null, needsUpdate: false }` while no clipping plane is active.</summary>
    public readonly ThreeUniform<object?> clippingUniform = new ThreeUniform<object?>(null);

    public readonly ThreeWebGLShadowMap shadowMap = new ThreeWebGLShadowMap();
    public bool autoClear = true;

    private readonly ThreeColor _clearColor = new ThreeColor(0x000000);
    private double _clearAlpha = 1;
    private double _pixelRatio = 1;
    private double _width;
    private double _height;

    public void setClearColor(ThreeColor color, double alpha = 1)
    {
        this._clearColor.set(color);
        this._clearAlpha = alpha;
    }

    public ThreeColor getClearColor(ThreeColor target) => target.copy(this._clearColor);

    public void setPixelRatio(double value)
    {
        this._pixelRatio = value;
    }

    /// <summary>`setSize(width, height, updateStyle)` (the drawing-buffer size is width × pixelRatio in three).</summary>
    public void setSize(double width, double height, bool updateStyle = true)
    {
        this._width = width;
        this._height = height;
    }

    /// <summary>`resetState()` only resets cached GL state in three.</summary>
    public void resetState() { }

    /// <summary>The live uniform set of a material that has been compiled/drawn (null before its first program).</summary>
    public ThreeUniformSet? uniformsOf(ThreeMaterial material) =>
        this.properties.TryGetValue(material, out ThreeMaterialProperties? props) ? props.uniforms : null;

    private RenderState renderStateFor(ThreeScene scene)
    {
        if (!this.renderStates.TryGetValue(scene, out RenderState? state))
        {
            state = new RenderState();
            this.renderStates[scene] = state;
        }

        return state;
    }

    private static bool materialNeedsLights(ThreeMaterial material) => material.isMeshStandardMaterial;

    /// <summary>`projectObject`: collect visible lights/shadow casters and renderable meshes in traversal order.</summary>
    private void projectObject(ThreeObject3D @object, ThreeCamera camera, List<ThreeMesh> renderables)
    {
        if (@object.visible == false) return;

        bool visible = @object.layers.test(camera.layers);

        if (visible)
        {
            if (@object is ThreeLight light)
            {
                this.currentRenderState!.lightsArray.Add(light);

                if (light.castShadow)
                {
                    this.currentRenderState.shadowsArray.Add(light);
                }
            }
            else if (@object is ThreeMesh mesh)
            {
                // Frustum culling only decides WHETHER a shared material is drawn this frame, not its values.
                if (mesh.material.visible) renderables.Add(mesh);
            }
        }

        List<ThreeObject3D> children = @object.children;

        for (int i = 0, l = children.Count; i < l; i++)
        {
            this.projectObject(children[i], camera, renderables);
        }
    }

    /// <summary>`getProgram(material, scene, object)` — uniform construction and light wiring.</summary>
    private void getProgram(ThreeMaterial material)
    {
        if (!this.properties.TryGetValue(material, out ThreeMaterialProperties? materialProperties))
        {
            materialProperties = new ThreeMaterialProperties();
            this.properties[material] = materialProperties;
        }

        ThreeWebGLLights lights = this.currentRenderState!.lights;

        int lightsStateVersion = lights.state.version;

        // PORT ADDITION: three evaluates the program key only when it has to (setProgram's needsProgramChange: a new
        // material version, …); the port asked for it every frame, and the terrain's keys are kilobytes long — most of
        // the main thread's per-frame garbage. Same key while the version and key source hold.
        string programCacheKey;
        if (materialProperties.programCacheKey != null && materialProperties.keyVersion == material.version &&
            ReferenceEquals(materialProperties.keySource, material.customProgramCacheKey))
        {
            programCacheKey = materialProperties.programCacheKey;
        }
        else
        {
            programCacheKey = material.type + "|" + material.customProgramCacheKey();
            materialProperties.programCacheKey = programCacheKey;
            materialProperties.keyVersion = material.version;
            materialProperties.keySource = material.customProgramCacheKey;
        }

        if (materialProperties.programs.Contains(programCacheKey))
        {
            // early out if program and light state is identical
            if (materialProperties.currentProgram == programCacheKey && materialProperties.lightsStateVersion == lightsStateVersion)
            {
                return;
            }
        }
        else
        {
            ThreeShaderParameters parameters = new ThreeShaderParameters(ThreeShaderLib.getUniforms(material));

            material.onBeforeCompile(parameters);

            materialProperties.programs.Add(programCacheKey);

            materialProperties.uniforms = parameters.uniforms;
        }

        ThreeUniformSet uniforms = materialProperties.uniforms!;

        // Not a (Raw)ShaderMaterial: every terrain material receives the clipping uniform.
        uniforms["clippingPlanes"] = this.clippingUniform;

        // store the light setup it was created for
        materialProperties.needsLights = materialNeedsLights(material);
        materialProperties.lightsStateVersion = lightsStateVersion;

        if (materialProperties.needsLights)
        {
            // wire up the material to this renderer's lighting state
            uniforms.uniform<double[]>("ambientLightColor").value = lights.state.ambient;
            uniforms.uniform<List<ThreeVector3>>("lightProbe").value = lights.state.probe;
            uniforms.uniform<List<ThreeDirectionalLightUniforms>>("directionalLights").value = lights.state.directional;
            uniforms.uniform<List<ThreeDirectionalLightShadowUniforms>>("directionalLightShadows").value = lights.state.directionalShadow;
            uniforms.uniform<List<object>>("spotLights").value = lights.state.spot;
            uniforms.uniform<List<object>>("spotLightShadows").value = lights.state.spotShadow;
            uniforms.uniform<List<object>>("rectAreaLights").value = lights.state.rectArea;
            uniforms.uniform<ThreeTexture?>("ltc_1").value = lights.state.rectAreaLTC1;
            uniforms.uniform<ThreeTexture?>("ltc_2").value = lights.state.rectAreaLTC2;
            uniforms.uniform<List<ThreePointLightUniforms>>("pointLights").value = lights.state.point;
            uniforms.uniform<List<ThreePointLightShadowUniforms>>("pointLightShadows").value = lights.state.pointShadow;
            uniforms.uniform<List<ThreeHemisphereLightUniforms>>("hemisphereLights").value = lights.state.hemi;

            uniforms.uniform<List<ThreeMatrix4>>("directionalShadowMatrix").value = lights.state.directionalShadowMatrix;
            uniforms.uniform<List<ThreeMatrix4>>("spotLightMatrix").value = lights.state.spotLightMatrix;
            uniforms.uniform<List<object>>("spotLightMap").value = lights.state.spotLightMap;
            uniforms.uniform<List<ThreeMatrix4>>("pointShadowMatrix").value = lights.state.pointShadowMatrix;
        }

        materialProperties.currentProgram = programCacheKey;
    }

    /// <summary>`setProgram(camera, scene, geometry, material, object)` — the uniform half of it.</summary>
    private void setProgram(ThreeMaterial material)
    {
        this.getProgram(material);

        ThreeUniformSet m_uniforms = this.properties[material].uniforms!;

        // scene.environment is null for the terrain, so the envMapIntensity branch never runs.

        // Set DFG LUT for physically-based materials
        if (m_uniforms.ContainsKey("dfgLUT"))
        {
            m_uniforms.uniform<ThreeTexture?>("dfgLUT").value = ThreeTexture.DFG_LUT;
        }

        // `fog && material.fog === true` — scene.fog is null, so the fog uniforms keep their ShaderLib defaults.
        ThreeWebGLMaterials.refreshMaterialUniforms(m_uniforms, material);
    }

    /// <summary>
    /// `render(scene, camera)`: matrices, shadow pass, light setup and the uniform refresh of every drawn material
    /// (plus the custom depth materials of shadow casters when the shadow pass runs).
    /// </summary>
    public void render(ThreeScene scene, ThreeCamera camera)
    {
        // update scene graph
        if (scene.matrixWorldAutoUpdate == true) scene.updateMatrixWorld();

        // update camera matrices and frustum
        if (camera.parent == null && camera.matrixWorldAutoUpdate == true) camera.updateMatrixWorld();

        this.currentRenderState = this.renderStateFor(scene);
        this.currentRenderState.lightsArray.Clear();
        this.currentRenderState.shadowsArray.Clear();

        // PORT (performance): one reused list and set instead of two per frame; render never
        // nests, and both are emptied again below.
        List<ThreeMesh> renderables = this._renderables;
        renderables.Clear();
        this.projectObject(scene, camera, renderables);

        bool shadowPass = this.shadowMap.enabled && (this.shadowMap.autoUpdate || this.shadowMap.needsUpdate) &&
            this.currentRenderState.shadowsArray.Count > 0;

        this.shadowMap.render(this.currentRenderState.shadowsArray);

        if (shadowPass)
        {
            // WebGLShadowMap.renderObject → getDepthMaterial: a caster's customDepthMaterial is drawn as-is.
            foreach (ThreeMesh mesh in renderables)
                if (mesh.castShadow && mesh.customDepthMaterial != null)
                    this.setProgram(mesh.customDepthMaterial);
        }

        this.currentRenderState.lights.setup(this.currentRenderState.lightsArray);

        this.currentRenderState.lights.setupView(this.currentRenderState.lightsArray, camera);

        HashSet<ThreeMaterial> refreshed = this._refreshed;
        refreshed.Clear();
        foreach (ThreeMesh mesh in renderables)
            if (refreshed.Add(mesh.material)) this.setProgram(mesh.material);
        refreshed.Clear();
        renderables.Clear();
    }

    private readonly List<ThreeMesh> _renderables = new List<ThreeMesh>();
    private readonly HashSet<ThreeMaterial> _refreshed = new HashSet<ThreeMaterial>(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// `compile(scene, camera)`: set up the scene's lights and build every material's program uniforms (no refresh,
    /// no `setupView` — three only wires the objects here).
    /// </summary>
    public void compile(ThreeScene scene, ThreeCamera camera)
    {
        this.currentRenderState = this.renderStateFor(scene);
        this.currentRenderState.lightsArray.Clear();
        this.currentRenderState.shadowsArray.Clear();

        scene.traverse(@object =>
        {
            if (!@object.visible) return;
            if (@object is ThreeLight light && light.layers.test(camera.layers))
            {
                this.currentRenderState.lightsArray.Add(light);

                if (light.castShadow)
                {
                    this.currentRenderState.shadowsArray.Add(light);
                }
            }
        });

        this.currentRenderState.lights.setup(this.currentRenderState.lightsArray);

        // Only initialize materials in the new scene, not the targetScene.
        HashSet<ThreeMaterial> materials = new HashSet<ThreeMaterial>(ReferenceEqualityComparer.Instance);
        scene.traverse(@object =>
        {
            if (@object is not ThreeMesh mesh) return;
            if (materials.Add(mesh.material)) this.getProgram(mesh.material);
        });
    }
}
