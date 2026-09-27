// Port of packages/client/src/render/environment/types.ts — keep in lockstep with the original.
//
// PORT NOTE: types.ts is a type-only module; this file is its mirrored path.

namespace Fluitown.Render;

/// <summary>A world-space point in the terrain geometry basis: X = east, Y = up, Z = south.</summary>
public sealed class P3
{
    public double x;
    public double y;
    public double z;

    public P3() { }

    public P3(double x, double y, double z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }
}

/// <summary>Visible world-space rectangle the camera currently frames (used to cull/cover background layers).</summary>
public sealed class CameraView
{
    public double left;
    public double right;
    public double top;
    public double bottom;
}

/// <summary>World-space bounds of one streamed terrain bake tile.</summary>
public sealed class TerrainBakeFrame
{
    public int i0;
    public int j0;
    public int width;
    public int height;
    public double originX;
    public double originY;
    public double tileSize;
}
