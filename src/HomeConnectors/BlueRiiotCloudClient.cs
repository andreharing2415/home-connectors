using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// Talks to the Blue Riiot cloud — the API the Blue Connect app itself uses —
/// and keeps its session fresh. Knows nothing about pools or probes:
/// <see cref="BlueRiiotHomeGateway"/> does.
/// <para>
/// The login is a plain POST with the account e-mail and password; what comes
/// back is a set of temporary AWS keys, and every later call is signed with them
/// (<see cref="AwsSigV4"/>, service <c>execute-api</c>). The API is undocumented;
/// its shape is the one every community client agrees on. The host is fixed
/// here, never read from configuration: the region only picks the signing scope.
/// </para>
/// <para>
/// The password is read from the store at login and held no longer than that
/// request; the temporary keys live in memory only. Nothing here logs a header,
/// a body or a key.
/// </para>
/// </summary>
public sealed partial class BlueRiiotCloudClient(
    HttpClient http,
    IHomeCredentialStore credentials,
    TimeProvider clock)
{
    public static readonly Uri BaseUri = new("https://api.riiotlabs.com/prod/");

    public const string DefaultRegion = "eu-west-1";

    private const string Service = "execute-api";

    // The app's own name and version: the API has been seen to answer
    // differently to a client it does not recognise.
    private const string UserAgent = "BlueConnect/3.2.1";

    private static readonly TimeSpan SessionSlack = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IHomeCredentialStore _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Lock _sessionGate = new();

    private BlueRiiotSession? _session;

    public bool IsConfigured => _credentials.Read(HomeProvider.BlueRiiot) is { IsComplete: true } c && IsRegion(c.Region);

    /// <summary><c>eu-west-1</c> and its kind; anything else is not an AWS region.</summary>
    public static bool IsRegion(string? region) => region is not null && RegionPattern().IsMatch(region.Trim());

    /// <summary>Forgets the session, so the next call logs in from scratch.</summary>
    public void Reset()
    {
        lock (_sessionGate)
        {
            _session = null;
        }
    }

    /// <summary>
    /// A signed GET of <paramref name="path"/>, relative to <see cref="BaseUri"/>.
    /// The body comes back cloned, so the caller may keep it.
    /// </summary>
    public async Task<HomeReply<JsonElement>> GetAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (_credentials.Read(HomeProvider.BlueRiiot) is not { IsComplete: true } stored || !IsRegion(stored.Region))
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.NotConfigured, "Sem a conta da Blue Riiot.");
        }

        var region = stored.Region.Trim();
        var url = new Uri(BaseUri, path);

        var session = await SessionAsync(stored, cancellationToken).ConfigureAwait(false);

        if (!session.Ok)
        {
            return HomeReply.Failure<JsonElement>(session.Code, session.Message);
        }

        var first = await SignedGetAsync(url, session.Value!, region, cancellationToken).ConfigureAwait(false);

        // One retry, and only for keys the gateway refused: they expire on the
        // cloud's clock, not on ours.
        if (!first.Ok && first.Code == HomeReplyCodes.Unauthorized)
        {
            Reset();
            var again = await SessionAsync(stored, cancellationToken).ConfigureAwait(false);

            return again.Ok
                ? await SignedGetAsync(url, again.Value!, region, cancellationToken).ConfigureAwait(false)
                : HomeReply.Failure<JsonElement>(again.Code, again.Message);
        }

        return first;
    }

    private async Task<HomeReply<BlueRiiotSession>> SessionAsync(HomeCredentials stored, CancellationToken cancellationToken)
    {
        lock (_sessionGate)
        {
            if (_session is { } kept && kept.ExpiresAt - SessionSlack > _clock.GetUtcNow())
            {
                return HomeReply.Success(kept);
            }
        }

        var body = JsonSerializer.Serialize(new { email = stored.AccessId.Trim(), password = stored.Secret });

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "user/login"));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.ParseAdd("*/*");
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var reply = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            // A login the API turns away with 400 is a wrong e-mail or password,
            // whatever the status says.
            return HomeReply.Failure<BlueRiiotSession>(
                reply.Code is HomeReplyCodes.Rejected ? HomeReplyCodes.Unauthorized : reply.Code,
                reply.Code is HomeReplyCodes.Rejected ? "A Blue Riiot recusou o e-mail ou a senha." : reply.Message);
        }

        if (!reply.Value.TryGetProperty("credentials", out var keys)
            || Text(keys, "access_key") is not { Length: > 0 } accessKey
            || Text(keys, "secret_key") is not { Length: > 0 } secretKey
            || Text(keys, "session_token") is not { Length: > 0 } sessionToken)
        {
            return HomeReply.Failure<BlueRiiotSession>(HomeReplyCodes.Unauthorized, "A Blue Riiot não devolveu as chaves da sessão.");
        }

        var session = new BlueRiiotSession(accessKey, secretKey, sessionToken, Expiration(keys));

        // Two callers racing here both log in and the later one wins; both
        // sessions are valid, so nothing is lost but one round trip.
        lock (_sessionGate)
        {
            _session = session;
        }

        return HomeReply.Success(session);
    }

    /// <summary>
    /// When the temporary keys stop working. The API has sent both an ISO time
    /// and epoch milliseconds here; neither means an hour, the lifetime these
    /// keys have always had.
    /// </summary>
    private DateTimeOffset Expiration(JsonElement keys)
    {
        if (keys.TryGetProperty("expiration", out var expiration))
        {
            if (expiration.ValueKind == JsonValueKind.Number && expiration.TryGetInt64(out var ms))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            }

            if (expiration.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(expiration.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                return at;
            }
        }

        return _clock.GetUtcNow() + TimeSpan.FromHours(1);
    }

    private async Task<HomeReply<JsonElement>> SignedGetAsync(Uri url, BlueRiiotSession session, string region, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var amzDate = AwsSigV4.AmzDate(now);

        var signed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = url.Host,
            ["x-amz-date"] = amzDate,
            ["x-amz-security-token"] = session.SessionToken,
        };

        var authorization = AwsSigV4.Authorization(
            "GET", url, signed, AwsSigV4.EmptyPayloadHash, session.AccessKey, session.SecretKey, region, Service, now);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", session.SessionToken);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HomeReply<JsonElement>> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unauthorized,
                    string.Create(CultureInfo.InvariantCulture, $"A Blue Riiot respondeu {(int)response.StatusCode}."));
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                return HomeReply.Failure<JsonElement>(HomeReplyCodes.Rejected,
                    string.Create(CultureInfo.InvariantCulture, $"A Blue Riiot respondeu {(int)response.StatusCode}."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable,
                    string.Create(CultureInfo.InvariantCulture, $"A Blue Riiot respondeu {(int)response.StatusCode}."));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var root = document.RootElement;

            // The API has answered 200 with an error in the body; a 200 is not
            // taken as success until the body says it is one.
            if (root.ValueKind == JsonValueKind.Object && Text(root, "errorMessage") is { Length: > 0 } error)
            {
                return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "A Blue Riiot respondeu com erro: " + error);
            }

            return HomeReply.Success(root.Clone());
        }
        catch (HttpRequestException failure)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, failure.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "A Blue Riiot não respondeu a tempo.");
        }
        catch (JsonException)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "A Blue Riiot respondeu algo que não é JSON.");
        }
    }

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"^[a-z]{2}(-[a-z]+)+-\d$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();

    private sealed record BlueRiiotSession(string AccessKey, string SecretKey, string SessionToken, DateTimeOffset ExpiresAt);
}
