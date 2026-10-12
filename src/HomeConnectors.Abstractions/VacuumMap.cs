using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeConnectors;

/// <summary>A point on the vacuum's map, in millimetres, y growing north.</summary>
public readonly record struct MapPoint(int X, int Y);

/// <summary>A rectangle on the map in millimetres, stored with the smaller corner first.</summary>
public sealed record MapRect
{
    public MapRect(int x1, int y1, int x2, int y2)
    {
        X1 = Math.Min(x1, x2);
        Y1 = Math.Min(y1, y2);
        X2 = Math.Max(x1, x2);
        Y2 = Math.Max(y1, y2);
    }

    public int X1 { get; }
    public int Y1 { get; }
    public int X2 { get; }
    public int Y2 { get; }

    public MapRect Shift(int dx, int dy) => new(X1 + dx, Y1 + dy, X2 + dx, Y2 + dy);
}

/// <summary>A segment on the map in millimetres: a virtual wall or a cut.</summary>
public sealed record MapLine(int X1, int Y1, int X2, int Y2)
{
    public MapLine Shift(int dx, int dy) => new(X1 + dx, Y1 + dy, X2 + dx, Y2 + dy);
}

/// <summary>A room the robot segmented, with the name the app (or the hub's client) gave it.</summary>
public sealed record VacuumRoom(int Id, string Name, int Cells, MapPoint Center);

/// <summary>The zones the robot keeps out of or away from: no-go rectangles, no-mop rectangles, virtual walls.</summary>
public sealed record VacuumZones(ImmutableArray<MapRect> NoGo, ImmutableArray<MapRect> NoMop, ImmutableArray<MapLine> Walls)
{
    public static VacuumZones Empty { get; } = new([], [], []);
}

/// <summary>
/// The saved map of a Dreame-built Mijia vacuum as the hub holds it: a grid of
/// cells, each a room id, a wall or nothing, plus the rooms' names and the
/// zones. The robot calls this the <c>rism</c>, the map it navigates by; the
/// live frame with the robot's path is not kept.
/// <para>
/// Cells run row by row from the south-west corner: cell (x, y) covers the
/// square whose south-west corner is (<see cref="Left"/> + x·<see cref="Grid"/>,
/// <see cref="Top"/> + y·<see cref="Grid"/>). "Top" is the robot's name for it,
/// even though it is the southern edge.
/// </para>
/// </summary>
public sealed record VacuumMap(
    int MapId,
    int Grid,
    int Left,
    int Top,
    int Width,
    int Height,
    ImmutableArray<byte> Cells,
    ImmutableArray<VacuumRoom> Rooms,
    VacuumZones Zones,
    MapPoint? Charger,
    DateTimeOffset TakenAt)
{
    /// <summary>A cell outside every room.</summary>
    public const byte Outside = 0;

    /// <summary>A cell the robot saw as wall or obstacle.</summary>
    public const byte Wall = 255;

    public byte At(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? Outside : Cells[(y * Width) + x];

    /// <summary>The centre of cell (x, y) in millimetres.</summary>
    public MapPoint World(int x, int y) => new(Left + (x * Grid) + (Grid / 2), Top + (y * Grid) + (Grid / 2));

    /// <summary>The cell a point falls in, which may lie off the grid.</summary>
    public (int X, int Y) CellOf(MapPoint point) =>
        ((int)Math.Floor((point.X - Left) / (double)Grid), (int)Math.Floor((point.Y - Top) / (double)Grid));

    public VacuumRoom? Room(int id) => Rooms.FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// Rooms counted and centred from the cells, keeping the names from
    /// <paramref name="names"/>. A room the robot names but has no cells for is
    /// left out.
    /// </summary>
    public static ImmutableArray<VacuumRoom> Measure(int width, int height, ImmutableArray<byte> cells, int left, int top, int grid, IReadOnlyDictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var sums = new Dictionary<int, (long X, long Y, int N)>();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var id = cells[(y * width) + x];

                if (id is Outside or Wall)
                {
                    continue;
                }

                var s = sums.GetValueOrDefault(id);
                sums[id] = (s.X + x, s.Y + y, s.N + 1);
            }
        }

        return [.. sums.OrderBy(p => p.Key).Select(p => new VacuumRoom(
            p.Key,
            names.GetValueOrDefault(p.Key, string.Empty),
            p.Value.N,
            new MapPoint(
                left + (int)(p.Value.X * grid / p.Value.N) + (grid / 2),
                top + (int)(p.Value.Y * grid / p.Value.N) + (grid / 2))))];
    }
}

/// <summary>
/// Where the robot is and where it has been in the current clean, from the live
/// frame the robot uploads with the map. The path is in millimetres, a list of
/// runs: each run is a polyline, and a gap between runs is the robot moving
/// without cleaning (the frame's "M" steps).
/// </summary>
public sealed record VacuumLive(MapPoint? Robot, int Angle, ImmutableArray<ImmutableArray<MapPoint>> Path, ImmutableArray<int> ActiveRooms)
{
    public static VacuumLive None { get; } = new(null, 0, [], []);

    /// <summary>What the robot's obstacle camera recognised on the floor, and where. Only the verdict leaves the robot, never the picture.</summary>
    public ImmutableArray<VacuumObstacle> Obstacles { get; init; } = [];

    /// <summary>
    /// Reads the frame's <c>tr</c> string: <c>M</c>, <c>W</c> and <c>S</c> start a
    /// run at an absolute point; <c>L</c> continues it by a relative step; <c>l</c>
    /// continues from the previous frame at an absolute point.
    /// </summary>
    public static ImmutableArray<ImmutableArray<MapPoint>> ParsePath(string? tr)
    {
        if (string.IsNullOrEmpty(tr))
        {
            return [];
        }

        var runs = ImmutableArray.CreateBuilder<ImmutableArray<MapPoint>>();
        var run = ImmutableArray.CreateBuilder<MapPoint>();
        var at = new MapPoint(0, 0);
        var i = 0;

        while (i < tr.Length)
        {
            var op = tr[i++];

            if (!TryNumber(tr, ref i, out var x) || i >= tr.Length || tr[i++] != ',' || !TryNumber(tr, ref i, out var y))
            {
                continue;
            }

            switch (op)
            {
                case 'L':
                    at = new MapPoint(at.X + x, at.Y + y);
                    run.Add(at);
                    break;

                case 'l':
                    at = new MapPoint(x, y);
                    run.Add(at);
                    break;

                case 'M' or 'W' or 'S':
                    if (run.Count > 1)
                    {
                        runs.Add(run.ToImmutable());
                    }

                    run.Clear();
                    at = new MapPoint(x, y);
                    run.Add(at);
                    break;
            }
        }

        if (run.Count > 1)
        {
            runs.Add(run.ToImmutable());
        }

        return runs.ToImmutable();

        static bool TryNumber(string text, ref int index, out int value)
        {
            var start = index;

            if (index < text.Length && text[index] == '-')
            {
                index++;
            }

            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            return int.TryParse(text.AsSpan(start, index - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>
    /// How much of each room the path has covered, 0 to 1, with the path drawn
    /// as wide as the robot cleans (<paramref name="width"/> mm). A room counts
    /// its own cells only.
    /// </summary>
    public ImmutableDictionary<int, double> Coverage(VacuumMap map, int width = 300)
    {
        ArgumentNullException.ThrowIfNull(map);

        var covered = new bool[map.Width * map.Height];
        var radius = width / 2.0 / map.Grid;

        foreach (var run in Path)
        {
            for (var k = 1; k < run.Length; k++)
            {
                Stroke(map.CellOf(run[k - 1]), map.CellOf(run[k]));
            }
        }

        var totals = new Dictionary<int, (int Done, int All)>();

        for (var c = 0; c < covered.Length; c++)
        {
            var id = map.Cells[c];

            if (id is VacuumMap.Outside or VacuumMap.Wall)
            {
                continue;
            }

            var t = totals.GetValueOrDefault(id);
            totals[id] = (t.Done + (covered[c] ? 1 : 0), t.All + 1);
        }

        return totals.ToImmutableDictionary(p => p.Key, p => p.Value.All == 0 ? 0 : p.Value.Done / (double)p.Value.All);

        void Stroke((int X, int Y) a, (int X, int Y) b)
        {
            var steps = Math.Max(1, Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));
            var r = (int)Math.Ceiling(radius);

            for (var s = 0; s <= steps; s++)
            {
                var cx = a.X + ((b.X - a.X) * s / (double)steps);
                var cy = a.Y + ((b.Y - a.Y) * s / (double)steps);

                for (var dy = -r; dy <= r; dy++)
                {
                    for (var dx = -r; dx <= r; dx++)
                    {
                        if ((dx * dx) + (dy * dy) > radius * radius)
                        {
                            continue;
                        }

                        var x = (int)Math.Round(cx) + dx;
                        var y = (int)Math.Round(cy) + dy;

                        if (x >= 0 && y >= 0 && x < map.Width && y < map.Height)
                        {
                            covered[(y * map.Width) + x] = true;
                        }
                    }
                }
            }
        }
    }
}

/// <summary>
/// One obstacle the robot's camera recognised: where, what, how sure (0–100).
/// The types are the Dreame firmware's (128–139); the M40's own plugin ships
/// no icons for them, so the app shows almost nothing.
/// </summary>
public sealed record VacuumObstacle(MapPoint At, int Type, int Confidence)
{
    public string Label => Type switch
    {
        128 => "base",
        129 => "balança",
        130 => "régua de tomadas",
        131 => "fio",
        132 => "brinquedo",
        133 => "sapato",
        134 => "meia",
        135 => "cocô de pet",
        136 => "lixeira",
        137 => "tecido",
        138 => "linha",
        139 => "mancha",
        0 => "obstáculo",
        _ => string.Create(CultureInfo.InvariantCulture, $"obstáculo {Type}"),
    };

    /// <summary>
    /// The frame's <c>ai_obstacle</c> or <c>laser_obstacle</c>:
    /// <c>[[x, y, type, confidence, …], …]</c>, numbers or numeric strings,
    /// confidence as a fraction. A bare <c>[x, y]</c> is an obstacle of no known
    /// type, seen for certain.
    /// </summary>
    public static ImmutableArray<VacuumObstacle> Parse(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = ImmutableArray.CreateBuilder<VacuumObstacle>();

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2
                && Number(item[0]) is { } x && Number(item[1]) is { } y)
            {
                var type = item.GetArrayLength() > 2 ? Number(item[2]) ?? 0 : 0;
                var confidence = item.GetArrayLength() > 3 ? Number(item[3]) ?? 1 : 1;
                found.Add(new VacuumObstacle(new MapPoint((int)x, (int)y), (int)type, (int)Math.Round(confidence <= 1 ? confidence * 100 : confidence)));
            }
        }

        return found.ToImmutable();

        static double? Number(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }
}

/// <summary>One change to the robot's map, as the plugin's editor sends it.</summary>
public abstract record VacuumMapEdit
{
    private VacuumMapEdit()
    {
    }

    /// <summary>Gives a room a name. The robot keeps names as base64 text.</summary>
    public sealed record Rename(int RoomId, string Name) : VacuumMapEdit;

    /// <summary>Joins two adjacent rooms into the first.</summary>
    public sealed record Merge(int First, int Second) : VacuumMapEdit;

    /// <summary>Cuts a room in two along a line that crosses it wall to wall.</summary>
    public sealed record Split(int RoomId, MapLine Cut) : VacuumMapEdit;

    /// <summary>Replaces every no-go zone, no-mop zone and virtual wall at once, which is how the robot takes them.</summary>
    public sealed record SetZones(VacuumZones Zones) : VacuumMapEdit;

    /// <summary>What it does, in the owner's words, for the log and the panel.</summary>
    public string Describe(VacuumMap? map) => this switch
    {
        Rename r => string.Create(CultureInfo.CurrentCulture, $"renomear {Label(map, r.RoomId)} para \"{r.Name}\""),
        Merge m => string.Create(CultureInfo.CurrentCulture, $"juntar {Label(map, m.Second)} a {Label(map, m.First)}"),
        Split s => string.Create(CultureInfo.CurrentCulture, $"dividir {Label(map, s.RoomId)}"),
        SetZones z => string.Create(CultureInfo.CurrentCulture,
            $"zonas: {z.Zones.NoGo.Length} proibida(s), {z.Zones.NoMop.Length} sem pano, {z.Zones.Walls.Length} parede(s)"),
        _ => GetType().Name,
    };

    private static string Label(VacuumMap? map, int id) =>
        map?.Room(id) is { Name.Length: > 0 } room ? room.Name : string.Create(CultureInfo.InvariantCulture, $"área {id}");
}

/// <summary>
/// The JSON the robot's "update map info" action (service 6, action 2, input
/// piid 4) takes for each edit. The shapes were read off the Mi Home plugin for
/// the M40 and checked against the robot on 2026-09-27: rename, merge, split
/// and zones all answered result 0 and showed up in the next map.
/// </summary>
public static class VacuumMapCommands
{
    public static string Payload(VacuumMapEdit edit, int mapId)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var payload = edit switch
        {
            VacuumMapEdit.Rename r => new JsonObject
            {
                ["nsr"] = new JsonObject
                {
                    [r.RoomId.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["type"] = 0,
                        ["name"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(r.Name.Trim())),
                        ["roomID"] = string.Empty,
                    },
                },
                ["mapid"] = mapId,
            },
            VacuumMapEdit.Merge m => new JsonObject
            {
                ["msr"] = new JsonArray(m.First, m.Second),
                ["mapid"] = mapId,
            },
            VacuumMapEdit.Split s => new JsonObject
            {
                ["dsrid"] = new JsonArray(s.Cut.X1, s.Cut.Y1, s.Cut.X2, s.Cut.Y2, s.RoomId),
                ["mapid"] = mapId,
            },
            VacuumMapEdit.SetZones z => new JsonObject
            {
                ["vw"] = new JsonObject
                {
                    ["line"] = Lines(z.Zones.Walls),
                    ["cliff"] = new JsonArray(),
                    ["rect"] = Rects(z.Zones.NoGo),
                    ["mop"] = Rects(z.Zones.NoMop),
                    ["nocpt"] = new JsonArray(),
                    ["addcpt"] = new JsonArray(),
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(edit), edit.GetType().Name),
        };

        return payload.ToJsonString();
    }

    private static JsonArray Rects(ImmutableArray<MapRect> rects) =>
        [.. rects.Select(r => (JsonNode)new JsonArray(r.X1, r.Y1, r.X2, r.Y2))];

    private static JsonArray Lines(ImmutableArray<MapLine> lines) =>
        [.. lines.Select(l => (JsonNode)new JsonArray(l.X1, l.Y1, l.X2, l.Y2))];

    /// <summary>
    /// The zones out of the map JSON's <c>vw</c> object. The robot stores each
    /// rectangle with a fifth element (always 0 so far), which is dropped.
    /// </summary>
    public static VacuumZones ParseZones(JsonElement vw)
    {
        if (vw.ValueKind != JsonValueKind.Object)
        {
            return VacuumZones.Empty;
        }

        return new VacuumZones(
            [.. Quads(vw, "rect").Select(q => new MapRect(q[0], q[1], q[2], q[3]))],
            [.. Quads(vw, "mop").Select(q => new MapRect(q[0], q[1], q[2], q[3]))],
            [.. Quads(vw, "line").Select(q => new MapLine(q[0], q[1], q[2], q[3]))]);
    }

    private static IEnumerable<int[]> Quads(JsonElement vw, string name)
    {
        if (!vw.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 4)
            {
                continue;
            }

            var q = new int[4];
            var ok = true;

            for (var i = 0; i < 4 && ok; i++)
            {
                ok = item[i].TryGetInt32(out q[i]);
            }

            if (ok)
            {
                yield return q;
            }
        }
    }
}
