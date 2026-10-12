using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// A Tholz Smart Connect module, seen through <see cref="IHomeDeviceGateway"/> and
/// reached on the LAN through <see cref="TholzSocketClient"/>.
/// <para>
/// Each device names one slot of one module — a heating (<c>heat0</c>) or an output
/// (<c>out1</c>) — by its <see cref="HomeDevice.Host"/> and <see cref="HomeDevice.Slot"/>.
/// A heating's power is a mode, not a relay: turning it on picks the most
/// thermostatic mode its type accepts (<see cref="TholzHeating"/>), so a gas backup
/// is never left forced on, and its setpoint is written in the tenths-of-a-degree
/// the socket speaks, clamped to the range the module itself reports.
/// </para>
/// </summary>
public sealed class TholzHomeGateway(TholzSocketClient client, TimeProvider clock) : IHomeDeviceGateway
{
    private const int Port = TholzSocketClient.DefaultPort;

    private readonly TholzSocketClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    // A getDevice answers for every slot of the module at once. The panel reads
    // its devices one after another, so the four slots of one module share one
    // exchange instead of four; a write forgets the snapshot so the next read
    // sees what the module did with it.
    private static readonly TimeSpan SnapshotLife = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, (long Stamp, HomeReply<JsonElement> Reply)> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    // No keys: the socket is gated on the LAN, not on a credential, so a Tholz
    // provider is "configured" wherever a device on the list names a host.
    public bool IsConfigured(HomeProvider provider) => provider == HomeProvider.Tholz;

    public async Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Provider != HomeProvider.Tholz)
        {
            return HomeReply.Failure<HomeDeviceState>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        var read = await SnapshotAsync(device.Host!, cancellationToken).ConfigureAwait(false);

        if (!read.Ok)
        {
            return HomeReply.Failure<HomeDeviceState>(read.Code, read.Message);
        }

        if (!TrySlot(read.Value, device, out var slot))
        {
            // The module answered, but not for this slot: the device is named wrong,
            // or the module no longer has it. Offline is the honest reading — not a
            // made-up power state.
            return HomeReply.Success(new HomeDeviceState(Online: false, Power: null, Temperature: null, _clock.GetUtcNow()));
        }

        HomePower? power = null;
        bool? running = null;
        double? temperature = null;
        var sensors = ImmutableArray<HomeSensor>.Empty;

        if (device.Class == HomeDeviceClass.Climate)
        {
            if (TryInt(slot, "opMode", out var opMode))
            {
                power = TholzHeating.IsOn(opMode) ? HomePower.On : HomePower.Off;
            }

            // opMode diz o que o the owner escolheu (automático o dia inteiro); o
            // "on", só leitura, diz se a saída está acionada agora — a bomba do
            // solar girando, a da recirculação empurrando.
            if (slot.TryGetProperty("on", out var active) && active.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                running = active.ValueKind == JsonValueKind.True;
            }

            if (TryInt(slot, "sp", out var sp))
            {
                temperature = DeciToCelsius(sp);
            }

            if (TryInt(slot, "type", out var type))
            {
                sensors = Sensors(slot, type);
            }
        }
        else if (slot.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            power = on.ValueKind == JsonValueKind.True ? HomePower.On : HomePower.Off;
        }

        return HomeReply.Success(new HomeDeviceState(Online: true, power, temperature, _clock.GetUtcNow(), sensors, Running: running));
    }

    public async Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(command);

        if (device.Provider != HomeProvider.Tholz)
        {
            return HomeReply.Failure<bool>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        var argument = device.Class == HomeDeviceClass.Climate
            ? await BuildHeatingAsync(device, command, cancellationToken).ConfigureAwait(false)
            : HomeReply.Success<object>(new Dictionary<string, object>
            {
                [CollectionFor(device)] = new Dictionary<string, object> { [device.Slot!] = new { on = command.Power == HomePower.On } },
            });

        if (!argument.Ok)
        {
            return HomeReply.Failure<bool>(argument.Code, argument.Message);
        }

        var write = await _client.SetDeviceAsync(device.Host!, Port, argument.Value!, cancellationToken).ConfigureAwait(false);
        _snapshots.TryRemove(device.Host!, out _);

        if (!write.Ok)
        {
            return HomeReply.Failure<bool>(write.Code, write.Message);
        }

        // A setDevice reply carries the final state; that it came back as the
        // documented object is the module accepting the write. The verifier reads
        // the slot again to say whether it took.
        return write.Value.TryGetProperty("response", out _)
            ? HomeReply.Success(true)
            : HomeReply.Failure<bool>(HomeReplyCodes.Rejected, "O módulo Tholz respondeu sem confirmar a escrita.");
    }

    /// <summary>
    /// A heating command needs the slot's <c>type</c> to choose a mode it accepts
    /// and its <c>minSp</c>/<c>maxSp</c> to clamp the setpoint, so the write is
    /// preceded by a read. Guessing either against real hardware is exactly what
    /// this avoids.
    /// </summary>
    private async Task<HomeReply<object>> BuildHeatingAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        var read = await SnapshotAsync(device.Host!, cancellationToken).ConfigureAwait(false);

        if (!read.Ok)
        {
            return HomeReply.Failure<object>(read.Code, read.Message);
        }

        if (!TrySlot(read.Value, device, out var slot) || !TryInt(slot, "type", out var type))
        {
            return HomeReply.Failure<object>(HomeReplyCodes.Offline,
                string.Create(CultureInfo.CurrentCulture, $"{device.DisplayName} não respondeu o estado para eu ajustar."));
        }

        var heating = new Dictionary<string, object> { ["opMode"] = TholzHeating.ModeFor(command.Power, type) };

        if (command.Temperature is { } celsius)
        {
            var min = TryInt(slot, "minSp", out var minSp) ? minSp : device.MinSetpoint * 10;
            var max = TryInt(slot, "maxSp", out var maxSp) ? maxSp : device.MaxSetpoint * 10;

            heating["sp"] = TholzHeating.ToDeciCelsius(celsius, min, max);
        }

        return HomeReply.Success<object>(new Dictionary<string, object>
        {
            ["heatings"] = new Dictionary<string, object> { [device.Slot!] = heating },
        });
    }

    /// <summary>Tholz modules on the owner's list are heaters and outputs; none is a camera.</summary>
    public Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream, "Um módulo Tholz não tem câmera."));
    }

    /// <inheritdoc cref="AllocateStreamAsync"/>
    public Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame, "Um módulo Tholz não tem câmera."));
    }

    /// <summary>
    /// Discovery would mean reading a module and listing its slots, but nothing
    /// here holds a host to read without a device already on the list. It is a
    /// configuration aid, so it answers empty rather than failing.
    /// </summary>
    public Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken) =>
        Task.FromResult(provider == HomeProvider.Tholz
            ? HomeReply.Success(ImmutableArray<DiscoveredDevice>.Empty)
            : HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.ProviderUnsupported, provider.ToString()));

    /// <summary>
    /// The top-level object of a getDevice/setDevice reply that holds this device's
    /// slot: a heating is a <c>heatings</c>, an RGB light a <c>leds</c>, and every
    /// other output (filter, waterfall, auxiliary relay) an <c>outputs</c>. All three
    /// carry the same editable <c>on</c>, so only the collection name differs.
    /// </summary>
    private static string CollectionFor(HomeDevice device) => device.Class switch
    {
        HomeDeviceClass.Climate => "heatings",
        HomeDeviceClass.Light => "leds",
        _ => "outputs",
    };

    /// <summary>Navigates <c>response.{heatings|leds|outputs}.{slot}</c> of a getDevice reply.</summary>
    private static bool TrySlot(JsonElement reply, HomeDevice device, out JsonElement slot)
    {
        slot = default;

        if (!reply.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var collection = CollectionFor(device);

        return response.TryGetProperty(collection, out var slots)
            && slots.ValueKind == JsonValueKind.Object
            && device.Slot is { } key
            && slots.TryGetProperty(key, out slot)
            && slot.ValueKind == JsonValueKind.Object;
    }

    /// <summary>The named readings a heating slot carries, skipping a sensor the module reports as faulted or absent.</summary>
    private static ImmutableArray<HomeSensor> Sensors(JsonElement slot, int type)
    {
        var labels = TholzHeating.SensorLabels(type);

        if (labels.IsEmpty)
        {
            return [];
        }

        var readings = ImmutableArray.CreateBuilder<HomeSensor>(labels.Length);

        foreach (var (key, label) in labels)
        {
            if (!TryInt(slot, key, out var deci))
            {
                // The module sends null for a sensor a unit does not have wired.
                continue;
            }

            readings.Add(new HomeSensor(label, deci == TholzHeating.SensorFault ? null : DeciToCelsius(deci)));
        }

        return readings.ToImmutable();
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>Tenths of a degree as the socket carries them, kept as tenths: 331 is 33,1 °C, not 33.</summary>
    private static double DeciToCelsius(int deci) => deci / 10.0;

    private async Task<HomeReply<JsonElement>> SnapshotAsync(string host, CancellationToken cancellationToken)
    {
        if (_snapshots.TryGetValue(host, out var kept) && kept.Reply.Ok && _clock.GetElapsedTime(kept.Stamp) < SnapshotLife)
        {
            return kept.Reply;
        }

        var reply = await _client.GetDeviceAsync(host, Port, cancellationToken).ConfigureAwait(false);

        if (reply.Ok)
        {
            _snapshots[host] = (_clock.GetTimestamp(), reply);
        }
        else
        {
            _snapshots.TryRemove(host, out _);
        }

        return reply;
    }
}
