// Port of packages/client/src/render/motion/criticallyDampedValue.ts — keep in lockstep with the original.
//
// PORT NOTES
// * Ported as a dependency of camera.ts. File and
//   module class follow the PORTING rules (`class CriticallyDampedValue` → module class `CriticallyDampedValueModule`).
using Fluitown.Runtime;
using static Fluitown.Render.CriticallyDampedValueModule;

namespace Fluitown.Render;

/// <summary>
/// Allocation-free scalar critically damped spring for presentation state.
///
/// The implicit integration stays finite across low frame rates and resume hitches. `frequency` is the
/// angular response rate in 1/seconds (larger = faster). The public velocity is intentional: consumers that
/// derive secondary motion (for example wing drag) can read the same resolved spring instead of differentiating
/// its sampled value again.
/// </summary>
public sealed class CriticallyDampedValue
{
    public double velocity = 0;
    public double value;

    public CriticallyDampedValue(double initialValue = 0)
    {
        this.value = finiteOr(initialValue, 0);
    }

    /// <summary>Re-anchor immediately and discard stored momentum (instance transfer, teleport, lifecycle reset).</summary>
    public double reset(double value = 0)
    {
        this.value = finiteOr(value, 0);
        this.velocity = 0;
        return this.value;
    }
}

public static partial class CriticallyDampedValueModule
{
    internal static double finiteOr(double value, double fallback)
    {
        return Number.isFinite(value) ? value : fallback;
    }
}
