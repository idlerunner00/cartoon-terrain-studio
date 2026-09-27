// Port of packages/client/src/render/worldLighting.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public static partial class RenderWorldLighting
{
    /// <summary>The single global key-light direction, pointing from a surface toward the light.</summary>
    private const double LX = -0.44;
    private const double LY = -0.62;
    private static readonly double LMAG = Js.Truthy(Math.hypot(LX, LY)) ? Math.hypot(LX, LY) : 1;

    public static class LIGHT_DIR
    {
        public static readonly double x = LX / LMAG;
        public static readonly double y = LY / LMAG;
    }
}
