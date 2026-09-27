using System;
using Fluitown.Render;
using Godot;

namespace TerrainStudio.Terrain;

/// <summary>
/// The 3D view: a perspective orbit camera around the focus, with a sky, depth fog and the renderer's perspective mode
/// (grass and herbs up close, shadows split along the view). The terrain streams a square around the target; tools keep
/// working because picking and overlays use <see cref="ActiveCamera"/>.
/// </summary>
public sealed partial class TerrainView
{
    private Camera3D? _orbitCamera;
    private Sky? _sky;
    private ProceduralSkyMaterial? _skyMaterial;
    private readonly CameraView _orbitStreaming = new();

    /// <summary>Whether the perspective 3D view is on (otherwise the original's oblique bird's-eye view).</summary>
    public bool Orbit { get; private set; }
    /// <summary>Orbit yaw (radians, 0 = looking north), pitch (radians above the horizon) and distance (metres).</summary>
    public float OrbitYaw { get; private set; }
    public float OrbitPitch { get; private set; } = 0.62f;
    public float OrbitDistance { get; private set; } = 110;

    /// <summary>The camera the picture is taken with (bird's-eye or orbit).</summary>
    public Camera3D ActiveCamera => Orbit && _orbitCamera != null ? _orbitCamera : _renderer.Camera;

    public void SetOrbit(bool on)
    {
        if (on == Orbit) return;
        Orbit = on;
        var environment = _renderer.SceneEnvironment;
        if (on)
        {
            _orbitCamera ??= CreateOrbitCamera();
            _orbitCamera.MakeCurrent();
            _skyMaterial ??= new ProceduralSkyMaterial { SunAngleMax = 30, SkyCurve = 0.12f, GroundCurve = 0.05f };
            _sky ??= new Sky { SkyMaterial = _skyMaterial };
            environment.BackgroundMode = Godot.Environment.BGMode.Sky;
            environment.Sky = _sky;
            environment.FogEnabled = true;
            environment.FogMode = Godot.Environment.FogModeEnum.Depth;
            environment.FogDensity = 1;
            environment.FogSkyAffect = 0.15f;
            _renderer.AmbientFromLights = true;
        }
        else
        {
            _renderer.Camera.MakeCurrent();
            environment.BackgroundMode = Godot.Environment.BGMode.Color;
            environment.FogEnabled = false;
            _renderer.AmbientFromLights = false;
        }
        float reach = OrbitReachMetres;
        _renderer.SetViewMode(on, reach * 0.35f, reach * 0.95f, SkyZenith, SkyHorizon);
    }

    /// <summary>How far the 3D view streams terrain and where its fog lies, as a multiple of the default (recordings use
    /// more for a deeper view; more terrain costs baking time).</summary>
    public float OrbitReachScale { get; set; } = 1;

    private float OrbitReachMetres => OrbitReachAt(OrbitDistance);

    private float OrbitReachAt(float distance) => Math.Clamp(distance * 2.6f * OrbitReachScale, 90, 420 * OrbitReachScale);

    /// <summary>Where the 3D view's fog closes at an orbit distance, in metres from the camera: nothing further shows.</summary>
    public float OrbitFogEnd(float distance) => OrbitReachAt(distance) * 1.05f;

    /// <summary>The orbit camera's vertical field of view, in degrees.</summary>
    public const float OrbitFov = 48;

    private Color SkyZenith => new Color(0.26f, 0.48f, 0.83f).Lerp(new Color(0.05f, 0.07f, 0.15f), (float)NightAmount);
    private Color SkyHorizon => new Color(0.72f, 0.83f, 0.94f).Lerp(new Color(0.12f, 0.12f, 0.2f), (float)NightAmount);

    private double NightAmount
    {
        get
        {
            double d = DayPhase;
            double fromNoon = Math.Abs(d - 0.5) * 2; // 0 noon .. 1 midnight
            return Math.Clamp((fromNoon - 0.45) / 0.35, 0, 1);
        }
    }

    private Camera3D CreateOrbitCamera()
    {
        var camera = new Camera3D
        {
            Name = "OrbitCamera",
            Projection = Camera3D.ProjectionType.Perspective,
            Fov = OrbitFov,
            Near = 0.1f,
            Far = 3000,
            CullMask = 1,
            Environment = _renderer.SceneEnvironment,
        };
        _renderer.SceneViewport.AddChild(camera);
        return camera;
    }

    public void OrbitBy(float yaw, float pitch)
    {
        OrbitYaw = Mathf.Wrap(OrbitYaw + yaw, -Mathf.Pi, Mathf.Pi);
        OrbitPitch = Math.Clamp(OrbitPitch + pitch, 0.08f, 1.45f);
    }

    public void OrbitZoom(float factor)
    {
        OrbitDistance = Math.Clamp(OrbitDistance / factor, 6, 600);
        if (Orbit) _renderer.SetViewMode(true, OrbitReachMetres * 0.35f, OrbitReachMetres * 0.95f, SkyZenith, SkyHorizon);
    }

    /// <summary>Moves the orbit target along the ground, relative to the camera (metres).</summary>
    public void OrbitMove(float right, float forward)
    {
        if (_session == null) return;
        float s = MathF.Sin(OrbitYaw), c = MathF.Cos(OrbitYaw);
        // Forward is away from the camera: the camera sits at +Z (south) of the target at yaw 0.
        double dxMetres = right * c - forward * s, dzMetres = -right * s - forward * c;
        double px = FluitownVegetation.PxPerMetre;
        _session.focusX += dxMetres * px;
        _session.focusY += dzMetres * px;
        ClampFocus();
    }

    /// <summary>Places the orbit camera for this frame (after the session moved the focus).</summary>
    private void UpdateOrbitCamera()
    {
        if (!Orbit || _orbitCamera == null || _session == null) return;
        var target = _renderer.CompileToWorld * new Vector3((float)_session.focusX, (float)_session.focusLift, (float)_session.focusY);
        var offset = new Vector3(MathF.Sin(OrbitYaw) * MathF.Cos(OrbitPitch), MathF.Sin(OrbitPitch), MathF.Cos(OrbitYaw) * MathF.Cos(OrbitPitch)) * OrbitDistance;
        _orbitCamera.GlobalPosition = target + offset;
        _orbitCamera.LookAt(target, Vector3.Up);
        var environment = _renderer.SceneEnvironment;
        float reach = OrbitReachMetres;
        environment.FogLightColor = SkyHorizon;
        environment.FogDepthBegin = reach * 0.45f;
        environment.FogDepthEnd = OrbitFogEnd(OrbitDistance);
        if (_skyMaterial != null)
        {
            _skyMaterial.SkyTopColor = SkyZenith;
            _skyMaterial.SkyHorizonColor = SkyHorizon;
            // Below the horizon the sky is the fog's colour: the far terrain fades into it without a seam (a darker
            // ground half showed as a grey band behind the fogged skyline).
            _skyMaterial.GroundHorizonColor = SkyHorizon;
            _skyMaterial.GroundBottomColor = SkyHorizon;
        }
    }

    /// <summary>The streamed square around the orbit target (clipped to an authored map).</summary>
    private void OrbitStreaming()
    {
        var session = _session!;
        double radius = Math.Clamp(OrbitReachMetres * FluitownVegetation.PxPerMetre, 1600, 9000 * OrbitReachScale);
        _orbitStreaming.left = session.focusX - radius;
        _orbitStreaming.right = session.focusX + radius;
        _orbitStreaming.top = session.focusY - radius;
        _orbitStreaming.bottom = session.focusY + radius;
        if (Source == TerrainSource.Authored && Layout != null)
        {
            double margin = Layout.tileSize * 1.5;
            _orbitStreaming.left = Math.Max(_orbitStreaming.left, Layout.originX - margin);
            _orbitStreaming.top = Math.Max(_orbitStreaming.top, Layout.originY - margin);
            _orbitStreaming.right = Math.Min(_orbitStreaming.right, Layout.originX + Layout.width * Layout.tileSize + margin);
            _orbitStreaming.bottom = Math.Min(_orbitStreaming.bottom, Layout.originY + Layout.height * Layout.tileSize + margin);
        }
        session.streamingViewOverride = _orbitStreaming;
        session.streamingDetailZoom = 1;
    }
}
