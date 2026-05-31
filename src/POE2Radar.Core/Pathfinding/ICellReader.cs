namespace POE2Radar.Core.Pathfinding;

/// <summary>
/// Lazy view of a single terrain layer. The pathfinder reads cells through this
/// interface rather than receiving a materialized grid — even at hundreds of cells
/// per side the A* loop only touches a few thousand of them, so we read on demand
/// and cache per-cell instead of allocating the whole grid up front.
///
/// Implementations MUST return 0 for out-of-bounds queries.
/// </summary>
public interface ICellReader
{
    /// <summary>Grid width in cells.</summary>
    int Width { get; }
    /// <summary>Grid height in cells.</summary>
    int Height { get; }

    /// <summary>
    /// Cell value at (x, y). 0 = impassable; &gt;0 = walkable (with optional cost
    /// weight, where higher is cheaper). For POE2Radar's binary grid this is 0 or 1.
    /// Out-of-bounds returns 0.
    /// </summary>
    int Read(int x, int y);
}
