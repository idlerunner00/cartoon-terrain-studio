// Port of packages/client/src/render/environment/actorShadowProjection.ts — keep in lockstep with the original.
//
// PORT NOTES
// * three.js value types → ThreeValues.cs / ThreeScene.cs (`{ value: Texture | null }` → ThreeUniform<ThreeTexture?>,
//   `{ value: new Matrix4() }` → ThreeUniform<ThreeMatrix4>, `{ value: new Vector4(...) }` → ThreeUniform<ThreeVector4>).
// * `ActorShadowTaps = 1 | 4` → int.
// * GLSL template literals are C# raw strings holding the identical text; `${x.toFixed(8)}` → Js.ToFixed.
// * `installActorShadowProjection` binds the three shared uniform objects and wraps the program cache key exactly like
//   the original; its `fragmentShader.replace(...)` half is GLSL text surgery on three's shader chunks, which the port
//   keeps in the generated Godot shaders (godot/shaders/terrain), so that half is not reproduced here.
// * Instances are main-thread-only renderer state (one per terrain layer).
using System;
using Fluitown.Runtime;

namespace Fluitown.Render;

/// <summary>
/// Projects the ACTOR scene's sun depth map onto the TERRAIN.
///
/// Why this exists at all: the world is drawn by two Three scenes sharing one renderer, one shear camera and
/// one depth buffer. Static world geometry (walls, trees, props, dressing) casts through
/// `ThreeTerrainLayer`'s invisible actor-wall proxy batch into the terrain sun's STATIC shadow map, which is
/// why a tree has a real ground shadow. Characters, pets, monsters, summons and the town statues live in the
/// Render3D actor scene, and Three shadow maps are per-scene — so their sun shadow could only ever fall on
/// other actors, never on the floor they stand on. That gap is exactly the missing-shadow report, and the
/// pooled ink disc (BlobShadows) was its stand-in.
///
/// The cheap fix is to notice that the depth map we need is ALREADY RENDERED. The actor scene flushes its own
/// sun shadow map every frame (that is what `renderExternal(scene, dynamicShadows = true)` does), containing
/// precisely the dynamic casters and nothing else. So the terrain does not need a second depth pass, a second
/// render target, or the static map rebuilt per frame: it samples that existing depth texture as one extra
/// projected lookup. The added cost is a handful of hardware-PCF taps on an already-drawn opaque pass —
/// ZERO extra draw calls, zero extra geometry, zero extra memory.
///
/// Two properties make the sampling unusually simple:
///  - The actor scene's root carries `scale = 1 / SCENE_UNITS_PER_WORLD_PX`, so its WORLD space is the
///    terrain's world-pixel space and the light's shadow matrix maps world px straight to shadow UV. The
///    terrain only has to fold in its own scene matrix (see <c>ActorShadowProjection.publish</c>) so a world
///    yaw stays exact.
///  - Caster set and receiver set are DISJOINT — the terrain is not in the actor depth map at all. Shadow
///    acne is therefore structurally impossible and the depth bias is zero; no peter-panning trade-off, and
///    the shadow stays welded to the foot that casts it.
///
/// The map read here is the one rendered at the END of the previous frame (terrain draws before the actor
/// scene). That is a deliberate one-frame latency: hoisting the depth pass in front of the terrain would cost
/// a second `updateMatrixWorld` over the whole actor graph every frame — far more than the artefact, which is
/// a shadow trailing its caster by one frame of movement (≈0.1 world px at running speed).
/// </summary>
public static partial class ActorShadowProjectionModule
{
    /// <summary>No bias: caster and receiver geometry are disjoint, so there is nothing to self-shadow.</summary>
    public const double ACTOR_SHADOW_BIAS = 0;

    /* ── The ONE ground-shadow mark ──────────────────────────────────────────────────────────────────────
     *
     * HANDINK's binding rule is that the whole frame reads as one illustrator's work on one sheet, and a cast
     * shadow is a MARK on that sheet. A tree's shadow and a player's shadow therefore have to be the same mark:
     * same width, same brush, same quantisation into a drawn edge by the wash bander.
     *
     * They were not. The runtime handed this projection the ACTOR SUN's own PCF radius — `LightRig3D`'s
     * `DirectionalLight.shadow.radius`, 4 texels at the high tier — because that number happened to be nearby.
     * That dial is authored for actor-ON-actor self shadowing inside the volumetric scene, where a soft
     * terminator across a face is exactly right. Spent on the ground instead it drew a penumbra ~3.5× the width
     * of the tree shadow next to it (measured in `actorShadowMark.test.ts`), which is precisely the owner's
     * report: the actor's shadow is a gentle smudge beside a confidently drawn edge. Nothing was missing and
     * nothing was too bright — the EDGE was wrong.
     */

    /// <summary>
    /// Add the ground projection to an already-authored receiving material by wrapping its existing
    /// `onBeforeCompile`/`customProgramCacheKey` — the terrain's surface and water shaders are long enough that a
    /// second author editing them in place is how a duplicate of this logic would eventually appear.
    ///
    /// A disabled projection installs NOTHING: no uniform, no GLSL, no cache-key suffix. `?actorgroundshadows=0`
    /// therefore compiles the exact program family that shipped before this feature, which is what makes the flag
    /// a usable A/B rather than a differently-broken second path.
    ///
    /// The receiving material must be a Three lit material (it needs `&lt;lights_fragment_begin&gt;`) and must expose a
    /// `varying vec3 vMmoratWorld` holding its scene-space position — both true for every terrain receiver.
    /// </summary>
    public static void installActorShadowProjection(
        ThreeMaterial material,
        ActorShadowProjection projection,
        int taps)
    {
        if (!projection.enabled) return;
        Action<ThreeShaderParameters> @base = material.onBeforeCompile;
        Func<string> baseCacheKey = material.customProgramCacheKey;
        material.onBeforeCompile = (shader) =>
        {
            @base(shader);
            shader.uniforms["uMmoratActorShadowMap"] = projection.map;
            shader.uniforms["uMmoratActorShadowMatrix"] = projection.matrix;
            shader.uniforms["uMmoratActorShadow"] = projection.@params;
            // fragmentShader: `#include <common>` += ACTOR_SHADOW_UNIFORM_GLSL + actorShadowParsGlsl(taps);
            // `#include <lights_fragment_begin>` += ACTOR_SHADOW_APPLY_GLSL (see the file header).
        };
        // v2 = the matched HANDINK mark (Vogel disk at the terrain sun's kernel width). The suffix is what keeps a
        // receiver that opted into the projection from sharing a program with one that did not.
        material.customProgramCacheKey = () => $"{baseCacheKey()}-actor-ground-shadow-v2-{Js.Str(taps)}tap";
    }
}

/// <summary>
/// The shared uniform refs handed to every consuming material, plus the per-frame publish seam. One instance
/// per terrain layer: surface and water hold the SAME uniform objects, so a frame update is three writes for
/// the whole world rather than one per tile material.
/// </summary>
public sealed class ActorShadowProjection
{
    /// <summary>Three's `DepthTexture` from the actor sun's shadow target — a `sampler2DShadow` under PCF.</summary>
    public readonly ThreeUniform<ThreeTexture?> map = new ThreeUniform<ThreeTexture?>(null);
    /// <summary>World-px → shadow UV, with the receiving scene's own world matrix already folded in.</summary>
    public readonly ThreeUniform<ThreeMatrix4> matrix = new ThreeUniform<ThreeMatrix4>(new ThreeMatrix4());
    public readonly ThreeUniform<ThreeVector4> @params = new ThreeUniform<ThreeVector4>(
        new ThreeVector4(0, ActorShadowProjectionModule.ACTOR_SHADOW_BIAS, 1, 1.0 / 1024));

    private ThreeTexture? fallbackMap = null;

    public readonly bool enabled;

    public ActorShadowProjection(bool enabled)
    {
        this.enabled = enabled;
    }

    /// <summary>
    /// Adopt the terrain sun's already-rendered comparison depth target as the disabled-state sampler. A detached
    /// `DepthTexture` is not enough on ANGLE: the custom shadow sampler needs a depth texture backed by a completed
    /// render target, even though strength zero exits before the lookup.
    /// </summary>
    public void bindFallback(ThreeTexture? map)
    {
        if (!this.enabled || map == null) return;
        this.fallbackMap = map;
        if (!this.active) this.map.value = map;
    }

    public bool active => this.enabled && this.@params.value.x > 0;
}
