using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace Flui;

/// <summary>Mandatory admission for imported and default surfaces. Work follows scene
/// creation; there is no per-frame traversal of the world or its voxel geometry.</summary>
public partial class ComicRendering : Node
{
    private sealed class Import
    {
        private readonly WeakReference<BaseMaterial3D> _source;
        public readonly ComicMaterial3D Material = new();
        public Import(BaseMaterial3D source) { _source = new(source); Refresh(); source.Changed += Refresh; }
        public void Refresh()
        {
            if (!_source.TryGetTarget(out var source) || !IsInstanceValid(source)) return;
            Material.CopySurface(source);
            var next = source.NextPass;
            Material.NextPass = next == null ? null : Surface(next);
        }
    }
    private static readonly ConditionalWeakTable<BaseMaterial3D, Import> Imports = new();
    private static ComicMaterial3D? _default;
    private readonly Dictionary<ulong, WorldOutline> _outlines = new();
    private List<Node> _pending = new(256), _draining = new(256);
    private Callable _drain;
    private bool _scheduled;
    public override void _EnterTree() { _drain = Callable.From(DrainAdmissions); GetTree().NodeAdded += Admit; }
    public override void _ExitTree()
    {
        GetTree().NodeAdded -= Admit;
        _pending.Clear(); _draining.Clear(); _outlines.Clear(); _scheduled = false;
    }
    /// <summary>Fluitown integration: nodes carrying this meta own a validated non-comic pipeline (the ported
    /// three.js terrain shaders and their cameras) and are left untouched.</summary>
    public const string ExemptMeta = "comic_exempt";
    private void Admit(Node node)
    {
        if (node.HasMeta(ExemptMeta)) return;
        if (node is WorldOutline outline) {
            ulong world = outline.GetWorld3D().GetInstanceId();
            if (_outlines.TryGetValue(world, out var previous) && IsInstanceValid(previous) && previous.IsInsideTree()) {
                if (previous.HasMeta("comic_automatic_outline")) { previous.Visible = false; previous.QueueFree(); }
                else { outline.Visible = false; outline.QueueFree(); return; }
            }
            _outlines[world] = outline;
            outline.TreeExiting += () => {
                if (_outlines.TryGetValue(world, out var owned) && ReferenceEquals(owned, outline)) _outlines.Remove(world);
            };
        }
        if (node is GeometryInstance3D || node is Camera3D) {
            _pending.Add(node);
            if (!_scheduled) { _scheduled = true; _drain.CallDeferred(); }
        }
    }
    private void DrainAdmissions()
    {
        _scheduled = false;
        if (!IsInsideTree()) { _pending.Clear(); return; }
        (_pending, _draining) = (_draining, _pending);
        try {
            // Keep the existing deferred publication point: all admissions are
            // finished here, with no per-frame quota postponing visible work.
            foreach (var node in _draining) {
                if (!IsInstanceValid(node) || !node.IsInsideTree() || node.IsQueuedForDeletion()) continue;
                if (node is GeometryInstance3D geometry) NormalizeGeometry(geometry);
                else if (node is Camera3D camera) EnsureOutline(camera);
            }
        } finally {
            _draining.Clear();
        }
    }
    private void EnsureOutline(Camera3D camera)
    {
        ulong world = camera.GetWorld3D().GetInstanceId();
        if (_outlines.TryGetValue(world, out var existing) && IsInstanceValid(existing) && existing.IsInsideTree() && !existing.IsQueuedForDeletion()) return;
        var outline = new WorldOutline { WaterCutoff = false };
        outline.SetMeta("comic_automatic_outline", true);
        Node parent = camera.GetViewport();
        if (parent == GetTree().Root && GetTree().CurrentScene != null) parent = GetTree().CurrentScene;
        parent.AddChild(outline);
    }
    public static Material Surface(Material? source)
    {
        if (source == null) return _default ??= new ComicMaterial3D();
        if (source is BaseMaterial3D imported) return Imports.GetValue(imported, static original => new Import(original)).Material;
        if (source is not ShaderMaterial material || material.Shader is not {} shader)
            throw new InvalidOperationException("Unsupported 3D material: " + source.ResourcePath);
        ComicShaders.RequireCentralStyle(shader);
        if (material.NextPass is {} next) {
            var admitted = Surface(next);
            if (admitted != next) material.NextPass = admitted;
        }
        return material;
    }
    public static void NormalizeMesh(Mesh? mesh)
    {
        if (mesh == null) return;
        int count = mesh.GetSurfaceCount();
        for (int i = 0; i < count; i++) {
            var original = mesh.SurfaceGetMaterial(i);
            var comic = Surface(original);
            if (original != comic) mesh.SurfaceSetMaterial(i, comic);
        }
    }
    public static void NormalizeGeometry(GeometryInstance3D geometry)
    {
        // Engine-generated Label3D glyph materials retain their distance-field text
        // rendering. Their ink/paper palette is set here; they are not lit surfaces.
        if (geometry is Label3D label) { label.OutlineModulate = new Color(.0015f, .002f, .003f); return; }
        try {
            if (geometry.MaterialOverride is {} originalOverride) {
                var admitted = Surface(originalOverride);
                if (admitted != originalOverride) geometry.MaterialOverride = admitted;
            }
            if (geometry.MaterialOverlay is {} originalOverlay) {
                var admitted = Surface(originalOverlay);
                if (admitted != originalOverlay) geometry.MaterialOverlay = admitted;
            }
            if (geometry is MeshInstance3D instance && instance.Mesh is {} mesh) {
                NormalizeMesh(mesh);
                int count = mesh.GetSurfaceCount();
                for (int i = 0; i < count; i++)
                    if (instance.GetSurfaceOverrideMaterial(i) is {} material) {
                        var admitted = Surface(material);
                        if (admitted != material) instance.SetSurfaceOverrideMaterial(i, admitted);
                    }
            }
            else if (geometry is MultiMeshInstance3D multi) NormalizeMesh(multi.Multimesh?.Mesh);
            else if (geometry is GpuParticles3D particles)
                for (int i = 0; i < particles.DrawPasses; i++) NormalizeMesh(particles.GetDrawPassMesh(i));
            else if (geometry is CpuParticles3D cpu) NormalizeMesh(cpu.Mesh);
            else if (geometry.MaterialOverride == null) geometry.MaterialOverride = Surface(null);
        }
        catch (Exception error) {
            geometry.Visible = false;
            GD.PushError($"Comic rendering rejected {geometry.GetPath()}: {error.Message}");
            throw;
        }
    }
}
