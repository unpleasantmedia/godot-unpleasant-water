using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantWater;

/// <summary>A lake a generated river fills: its surface's height, and its outline in the world, seen from above.</summary>
public sealed record PlannedLake(float Level, Vector2[] Outline);

/// <summary>A stretch of generated river, in the world; it flows from a lake if it says so (an index into the plan's lakes).</summary>
public sealed record PlannedRiver(Vector3[] Points, int? FromLake);

public sealed record RiverPlan(List<PlannedRiver> Rivers, List<PlannedLake> Lakes);

/// <summary>
/// Works out where water let go at a place runs, on a grid over the ground. It spreads as water
/// does, always on from the lowest place it has reached, until it gets somewhere it can end; the
/// way back from there is the route that has to climb least, and never crosses itself. Where that
/// route has to rise out of a hollow, the hollow fills up to the top of the rise, as a lake if it is
/// big enough; a small one the river just runs level through.
/// </summary>
public sealed class WaterRiverPlanner(IWaterGround ground, IEnumerable<WaterBody> bodies)
{
    private const float CellSize = 8f;
    private const int MostSpreadCells = 500000;
    private const int MostLakeCells = 60000;
    private const float SmallestLakeArea = 1500f;
    private const float ShallowestLake = 1.5f;
    private const int CellsPerPoint = 5;
    private const int SmoothingPasses = 3;
    private const int MostOutlinePoints = 120;

    // What each cell further costs in the spread, in metres: on the flat, the shorter way wins.
    private const float DistanceCost = 0.01f;

    private static readonly (int X, int Z)[] Around = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    private readonly List<WaterBody> _bodies = bodies.ToList();
    private readonly Dictionary<(int X, int Z), float> _heights = [];

    private float HeightOf((int X, int Z) cell)
    {
        if (!_heights.TryGetValue(cell, out var height))
            _heights[cell] = height = ground.HeightAt(WorldOf(cell));
        return height;
    }

    private static Vector3 WorldOf((int X, int Z) cell) => new(cell.X * CellSize, 0f, cell.Z * CellSize);

    /// <summary>What runs from a place, stopping where the ground comes down to a height.</summary>
    public RiverPlan Plan(Vector3 source, float stopHeight)
    {
        var plan = new RiverPlan([], []);
        var start = (Mathf.RoundToInt(source.X / CellSize), Mathf.RoundToInt(source.Z / CellSize));
        if (float.IsNaN(HeightOf(start)))
            return plan;
        var (route, endLevel) = Route(start, stopHeight);

        int? fromLake = null;
        var first = 0;
        var startLevel = HeightOf(route[0]);
        var at = 0;
        while (at < route.Count - 1)
        {
            if (HeightOf(route[at + 1]) <= HeightOf(route[at]))
            {
                at++;
                continue;
            }

            // Rising out of a hollow: up to the highest it has to go before it is lower than the hollow's bottom.
            var pit = HeightOf(route[at]);
            var peak = at + 1;
            for (var i = at + 1; i < route.Count && HeightOf(route[i]) >= pit; i++)
            {
                if (HeightOf(route[i]) > HeightOf(route[peak]))
                    peak = i;
            }

            var level = HeightOf(route[peak]);
            if (peak < route.Count - 1 && level - pit >= ShallowestLake && Basin(route[at], level) is { } basin && basin.Count * CellSize * CellSize >= SmallestLakeArea)
            {
                var entered = route.FindIndex(first, basin.Contains);
                AddRiver(plan, route.GetRange(first, (entered < 0 ? at : entered) - first + 1), fromLake, startLevel, level);
                plan.Lakes.Add(new PlannedLake(level, Outline(basin, level, route[peak + 1])));
                fromLake = plan.Lakes.Count - 1;
                startLevel = level;
                first = peak;
            }

            at = peak;
        }

        var last = route.GetRange(first, route.Count - first);
        // Stopped short of a lake or sea not far on (at the stop height, say): on into it, to join it and blend with it.
        if (last.Count >= 2)
        {
            var (end, before) = (last[^1], last[Mathf.Max(last.Count - 1 - CellsPerPoint, 0)]);
            var way = new Vector2(end.X - before.X, end.Z - before.Z).Normalized();
            var cellsBefore = last.Count;
            for (var i = 1; i <= ReachForWater; i++)
            {
                (int X, int Z) ahead = (end.X + Mathf.RoundToInt(way.X * i), end.Z + Mathf.RoundToInt(way.Y * i));
                if (ahead != last[^1])
                    last.Add(ahead);
                if (_bodies.FirstOrDefault(b => b.GlobalPosition.Y <= startLevel && b.Covers(WorldOf(ahead))) is { } body)
                {
                    endLevel = body.GlobalPosition.Y;
                    break;
                }

                if (i == ReachForWater)
                    last.RemoveRange(cellsBefore, last.Count - cellsBefore);
            }
        }

        AddRiver(plan, last, fromLake, startLevel, endLevel);
        return plan;
    }

    // How many cells on from where it ends it looks for water to run into.
    private const int ReachForWater = 40;

    /// <summary>The route from a cell to where it can end, and the height it ends at if that is set by what it ends in.</summary>
    private (List<(int X, int Z)> Route, float? EndLevel) Route((int X, int Z) start, float stopHeight)
    {
        var queue = new PriorityQueue<(int X, int Z), float>();
        var parents = new Dictionary<(int X, int Z), (int X, int Z)>();
        var steps = new Dictionary<(int X, int Z), int> { [start] = 0 };
        queue.Enqueue(start, HeightOf(start));
        var end = start;
        float? endLevel = null;
        var lowest = HeightOf(start);
        var spread = 0;
        while (queue.TryDequeue(out var cell, out _) && spread++ < MostSpreadCells)
        {
            var height = HeightOf(cell);
            if (height < lowest)
                (lowest, end) = (height, cell);
            if (height <= stopHeight)
            {
                (end, endLevel) = (cell, stopHeight);
                break;
            }

            // (Only where the ground is under its surface, not to measure every cell against every outline.)
            if (cell != start && _bodies.FirstOrDefault(b => height <= b.GlobalPosition.Y && b.Covers(WorldOf(cell))) is { } into)
            {
                (end, endLevel) = (cell, into.GlobalPosition.Y);
                break;
            }

            var atEdge = false;
            foreach (var (dx, dz) in Around)
            {
                var near = (cell.X + dx, cell.Z + dz);
                var nearHeight = HeightOf(near);
                if (float.IsNaN(nearHeight))
                {
                    atEdge = true;
                    continue;
                }

                if (steps.ContainsKey(near))
                    continue;
                steps[near] = steps[cell] + 1;
                parents[near] = cell;
                queue.Enqueue(near, nearHeight + steps[near] * DistanceCost);
            }

            if (atEdge)
            {
                end = cell;
                break;
            }
        }

        var route = new List<(int X, int Z)> { end };
        while (parents.TryGetValue(route[^1], out var back))
            route.Add(back);
        route.Reverse();
        return (route, endLevel);
    }

    /// <summary>What fills from a hollow's bottom up to a height (the top of its way out not included); null if too much to be a lake.</summary>
    private HashSet<(int X, int Z)>? Basin((int X, int Z) pit, float level)
    {
        var basin = new HashSet<(int X, int Z)> { pit };
        var open = new Stack<(int X, int Z)>();
        open.Push(pit);
        while (open.TryPop(out var cell))
        {
            foreach (var (dx, dz) in Around)
            {
                var near = (cell.X + dx, cell.Z + dz);
                if (HeightOf(near) < level && basin.Add(near))
                {
                    if (basin.Count > MostLakeCells)
                        return null;
                    open.Push(near);
                }
            }
        }

        return basin;
    }

    /// <summary>A stretch of cells as a river: smoothed, thinned out, never running uphill, between the heights it starts and ends at.</summary>
    private void AddRiver(RiverPlan plan, List<(int X, int Z)> cells, int? fromLake, float startLevel, float? endLevel)
    {
        if (cells.Count < 2)
            return;
        var line = cells.Select(c => new Vector2(c.X, c.Z) * CellSize).ToArray();
        for (var pass = 0; pass < SmoothingPasses; pass++)
        {
            var smoothed = (Vector2[])line.Clone();
            for (var i = 1; i < line.Length - 1; i++)
                smoothed[i] = (line[i - 1] + line[i] * 2f + line[i + 1]) / 4f;
            line = smoothed;
        }

        var kept = line.Where((_, i) => i % CellsPerPoint == 0 || i == line.Length - 1).ToList();
        var points = new Vector3[kept.Count];
        var lowest = startLevel;
        for (var i = 0; i < points.Length; i++)
        {
            var height = ground.HeightAt(new Vector3(kept[i].X, 0f, kept[i].Y));
            lowest = float.IsNaN(height) ? lowest : Mathf.Min(lowest, height);
            if (endLevel is { } floor)
                lowest = Mathf.Max(lowest, Mathf.Min(floor, startLevel));
            points[i] = new Vector3(kept[i].X, i == 0 ? startLevel : lowest, kept[i].Y);
        }

        if (endLevel is { } end)
            points[^1].Y = Mathf.Min(end, startLevel);
        plan.Rivers.Add(new PlannedRiver(points, fromLake));
    }

    /// <summary>
    /// The outer edge of a lake's cells, reaching one cell further where the ground stands above the
    /// water anyway (the ground then hides its jagged edge), never over its way out.
    /// </summary>
    private Vector2[] Outline(HashSet<(int X, int Z)> basin, float level, (int X, int Z) outlet)
    {
        var cells = new HashSet<(int X, int Z)>(basin);
        foreach (var cell in basin)
        {
            foreach (var (dx, dz) in Around)
            {
                var near = (cell.X + dx, cell.Z + dz);
                if (near != outlet && HeightOf(near) >= level)
                    cells.Add(near);
            }
        }

        // Each cell's sides not shared with another, going round it the same way: they join up into its outline.
        var edges = new Dictionary<(int X, int Z), List<(int X, int Z)>>();
        void Edge((int X, int Z) from, (int X, int Z) to)
        {
            if (!edges.TryGetValue(from, out var list))
                edges[from] = list = [];
            list.Add(to);
        }

        foreach (var (x, z) in cells)
        {
            if (!cells.Contains((x, z - 1)))
                Edge((x, z), (x + 1, z));
            if (!cells.Contains((x + 1, z)))
                Edge((x + 1, z), (x + 1, z + 1));
            if (!cells.Contains((x, z + 1)))
                Edge((x + 1, z + 1), (x, z + 1));
            if (!cells.Contains((x - 1, z)))
                Edge((x, z + 1), (x, z));
        }

        // The corner furthest to one side can only be on the outer edge, not round an island.
        var start = edges.Keys.OrderBy(c => c.X).ThenBy(c => c.Z).First();
        var loop = new List<Vector2>();
        var corner = start;
        do
        {
            loop.Add((new Vector2(corner.X, corner.Z) - new Vector2(0.5f, 0.5f)) * CellSize);
            var ways = edges[corner];
            var next = ways[^1];
            ways.RemoveAt(ways.Count - 1);
            corner = next;
        }
        while (corner != start && edges.TryGetValue(corner, out var left) && left.Count > 0);

        for (var pass = 0; pass < SmoothingPasses; pass++)
        {
            var smoothed = new List<Vector2>(loop.Count);
            for (var i = 0; i < loop.Count; i++)
                smoothed.Add((loop[(i + loop.Count - 1) % loop.Count] + loop[i] * 2f + loop[(i + 1) % loop.Count]) / 4f);
            loop = smoothed;
        }

        var every = System.Math.Max(1, loop.Count / MostOutlinePoints);
        return loop.Where((_, i) => i % every == 0).ToArray();
    }
}
