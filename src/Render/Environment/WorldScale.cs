namespace Fluitown.Render;

/// <summary>
/// The world's human scale: plants and props are sized against the height of a person standing on the terrain,
/// in world px (the production player is 32.2 px tall: a 14 px gameplay radius at 2.3× presence).
/// </summary>
public static class WorldScale
{
    public const double PLAYER_HEIGHT_PX = 32.2;
}
