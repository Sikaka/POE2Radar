namespace POE2Radar.Core.Pathfinding;

/// <summary>One cell on a path — just a grid coordinate. This is a draw-only path
/// (rendered as a guidance line); it never drives movement, and PoE2 has no gap
/// closers, so there is no per-step action.</summary>
public readonly record struct PathCell(int X, int Y);

public readonly record struct Path(bool Found, float Cost, IReadOnlyList<PathCell> Cells)
{
    public static readonly Path NoPath = new(false, 0f, Array.Empty<PathCell>());
}

/// <summary>
/// Walk-only A* pathfinder over a grid exposed via <see cref="ICellReader"/>. Per-instance
/// buffers are kept and reused via a generation stamp — no per-call buffer alloc.
///
/// <para>Cost model: walking onto a cell with value v costs <c>(6 - v) × stepDistance</c>
/// when <c>flatCost</c> is false (weighted terrain), or just <c>stepDistance</c> when
/// <c>flatCost</c> is true (binary grids where every walkable cell is equal). Diagonal
/// steps cost √2 × the cardinal cost.</para>
/// </summary>
public sealed class AStar
{
    // 8-neighbor offsets: (dx, dy, baseStepCost). Stack-friendly fixed-size data.
    private static readonly (int dx, int dy, float cost)[] Neighbors =
    {
        ( 1,  0, 1f),
        (-1,  0, 1f),
        ( 0,  1, 1f),
        ( 0, -1, 1f),
        ( 1,  1, 1.4142136f),
        ( 1, -1, 1.4142136f),
        (-1,  1, 1.4142136f),
        (-1, -1, 1.4142136f),
    };

    private readonly int _width;
    private readonly int _height;
    private readonly float[] _gScore;
    private readonly int[]   _cameFrom;
    private readonly int[]   _generation;
    private int _currentGen;

    public int Width  => _width;
    public int Height => _height;

    public AStar(int width, int height)
    {
        _width  = width;
        _height = height;
        var n = width * height;
        _gScore     = new float[n];
        _cameFrom   = new int[n];
        _generation = new int[n];
    }

    /// <summary>
    /// Pathfind from <paramref name="start"/> to <paramref name="goal"/>. Snaps either end
    /// to the nearest walkable cell within ~8 cells if needed. Returns <see cref="Path.NoPath"/>
    /// after <paramref name="maxNodes"/> dequeues.
    /// </summary>
    public Path FindPath(
        ICellReader pf, PathCell start, PathCell goal,
        int maxNodes = 200_000, bool flatCost = false)
    {
        if (pf.Width != _width || pf.Height != _height)
            throw new ArgumentException($"Reader dims {pf.Width}x{pf.Height} != A* dims {_width}x{_height}");

        var (sx, sy) = (Math.Clamp(start.X, 0, _width - 1), Math.Clamp(start.Y, 0, _height - 1));
        var (gx, gy) = (Math.Clamp(goal .X, 0, _width - 1), Math.Clamp(goal .Y, 0, _height - 1));

        if (pf.Read(sx, sy) == 0) (sx, sy) = SnapToWalkable(pf, sx, sy);
        if (pf.Read(gx, gy) == 0) (gx, gy) = SnapToWalkable(pf, gx, gy);
        if (pf.Read(sx, sy) == 0 || pf.Read(gx, gy) == 0) return Path.NoPath;

        unchecked { _currentGen++; }
        if (_currentGen == 0) { Array.Clear(_generation); _currentGen = 1; }

        var open = new PriorityQueue<int, float>();
        var startIdx = sy * _width + sx;
        var goalIdx  = gy * _width + gx;

        _gScore    [startIdx] = 0f;
        _cameFrom  [startIdx] = -1;
        _generation[startIdx] = _currentGen;
        open.Enqueue(startIdx, Heuristic(sx, sy, gx, gy));

        var dequeued = 0;
        while (open.TryDequeue(out var currentIdx, out _) && dequeued++ < maxNodes)
        {
            if (currentIdx == goalIdx)
                return ReconstructPath(currentIdx, _gScore[currentIdx]);

            var cx = currentIdx % _width;
            var cy = currentIdx / _width;
            var currentG = _gScore[currentIdx];

            foreach (var (dx, dy, baseCost) in Neighbors)
            {
                var nx = cx + dx;
                var ny = cy + dy;
                if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height) continue;

                var cellValue = pf.Read(nx, ny);
                if (cellValue == 0) continue;

                var stepCost  = flatCost ? baseCost : baseCost * (6 - cellValue);
                var tentative = currentG + stepCost;
                var nIdx = ny * _width + nx;

                var seen = _generation[nIdx] == _currentGen;
                if (seen && tentative >= _gScore[nIdx]) continue;

                _gScore    [nIdx] = tentative;
                _cameFrom  [nIdx] = currentIdx;
                _generation[nIdx] = _currentGen;
                open.Enqueue(nIdx, tentative + Heuristic(nx, ny, gx, gy));
            }
        }

        return Path.NoPath;
    }

    /// <summary>
    /// Octile distance with a tiny inflation. The pure octile heuristic is admissible (never
    /// overestimates) but produces enormous fans of equal-cost cells in open terrain — A*
    /// expands all of them. Multiplying by ~1.001 breaks the ties without meaningfully
    /// affecting path optimality (worst case 0.1 % longer than optimal) and dramatically cuts
    /// node count in long-distance searches. Standard "weighted A*" trick.
    /// </summary>
    private static float Heuristic(int x, int y, int gx, int gy)
    {
        var dx = Math.Abs(x - gx);
        var dy = Math.Abs(y - gy);
        var octile = (dx + dy) + (1.4142136f - 2f) * Math.Min(dx, dy);
        return octile * 1.001f;
    }

    private Path ReconstructPath(int goalIdx, float cost)
    {
        var cells = new List<PathCell>();
        var idx = goalIdx;
        while (idx != -1)
        {
            cells.Add(new PathCell(idx % _width, idx / _width));
            idx = _cameFrom[idx];
        }
        cells.Reverse();
        return new Path(true, cost, cells);
    }

    private (int x, int y) SnapToWalkable(ICellReader pf, int x, int y, int maxRadius = 32)
    {
        for (var r = 1; r <= maxRadius; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    var nx = x + dx;
                    var ny = y + dy;
                    if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height) continue;
                    if (pf.Read(nx, ny) > 0) return (nx, ny);
                }
            }
        }
        return (x, y);
    }
}
