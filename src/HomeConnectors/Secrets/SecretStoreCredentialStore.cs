using System.Text.Json;

namespace HomeConnectors.Adapters.Secrets;

/// <summary>
/// The provider keys as named secrets: one entry per provider, under
/// <c>credentials/{Provider}</c>, holding the three fields as JSON. Whatever
/// keeps the secrets — a vault, a protected file — keeps the keys too, and this
/// class never caches them, so a key cleared is cleared for the next call.
/// </summary>
public sealed class SecretStoreCredentialStore(IHomeSecretStore secrets) : IHomeCredentialStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHomeSecretStore _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

    public static string NameFor(HomeProvider provider) => "credentials/" + provider;

    public HomeCredentials? Read(HomeProvider provider)
    {
        if (_secrets.Read(NameFor(provider)) is not { } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Entry>(json, Json) is { } entry
                ? new HomeCredentials(provider, entry.AccessId ?? string.Empty, entry.Secret ?? string.Empty, entry.Region ?? string.Empty)
                : null;
        }
        catch (JsonException)
        {
            // Fails closed: an entry that does not parse is no keys, not an error
            // that would carry its contents into a log.
            return null;
        }
    }

    public void Save(HomeCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var entry = new Entry(credentials.AccessId.Trim(), credentials.Secret.Trim(), credentials.Region.Trim());
        _secrets.Write(NameFor(credentials.Provider), JsonSerializer.Serialize(entry, Json));
    }

    public void Clear(HomeProvider provider) => _secrets.Delete(NameFor(provider));

    private sealed record Entry(string? AccessId, string? Secret, string? Region);
}
