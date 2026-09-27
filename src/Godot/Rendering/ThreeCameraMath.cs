using Godot;

namespace Fluitown.GodotApp.Rendering;

/// <summary>
/// Converts the original terrain camera (identity view matrix, oblique orthographic projection, world yaw on
/// the scene root) into an equivalent Godot orthographic camera placed in
/// compile space (the unrotated world the geometry is built in).
/// </summary>
public static class ThreeCameraMath
{
    public readonly record struct OrthoCamera(Transform3D Transform, float Size, float Near, float Far);

    /// <summary>Column-major 4×4 (three.js <c>elements</c>) → Godot Projection.</summary>
    public static Projection ToProjection(double[] e) =>
        new(
            new Vector4((float)e[0], (float)e[1], (float)e[2], (float)e[3]),
            new Vector4((float)e[4], (float)e[5], (float)e[6], (float)e[7]),
            new Vector4((float)e[8], (float)e[9], (float)e[10], (float)e[11]),
            new Vector4((float)e[12], (float)e[13], (float)e[14], (float)e[15]));

    /// <summary>Column-major rigid 4×4 → Godot Transform3D.</summary>
    public static Transform3D ToTransform(double[] e) =>
        new(
            new Basis(
                new Vector3((float)e[0], (float)e[1], (float)e[2]),
                new Vector3((float)e[4], (float)e[5], (float)e[6]),
                new Vector3((float)e[8], (float)e[9], (float)e[10])),
            new Vector3((float)e[12], (float)e[13], (float)e[14]));

    /// <summary>
    /// The terrain camera. <paramref name="projection"/> is the original's oblique orthographic matrix
    /// (<c>configureOrthographicProjection</c>); <paramref name="sceneMatrix"/> the yawed scene root.
    /// </summary>
    public static OrthoCamera TerrainCamera(double[] projection, double[] sceneMatrix, int width, int height)
    {
        // Row 0: (2z/w, 0, 0, 2ox/w − 1); row 1: (0, 2zH/h, −2zG/h, 1 − 2oy/h); row 2: (0, −2G/r, −2H/r, (dmax+dmin)/r).
        double zoom = projection[0] * width / 2.0;
        double ox = (projection[12] + 1.0) * width / 2.0;
        double oy = (1.0 - projection[13]) * height / 2.0;
        double h = projection[5] * height / (2.0 * zoom);
        double g = -projection[9] * height / (2.0 * zoom);
        double r = -2.0 * g / projection[6];
        double sum = projection[14] * r;
        double dmin = (sum - r) / 2.0;
        double dmax = (sum + r) / 2.0;

        // Camera basis in the yawed world: right = +X, up = (0, H, −G), back (towards the eye) = (0, G, H).
        var right = new Vector3(1, 0, 0);
        var up = new Vector3(0, (float)h, (float)-g);
        var back = new Vector3(0, (float)g, (float)h);
        double left = -ox / zoom, rightEdge = (width - ox) / zoom;
        double top = oy / zoom, bottom = (oy - height) / zoom;
        double xc = (left + rightEdge) / 2.0, yc = (top + bottom) / 2.0;
        double zc = dmax + 1.0;
        var origin = right * (float)xc + up * (float)yc + back * (float)zc;
        var yawed = new Transform3D(new Basis(right, up, back), origin);
        // Undo the scene yaw: compile-space camera = scene⁻¹ · camera.
        var scene = ToTransform(sceneMatrix);
        var compile = scene.AffineInverse() * yawed;
        return new OrthoCamera(compile, (float)(height / zoom), 0.5f, (float)(dmax - dmin + 2.0));
    }
}
