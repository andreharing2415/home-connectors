using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// A Daikin split (Wi-Fi module of the app Daikin Smart AC), seen through
/// <see cref="IHomeDeviceGateway"/> and reached on the LAN through
/// <see cref="DaikinLocalClient"/>, keyed by the module's AES key in the store.
/// <para>
/// Power and setpoint are the only things written. Turning it on leaves the
/// mode, the fan and the swing as the remote last set them, the same as pressing
/// the power button; the setpoint is clamped to the device's range before it
/// goes out. The module reports its state in the reply to every write, and the
/// verifier reads it again anyway.
/// </para>
/// </summary>
public sealed class DaikinHomeGateway(DaikinLocalClient client, IHomeCredentialStore credentials, TimeProvider clock) : IHomeDeviceGateway
{
    // The module reports 255 for a sensor it does not have: this split has no
    // outdoor probe wired to the Wi-Fi board.
    private const double MissingSensor = 255;

    private readonly DaikinLocalClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly IHomeCredentialStore _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public bool IsConfigured(HomeProvider provider) =>
        provider == HomeProvider.Daikin && _credentials.Read(HomeProvider.Daikin) is { IsComplete: true };

    public async Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var link = Resolve(device);

        if (!link.Ok)
        {
            return HomeReply.Failure<HomeDeviceState>(link.Code, link.Message);
        }

        var read = await _client.ReadAsync(link.Value.Host, link.Value.Key, cancellationToken).ConfigureAwait(false);

        return read.Ok
            ? HomeReply.Success(State(read.Value))
            : HomeReply.Failure<HomeDeviceState>(read.Code, read.Message);
    }

    public async Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(command);

        var link = Resolve(device);

        if (!link.Ok)
        {
            return HomeReply.Failure<bool>(link.Code, link.Message);
        }

        var port1 = new Dictionary<string, object> { ["power"] = command.Power == HomePower.On ? 1 : 0 };

        if (command.Temperature is { } celsius)
        {
            port1["temperature"] = (double)Math.Clamp(celsius, device.MinSetpoint, device.MaxSetpoint);
        }

        var write = await _client.WriteAsync(link.Value.Host, link.Value.Key, port1, cancellationToken).ConfigureAwait(false);

        if (!write.Ok)
        {
            return HomeReply.Failure<bool>(write.Code, write.Message);
        }

        return write.Value.TryGetProperty("port1", out _)
            ? HomeReply.Success(true)
            : HomeReply.Failure<bool>(HomeReplyCodes.Rejected, "O Daikin respondeu sem confirmar a escrita.");
    }

    /// <summary>An air conditioner has no camera.</summary>
    public Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream, "Um ar-condicionado não tem câmera."));
    }

    /// <inheritdoc cref="AllocateStreamAsync"/>
    public Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame, "Um ar-condicionado não tem câmera."));
    }

    /// <summary>One key opens one module; there is nothing to list beyond the device already on the list.</summary>
    public Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken) =>
        Task.FromResult(provider == HomeProvider.Daikin
            ? HomeReply.Success(ImmutableArray<DiscoveredDevice>.Empty)
            : HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.ProviderUnsupported, provider.ToString()));

    /// <summary>Reads <c>port1</c> of an <c>/acstatus</c> reply into the house's terms.</summary>
    internal HomeDeviceState State(JsonElement reply)
    {
        if (!reply.TryGetProperty("port1", out var port) || port.ValueKind != JsonValueKind.Object)
        {
            return new HomeDeviceState(Online: false, Power: null, Temperature: null, _clock.GetUtcNow());
        }

        HomePower? power = TryNumber(port, "power", out var p) ? (p == 0 ? HomePower.Off : HomePower.On) : null;
        double? setpoint = TryNumber(port, "temperature", out var t) ? t : null;

        var sensors = ImmutableArray.CreateBuilder<HomeSensor>();

        if (port.TryGetProperty("sensors", out var readings) && readings.ValueKind == JsonValueKind.Object)
        {
            if (TryNumber(readings, "room_temp", out var room))
            {
                sensors.Add(new HomeSensor("ambiente", room >= MissingSensor ? null : room));
            }

            if (TryNumber(readings, "out_temp", out var outside) && outside < MissingSensor)
            {
                sensors.Add(new HomeSensor("externa", outside));
            }
        }

        var status = power == HomePower.On && TryNumber(port, "mode", out var mode) ? ModeName((int)mode) : null;

        return new HomeDeviceState(Online: true, power, setpoint, _clock.GetUtcNow(), sensors.ToImmutable(), status);
    }

    /// <summary>The mode codes the module speaks, in the app's words.</summary>
    internal static string? ModeName(int mode) => mode switch
    {
        0 => "automático",
        2 => "desumidificar",
        3 => "resfriar",
        4 => "aquecer",
        6 => "ventilar",
        _ => null,
    };

    private HomeReply<(string Host, byte[] Key)> Resolve(HomeDevice device)
    {
        if (device.Provider != HomeProvider.Daikin)
        {
            return HomeReply.Failure<(string, byte[])>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        if (_credentials.Read(HomeProvider.Daikin) is not { IsComplete: true } stored
            || DaikinLocalClient.ParseKey(stored.Secret) is not { } key)
        {
            return HomeReply.Failure<(string, byte[])>(HomeReplyCodes.NotConfigured, "Falta a chave do ar-condicionado Daikin.");
        }

        if (!string.Equals(stored.AccessId, device.Id, StringComparison.Ordinal))
        {
            return HomeReply.Failure<(string, byte[])>(HomeReplyCodes.NotConfigured,
                string.Create(CultureInfo.CurrentCulture, $"A chave guardada é de outro aparelho, não de {device.DisplayName}."));
        }

        // The list's Host wins when it names one, as for the Xiaomi: the store holds
        // what the owner pasted once, the list is what he edits when the router moves it.
        var host = string.IsNullOrWhiteSpace(device.Host) ? stored.Region : device.Host;

        return HomeReply.Success((host, key));
    }

    private static bool TryNumber(JsonElement element, string name, out double value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
