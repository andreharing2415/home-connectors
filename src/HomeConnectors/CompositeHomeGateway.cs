using System.Collections.Immutable;
using System.Runtime.Versioning;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// Routes each call by the device's own <see cref="HomeDevice.Provider"/> —
/// the same dispatch-by-kind shape as <see cref="CompositeActionExecutor"/>,
/// one level down. Routing is by provider identity, never by which gateway
/// happens to be configured right now: an unconfigured Tuya device must still
/// reach <see cref="TuyaHomeGateway"/> and answer
/// <see cref="HomeReplyCodes.NotConfigured"/> from there, not silently fall to
/// the Hikvision adapter and answer <see cref="HomeReplyCodes.ProviderUnsupported"/>
/// instead.
/// <para>
/// <see cref="HomeProvider.Hikvision"/> goes to its NVR adapter and
/// <see cref="HomeProvider.Tholz"/> to its LAN socket, <see cref="HomeProvider.Xiaomi"/>
/// to its miIO adapter, <see cref="HomeProvider.BlueRiiot"/> to its cloud reader,
/// <see cref="HomeProvider.Daikin"/> to its LAN module,
/// <see cref="HomeProvider.GoodWe"/> to its Modbus reader, and
/// <see cref="HomeProvider.Zagonel"/> to its cloud MQTT broker. Everything else —
/// including <see cref="HomeProvider.GoogleNest"/>, which has no adapter of its
/// own — goes to <see cref="TuyaHomeGateway"/>, which already refuses a device
/// whose provider is not its own.
/// </para>
/// </summary>
public sealed class CompositeHomeGateway(
    TuyaHomeGateway tuya,
    TholzHomeGateway tholz,
    HikvisionHomeGateway hikvision,
    XiaomiHomeGateway xiaomi,
    BlueRiiotHomeGateway blueRiiot,
    DaikinHomeGateway daikin,
    GoodWeHomeGateway goodWe,
    ZagonelHomeGateway zagonel) : IHomeDeviceGateway
{
    private readonly IHomeDeviceGateway _tuya = tuya ?? throw new ArgumentNullException(nameof(tuya));
    private readonly IHomeDeviceGateway _tholz = tholz ?? throw new ArgumentNullException(nameof(tholz));
    private readonly IHomeDeviceGateway _hikvision = hikvision ?? throw new ArgumentNullException(nameof(hikvision));
    private readonly IHomeDeviceGateway _xiaomi = xiaomi ?? throw new ArgumentNullException(nameof(xiaomi));
    private readonly IHomeDeviceGateway _blueRiiot = blueRiiot ?? throw new ArgumentNullException(nameof(blueRiiot));
    private readonly IHomeDeviceGateway _daikin = daikin ?? throw new ArgumentNullException(nameof(daikin));
    private readonly IHomeDeviceGateway _goodWe = goodWe ?? throw new ArgumentNullException(nameof(goodWe));
    private readonly IHomeDeviceGateway _zagonel = zagonel ?? throw new ArgumentNullException(nameof(zagonel));

    public bool IsConfigured(HomeProvider provider) => For(provider).IsConfigured(provider);

    public Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken) =>
        For(device.Provider).ReadStateAsync(device, cancellationToken);

    public Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken) =>
        For(device.Provider).SendAsync(device, command, cancellationToken);

    public Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken) =>
        For(device.Provider).AllocateStreamAsync(device, cancellationToken);

    public Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken) =>
        For(device.Provider).CaptureFrameAsync(device, cancellationToken);

    public Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken) =>
        For(provider).DiscoverAsync(provider, cancellationToken);

    private IHomeDeviceGateway For(HomeProvider provider) => provider switch
    {
        HomeProvider.Hikvision => _hikvision,
        HomeProvider.Tholz => _tholz,
        HomeProvider.Xiaomi => _xiaomi,
        HomeProvider.BlueRiiot => _blueRiiot,
        HomeProvider.Daikin => _daikin,
        HomeProvider.GoodWe => _goodWe,
        HomeProvider.Zagonel => _zagonel,
        _ => _tuya,
    };
}
