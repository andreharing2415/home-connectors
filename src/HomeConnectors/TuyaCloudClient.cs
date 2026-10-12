using System.Net.Http;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>The data centers Tuya runs, by the short key the owner configures. Brazil is <c>us</c>.</summary>
public static class TuyaRegions
{
    private static readonly Dictionary<string, Uri> Endpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["us"] = new Uri("https://openapi.tuyaus.com"),
        ["us-e"] = new Uri("https://openapi-ueaz.tuyaus.com"),
        ["eu"] = new Uri("https://openapi.tuyaeu.com"),
        ["eu-w"] = new Uri("https://openapi-weaz.tuyaeu.com"),
        ["cn"] = new Uri("https://openapi.tuyacn.com"),
        ["in"] = new Uri("https://openapi.tuyain.com"),
        ["sg"] = new Uri("https://openapi-sg.iotbing.com"),
    };

    public static IReadOnlyCollection<string> Keys => Endpoints.Keys;

    /// <summary>The endpoint for a region key, or null. Never a URL from configuration: only these seven hosts are reachable.</summary>
    public static Uri? Resolve(string? region) =>
        region is not null && Endpoints.TryGetValue(region.Trim(), out var uri) ? uri : null;
}

/// <summary>One answer from Tuya, before anyone interprets <c>result</c>.</summary>
public sealed record TuyaResponse(bool Success, long Code, string Message, JsonElement Result)
{
    /// <summary>Tuya's own codes for a token that is stale or a signature that does not check out.</summary>
    public bool IsTokenProblem => Code is 1010 or 1011 or 1012 or 1013 or 1004 or 1106;
}

/// <summary>
/// Signs and sends requests to the Tuya IoT Platform and keeps the access token
/// fresh. Knows nothing about devices: <see cref="TuyaHomeGateway"/> does.
/// <para>
/// The client id and secret are read from the store on each token refresh and
/// never held longer than the request that uses them; nothing here logs a
/// header, a body or a token.
/// </para>
/// </summary>
public sealed class TuyaCloudClient(
    HttpClient http,
    IHomeCredentialStore credentials,
    TimeProvider clock)
{
    private static readonly TimeSpan TokenSlack = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IHomeCredentialStore _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    // A Lock rather than a semaphore: the token exchange is short, and a lock
    // leaves the client with nothing to dispose.
    private readonly Lock _tokenGate = new();

    private string _accessToken = string.Empty;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public bool IsConfigured => _credentials.Read(HomeProvider.Tuya) is { IsComplete: true } c && TuyaRegions.Resolve(c.Region) is not null;

    /// <summary>Forgets the cached token, so the next call authenticates from scratch.</summary>
    public void Reset()
    {
        lock (_tokenGate)
        {
            _accessToken = string.Empty;
            _tokenExpiresAt = DateTimeOffset.MinValue;
        }
    }

    public Task<HomeReply<TuyaResponse>> GetAsync(string path, IReadOnlyDictionary<string, string>? query, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, query, body: null, cancellationToken);

    public Task<HomeReply<TuyaResponse>> PostAsync(string path, string jsonBody, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, query: null, jsonBody, cancellationToken);

    private async Task<HomeReply<TuyaResponse>> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? query,
        string? body,
        CancellationToken cancellationToken)
    {
        if (_credentials.Read(HomeProvider.Tuya) is not { IsComplete: true } credentials
            || TuyaRegions.Resolve(credentials.Region) is not { } endpoint)
        {
            return HomeReply.Failure<TuyaResponse>(HomeReplyCodes.NotConfigured, "Sem chaves da Tuya.");
        }

        var token = await TokenAsync(credentials, endpoint, cancellationToken).ConfigureAwait(false);

        if (!token.Ok)
        {
            return HomeReply.Failure<TuyaResponse>(token.Code, token.Message);
        }

        var first = await CallAsync(credentials, endpoint, method, path, query, body, token.Value!, cancellationToken).ConfigureAwait(false);

        // One retry, and only for a token the cloud says went stale under us.
        if (first.Ok && first.Value is { Success: false, IsTokenProblem: true })
        {
            Reset();
            var again = await TokenAsync(credentials, endpoint, cancellationToken).ConfigureAwait(false);

            if (!again.Ok)
            {
                return HomeReply.Failure<TuyaResponse>(again.Code, again.Message);
            }

            return await CallAsync(credentials, endpoint, method, path, query, body, again.Value!, cancellationToken).ConfigureAwait(false);
        }

        return first;
    }

    private async Task<HomeReply<string>> TokenAsync(HomeCredentials credentials, Uri endpoint, CancellationToken cancellationToken)
    {
        lock (_tokenGate)
        {
            if (_accessToken.Length > 0 && _tokenExpiresAt - TokenSlack > _clock.GetUtcNow())
            {
                return HomeReply.Success(_accessToken);
            }
        }

        var reply = await CallAsync(
            credentials, endpoint, HttpMethod.Get, "/v1.0/token",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["grant_type"] = "1" },
            body: null, accessToken: string.Empty, cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            return HomeReply.Failure<string>(reply.Code, reply.Message);
        }

        var response = reply.Value!;

        if (!response.Success)
        {
            return HomeReply.Failure<string>(HomeReplyCodes.Unauthorized,
                string.Create(CultureInfo.InvariantCulture, $"Tuya recusou as chaves ({response.Code}: {response.Message})."));
        }

        if (!response.Result.TryGetProperty("access_token", out var tokenElement)
            || tokenElement.GetString() is not { Length: > 0 } token)
        {
            return HomeReply.Failure<string>(HomeReplyCodes.Unauthorized, "Tuya não devolveu um token.");
        }

        var lifetime = response.Result.TryGetProperty("expire_time", out var expire) && expire.TryGetInt64(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromHours(1);

        // Two callers racing here both fetch a token and the later one wins;
        // both tokens are valid, so nothing is lost but one round trip.
        lock (_tokenGate)
        {
            _accessToken = token;
            _tokenExpiresAt = _clock.GetUtcNow() + lifetime;
        }

        return HomeReply.Success(token);
    }

    private async Task<HomeReply<TuyaResponse>> CallAsync(
        HomeCredentials credentials,
        Uri endpoint,
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? query,
        string? body,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var canonical = TuyaSigner.CanonicalPath(path, query);
        var t = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = Guid.NewGuid().ToString("N");
        var stringToSign = TuyaSigner.StringToSign(method.Method, canonical, body);
        var sign = TuyaSigner.Sign(credentials.AccessId, credentials.Secret, accessToken, t, nonce, stringToSign);

        using var request = new HttpRequestMessage(method, new Uri(endpoint, canonical));
        request.Headers.TryAddWithoutValidation("client_id", credentials.AccessId);
        request.Headers.TryAddWithoutValidation("sign", sign);
        request.Headers.TryAddWithoutValidation("sign_method", "HMAC-SHA256");
        request.Headers.TryAddWithoutValidation("t", t.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("nonce", nonce);
        request.Headers.TryAddWithoutValidation("lang", "en");

        if (accessToken.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("access_token", accessToken);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return HomeReply.Failure<TuyaResponse>(HomeReplyCodes.Unauthorized,
                    string.Create(CultureInfo.InvariantCulture, $"Tuya respondeu {(int)response.StatusCode}."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return HomeReply.Failure<TuyaResponse>(HomeReplyCodes.Unreachable,
                    string.Create(CultureInfo.InvariantCulture, $"Tuya respondeu {(int)response.StatusCode}."));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var root = document.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            var code = root.TryGetProperty("code", out var c) && c.TryGetInt64(out var value) ? value : 0;
            var message = root.TryGetProperty("msg", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;

            return HomeReply.Success(new TuyaResponse(success, code, message, result));
        }
        catch (HttpRequestException failure)
        {
            return HomeReply.Failure<TuyaResponse>(HomeReplyCodes.Unreachable, failure.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HomeReply.Failure<TuyaResponse>(HomeReplyCodes.Unreachable, "Tuya não respondeu a tempo.");
        }
        catch (JsonException)
        {
            return HomeReply.Failure<TuyaResponse>(HomeReplyCodes.Unreachable, "Tuya respondeu algo que não é JSON.");
        }
    }
}
