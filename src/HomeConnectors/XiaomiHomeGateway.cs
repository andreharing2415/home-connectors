using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// A Xiaomi robot vacuum on the LAN, seen through <see cref="IHomeDeviceGateway"/>
/// and reached through <see cref="MiioClient"/> with MIoT
/// <c>get_properties</c>/<c>action</c> calls addressed as <see cref="XiaomiVacuum"/>
/// lays them out.
/// <para>
/// The token comes from the protected store (<see cref="HomeProvider.Xiaomi"/>):
/// device id, token, host. One entry, so one Xiaomi device — a device on the
/// list whose id is not the stored one is refused rather than tried with a
/// token that is not its own.
/// </para>
/// <para>
/// No camera: the M40 has no photo camera (its obstacle sensor is structured
/// light). The map is <see cref="XiaomiVacuumMapService"/>'s: it comes through
/// the Xiaomi cloud, not this gateway.
/// </para>
/// </summary>
public sealed class XiaomiHomeGateway(MiioClient client, IHomeCredentialStore credentials, TimeProvider clock) : IHomeDeviceGateway
{
    private readonly MiioClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly IHomeCredentialStore _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public bool IsConfigured(HomeProvider provider) =>
        provider == HomeProvider.Xiaomi && _credentials.Read(HomeProvider.Xiaomi) is { IsComplete: true };

    public async Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var resolved = Resolve(device);

        if (!resolved.Ok)
        {
            return HomeReply.Failure<HomeDeviceState>(resolved.Code, resolved.Message);
        }

        var (host, token) = resolved.Value;

        var parameters = XiaomiVacuum.ReadSet
            .Select(a => new { did = device.Id, siid = a.Siid, piid = a.Iid })
            .ToArray();

        var reply = await _client.CallAsync(host, token, "get_properties", parameters, cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            return HomeReply.Failure<HomeDeviceState>(reply.Code, reply.Message);
        }

        var values = new Dictionary<XiaomiVacuum.Address, int>();

        if (reply.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reply.Value.EnumerateArray())
            {
                // code 0 is a value; anything else is the device saying it has
                // no such property, which is left out rather than read as zero.
                if (item.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) && c == 0
                    && item.TryGetProperty("siid", out var siid) && item.TryGetProperty("piid", out var piid)
                    && item.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var number))
                {
                    values[new XiaomiVacuum.Address(siid.GetInt32(), piid.GetInt32())] = number;
                }
            }
        }

        if (!values.TryGetValue(XiaomiVacuum.Status, out var status))
        {
            return HomeReply.Success(new HomeDeviceState(Online: false, Power: null, Temperature: null, _clock.GetUtcNow()));
        }

        var said = XiaomiVacuum.Describe(status);

        if (values.TryGetValue(XiaomiVacuum.Fault, out var fault) && fault != 0)
        {
            said = string.Create(CultureInfo.CurrentCulture, $"{said}, erro {fault}");
        }

        return HomeReply.Success(new HomeDeviceState(
            Online: true,
            XiaomiVacuum.PowerOf(status),
            Temperature: null,
            _clock.GetUtcNow(),
            Status: said,
            Gauges: XiaomiVacuum.Gauges(values)));
    }

    public async Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(command);

        var resolved = Resolve(device);

        if (!resolved.Ok)
        {
            return HomeReply.Failure<bool>(resolved.Code, resolved.Message);
        }

        if (command.Temperature is not null)
        {
            return HomeReply.Failure<bool>(HomeReplyCodes.Rejected,
                string.Create(CultureInfo.CurrentCulture, $"{device.DisplayName} não tem temperatura."));
        }

        var (host, token) = resolved.Value;
        var action = command.Power == HomePower.On ? XiaomiVacuum.StartClean : XiaomiVacuum.GoHome;

        if (command.Power == HomePower.On)
        {
            var mode = await SetCleanModeAsync(_client, host, token, device.Id, command.Clean ?? VacuumCleanMode.SweepAndMop, cancellationToken).ConfigureAwait(false);

            if (!mode.Ok)
            {
                return mode;
            }
        }

        var reply = await _client.CallAsync(
            host,
            token,
            "action",
            new { did = device.Id, siid = action.Siid, aiid = action.Iid, @in = Array.Empty<object>() },
            cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            return HomeReply.Failure<bool>(reply.Code, reply.Message);
        }

        // An action answers {"code":0} when the device took it. Anything else —
        // a negative code — is the device refusing, e.g. being told to clean
        // with the dustbin out.
        return reply.Value.ValueKind == JsonValueKind.Object
            && reply.Value.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) && c == 0
                ? HomeReply.Success(true)
                : HomeReply.Failure<bool>(HomeReplyCodes.Rejected,
                    string.Create(CultureInfo.CurrentCulture, $"{device.DisplayName} recusou o comando ({reply.Value})."));
    }

    public Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream, "O aspirador não tem câmera de imagem."));
    }

    public Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame, "O aspirador não tem câmera de imagem."));
    }

    /// <summary>
    /// Puts the robot in this mode before a clean: reads clean-setting, swaps
    /// its low byte and writes it back only when it differs, so the mop's
    /// wetness stays whatever the owner set in the app.
    /// </summary>
    internal static async Task<HomeReply<bool>> SetCleanModeAsync(
        MiioClient client, string host, byte[] token, string did, VacuumCleanMode mode, CancellationToken cancellationToken)
    {
        var address = XiaomiVacuum.CleanSetting;
        var read = await client.CallAsync(
            host, token, "get_properties", new object[] { new { did, siid = address.Siid, piid = address.Iid } }, cancellationToken).ConfigureAwait(false);

        if (!read.Ok)
        {
            return HomeReply.Failure<bool>(read.Code, read.Message);
        }

        if (read.Value.ValueKind != JsonValueKind.Array || read.Value.GetArrayLength() == 0
            || !read.Value[0].TryGetProperty("code", out var code) || !code.TryGetInt32(out var c) || c != 0
            || !read.Value[0].TryGetProperty("value", out var value) || !value.TryGetInt32(out var setting))
        {
            return HomeReply.Failure<bool>(HomeReplyCodes.Rejected, "O aspirador não disse em que modo está (" + read.Value + ").");
        }

        var wanted = XiaomiVacuum.WithMode(setting, mode);

        if (wanted == setting)
        {
            return HomeReply.Success(true);
        }

        var set = await client.CallAsync(
            host, token, "set_properties", new object[] { new { did, siid = address.Siid, piid = address.Iid, value = wanted } }, cancellationToken).ConfigureAwait(false);

        if (!set.Ok)
        {
            return HomeReply.Failure<bool>(set.Code, set.Message);
        }

        return set.Value.ValueKind == JsonValueKind.Array && set.Value.GetArrayLength() > 0
            && set.Value[0].TryGetProperty("code", out var sc) && sc.TryGetInt32(out var n) && n == 0
                ? HomeReply.Success(true)
                : HomeReply.Failure<bool>(HomeReplyCodes.Rejected, "O aspirador recusou o modo de limpeza (" + set.Value + ").");
    }

    /// <summary>Discovery would need the Xiaomi cloud login; the device is added by hand from the token extractor's list.</summary>
    public Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken) =>
        Task.FromResult(provider == HomeProvider.Xiaomi
            ? HomeReply.Success(ImmutableArray<DiscoveredDevice>.Empty)
            : HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.ProviderUnsupported, provider.ToString()));

    /// <summary>The host and token for this device, or why there are none.</summary>
    private HomeReply<(string Host, byte[] Token)> Resolve(HomeDevice device)
    {
        if (device.Provider != HomeProvider.Xiaomi)
        {
            return HomeReply.Failure<(string, byte[])>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        if (device.Class != HomeDeviceClass.Vacuum)
        {
            return HomeReply.Failure<(string, byte[])>(HomeReplyCodes.ProviderUnsupported,
                string.Create(CultureInfo.CurrentCulture, $"Só sei falar com aspiradores Xiaomi, e {device.DisplayName} é {device.Class}."));
        }

        if (_credentials.Read(HomeProvider.Xiaomi) is not { IsComplete: true } stored
            || MiioClient.ParseToken(stored.Secret) is not { } token)
        {
            return HomeReply.Failure<(string, byte[])>(HomeReplyCodes.NotConfigured, "Falta o token do aspirador Xiaomi.");
        }

        if (!string.Equals(stored.AccessId, device.Id, StringComparison.Ordinal))
        {
            return HomeReply.Failure<(string, byte[])>(HomeReplyCodes.NotConfigured,
                string.Create(CultureInfo.CurrentCulture, $"O token guardado é de outro aparelho, não de {device.DisplayName}."));
        }

        // The list's Host wins when it names one: the store holds what the owner
        // pasted once, the list is what he edits when the router moves it.
        var host = string.IsNullOrWhiteSpace(device.Host) ? stored.Region : device.Host;

        return HomeReply.Success((host, token));
    }
}
