using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>One read of the robot: its saved map, and where it is and has been right now.</summary>
public sealed record VacuumSnapshot(VacuumMap Map, VacuumLive Live);

/// <summary>One backup the robot keeps of a map: the file in the cloud and when it was taken.</summary>
public sealed record VacuumBackup(int MapId, string ObjectName, DateTimeOffset TakenAt, bool First);

/// <summary>
/// The M40's map, end to end. Reading goes through the cloud (the robot uploads
/// its map only when asked by a cloud RPC); editing and restoring go over miIO
/// on the LAN with the token already in the store.
/// <para>
/// Service 6 of the spec: action 1 asks for a map upload and answers with the
/// object name and its key; action 2 takes an edit (<see cref="VacuumMapCommands"/>)
/// and answers result 0; property 9 names the file listing the backups;
/// property 10 takes a backup to restore and property 11 reports how it went
/// (3 is done).
/// </para>
/// </summary>
public sealed class XiaomiVacuumMapService(
    XiaomiCloudClient cloud,
    MiioClient miio,
    IHomeCredentialStore credentials,
    HomeCatalog catalog,
    TimeProvider clock)
{
    private readonly XiaomiCloudClient _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
    private readonly MiioClient _miio = miio ?? throw new ArgumentNullException(nameof(miio));
    private readonly IHomeCredentialStore _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly HomeCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public XiaomiCloudClient Cloud => _cloud;

    /// <summary>The vacuum on the list, if there is one with a token.</summary>
    public bool HasVacuum => Resolve().Ok;

    /// <summary>Asks the robot to upload its map, then downloads and decodes the saved map.</summary>
    public async Task<HomeReply<VacuumSnapshot>> FetchAsync(CancellationToken cancellationToken)
    {
        var target = Resolve();

        if (!target.Ok)
        {
            return HomeReply.Failure<VacuumSnapshot>(target.Code, target.Message);
        }

        var did = target.Value.Did;
        var request = new
        {
            method = "action",
            @params = new
            {
                did,
                siid = 6,
                aiid = 1,
                @in = new object[] { new { piid = 2, value = """{"req_type":1,"frame_type":"I","force_type":1}""" } },
            },
        };

        var reply = await _cloud.CallAsync("v2/home/rpc/" + did, request, cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            return HomeReply.Failure<VacuumSnapshot>(reply.Code, reply.Message);
        }

        // "object,key" in output 3, or "1,object,key" in output 13 — the robot
        // has answered both ways. The last two fields are what matter.
        var named = (OutValue(reply.Value, 3) ?? OutValue(reply.Value, 13))?.Split(',');

        if (named is not { Length: >= 2 } || named[^2] is not { Length: > 0 } objectName || named[^1] is not { Length: > 0 } key)
        {
            return HomeReply.Failure<VacuumSnapshot>(HomeReplyCodes.Rejected, "O aspirador não enviou o mapa (" + reply.Value + ").");
        }

        // The robot answers before the upload lands; give it a few tries.
        HomeReply<byte[]> file = HomeReply.Failure<byte[]>(HomeReplyCodes.Unreachable, "sem tentativa");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 3 : 2), _clock, cancellationToken).ConfigureAwait(false);
            file = await _cloud.DownloadObjectAsync(objectName, cancellationToken).ConfigureAwait(false);

            if (file.Ok || file.Code == HomeReplyCodes.Unauthorized)
            {
                break;
            }
        }

        if (!file.Ok)
        {
            return HomeReply.Failure<VacuumSnapshot>(file.Code, file.Message);
        }

        try
        {
            var frame = DreameMapDecoder.Unwrap(Encoding.ASCII.GetString(file.Value!), key);

            return DreameMapDecoder.ReadSaved(frame, _clock.GetUtcNow()) is { } map
                ? HomeReply.Success(new VacuumSnapshot(map, DreameMapDecoder.ReadLive(frame)))
                : HomeReply.Failure<VacuumSnapshot>(HomeReplyCodes.Rejected, "O aspirador ainda não tem mapa salvo.");
        }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException or System.IO.InvalidDataException or JsonException)
        {
            return HomeReply.Failure<VacuumSnapshot>(HomeReplyCodes.Rejected, "Não consegui abrir o mapa: " + e.Message);
        }
    }

    /// <summary>Sends one edit over the LAN. Success is result 0 in the action's output.</summary>
    public async Task<HomeReply<bool>> ApplyAsync(VacuumMapEdit edit, int mapId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var target = Resolve();

        if (!target.Ok)
        {
            return HomeReply.Failure<bool>(target.Code, target.Message);
        }

        var (did, host, token) = target.Value;
        var payload = VacuumMapCommands.Payload(edit, mapId);

        var reply = await _miio.CallAsync(
            host,
            token,
            "action",
            new { did, siid = 6, aiid = 2, @in = new object[] { new { piid = 4, value = payload } } },
            cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            return HomeReply.Failure<bool>(reply.Code, reply.Message);
        }

        var code = reply.Value.ValueKind == JsonValueKind.Object && reply.Value.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : -1;
        var result = OutValue(reply.Value, 6);

        return code == 0 && result is "0"
            ? HomeReply.Success(true)
            : HomeReply.Failure<bool>(HomeReplyCodes.Rejected,
                string.Create(CultureInfo.CurrentCulture, $"O aspirador recusou: {edit.Describe(null)} ({reply.Value})."));
    }

    /// <summary>
    /// Cleans the given rooms, once each, in the order given. Vacuum-extend ·
    /// start-clean (4.1) with work mode 18 ("area clean") and the rooms in
    /// clean-extend-data as the plugin sends them: <c>{"selects":[[room, repeats,
    /// suction, water, order]]}</c>. Suction 1 is standard, water 2 is medium.
    /// The selects carry no mode: the robot cleans in the one clean-setting
    /// holds, so that is set first to the mode asked for.
    /// </summary>
    public async Task<HomeReply<bool>> CleanRoomsAsync(IReadOnlyList<int> rooms, VacuumCleanMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        if (rooms.Count == 0)
        {
            return HomeReply.Failure<bool>(HomeReplyCodes.Rejected, "Nenhum cômodo escolhido.");
        }

        var target = Resolve();

        if (!target.Ok)
        {
            return HomeReply.Failure<bool>(target.Code, target.Message);
        }

        var (did, host, token) = target.Value;
        var set = await XiaomiHomeGateway.SetCleanModeAsync(_miio, host, token, did, mode, cancellationToken).ConfigureAwait(false);

        if (!set.Ok)
        {
            return set;
        }

        var selects = JsonSerializer.Serialize(new { selects = rooms.Select((room, i) => new[] { room, 1, 1, 2, i + 1 }) });

        var reply = await _miio.CallAsync(
            host,
            token,
            "action",
            new
            {
                did,
                siid = 4,
                aiid = 1,
                @in = new object[] { new { piid = 1, value = 18 }, new { piid = 10, value = selects } },
            },
            cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            return HomeReply.Failure<bool>(reply.Code, reply.Message);
        }

        return reply.Value.ValueKind == JsonValueKind.Object && reply.Value.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) && n == 0
            ? HomeReply.Success(true)
            : HomeReply.Failure<bool>(HomeReplyCodes.Rejected, "O aspirador recusou a limpeza (" + reply.Value + ").");
    }

    /// <summary>The backups the robot lists, newest first.</summary>
    public async Task<HomeReply<ImmutableArray<VacuumBackup>>> BackupsAsync(CancellationToken cancellationToken)
    {
        var listed = await ReadPropertyAsync(9, cancellationToken).ConfigureAwait(false);

        if (!listed.Ok)
        {
            return HomeReply.Failure<ImmutableArray<VacuumBackup>>(listed.Code, listed.Message);
        }

        string objectName;

        try
        {
            using var pointer = JsonDocument.Parse(listed.Value!);
            objectName = pointer.RootElement.GetProperty("object_name").GetString() ?? string.Empty;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return HomeReply.Failure<ImmutableArray<VacuumBackup>>(HomeReplyCodes.Rejected, "Lista de backups ilegível: " + listed.Value);
        }

        var file = await _cloud.DownloadObjectAsync(objectName, cancellationToken).ConfigureAwait(false);

        if (!file.Ok)
        {
            return HomeReply.Failure<ImmutableArray<VacuumBackup>>(file.Code, file.Message);
        }

        try
        {
            return HomeReply.Success(ParseBackups(file.Value!));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return HomeReply.Failure<ImmutableArray<VacuumBackup>>(HomeReplyCodes.Rejected, "Lista de backups ilegível: " + e.Message);
        }
    }

    /// <summary>The list file: per map, its backups with object name and time.</summary>
    public static ImmutableArray<VacuumBackup> ParseBackups(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var backups = ImmutableArray.CreateBuilder<VacuumBackup>();

        foreach (var map in document.RootElement.EnumerateArray())
        {
            var id = map.GetProperty("id").GetInt32();

            foreach (var entry in map.GetProperty("info").EnumerateArray())
            {
                backups.Add(new VacuumBackup(
                    id,
                    entry.GetProperty("objname").GetString()!,
                    DateTimeOffset.FromUnixTimeSeconds(entry.GetProperty("time").GetInt64()),
                    entry.TryGetProperty("first", out var first) && first.TryGetInt32(out var f) && f == 1));
            }
        }

        return [.. backups.OrderByDescending(b => b.TakenAt)];
    }

    /// <summary>A backup's file (a .tbz2 of the robot's own map folder), for the hub to keep.</summary>
    public Task<HomeReply<byte[]>> DownloadBackupAsync(VacuumBackup backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return _cloud.DownloadObjectAsync(backup.ObjectName, cancellationToken);
    }

    /// <summary>
    /// Hands the robot a backup to restore, then waits for it to report done.
    /// This replaces the map the robot has; the caller asks first.
    /// </summary>
    public async Task<HomeReply<bool>> RestoreAsync(VacuumBackup backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var target = Resolve();

        if (!target.Ok)
        {
            return HomeReply.Failure<bool>(target.Code, target.Message);
        }

        var url = await _cloud.ObjectUrlAsync(backup.ObjectName, cancellationToken).ConfigureAwait(false);

        if (!url.Ok)
        {
            return HomeReply.Failure<bool>(url.Code, url.Message);
        }

        var (did, host, token) = target.Value;
        var value = JsonSerializer.Serialize(new { map_id = backup.MapId, map_url = url.Value, timestamp = backup.TakenAt.ToUnixTimeSeconds() });

        var set = await _miio.CallAsync(
            host,
            token,
            "set_properties",
            new object[] { new { did, siid = 6, piid = 10, value } },
            cancellationToken).ConfigureAwait(false);

        if (!set.Ok)
        {
            return HomeReply.Failure<bool>(set.Code, set.Message);
        }

        var status = string.Empty;

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _clock, cancellationToken).ConfigureAwait(false);
            var read = await ReadPropertyAsync(11, cancellationToken).ConfigureAwait(false);
            status = read.Ok ? read.Value : status;

            if (status == "3")
            {
                return HomeReply.Success(true);
            }
        }

        return HomeReply.Failure<bool>(HomeReplyCodes.Rejected,
            string.Create(CultureInfo.CurrentCulture, $"O aspirador não confirmou a restauração (estado {status})."));
    }

    /// <summary>A service-6 property as text: strings as they are, numbers as digits.</summary>
    private async Task<HomeReply<string>> ReadPropertyAsync(int piid, CancellationToken cancellationToken)
    {
        var target = Resolve();

        if (!target.Ok)
        {
            return HomeReply.Failure<string>(target.Code, target.Message);
        }

        var (did, host, token) = target.Value;
        var reply = await _miio.CallAsync(host, token, "get_properties", new object[] { new { did, siid = 6, piid } }, cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            return HomeReply.Failure<string>(reply.Code, reply.Message);
        }

        if (reply.Value.ValueKind == JsonValueKind.Array && reply.Value.GetArrayLength() > 0
            && reply.Value[0] is var item
            && item.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) && c == 0
            && item.TryGetProperty("value", out var value))
        {
            return HomeReply.Success(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
        }

        return HomeReply.Failure<string>(HomeReplyCodes.Rejected,
            string.Create(CultureInfo.InvariantCulture, $"Propriedade 6.{piid} indisponível ({reply.Value})."));
    }

    /// <summary>An output value of an action reply (<c>out: [{piid, value}]</c>) as text.</summary>
    private static string? OutValue(JsonElement reply, int piid)
    {
        if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("out", out var outs) || outs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var o in outs.EnumerateArray())
        {
            if (o.TryGetProperty("piid", out var p) && p.TryGetInt32(out var id) && id == piid && o.TryGetProperty("value", out var v))
            {
                var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();

                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    /// <summary>The vacuum's id, host and token: the device on the list whose id is the stored one.</summary>
    private HomeReply<(string Did, string Host, byte[] Token)> Resolve()
    {
        if (_credentials.Read(HomeProvider.Xiaomi) is not { IsComplete: true } stored
            || MiioClient.ParseToken(stored.Secret) is not { } token)
        {
            return HomeReply.Failure<(string, string, byte[])>(HomeReplyCodes.NotConfigured, "Falta o token do aspirador Xiaomi.");
        }

        var device = _catalog.All.FirstOrDefault(d => d.Provider == HomeProvider.Xiaomi && d.Id == stored.AccessId);
        var host = device?.Host is { Length: > 0 } listed ? listed : stored.Region;

        return HomeReply.Success((stored.AccessId, host, token));
    }
}
