using System.Collections.Immutable;
using HomeConnectors;

namespace HomeHub.Client;

/// <summary>
/// <see cref="IHomeDeviceGateway"/> over HTTP: the house as the hub serves it.
/// A client using this holds no vendor key and opens no socket to a device —
/// the hub is the only process talking to the house, which matters for modules
/// like Tholz's that serve one kept connection at a time.
/// <para>
/// Only the device's id crosses the wire. The hub looks it up in its own
/// catalog, so a client cannot make the hub reach a host the owner never listed.
/// </para>
/// </summary>
public sealed class HubHomeGateway(HomeHubConnection hub) : IHomeDeviceGateway
{
    private static readonly TimeSpan StatusLife = TimeSpan.FromSeconds(30);

    private readonly HomeHubConnection _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    private readonly Lock _gate = new();
    private ImmutableDictionary<HomeProvider, bool> _configured = ImmutableDictionary<HomeProvider, bool>.Empty;
    private DateTimeOffset _configuredAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Synchronous by contract, so it answers from a list refreshed at most every
    /// 30 s. The first call waits up to two seconds for the hub; a hub that is
    /// down reads as "nothing configured", the same as no keys.
    /// </summary>
    public bool IsConfigured(HomeProvider provider)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _configuredAt > StatusLife)
            {
                var fetch = Task.Run(() => _hub.GetAsync<ImmutableArray<ProviderStatus>>(HubWire.Providers, CancellationToken.None));

                if (fetch.Wait(TimeSpan.FromSeconds(2)) && fetch.Result.Ok)
                {
                    _configured = fetch.Result.Value.ToImmutableDictionary(s => s.Provider, s => s.Configured);
                    _configuredAt = DateTimeOffset.UtcNow;
                }
            }

            return _configured.TryGetValue(provider, out var configured) && configured;
        }
    }

    public Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        return _hub.GetAsync<HomeDeviceState>(HubWire.DeviceState(device.Id), cancellationToken);
    }

    public Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(command);
        return _hub.PostAsync<bool>(HubWire.DeviceCommand(device.Id), command, cancellationToken);
    }

    public Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        return _hub.PostAsync<CameraFeed>(HubWire.DeviceStream(device.Id), null, cancellationToken);
    }

    public async Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var reply = await _hub.GetBytesAsync(HubWire.DeviceFrame(device.Id), cancellationToken).ConfigureAwait(false);

        return reply.Ok && reply.Value is { Length: > 0 } jpeg
            ? HomeReply.Success(new CameraFrame(jpeg, DateTimeOffset.UtcNow))
            : HomeReply.Failure<CameraFrame>(reply.Ok ? HomeReplyCodes.NoFrame : reply.Code, reply.Ok ? "A câmera não devolveu imagem." : reply.Message);
    }

    public Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken) =>
        _hub.GetAsync<ImmutableArray<DiscoveredDevice>>(HubWire.Discover(provider), cancellationToken);

    /// <summary>Forgets the cached provider list, so the next <see cref="IsConfigured"/> asks the hub again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _configuredAt = DateTimeOffset.MinValue;
        }
    }
}
