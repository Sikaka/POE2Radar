namespace POE2Radar.Core.Pathfinding;

/// <summary>
/// Records which walkable grid tiles the player has visited, so the renderer can dim/fog
/// the unexplored portions of the map. Pure visual bookkeeping — reads nothing, writes nothing
/// to the game.
/// <para>
/// Each <see cref="Update"/> stamps a filled disc of radius <see cref="RevealRadius"/> grid cells
/// around the player into a set of packed (x,y) keys. The set is reset whenever the area instance
/// address changes (zone transition). It grows unbounded within a single area, which is acceptable:
/// a packed key is 8 bytes and the set is dropped wholesale on the next area change.
/// </para>
/// </summary>
public sealed class ExplorationTracker
{
    /// <summary>Visited tiles, keyed by <see cref="Key"/>. Replaced (not cleared) on area change.</summary>
    private HashSet<long> _visited = new();

    /// <summary>AreaInstance address the current set belongs to; a change signals a new zone.</summary>
    private nint _areaKey;

    /// <summary>Radius, in grid cells, of the disc revealed around the player each update.
    /// Settable at runtime (driven by the user's fog-radius setting); larger = more revealed per step.</summary>
    public int RevealRadius { get; set; } = 36;

    /// <summary>True if the tile at (<paramref name="x"/>, <paramref name="y"/>) has been visited.</summary>
    public bool IsExplored(int x, int y) => _visited.Contains(Key(x, y));

    /// <summary>Number of distinct tiles revealed so far in the current area.</summary>
    public int ExploredCount => _visited.Count;

    /// <summary>
    /// Stamps a <see cref="RevealRadius"/> disc around the player's grid position. Resets the
    /// visited set first if <paramref name="areaInstance"/> differs from the last call (new area).
    /// </summary>
    /// <param name="playerGridX">Player X in grid cells (truncated to int).</param>
    /// <param name="playerGridY">Player Y in grid cells (truncated to int).</param>
    /// <param name="areaInstance">Current AreaInstance address; identifies the active zone.</param>
    public void Update(float playerGridX, float playerGridY, nint areaInstance)
    {
        if (areaInstance != _areaKey) { _visited = new(); _areaKey = areaInstance; }

        var cx = (int)playerGridX;
        var cy = (int)playerGridY;
        for (var dy = -RevealRadius; dy <= RevealRadius; dy++)
            for (var dx = -RevealRadius; dx <= RevealRadius; dx++)
                if (dx * dx + dy * dy <= RevealRadius * RevealRadius)
                    _visited.Add(Key(cx + dx, cy + dy));
    }

    /// <summary>Packs a tile coordinate into a single 64-bit key (y in the high word, x in the low).</summary>
    private static long Key(int x, int y) => ((long)y << 32) | (uint)x;
}
