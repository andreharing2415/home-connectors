using System.Collections.Immutable;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// A Zagonel Smart Banho shower, seen through <see cref="IHomeDeviceGateway"/> and
/// reached on its cloud broker through <see cref="ZagonelMqttClient"/>. The device
/// is named by its cloud id (<see cref="HomeDevice.Id"/>), the Mongo id the app's
/// own API hands out — not the MAC the display shows.
/// <para>
/// A shower is read far more than it is driven: the hub reports the water it is
/// delivering, whether it is running, and the temperature it is set to. The one
/// thing it writes is that target — the active preset (<c>Preset_1</c>), in the
/// thousandths of a degree the broker speaks. It does not pretend to a power
/// switch: no cloud command opens the valve, so an on/off with no temperature is
/// refused in plain words rather than silently doing nothing.
/// </para>
/// </summary>
public sealed class ZagonelHomeGateway(ZagonelMqttClient client, TimeProvider clock) : IHomeDeviceGateway
{
    private readonly ZagonelMqttClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    // No keys: the broker is open on the internet and gates on the device id, not
    // on a credential, so a Zagonel provider is "configured" wherever a device on
    // the list names one.
    public bool IsConfigured(HomeProvider provider) => provider == HomeProvider.Zagonel;

    public async Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Provider != HomeProvider.Zagonel)
        {
            return HomeReply.Failure<HomeDeviceState>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        var read = await _client.GetStatusAsync(device.Id, cancellationToken).ConfigureAwait(false);

        if (!read.Ok)
        {
            return HomeReply.Failure<HomeDeviceState>(read.Code, read.Message);
        }

        var status = read.Value;

        if (!TryStr(status, "St", out var st))
        {
            // The broker answered, but not with a Status. Offline is the honest
            // reading, not a made-up temperature.
            return HomeReply.Success(new HomeDeviceState(Online: false, Power: null, Temperature: null, _clock.GetUtcNow()));
        }

        var running = string.Equals(st, "RUN", StringComparison.Ordinal);

        double? setpoint = TryInt(status, "Ts", out var ts) ? ts / 1000.0 : null;

        var sensors = ImmutableArray.CreateBuilder<HomeSensor>(2);

        // "saída" is the water that reaches the body — what the app shows as the
        // current temperature; "entrada" is the cold feed before the heater.
        if (TryInt(status, "To", out var outlet))
        {
            sensors.Add(new HomeSensor("saída", outlet / 1000.0));
        }

        if (TryInt(status, "Ti", out var inlet))
        {
            sensors.Add(new HomeSensor("entrada", inlet / 1000.0));
        }

        var measures = ImmutableArray<HomeMeasure>.Empty;

        if (running)
        {
            // Only while water runs do flow and power mean anything; idle they are
            // zero and only clutter the reading.
            var builder = ImmutableArray.CreateBuilder<HomeMeasure>(2);

            if (TryInt(status, "Fl", out var flow))
            {
                builder.Add(new HomeMeasure("vazão", flow / 1000.0, "L/min"));
            }

            if (TryInt(status, "Pw", out var power))
            {
                // Pw is tenths of a watt (the app divides by 10): a 7,5 kW shower
                // reports 75000, not 7500.
                builder.Add(new HomeMeasure("potência", power / 10.0, "W"));
            }

            measures = builder.ToImmutable();
        }

        return HomeReply.Success(new HomeDeviceState(
            Online: true,
            Power: running ? HomePower.On : HomePower.Off,
            Temperature: setpoint,
            ObservedAt: _clock.GetUtcNow(),
            Sensors: sensors.ToImmutable(),
            Status: running ? "em uso" : "ocioso",
            Measures: measures,
            Running: running));
    }

    public async Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(command);

        if (device.Provider != HomeProvider.Zagonel)
        {
            return HomeReply.Failure<bool>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        if (command.Temperature is not { } celsius)
        {
            // A shower has no cloud power switch; the only thing to set is the
            // target temperature. Say so instead of turning nothing.
            return HomeReply.Failure<bool>(HomeReplyCodes.Rejected,
                "O chuveiro Zagonel não liga nem desliga pela nuvem; eu só ajusto a temperatura programada.");
        }

        var target = Math.Clamp(celsius, device.MinSetpoint, device.MaxSetpoint);

        var write = await _client.SendFieldAsync(device.Id, "Preset_1", target * 1000, cancellationToken).ConfigureAwait(false);

        if (!write.Ok)
        {
            return HomeReply.Failure<bool>(write.Code, write.Message);
        }

        // The broker echoes a frame once it has taken the write; the executor's
        // verifier reads the setpoint back to say it stuck.
        return HomeReply.Success(true);
    }

    /// <summary>A Zagonel shower has no camera.</summary>
    public Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream, "Um chuveiro Zagonel não tem câmera."));
    }

    /// <inheritdoc cref="AllocateStreamAsync"/>
    public Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame, "Um chuveiro Zagonel não tem câmera."));
    }

    /// <summary>
    /// Discovery would mean a device id to read, and nothing here holds one without
    /// a device already on the list. A configuration aid, so it answers empty
    /// rather than failing.
    /// </summary>
    public Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken) =>
        Task.FromResult(provider == HomeProvider.Zagonel
            ? HomeReply.Success(ImmutableArray<DiscoveredDevice>.Empty)
            : HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.ProviderUnsupported, provider.ToString()));

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryStr(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return value.Length > 0;
        }

        value = string.Empty;
        return false;
    }
}
