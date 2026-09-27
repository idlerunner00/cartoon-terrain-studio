// Port of packages/client/src/render/environment/impactLightPool.ts — keep in lockstep with the original.
//
// PORT NOTES
// * three.js `PointLight` / `Scene` → ThreeScene.cs stand-ins (ThreePointLight, ThreeObject3D).
// * `ImpactLightQuality = 'high' | 'medium' | 'low'` → string; `ACTIVE_BY_QUALITY` (frozen record) → a switch.
// * `light.userData[membershipTag] = true` is not modelled (the stand-ins carry no userData); the tag only drives the
//   Aether carrier-only scene filter, which reads no light value.
// * Main-thread-only renderer state (the pool mutates lights of one live scene).
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class ImpactLightPoolOptions
{
    /// <summary>Fixed program-family capacity. Use zero to remove the feature without leaving light shader loops.</summary>
    public double capacity;
    /// <summary>World-pixel coordinates -> this scene's local coordinates. Terrain uses 1, actor metre-space uses ~0.04.</summary>
    public double? positionScale;
}

public static partial class ImpactLightPoolModule
{
    internal static double ACTIVE_BY_QUALITY(string quality) => quality switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 0,
        _ => double.NaN, // `Record` lookup of an unknown key → undefined → NaN in arithmetic.
    };
}

/// <summary>
/// Tiny fixed pool of non-shadowing world-space impact lights. Keeping the light count fixed avoids shader
/// relinks during cast storms; casts only change retained transforms/colours/intensities. Simultaneous MMO
/// bursts replace the weakest/oldest slot instead of allocating or growing an unbounded light list.
/// </summary>
public sealed class ImpactLightPool
{
    private sealed class ImpactLightSlot
    {
        public ThreePointLight light;
        public double age;
        public double duration;
        public double strength;
    }

    private readonly List<ImpactLightSlot> slots = new List<ImpactLightSlot>();
    private readonly double positionScale;
    private double activeLimit = ImpactLightPoolModule.ACTIVE_BY_QUALITY("high");
    private readonly ThreeObject3D scene;

    public ImpactLightPool(ThreeObject3D scene, ImpactLightPoolOptions options)
    {
        this.scene = scene;
        this.positionScale = Math.max(1e-6, options.positionScale ?? 1);
        double capacity = Math.max(0, Math.min(3, Math.trunc(options.capacity)));
        this.activeLimit = Math.min(this.activeLimit, capacity);
        for (int index = 0; index < capacity; index++)
        {
            ThreePointLight light = new ThreePointLight(0xffffff, 0, 260, 2);
            light.name = $"fluitown-impact-light-{Js.Str(index)}";
            light.castShadow = false;
            light.visible = true;
            this.slots.Add(new ImpactLightSlot { light = light, age = 1, duration = 1, strength = 0 });
            scene.add(light);
        }
    }

    public void advance(double deltaSeconds)
    {
        double dt = Math.max(0, Math.min(0.1, Number.isFinite(deltaSeconds) ? deltaSeconds : 0));
        for (int i = 0; i < this.activeLimit; i++)
        {
            ImpactLightSlot slot = this.slots[i];
            if (slot.strength <= 0) continue;
            slot.age += dt;
            double remaining = Math.max(0, 1 - slot.age / slot.duration);
            // Fast physical release followed by a brief readable tail; no oscillation or negative light.
            double envelope = remaining * remaining * (0.42 + 0.58 * Math.exp(-slot.age * 9));
            slot.light.intensity = (750 + slot.strength * 850) * envelope;
            if (remaining <= 0) slot.strength = 0;
        }
    }
}
