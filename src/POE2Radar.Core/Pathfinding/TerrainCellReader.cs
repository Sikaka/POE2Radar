using POE2Radar.Core.Game;

namespace POE2Radar.Core.Pathfinding;

/// <summary>
/// <see cref="ICellReader"/> adapter over POE2Radar's binary walkable grid
/// (<see cref="Poe2Live.TerrainData"/>: <c>byte[] Walkable</c>, 0 = blocked / 1 = walkable,
/// indexed <c>Walkable[y * Width + x]</c>).
///
/// <para>The grid is binary, so every walkable cell has the same value (1). Pathfinding over
/// this reader should pass <c>flatCost: true</c> to A* so every walkable step costs the same.
/// <see cref="Read"/> returns the raw cell value (1 or 0) and 0 for out-of-bounds.</para>
/// </summary>
public sealed class TerrainCellReader : ICellReader
{
    private readonly byte[] _walkable;

    public int Width  { get; }
    public int Height { get; }

    public TerrainCellReader(Poe2Live.TerrainData terrain)
    {
        _walkable = terrain.Walkable;
        Width  = terrain.Width;
        Height = terrain.Height;
    }

    public int Read(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
        return _walkable[y * Width + x];
    }
}
