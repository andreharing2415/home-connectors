using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// Reads the map file a Dreame-built Mijia vacuum uploads to the Xiaomi cloud.
/// <para>
/// The file is URL-safe base64 of an AES-256-CBC ciphertext of a zlib stream.
/// The key is the first 32 hex characters of SHA-256 over the key that comes
/// after the comma in the object name the robot reports; the IV is fixed per
/// model and lives in the Mi Home plugin (for the M40, <c>xiaomi.vacuum.d110ch</c>,
/// <see cref="M40Iv"/>). Inside: a 27-byte header, width×height pixels of the
/// live frame, then JSON. The saved map — rooms, names, zones — is the JSON's
/// <c>rism</c>: base64 zlib again, same header, its own pixels and JSON.
/// </para>
/// </summary>
public static class DreameMapDecoder
{
    /// <summary>The M40's IV, from the plugin's bundle (<c>AES_IV</c>).</summary>
    public const string M40Iv = "OFULk9To37qRdXY3";

    private const int HeaderSize = 27;

    /// <summary>Decrypts and inflates the downloaded file into the frame bytes.</summary>
    public static byte[] Unwrap(string file, string key, string iv = M40Iv)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(key);

        var cipher = FromBase64Url(file.Trim());

        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]);
        var plain = aes.DecryptCbc(cipher, Encoding.ASCII.GetBytes(iv), PaddingMode.PKCS7);

        return Inflate(plain);
    }

    /// <summary>The saved map inside a frame, or null when the frame carries none.</summary>
    public static VacuumMap? ReadSaved(byte[] frame, DateTimeOffset takenAt)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var (_, json) = Split(frame);

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("rism", out var rism) || rism.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var saved = Inflate(FromBase64Url(rism.GetString()!));

        // The saved map's own header carries the id edits must name; the live
        // frame's id is another counter.
        return Read(saved, takenAt);
    }

    /// <summary>The robot's position and the current clean's path, from the live frame.</summary>
    public static VacuumLive ReadLive(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var (_, json) = Split(frame);
        short S(int offset) => BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(offset));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var path = root.TryGetProperty("tr", out var tr) && tr.ValueKind == JsonValueKind.String
            ? VacuumLive.ParsePath(tr.GetString())
            : [];

        // "sa": [[room, ...], ...] — the rooms of a room clean under way.
        var active = ImmutableArray.CreateBuilder<int>();

        if (root.TryGetProperty("sa", out var sa) && sa.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sa.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() > 0 && entry[0].TryGetInt32(out var id))
                {
                    active.Add(id);
                }
            }
        }

        // Header: robot x, y, angle at 5, 7, 9. The robot is absent when all are zero.
        var (x, y, a) = (S(5), S(7), S(9));
        MapPoint? robot = x == 0 && y == 0 && a == 0 ? null : new MapPoint(x, y);

        return new VacuumLive(robot, a, path, active.ToImmutable())
        {
            // The recognised ones ("ai_obstacle") and, on the M40, what the
            // structured light found ("laser_obstacle").
            Obstacles =
            [
                .. root.TryGetProperty("ai_obstacle", out var ai) ? VacuumObstacle.Parse(ai) : [],
                .. root.TryGetProperty("laser_obstacle", out var laser) ? VacuumObstacle.Parse(laser) : [],
            ],
        };
    }

    /// <summary>A map from its own bytes: header, pixels, JSON (the <c>rism</c> layout).</summary>
    public static VacuumMap Read(byte[] data, DateTimeOffset takenAt)
    {
        ArgumentNullException.ThrowIfNull(data);

        var (h, json) = Split(data);
        var cells = new byte[h.Width * h.Height];

        for (var i = 0; i < cells.Length; i++)
        {
            var pixel = data[HeaderSize + i];

            // Bit 7 is wall; the low six bits are the room. As the plugin reads it.
            cells[i] = (pixel & 0x80) != 0 ? VacuumMap.Wall : (byte)(pixel & 0x3F);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var names = new Dictionary<int, string>();

        if (root.TryGetProperty("seg_inf", out var segments) && segments.ValueKind == JsonValueKind.Object)
        {
            foreach (var segment in segments.EnumerateObject())
            {
                if (int.TryParse(segment.Name, out var id)
                    && segment.Value.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } encoded)
                {
                    names[id] = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                }
            }
        }

        var zones = root.TryGetProperty("vw", out var vw) ? VacuumMapCommands.ParseZones(vw) : VacuumZones.Empty;

        MapPoint? charger = root.TryGetProperty("charger", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 2
            ? new MapPoint(c[0].GetInt32(), c[1].GetInt32())
            : new MapPoint(h.ChargerX, h.ChargerY);

        var immutable = ImmutableArray.Create(cells);

        return new VacuumMap(
            h.MapId,
            h.Grid,
            h.Left,
            h.Top,
            h.Width,
            h.Height,
            immutable,
            VacuumMap.Measure(h.Width, h.Height, immutable, h.Left, h.Top, h.Grid, names),
            zones,
            charger,
            takenAt);
    }

    /// <summary>The header and the JSON after the pixels.</summary>
    private static (Header Header, ReadOnlyMemory<byte> Json) Split(byte[] data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException("Mapa curto demais para o cabeçalho.");
        }

        short S(int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset));

        // <hhbhhhhhhhhhhh: map id, frame id, frame type, robot x y a, charger x y a, grid, width, height, left, top.
        var header = new Header(
            MapId: S(0),
            ChargerX: S(11),
            ChargerY: S(13),
            Grid: S(17),
            Width: S(19),
            Height: S(21),
            Left: S(23),
            Top: S(25));

        var pixels = header.Width * header.Height;

        if (header.Grid <= 0 || header.Width <= 0 || header.Height <= 0 || data.Length < HeaderSize + pixels)
        {
            throw new InvalidDataException("Cabeçalho do mapa inconsistente.");
        }

        return (header, data.AsMemory(HeaderSize + pixels));
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    private sealed record Header(short MapId, short ChargerX, short ChargerY, short Grid, short Width, short Height, short Left, short Top);
}
