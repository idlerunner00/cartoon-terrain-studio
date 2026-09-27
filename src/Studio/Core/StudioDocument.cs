using System.Collections.Generic;
using Fluitown.Domain;

namespace TerrainStudio.Core;

/// <summary>
/// The map being painted: a <see cref="TerrainArtifact"/> (the same document format as the web Terrain Studio), its
/// name and file, and a snapshot undo history (one snapshot per gesture, 80 deep, like the original editor).
/// </summary>
public sealed class StudioDocument
{
    public const int HistoryLimit = 80;

    public TerrainArtifact Artifact { get; private set; }
    public string Name { get; set; }
    public string? Notes { get; set; }
    public string? FilePath { get; set; }
    public bool Dirty { get; private set; }
    /// <summary>Incremented on every change of the artifact (edits, undo, replacement).</summary>
    public int Revision { get; private set; }

    private readonly List<TerrainArtifact> _undo = new();
    private readonly List<TerrainArtifact> _redo = new();

    public StudioDocument(TerrainArtifact artifact, string name)
    {
        Artifact = artifact;
        Name = name;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Snapshot before a gesture (a stroke, a fill, a paste …).</summary>
    public void BeginEdit()
    {
        _undo.Add(TerrainEditor.cloneTerrainArtifact(Artifact));
        if (_undo.Count > HistoryLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Drops the snapshot of a gesture that changed nothing.</summary>
    public void DiscardEdit()
    {
        if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
    }

    /// <summary>The artifact changed (after an edit function returned changed cells).</summary>
    public void Touch()
    {
        Revision++;
        Dirty = true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(Artifact);
        Artifact = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Touch();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(Artifact);
        Artifact = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Touch();
        return true;
    }

    /// <summary>A new document (blank, loaded, generated): the history starts over.</summary>
    public void Replace(TerrainArtifact artifact, string name, string? filePath = null)
    {
        Artifact = artifact;
        Name = name;
        FilePath = filePath;
        _undo.Clear();
        _redo.Clear();
        Revision++;
        Dirty = false;
    }

    public void MarkSaved(string path)
    {
        FilePath = path;
        Dirty = false;
    }
}
