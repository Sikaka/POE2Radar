using POE2Radar.Core.Game;

namespace POE2Radar.Core.Pathfinding;

/// <summary>
/// Single entry point for draw-only path planning on the radar overlay. Given the live
/// terrain grid and a start/goal in grid coordinates, returns a short list of smoothed
/// grid waypoints (the guidance line to draw), or an empty list if no path exists.
///
/// <para>The path is DRAW-ONLY: it is rendered as a guidance line and never drives input.
/// The grid is binary (0 = blocked, 1 = walkable), so A* runs with <c>flatCost: true</c> and
/// the smoother uses <c>minWalkable = 1</c>.</para>
///
/// <para>Owns and reuses a single <see cref="AStar"/> instance sized to the grid; it is
/// rebuilt only when the grid dimensions change (i.e. on a zone with a different-sized map).
/// Not thread-safe — call from a single tick loop.</para>
/// </summary>
public sealed class PathPlanner
{
    private AStar? _astar;
    private int _gridWidth;
    private int _gridHeight;

    /// <summary>
    /// Plan a smoothed, draw-only path from <paramref name="start"/> to <paramref name="goal"/>
    /// (both in grid cells) over <paramref name="terrain"/>. Returns an empty list when no path
    /// is found.
    /// </summary>
    public IReadOnlyList<(int x, int y)> Plan(
        Poe2Live.TerrainData terrain, (int x, int y) start, (int x, int y) goal,
        int maxNodes = 1_000_000)
    {
        if (terrain.Width <= 0 || terrain.Height <= 0) return Array.Empty<(int, int)>();

        if (_astar is null || _gridWidth != terrain.Width || _gridHeight != terrain.Height)
        {
            _astar = new AStar(terrain.Width, terrain.Height);
            _gridWidth = terrain.Width;
            _gridHeight = terrain.Height;
        }

        var reader = new TerrainCellReader(terrain);
        var path = _astar.FindPath(
            reader,
            new PathCell(start.x, start.y),
            new PathCell(goal.x, goal.y),
            maxNodes,
            flatCost: true);

        if (!path.Found || path.Cells.Count == 0) return Array.Empty<(int, int)>();

        var smoothed = PathSmoother.Smooth(reader, path.Cells, minWalkable: 1);

        var result = new (int x, int y)[smoothed.Count];
        for (var i = 0; i < smoothed.Count; i++)
            result[i] = (smoothed[i].X, smoothed[i].Y);
        return result;
    }
}
