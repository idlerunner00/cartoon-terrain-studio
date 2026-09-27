using System.Threading;
using System.Threading.Tasks;
using Fluitown.GodotApp.Rendering;
using Fluitown.GodotApp.Runtime;
using Fluitown.Render;

namespace TerrainStudio.Terrain;

/// <summary>
/// The three.js scene-graph side of the tile manager on Godot nodes, for the studio: the lane meshes (and the comic
/// look's vegetation) of a worker payload are built on the thread pool (<see cref="TerrainSceneRenderer.PrepareTile"/>
/// touches no scene tree), nodes are created on the main thread at install and mounted/unmounted through visibility,
/// one node set per <see cref="TerrainTile"/> record. The studio has no physical bodies, so no collision is built.
/// The meshes are copies, so a tile's geometry goes back to the worker pool once its nodes exist
/// (<see cref="ITerrainTileCopyingSink"/>).
/// </summary>
public sealed class StudioTileSink : ITerrainTileSink, ITerrainTilePublicationSink, ITerrainTileCopyingSink
{
    private readonly TerrainSceneRenderer _renderer;

    public StudioTileSink(TerrainSceneRenderer renderer) => _renderer = renderer;

    private sealed class Upload
    {
        public required Task<TerrainSceneRenderer.PreparedTile> Build;
        public CancellationTokenSource? Cancel;
        public TerrainSceneRenderer.TileNodes? Nodes;
    }

    /// <summary>Tiles whose nodes are currently in the scene.</summary>
    public int InstalledTiles { get; private set; }

    /// <summary>Tiles installed since the sink was created (a measure of re-bake activity).</summary>
    public int TotalInstalls { get; private set; }

    public object beginUpload(TerrainGeometryPayload geometry)
    {
        var compileToWorld = _renderer.CompileToWorld;
        var cancel = new CancellationTokenSource();
        return new Upload
        {
            Build = Task.Run(() => TerrainSceneRenderer.PrepareTile(geometry, compileToWorld), cancel.Token),
            Cancel = cancel,
        };
    }

    public bool uploadComplete(object upload) => ((Upload)upload).Build.IsCompleted;

    public void cancelUpload(object upload)
    {
        // The manager returns the payload's stores to its worker pool right after this call, so a build must be done
        // reading by then. It is: the manager cancels an unfinished build only at teardown, after its workers are gone
        // and nothing is reused any more. So nothing waits here (a waiting main thread freezes the view): a build that
        // has not started is dropped, a running one is released when it ends.
        var u = (Upload)upload;
        u.Cancel?.Cancel();
        u.Build.ContinueWith(static build =>
        {
            if (build.IsCompletedSuccessfully) build.Result.Dispose();
        }, TaskScheduler.Default);
    }

    public void install(TerrainTile tile)
    {
        var upload = tile.gpu as Upload;
        var prepared = upload != null ? upload.Build.Result : TerrainSceneRenderer.PrepareTile(tile.geometry, _renderer.CompileToWorld);
        var nodes = _renderer.CreateTile(prepared, visible: false, casterActive: false);
        if (upload == null) tile.gpu = upload = new Upload { Build = Task.FromResult(prepared) };
        upload.Nodes = nodes;
        InstalledTiles++;
        TotalInstalls++;
    }

    private static TerrainSceneRenderer.TileNodes? NodesOf(TerrainTile tile) => (tile.gpu as Upload)?.Nodes;

    public bool readyToPublish(TerrainTile tile) => true;

    public void publish(TerrainTile tile) { }

    public void setShown(TerrainTile tile, bool shown)
    {
        if (NodesOf(tile) is { } nodes) TerrainSceneRenderer.SetTileVisible(nodes, shown);
    }

    public void setCasterMounted(TerrainTile tile, bool mounted)
    {
        if (NodesOf(tile) is { } nodes) TerrainSceneRenderer.SetTileShadowCaster(nodes, mounted);
    }

    public void destroy(TerrainTile tile)
    {
        if (NodesOf(tile) is { } nodes)
        {
            TerrainSceneRenderer.FreeTile(nodes);
            InstalledTiles--;
        }
        tile.gpu = null;
    }
}
