#region
using Chaos.Geometry;
using Chaos.Geometry.Abstractions;
using Chaos.Geometry.Abstractions.Definitions;
#endregion

namespace Chaos.Client.Systems;

/// <summary>
///     Pure path computation. Paths are found with a turn-aware Dijkstra whose cost is lexicographic: fewest STEPS
///     first (the path is always shortest), then fewest TURNS (no zig-zag staircases), then turns as LATE as possible
///     (head out straight toward the click, make the corrective turns near the destination). This replaced an external
///     A* + greedy reshaping pass, which could only approximate those preferences and oscillated on busy maps.
/// </summary>
public static class Pathfinder
{
    //lexicographic cost weights - STEP dominates TURN dominates lateness. A turn's lateness bias is (LATE_CAP - steps
    //so far), so an early turn costs more than a late one; LATE_CAP bounds it below TURN_W so it can never trade
    //against an extra turn, and TURN_W * any plausible turn count stays far below STEP_W
    private const long STEP_W = 10_000_000;
    private const long TURN_W = 10_000;
    private const int LATE_CAP = 4096;

    //a warp tile costs more than one extra plain step so the pathfinder avoids a warp at the cost of a
    //single-step detour, but won't go two steps out of the way - balances avoidance with practicality
    private const long WARP_PENALTY = 12_000_000;

    //cardinal directions, index-matched everywhere in this class - 0 up, 1 right, 2 down, 3 left
    private static readonly (int Dx, int Dy)[] Dirs = [(0, -1), (1, 0), (0, 1), (-1, 0)];

    /// <summary>
    ///     Returns the cardinal direction from one tile to an adjacent tile, or null if not adjacent.
    /// </summary>
    public static Direction? DirectionToward(
        int fromX,
        int fromY,
        int toX,
        int toY)
        => (toX - fromX, toY - fromY) switch
        {
            (0, -1) => Direction.Up,
            (1, 0)  => Direction.Right,
            (0, 1)  => Direction.Down,
            (-1, 0) => Direction.Left,
            _       => null
        };

    /// <summary>
    ///     Finds a path from the player to the best adjacent tile around the target entity. Returns null if no path
    ///     exists or if already adjacent (sets <paramref name="alreadyAdjacent" />).
    /// </summary>
    public static Stack<IPoint>? FindPathToEntity(
        int fromX,
        int fromY,
        int targetX,
        int targetY,
        int mapWidth,
        int mapHeight,
        IReadOnlyCollection<IPoint> blockedPoints,
        Func<int, int, bool>? isTileWalkable,
        out bool alreadyAdjacent,
        Func<int, int, bool>? isTileWarp = null)
    {
        alreadyAdjacent = false;

        if (!IsInGrid(
                fromX,
                fromY,
                mapWidth,
                mapHeight))
            return null;

        if (IsAdjacent(
                fromX,
                fromY,
                targetX,
                targetY))
        {
            alreadyAdjacent = true;

            return null;
        }

        var walkable = BuildWalkable(
            mapWidth,
            mapHeight,
            blockedPoints,
            isTileWalkable);

        //every walkable tile cardinally adjacent to the target is a valid stopping point; Dijkstra finds the
        //cheapest one in a single run
        var goals = new List<(int X, int Y)>(4);

        foreach ((var dx, var dy) in Dirs)
        {
            var adjX = targetX + dx;
            var adjY = targetY + dy;

            if (!IsInGrid(adjX, adjY, mapWidth, mapHeight))
                continue;

            if ((adjX == fromX) && (adjY == fromY))
            {
                alreadyAdjacent = true;

                return null;
            }

            if (walkable(adjX, adjY))
                goals.Add((adjX, adjY));
        }

        if (goals.Count == 0)
            return null;

        return RunNicePath(
            fromX,
            fromY,
            goals,
            mapWidth,
            mapHeight,
            walkable,
            isTileWarp);
    }

    /// <summary>
    ///     Finds a path from the player to the target tile. Returns null if no path exists.
    /// </summary>
    public static Stack<IPoint>? FindPathToTile(
        int fromX,
        int fromY,
        int toX,
        int toY,
        int mapWidth,
        int mapHeight,
        IReadOnlyCollection<IPoint> blockedPoints,
        Func<int, int, bool>? isTileWalkable,
        Func<int, int, bool>? isTileWarp = null)
    {
        if (!IsInGrid(
                fromX,
                fromY,
                mapWidth,
                mapHeight)
            || !IsInGrid(
                toX,
                toY,
                mapWidth,
                mapHeight))
            return null;

        var walkable = BuildWalkable(
            mapWidth,
            mapHeight,
            blockedPoints,
            isTileWalkable);

        if (!walkable(toX, toY))
            return null;

        return RunNicePath(
            fromX,
            fromY,
            [(toX, toY)],
            mapWidth,
            mapHeight,
            walkable,
            isTileWarp);
    }

    //turn-aware Dijkstra over (tile, heading) states. All 4 headings at the start cost 0, so the first leg is free
    //to point wherever serves the path best; every later heading change pays TURN_W plus an earliness bias
    private static Stack<IPoint>? RunNicePath(
        int fromX,
        int fromY,
        IReadOnlyList<(int X, int Y)> goals,
        int mapWidth,
        int mapHeight,
        Func<int, int, bool> walkable,
        Func<int, int, bool>? isTileWarp = null)
    {
        var tileCount = mapWidth * mapHeight;
        var dist = new long[tileCount * 4];
        var prev = new int[tileCount * 4];
        Array.Fill(dist, long.MaxValue);
        Array.Fill(prev, -1);

        var goalTiles = new HashSet<int>(goals.Count);

        foreach ((var gx, var gy) in goals)
            goalTiles.Add((gy * mapWidth) + gx);

        var queue = new PriorityQueue<int, long>();
        var startTile = (fromY * mapWidth) + fromX;

        for (var d = 0; d < 4; d++)
        {
            var s = (startTile * 4) + d;
            dist[s] = 0;
            queue.Enqueue(s, 0);
        }

        var goalState = -1;

        while (queue.TryDequeue(out var state, out var cost))
        {
            if (cost > dist[state])
                continue; //stale entry

            var tile = state / 4;

            if (goalTiles.Contains(tile))
            {
                goalState = state;

                break; //first goal pop = globally cheapest
            }

            var dir = state % 4;
            var x = tile % mapWidth;
            var y = tile / mapWidth;
            var steps = cost / STEP_W;

            for (var nd = 0; nd < 4; nd++)
            {
                var nx = x + Dirs[nd].Dx;
                var ny = y + Dirs[nd].Dy;

                if (!IsInGrid(nx, ny, mapWidth, mapHeight) || !walkable(nx, ny))
                    continue;

                var nCost = cost + STEP_W;

                if (isTileWarp is not null && isTileWarp(nx, ny))
                    nCost += WARP_PENALTY;

                if (nd != dir)
                    nCost += TURN_W + (LATE_CAP - Math.Min(steps, LATE_CAP - 1));

                var nState = ((((ny * mapWidth) + nx) * 4) + nd);

                if (nCost < dist[nState])
                {
                    dist[nState] = nCost;
                    prev[nState] = state;
                    queue.Enqueue(nState, nCost);
                }
            }
        }

        if (goalState < 0)
            return null;

        //walk the predecessor chain back to the start, then flip into a walk-ordered stack (Pop = first step)
        var outTiles = new List<IPoint>();
        var cur = goalState;

        while ((cur >= 0) && ((cur / 4) != startTile))
        {
            var tile = cur / 4;
            outTiles.Add(new Point(tile % mapWidth, tile / mapWidth));
            cur = prev[cur];
        }

        if (outTiles.Count == 0)
            return null;

        return new Stack<IPoint>(outTiles); //already goal->start order, so Pop yields the first step
    }

    private static Func<int, int, bool> BuildWalkable(
        int width,
        int height,
        IReadOnlyCollection<IPoint> blockedPoints,
        Func<int, int, bool>? isTileWalkable)
    {
        var blocked = new HashSet<int>(blockedPoints.Count);

        foreach (var p in blockedPoints)
            if ((p.X >= 0) && (p.Y >= 0) && (p.X < width) && (p.Y < height))
                blocked.Add((p.Y * width) + p.X);

        return (x, y) => !blocked.Contains((y * width) + x) && ((isTileWalkable is null) || isTileWalkable(x, y));
    }

    private static bool IsInGrid(int x, int y, int width, int height)
        => (x >= 0) && (x < width) && (y >= 0) && (y < height);

    /// <summary>
    ///     Returns true if two tile positions are cardinally adjacent (Manhattan distance == 1).
    /// </summary>
    public static bool IsAdjacent(
        int x1,
        int y1,
        int x2,
        int y2)
        => (Math.Abs(x1 - x2) + Math.Abs(y1 - y2)) == 1;
}
