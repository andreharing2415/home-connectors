using System.Collections.Immutable;
using System.Globalization;

namespace HomeConnectors;

/// <summary>
/// What the hub works out on the map without the robot: which rectangles keep
/// the robot out of a room, which small blobs are furniture legs rather than
/// walls, and how a map the owner arranged lines up with one the robot drew anew.
/// </summary>
public static class VacuumMapGeometry
{
    /// <summary>
    /// Fills the small enclosed blobs inside rooms — a vase, a table leg, a
    /// chair — with the room around them. A blob is a run of wall or empty
    /// cells, no larger than <paramref name="maxCells"/>, that touches no edge
    /// of the grid and has only room cells around it. One that leans on an
    /// outer wall is left alone: from the grid it cannot be told apart from the
    /// wall itself.
    /// <para>
    /// This changes the picture the hub draws and the rectangles it computes.
    /// The robot has no command to edit cells, and it would see the vase again
    /// on the next run anyway.
    /// </para>
    /// </summary>
    public static (VacuumMap Map, int Filled) FillSmallObstacles(VacuumMap map, int maxCells)
    {
        ArgumentNullException.ThrowIfNull(map);

        var w = map.Width;
        var h = map.Height;
        var cells = map.Cells.ToArray();
        var seen = new bool[cells.Length];
        var stack = new Stack<int>();
        var blob = new List<int>();
        var filled = 0;

        for (var start = 0; start < cells.Length; start++)
        {
            if (seen[start] || IsRoom(cells[start]))
            {
                continue;
            }

            blob.Clear();
            var touchesEdge = false;
            var neighbours = new Dictionary<byte, int>();
            seen[start] = true;
            stack.Push(start);

            while (stack.Count > 0)
            {
                var i = stack.Pop();
                blob.Add(i);
                var x = i % w;
                var y = i / w;

                if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                {
                    touchesEdge = true;
                }

                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }

            if (touchesEdge || blob.Count > maxCells || neighbours.Count == 0)
            {
                continue;
            }

            var room = neighbours.MaxBy(p => p.Value).Key;

            foreach (var i in blob)
            {
                cells[i] = room;
            }

            filled++;

            void Visit(int x, int y)
            {
                if (x < 0 || y < 0 || x >= w || y >= h)
                {
                    return;
                }

                var j = (y * w) + x;

                if (IsRoom(cells[j]))
                {
                    neighbours[cells[j]] = neighbours.GetValueOrDefault(cells[j]) + 1;
                    return;
                }

                if (!seen[j])
                {
                    seen[j] = true;
                    stack.Push(j);
                }
            }
        }

        if (filled == 0)
        {
            return (map, 0);
        }

        var result = ImmutableArray.Create(cells);
        var names = map.Rooms.ToDictionary(r => r.Id, r => r.Name);

        return (map with
        {
            Cells = result,
            Rooms = VacuumMap.Measure(w, h, result, map.Left, map.Top, map.Grid, names),
        }, filled);
    }

    /// <summary>
    /// Rectangles that between them cover a room without reaching into any
    /// other: what "delete this area" becomes on a robot that cannot delete.
    /// A rectangle may take in walls and empty space — the robot cannot go
    /// there anyway — but never a cell of another room. Each one is the
    /// rectangle that covers the most room cells still uncovered, taken
    /// greedily until <paramref name="coverage"/> of the room is covered or
    /// <paramref name="maxRects"/> are used.
    /// </summary>
    public static ImmutableArray<MapRect> CoverRoom(VacuumMap map, int roomId, double coverage = 0.98, int maxRects = 6)
    {
        ArgumentNullException.ThrowIfNull(map);

        var w = map.Width;
        var h = map.Height;
        var target = (byte)roomId;
        var total = 0;
        int x0 = w, y0 = h, x1 = -1, y1 = -1;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (map.At(x, y) == target)
                {
                    total++;
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x);
                    y1 = Math.Max(y1, y);
                }
            }
        }

        if (total == 0)
        {
            return [];
        }

        // One cell of margin, so the walls around the room can be covered too.
        x0 = Math.Max(0, x0 - 1);
        y0 = Math.Max(0, y0 - 1);
        x1 = Math.Min(w - 1, x1 + 1);
        y1 = Math.Min(h - 1, y1 + 1);
        var bw = x1 - x0 + 1;
        var bh = y1 - y0 + 1;

        var open = new bool[bw * bh];
        var wanted = new bool[bw * bh];

        for (var y = 0; y < bh; y++)
        {
            for (var x = 0; x < bw; x++)
            {
                var c = map.At(x0 + x, y0 + y);
                open[(y * bw) + x] = !IsRoom(c) || c == target;
                wanted[(y * bw) + x] = c == target;
            }
        }

        var done = 0;
        var rects = ImmutableArray.CreateBuilder<MapRect>();

        while (done < total * coverage && rects.Count < maxRects)
        {
            if (Best(bw, bh, open, wanted) is not { Gain: >= 4 } r)
            {
                break;
            }

            for (var y = r.Y; y < r.Y + r.H; y++)
            {
                for (var x = r.X; x < r.X + r.W; x++)
                {
                    wanted[(y * bw) + x] = false;
                }
            }

            done += r.Gain;
            rects.Add(new MapRect(
                map.Left + ((x0 + r.X) * map.Grid),
                map.Top + ((y0 + r.Y) * map.Grid),
                map.Left + ((x0 + r.X + r.W) * map.Grid),
                map.Top + ((y0 + r.Y + r.H) * map.Grid)));
        }

        return rects.ToImmutable();
    }

    /// <summary>
    /// Among the maximal all-open rectangles of the grid (the histogram
    /// method's candidates), the one holding the most wanted cells.
    /// </summary>
    private static (int X, int Y, int W, int H, int Gain)? Best(int w, int h, bool[] open, bool[] wanted)
    {
        // Prefix sums of wanted cells, so any rectangle is scored in O(1).
        var sum = new int[(w + 1) * (h + 1)];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                sum[((y + 1) * (w + 1)) + x + 1] = (wanted[(y * w) + x] ? 1 : 0)
                    + sum[(y * (w + 1)) + x + 1] + sum[((y + 1) * (w + 1)) + x] - sum[(y * (w + 1)) + x];
            }
        }

        int Count(int x, int y, int rw, int rh) =>
            sum[((y + rh) * (w + 1)) + x + rw] - sum[(y * (w + 1)) + x + rw] - sum[((y + rh) * (w + 1)) + x] + sum[(y * (w + 1)) + x];

        var heights = new int[w];
        (int X, int Y, int W, int H, int Gain)? best = null;
        var stack = new Stack<int>();

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                heights[x] = open[(y * w) + x] ? heights[x] + 1 : 0;
            }

            stack.Clear();

            for (var x = 0; x <= w; x++)
            {
                var current = x == w ? 0 : heights[x];

                while (stack.Count > 0 && heights[stack.Peek()] >= current)
                {
                    var height = heights[stack.Pop()];
                    var left = stack.Count == 0 ? 0 : stack.Peek() + 1;

                    if (height > 0)
                    {
                        var top = y - height + 1;
                        var gain = Count(left, top, x - left, height);

                        if (gain > (best?.Gain ?? 0))
                        {
                            best = (left, top, x - left, height, gain);
                        }
                    }
                }

                stack.Push(x);
            }
        }

        return best;
    }

    /// <summary>
    /// How far the new map is shifted from the old one, taken from the charger:
    /// the base does not move when the robot loses its reference, so the shift
    /// between the two chargers is the shift between the two maps.
    /// </summary>
    public static (int Dx, int Dy) Offset(VacuumMap reference, VacuumMap current)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(current);

        return reference.Charger is { } a && current.Charger is { } b ? (b.X - a.X, b.Y - a.Y) : (0, 0);
    }

    /// <summary>
    /// For each room of <paramref name="current"/>, how many of its cells fall
    /// in each room of <paramref name="reference"/> once shifted by the charger
    /// offset. The basis for carrying names, merges and hidden rooms across.
    /// </summary>
    public static Dictionary<int, Dictionary<int, int>> Overlap(VacuumMap reference, VacuumMap current)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(current);

        var (dx, dy) = Offset(reference, current);
        var result = new Dictionary<int, Dictionary<int, int>>();

        for (var y = 0; y < current.Height; y++)
        {
            for (var x = 0; x < current.Width; x++)
            {
                var id = current.At(x, y);

                if (!IsRoom(id))
                {
                    continue;
                }

                var p = current.World(x, y);
                var (rx, ry) = reference.CellOf(new MapPoint(p.X - dx, p.Y - dy));
                var old = reference.At(rx, ry);

                if (!IsRoom(old))
                {
                    continue;
                }

                var row = result.TryGetValue(id, out var r) ? r : result[id] = [];
                row[old] = row.GetValueOrDefault(old) + 1;
            }
        }

        return result;
    }

    private static bool IsRoom(byte cell) => cell is not VacuumMap.Outside and not VacuumMap.Wall;
}

/// <summary>
/// The map as the owner left it, kept by the hub so it can be put back: the robot's
/// rooms, names and zones, and the rooms he deleted, which the robot only knows
/// as no-go zones.
/// </summary>
public sealed record VacuumMapRecipe(VacuumMap Reference, ImmutableArray<int> Hidden, DateTimeOffset SavedAt);

/// <summary>What replaying a recipe onto a new map would send, and what it could not work out.</summary>
public sealed record VacuumReplayPlan(ImmutableArray<VacuumMapEdit> Edits, ImmutableArray<int> Hidden, ImmutableArray<string> Notes);

/// <summary>
/// Carries the owner's arrangement onto a map the robot drew from scratch. Rooms
/// are matched by where they are, not by id: after a remap the robot numbers
/// them anew.
/// </summary>
public static class VacuumMapReplay
{
    public static VacuumReplayPlan Plan(VacuumMapRecipe recipe, VacuumMap current)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(current);

        var reference = recipe.Reference;
        var overlap = VacuumMapGeometry.Overlap(reference, current);
        var edits = ImmutableArray.CreateBuilder<VacuumMapEdit>();
        var notes = ImmutableArray.CreateBuilder<string>();

        // Each new room belongs to the old room holding most of its cells, if
        // that is a clear majority.
        var owner = new Dictionary<int, int>();

        foreach (var room in current.Rooms)
        {
            if (overlap.TryGetValue(room.Id, out var row) && row.MaxBy(p => p.Value) is var top && top.Value >= room.Cells * 0.6)
            {
                owner[room.Id] = top.Key;
            }
            else
            {
                notes.Add(string.Create(CultureInfo.CurrentCulture, $"a área {room.Id} não corresponde a nenhum cômodo guardado"));
            }
        }

        // Two new rooms inside one old room: the robot cut it where the owner had
        // joined it. Merge them back into the larger one.
        var survivors = new Dictionary<int, int>();

        foreach (var group in owner.GroupBy(p => p.Value))
        {
            var ids = group.Select(p => p.Key).OrderByDescending(id => current.Room(id)!.Cells).ToList();
            survivors[group.Key] = ids[0];

            foreach (var other in ids.Skip(1))
            {
                edits.Add(new VacuumMapEdit.Merge(ids[0], other));
            }
        }

        // One new room covering two old ones: the robot joined what the owner had
        // split. Where to cut is a judgement the hub leaves to the owner.
        foreach (var room in current.Rooms)
        {
            if (overlap.TryGetValue(room.Id, out var row))
            {
                var big = row.Where(p => p.Value >= room.Cells * 0.25).Select(p => p.Key).ToList();

                if (big.Count > 1)
                {
                    notes.Add(string.Create(CultureInfo.CurrentCulture,
                        $"a área {room.Id} cobre {string.Join(" e ", big.Select(id => Name(reference, id)))}: divida no mapa"));
                }
            }
        }

        foreach (var (oldId, newId) in survivors)
        {
            var name = reference.Room(oldId)?.Name ?? string.Empty;

            if (name.Length > 0 && !string.Equals(current.Room(newId)?.Name, name, StringComparison.Ordinal))
            {
                edits.Add(new VacuumMapEdit.Rename(newId, name));
            }
        }

        var hidden = recipe.Hidden
            .Where(survivors.ContainsKey)
            .Select(id => survivors[id])
            .ToImmutableArray();

        var (dx, dy) = VacuumMapGeometry.Offset(reference, current);
        var zones = new VacuumZones(
            [.. reference.Zones.NoGo.Select(r => r.Shift(dx, dy))],
            [.. reference.Zones.NoMop.Select(r => r.Shift(dx, dy))],
            [.. reference.Zones.Walls.Select(l => l.Shift(dx, dy))]);

        if (!Same(zones, current.Zones))
        {
            edits.Add(new VacuumMapEdit.SetZones(zones));
        }

        return new VacuumReplayPlan(edits.ToImmutable(), hidden, notes.ToImmutable());
    }

    private static string Name(VacuumMap map, int id) =>
        map.Room(id) is { Name.Length: > 0 } room ? room.Name : string.Create(CultureInfo.InvariantCulture, $"área {id}");

    private static bool Same(VacuumZones a, VacuumZones b) =>
        a.NoGo.SequenceEqual(b.NoGo) && a.NoMop.SequenceEqual(b.NoMop) && a.Walls.SequenceEqual(b.Walls);
}
