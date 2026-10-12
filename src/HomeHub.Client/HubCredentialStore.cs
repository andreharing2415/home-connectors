using HomeConnectors;

namespace HomeHub.Client;

/// <summary>
/// Sends keys to the hub's vault. Write-only by design: the hub never returns
/// a key, so <see cref="Read"/> is always null — a client that needs to know
/// whether a provider has keys asks <see cref="HubHomeGateway.IsConfigured"/>.
/// </summary>
public sealed class HubCredentialStore(HomeHubConnection hub, HubHomeGateway? gateway = null) : IHomeCredentialStore
{
    private readonly HomeHubConnection _hub = hub ?? throw new ArgumentNullException(nameof(hub));

    public HomeCredentials? Read(HomeProvider provider) => null;

    public void Save(HomeCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var body = new CredentialsBody(credentials.AccessId, credentials.Secret, credentials.Region);
        var reply = Task.Run(() => _hub.PutAsync<bool>(HubWire.Credentials(credentials.Provider), body, CancellationToken.None))
            .GetAwaiter().GetResult();

        gateway?.Invalidate();

        if (!reply.Ok)
        {
            throw new InvalidOperationException(reply.Message);
        }
    }

    public void Clear(HomeProvider provider)
    {
        var reply = Task.Run(() => _hub.DeleteAsync<bool>(HubWire.Credentials(provider), CancellationToken.None))
            .GetAwaiter().GetResult();

        gateway?.Invalidate();

        if (!reply.Ok)
        {
            throw new InvalidOperationException(reply.Message);
        }
    }
}
