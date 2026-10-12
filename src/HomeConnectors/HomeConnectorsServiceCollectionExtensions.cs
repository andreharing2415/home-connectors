using HomeConnectors.Adapters;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Every adapter, wired behind one <see cref="HomeConnectors.IHomeDeviceGateway"/>.
/// The host supplies what is its own to decide: the <see cref="HomeConnectors.HomeCatalog"/>
/// (which devices exist), the <see cref="HomeConnectors.IHomeCredentialStore"/> (where
/// the keys live), a <see cref="TimeProvider"/>, and the <see cref="HikvisionStreamServer"/>
/// (where camera transcodes are written).
/// </summary>
public static class HomeConnectorsServiceCollectionExtensions
{
    public static IServiceCollection AddHomeConnectors(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // One HttpClient for the clouds. Ten seconds is a cloud round trip and a
        // half; anything slower is reported as unreachable, not waited on.
        services.TryAddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(10) });

        services.TryAddSingleton<TuyaCloudClient>();
        services.TryAddSingleton<TuyaHomeGateway>();
        services.TryAddSingleton<HikvisionHomeGateway>();
        services.TryAddSingleton<TholzSocketClient>();
        services.TryAddSingleton<TholzHomeGateway>();
        services.TryAddSingleton<MiioClient>();
        services.TryAddSingleton<XiaomiHomeGateway>();
        services.TryAddSingleton<BlueRiiotCloudClient>();
        services.TryAddSingleton<BlueRiiotHomeGateway>();
        services.TryAddSingleton<DaikinLocalClient>();
        services.TryAddSingleton<DaikinHomeGateway>();
        services.TryAddSingleton<GoodWeModbusClient>();
        services.TryAddSingleton<GoodWeHomeGateway>();
        services.TryAddSingleton<ZagonelMqttClient>();
        services.TryAddSingleton<ZagonelHomeGateway>();

        services.TryAddSingleton<HomeConnectors.IHomeDeviceGateway>(provider => new CompositeHomeGateway(
            provider.GetRequiredService<TuyaHomeGateway>(),
            provider.GetRequiredService<TholzHomeGateway>(),
            provider.GetRequiredService<HikvisionHomeGateway>(),
            provider.GetRequiredService<XiaomiHomeGateway>(),
            provider.GetRequiredService<BlueRiiotHomeGateway>(),
            provider.GetRequiredService<DaikinHomeGateway>(),
            provider.GetRequiredService<GoodWeHomeGateway>(),
            provider.GetRequiredService<ZagonelHomeGateway>()));

        return services;
    }
}
