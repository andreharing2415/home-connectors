using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeConnectors.Adapters.Secrets;

/// <summary>
/// Secrets in an OpenBao (or HashiCorp Vault) KV version 2 engine, one secret
/// per name under <c>{mount}/data/{prefix}/{name}</c>, the text in its
/// <c>value</c> field.
/// <para>
/// Calls are synchronous because <see cref="IHomeSecretStore"/> is: the vault is
/// expected on the same machine or the same LAN, where a read is a millisecond.
/// A vault that is down, sealed or refuses the token reads as "no secret" — the
/// adapter then answers <see cref="HomeReplyCodes.NotConfigured"/>, which is
/// what the owner needs to see — while a write that fails throws, because a key
/// the owner typed must not vanish silently.
/// </para>
/// </summary>
public sealed class OpenBaoSecretStore : IHomeSecretStore, IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<string?> _token;
    private readonly string _mount;
    private readonly string _prefix;

    /// <param name="address">The vault's API root, e.g. <c>http://127.0.0.1:8200/</c>.</param>
    /// <param name="token">Reads the current token; called per request so a renewed or replaced token is picked up.</param>
    /// <param name="mount">The KV v2 mount path, e.g. <c>home</c>.</param>
    /// <param name="prefix">A folder inside the mount, so one vault can hold more than one house.</param>
    public OpenBaoSecretStore(Uri address, Func<string?> token, string mount, string prefix)
    {
        ArgumentNullException.ThrowIfNull(address);

        _token = token ?? throw new ArgumentNullException(nameof(token));
        _mount = Clean(mount, nameof(mount));
        _prefix = Clean(prefix, nameof(prefix));
        _http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
    }

    public string? Read(string name)
    {
        using var request = Request(HttpMethod.Get, "data", name);

        try
        {
            using var response = _http.Send(request);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = response.Content.ReadAsStream();
            var body = JsonNode.Parse(stream);

            return body?["data"]?["data"]?["value"]?.GetValue<string>();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Write(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        using var request = Request(HttpMethod.Post, "data", name);
        request.Content = JsonContent.Create(new { data = new { value } });

        using var response = _http.Send(request);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"O cofre recusou gravar o segredo (HTTP {(int)response.StatusCode}).");
        }
    }

    public void Delete(string name)
    {
        // Metadata, not data: removes every version, so a cleared key cannot be
        // brought back by reading an older one.
        using var request = Request(HttpMethod.Delete, "metadata", name);
        using var response = _http.Send(request);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"O cofre recusou apagar o segredo (HTTP {(int)response.StatusCode}).");
        }
    }

    public void Dispose() => _http.Dispose();

    private HttpRequestMessage Request(HttpMethod method, string area, string name)
    {
        var request = new HttpRequestMessage(method, $"v1/{_mount}/{area}/{_prefix}/{Clean(name, nameof(name))}");

        if (_token() is { Length: > 0 } token)
        {
            request.Headers.Add("X-Vault-Token", token);
        }

        return request;
    }

    /// <summary>Path segments only: letters, digits, dash, underscore, dot and inner slashes — never "..", never a leading slash.</summary>
    private static string Clean(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains("..", StringComparison.Ordinal)
            || value.StartsWith('/')
            || value.EndsWith('/')
            || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/'))
        {
            throw new ArgumentException("Nome de segredo inválido.", parameter);
        }

        return value;
    }
}
