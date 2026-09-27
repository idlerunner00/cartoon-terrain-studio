using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>The live world-surface uniforms the terrain materials share by identity (time, paper sheet, weather,
/// wind and ink). Main-thread renderer state, hence a plain static.</summary>
public sealed class WorldSurfaceUniforms
{
    public ThreeUniform<double> time = null!;
    public ThreeUniform<ThreeVector3> paperSheet = null!;
    public ThreeUniform<ThreeVector4> weatherSurface = null!;
    public ThreeUniform<double> weatherSnow = null!;
    public ThreeUniform<ThreeVector2> windDirection = null!;
    public InkLookUniforms ink = null!;
}

public static class WorldSurface
{
    public static readonly WorldSurfaceUniforms UNIFORMS = new()
    {
        time = new ThreeUniform<double>(0),
        paperSheet = new ThreeUniform<ThreeVector3>(new ThreeVector3(1, 0, 1)),
        weatherSurface = new ThreeUniform<ThreeVector4>(new ThreeVector4(0, 0, 0, 0)),
        weatherSnow = new ThreeUniform<double>(0),
        windDirection = new ThreeUniform<ThreeVector2>(new ThreeVector2(Math.cos(-0.38), Math.sin(-0.38))),
        ink = InkLook.createInkLookUniforms(),
    };
}
