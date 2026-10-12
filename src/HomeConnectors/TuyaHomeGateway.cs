using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// The Tuya IoT Platform, seen through <see cref="IHomeDeviceGateway"/>.
/// <para>
/// Every path here takes a device id from a <see cref="HomeDevice"/> the catalog
/// produced. The endpoints are the documented device-control set:
/// <c>/v1.0/devices/{id}/status</c>, <c>/v1.0/devices/{id}/commands</c>,
/// <c>/v1.0/devices/{id}/stream/actions/allocate</c>, and for discovery
/// <c>/v1.0/iot-01/associated-users/devices</c>.
/// </para>
/// </summary>
public sealed class TuyaHomeGateway(TuyaCloudClient client, TimeProvider clock) : IHomeDeviceGateway
{
    /// <summary>
    /// The status code every Tuya curtain motor reports its position on — the
    /// standard "cl" category schema, the same across vendors. There is no
    /// per-device override for it: <see cref="HomeDevice.PowerCode"/> stays the
    /// write-only command code (<c>control</c>), and this is what is read back.
    /// </summary>
    private const string CurtainPositionCode = "percent_state";

    /// <summary>
    /// Which way the motor last ran: <c>opening</c> or <c>closing</c>. Switch-type
    /// curtain modules — the suites' — report no position at all, or one that was
    /// never calibrated (0 with the blind fully open), and this is the only thing
    /// they say truly. When it is there it decides; the position is then left out.
    /// </summary>
    private const string CurtainMotionCode = "work_state";

    /// <summary>At or above this, the motor counts as open; at or below the mirror, closed. Between the two, it is mid-travel — no state is asserted.</summary>
    private const int CurtainOpenThreshold = 90;

    /// <summary>
    /// Some thermostats — the pool heat pump among them — keep their schema in
    /// tenths of a degree ("scale": 1) and the status list does not say so: it
    /// reports 331 for 33,1 °C. No setpoint or water reading in whole degrees
    /// reaches 100, and none in tenths stays below it above 10 °C, so the value
    /// itself tells the scale. The scale a read found is what the next write uses.
    /// </summary>
    private const int TenthsThreshold = 100;

    /// <summary>The status codes a climate device measures on, with the name the panel shows.</summary>
    private static readonly Dictionary<string, string> SensorLabels = new(StringComparer.Ordinal)
    {
        ["temp_current"] = "atual",
        ["temp_cur"] = "atual",
        ["water_temp"] = "água",
        ["temp_in"] = "entrada",
        ["temp_out"] = "saída",
    };

    private readonly ConcurrentDictionary<string, bool> _tenths = new(StringComparer.Ordinal);

    private const int CurtainClosedThreshold = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TuyaCloudClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public bool IsConfigured(HomeProvider provider) => provider == HomeProvider.Tuya && _client.IsConfigured;

    public async Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Provider != HomeProvider.Tuya)
        {
            return HomeReply.Failure<HomeDeviceState>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        // The device record says whether it is online; the status list says what
        // its data points hold. Both are read, because a stale status on an
        // offline relay looks exactly like a lamp that is on.
        var info = await _client.GetAsync(Path("/v1.0/devices/", device.Id), query: null, cancellationToken).ConfigureAwait(false);

        if (!info.Ok)
        {
            return HomeReply.Failure<HomeDeviceState>(info.Code, info.Message);
        }

        if (!info.Value!.Success)
        {
            return Rejected<HomeDeviceState>(info.Value);
        }

        var online = info.Value.Result.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True;

        var status = await _client.GetAsync(Path("/v1.0/devices/", device.Id, "/status"), query: null, cancellationToken).ConfigureAwait(false);

        if (!status.Ok)
        {
            return HomeReply.Failure<HomeDeviceState>(status.Code, status.Message);
        }

        if (!status.Value!.Success)
        {
            return Rejected<HomeDeviceState>(status.Value);
        }

        HomePower? power = null;
        double? temperature = null;
        var sensors = ImmutableArray.CreateBuilder<HomeSensor>();
        var gauges = ImmutableArray.CreateBuilder<HomeGauge>();
        HomePower? moved = null;

        if (status.Value.Result.ValueKind == JsonValueKind.Array)
        {
            foreach (var point in status.Value.Result.EnumerateArray())
            {
                if (!point.TryGetProperty("code", out var codeElement) || codeElement.GetString() is not { } code)
                {
                    continue;
                }

                if (device.Class == HomeDeviceClass.Curtain)
                {
                    if (string.Equals(code, CurtainMotionCode, StringComparison.Ordinal)
                        && point.TryGetProperty("value", out var motion) && motion.ValueKind == JsonValueKind.String)
                    {
                        moved = motion.GetString() switch { "opening" => HomePower.On, "closing" => HomePower.Off, _ => moved };
                    }
                    else if (string.Equals(code, CurtainPositionCode, StringComparison.Ordinal)
                        && point.TryGetProperty("value", out var percentValue)
                        && percentValue.TryGetInt32(out var percent))
                    {
                        power = percent switch
                        {
                            >= CurtainOpenThreshold => HomePower.On,
                            <= CurtainClosedThreshold => HomePower.Off,
                            _ => null,
                        };

                        // How far open, as the motor reads it: the model draws the blind there, mid-travel included.
                        gauges.Add(new HomeGauge("abertura", Math.Clamp(percent, 0, 100)));
                    }

                    continue;
                }

                if (string.Equals(code, device.PowerCode, StringComparison.Ordinal)
                    && point.TryGetProperty("value", out var powerValue)
                    && powerValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    power = powerValue.ValueKind == JsonValueKind.True ? HomePower.On : HomePower.Off;
                }
                else if (device.TemperatureCode is { } temperatureCode
                    && string.Equals(code, temperatureCode, StringComparison.Ordinal)
                    && point.TryGetProperty("value", out var temperatureValue)
                    && temperatureValue.TryGetInt32(out var setpoint))
                {
                    var tenths = setpoint >= TenthsThreshold;
                    _tenths[device.Id] = tenths;
                    temperature = tenths ? setpoint / 10.0 : setpoint;
                }
                else if (device.Class == HomeDeviceClass.Climate
                    && SensorLabels.TryGetValue(code, out var label)
                    && point.TryGetProperty("value", out var sensorValue)
                    && sensorValue.TryGetInt32(out var reading))
                {
                    sensors.Add(new HomeSensor(label, reading >= TenthsThreshold ? reading / 10.0 : reading));
                }
            }
        }

        if (moved is not null)
        {
            power = moved;
            gauges.Clear();
        }

        return HomeReply.Success(new HomeDeviceState(online, power, temperature, _clock.GetUtcNow(), sensors.ToImmutable(), Gauges: gauges.ToImmutable()));
    }

    public async Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(command);

        if (device.Provider != HomeProvider.Tuya)
        {
            return HomeReply.Failure<bool>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        if (device.Class == HomeDeviceClass.Lock)
        {
            return await UnlockAsync(device, command, cancellationToken).ConfigureAwait(false);
        }

        var commands = device.Class == HomeDeviceClass.Curtain
            ? new List<object> { new { code = device.PowerCode, value = command.Power == HomePower.On ? "open" : "close" } }
            : new List<object> { new { code = device.PowerCode, value = command.Power == HomePower.On } };

        if (command.Temperature is { } celsius && device.TemperatureCode is { } temperatureCode)
        {
            var tenths = _tenths.TryGetValue(device.Id, out var known) && known;
            commands.Add(new { code = temperatureCode, value = tenths ? celsius * 10 : celsius });
        }

        var body = JsonSerializer.Serialize(new { commands }, Json);
        var reply = await _client.PostAsync(Path("/v1.0/devices/", device.Id, "/commands"), body, cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            return HomeReply.Failure<bool>(reply.Code, reply.Message);
        }

        if (!reply.Value!.Success)
        {
            return Rejected<bool>(reply.Value);
        }

        return HomeReply.Success(true);
    }

    /// <summary>
    /// Tuya's remote, password-free unlock: a short-lived ticket first, then the
    /// door operation quoting it. Needs the "Smart Lock Open Service" API on the
    /// cloud project and a lock that reports <c>remote_no_dp_key</c>; a lock that
    /// does not is refused by the cloud, and that refusal is passed on as-is.
    /// </summary>
    private async Task<HomeReply<bool>> UnlockAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        if (command.Power == HomePower.Off)
        {
            return HomeReply.Failure<bool>(HomeReplyCodes.Rejected,
                string.Create(CultureInfo.CurrentCulture, $"{device.DisplayName} tranca sozinha; daqui só destranco."));
        }

        var ticket = await _client.PostAsync(Path("/v1.0/smart-lock/devices/", device.Id, "/password-ticket"), "{}", cancellationToken).ConfigureAwait(false);

        if (!ticket.Ok)
        {
            return HomeReply.Failure<bool>(ticket.Code, ticket.Message);
        }

        if (!ticket.Value!.Success)
        {
            return Rejected<bool>(ticket.Value);
        }

        if (!ticket.Value.Result.TryGetProperty("ticket_id", out var ticketElement)
            || ticketElement.GetString() is not { Length: > 0 } ticketId)
        {
            return HomeReply.Failure<bool>(HomeReplyCodes.Rejected, "Tuya não deu um bilhete para destrancar.");
        }

        var body = JsonSerializer.Serialize(new { ticket_id = ticketId, open = true }, Json);
        var reply = await _client.PostAsync(Path("/v1.0/smart-lock/devices/", device.Id, "/password-free/door-operate"), body, cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            return HomeReply.Failure<bool>(reply.Code, reply.Message);
        }

        return reply.Value!.Success ? HomeReply.Success(true) : Rejected<bool>(reply.Value);
    }

    public async Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Provider != HomeProvider.Tuya)
        {
            return HomeReply.Failure<CameraFeed>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        // HLS, because it is the one of the three a page can play. RTSP needs a
        // player the hub does not ship, and WebRTC needs a signalling exchange.
        var reply = await _client.PostAsync(
            Path("/v1.0/devices/", device.Id, "/stream/actions/allocate"),
            """{"type":"hls"}""",
            cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            return HomeReply.Failure<CameraFeed>(reply.Code, reply.Message);
        }

        if (!reply.Value!.Success)
        {
            return Rejected<CameraFeed>(reply.Value);
        }

        if (!reply.Value.Result.TryGetProperty("url", out var urlElement)
            || urlElement.GetString() is not { Length: > 0 } url
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream, "Tuya não devolveu uma URL https de transmissão.");
        }

        // Tuya's stream URLs are short-lived and the response does not always
        // say by how much. Thirty seconds is documented for the allocate call to
        // be consumed; the player keeps the session alive once it starts.
        return HomeReply.Success(new CameraFeed(uri, "hls", _clock.GetUtcNow().AddMinutes(5)));
    }

    /// <summary>
    /// Tuya has no still-picture endpoint for a device this project can call:
    /// the cloud hands out streams, and a frame would have to be pulled out of
    /// one with ffmpeg. Refused plainly instead of half-implemented — the owner
    /// hears "não consigo tirar foto da campainha", not silence.
    /// </summary>
    public Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(device.Provider != HomeProvider.Tuya
            ? HomeReply.Failure<CameraFrame>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString())
            : HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame,
                "A nuvem da Tuya não me dá uma foto parada; só a transmissão ao vivo."));
    }

    public async Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken)
    {
        if (provider != HomeProvider.Tuya)
        {
            return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.ProviderUnsupported, provider.ToString());
        }

        var found = ImmutableArray.CreateBuilder<DiscoveredDevice>();
        var lastRowKey = string.Empty;

        // Paged. The loop is bounded so a cloud that keeps handing back the same
        // key cannot keep the hub here forever.
        for (var page = 0; page < 20; page++)
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal) { ["size"] = "100" };

            if (lastRowKey.Length > 0)
            {
                query["last_row_key"] = lastRowKey;
            }

            var reply = await _client.GetAsync("/v1.0/iot-01/associated-users/devices", query, cancellationToken).ConfigureAwait(false);

            if (!reply.Ok)
            {
                return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(reply.Code, reply.Message);
            }

            if (!reply.Value!.Success)
            {
                return Rejected<ImmutableArray<DiscoveredDevice>>(reply.Value);
            }

            var result = reply.Value.Result;

            if (result.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
            {
                foreach (var device in devices.EnumerateArray())
                {
                    found.Add(new DiscoveredDevice(
                        Text(device, "id"),
                        Text(device, "name"),
                        Text(device, "category"),
                        Text(device, "product_name"),
                        device.TryGetProperty("online", out var online) && online.ValueKind == JsonValueKind.True,
                        Codes(device)));
                }
            }

            var hasMore = result.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
            var nextKey = Text(result, "last_row_key");

            if (!hasMore || nextKey.Length == 0 || string.Equals(nextKey, lastRowKey, StringComparison.Ordinal))
            {
                break;
            }

            lastRowKey = nextKey;
        }

        return HomeReply.Success(found.ToImmutable());
    }

    private static ImmutableArray<string> Codes(JsonElement device)
    {
        if (!device.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var codes = ImmutableArray.CreateBuilder<string>();

        foreach (var point in status.EnumerateArray())
        {
            var code = Text(point, "code");

            if (code.Length > 0)
            {
                codes.Add(code);
            }
        }

        return codes.ToImmutable();
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static HomeReply<T> Rejected<T>(TuyaResponse response)
    {
        // 2001 is Tuya for "device is offline"; the rest is the cloud saying no.
        var code = response.Code == 2001
            ? HomeReplyCodes.Offline
            : response.IsTokenProblem ? HomeReplyCodes.Unauthorized : HomeReplyCodes.Rejected;

        return HomeReply.Failure<T>(code,
            string.Create(CultureInfo.InvariantCulture, $"Tuya recusou ({response.Code}: {response.Message})."));
    }

    /// <summary>The id is opaque and goes through the URL; escaping it is what keeps it inside one path segment.</summary>
    private static string Path(string prefix, string deviceId, string suffix = "") =>
        string.Concat(prefix, Uri.EscapeDataString(deviceId), suffix);
}
