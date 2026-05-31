using System.Collections.Concurrent;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;

namespace POE2Radar.Overlay.Navigation;

/// <summary>
/// Off-thread A* path replanner for the navigation overlay. ONE shared instance owns a SINGLE reused
/// <see cref="PathPlanner"/> (which allocates width*height buffers — tens of MB on a large grid — and is
/// NOT thread-safe) and a single worker thread that processes replan requests SERIALLY. Because exactly
/// one thread ever calls <see cref="PathPlanner.Plan"/>, the planner's internal A* buffers are never used
/// concurrently.
///
/// <para>The tick thread hands work in via <see cref="Enqueue"/> and collects finished routes via
/// <see cref="TryDrainResults"/>; the tick/render thread NEVER calls A* itself. Requests are coalesced to
/// the LATEST per target id so a fast-moving player can't back up a queue of stale plans. Terrain.Walkable
/// is immutable per area, so the worker reading the snapshotted <see cref="Poe2Live.TerrainData"/> reference
/// off-thread is safe.</para>
/// </summary>
public sealed class BackgroundReplanner : IDisposable
{
    /// <summary>A replan request: plan a route for <paramref name="TargetId"/> from start to goal on a
    /// snapshotted (immutable) terrain grid.</summary>
    public readonly record struct Request(
        string TargetId, Poe2Live.TerrainData Terrain, (int x, int y) Start, (int x, int y) Goal);

    /// <summary>A finished route: the smoothed waypoints for <paramref name="TargetId"/> toward
    /// <paramref name="Goal"/>.</summary>
    public readonly record struct Result(
        string TargetId, (float x, float y) Goal, IReadOnlyList<(int x, int y)> Waypoints);

    // Only the worker thread ever touches this planner → its A* buffers are never used concurrently.
    private readonly PathPlanner _planner = new();

    // Pending requests, coalesced to the LATEST per target id. Guarded by _gate; _signal wakes the worker.
    private readonly Dictionary<string, Request> _pending = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);

    // Finished routes for the tick thread to drain.
    private readonly ConcurrentQueue<Result> _results = new();

    private readonly Thread _worker;
    private volatile bool _stop;

    public BackgroundReplanner()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "POE2Radar.BackgroundReplanner",
        };
        _worker.Start();
    }

    /// <summary>Tick thread → worker: queue a replan, replacing any pending request for the same target.</summary>
    public void Enqueue(Request request)
    {
        bool wasEmpty;
        lock (_gate)
        {
            wasEmpty = _pending.Count == 0;
            _pending[request.TargetId] = request; // coalesce to the latest per id
        }
        // Release once per "there is at least one item now"; the worker drains all pending per wake.
        if (wasEmpty) _signal.Release();
        else _signal.Release(); // harmless extra permit; worker tolerates spurious wakes
    }

    /// <summary>Worker → tick thread: drain all finished routes. Cheap; never blocks on A*.</summary>
    public bool TryDrainResults(out List<Result> results)
    {
        results = new List<Result>();
        while (_results.TryDequeue(out var r)) results.Add(r);
        return results.Count > 0;
    }

    private void WorkerLoop()
    {
        while (!_stop)
        {
            _signal.Wait();
            if (_stop) return;

            // Drain everything currently pending (coalesced), planning each serially on the one planner.
            while (true)
            {
                Request req;
                lock (_gate)
                {
                    if (_pending.Count == 0) break;
                    // Take an arbitrary pending entry.
                    using var it = _pending.GetEnumerator();
                    it.MoveNext();
                    req = it.Current.Value;
                    _pending.Remove(req.TargetId);
                }

                try
                {
                    var waypoints = _planner.Plan(req.Terrain, req.Start, req.Goal);
                    _results.Enqueue(new Result(req.TargetId, (req.Goal.x, req.Goal.y), waypoints));
                }
                catch (Exception ex)
                {
                    // Never let a planning failure kill the worker; surface an empty route so the
                    // tracker clears its in-flight flag.
                    Console.Error.WriteLine($"Replan failed for {req.TargetId}: {ex.Message}");
                    _results.Enqueue(new Result(req.TargetId, (req.Goal.x, req.Goal.y),
                        Array.Empty<(int, int)>()));
                }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Release();           // wake the worker so it can observe _stop
        _worker.Join(1000);
        _signal.Dispose();
    }
}
