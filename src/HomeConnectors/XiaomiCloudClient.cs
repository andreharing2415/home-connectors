using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>A signed-in Xiaomi account: what the cloud needs to sign a request. Never the password.</summary>
public sealed record XiaomiCloudSession(string UserId, string Ssecurity, string ServiceToken);

/// <summary>A QR login in progress: the image to show and where to wait for the scan.</summary>
public sealed record XiaomiQrLogin(byte[] Image, Uri LongPoll, TimeSpan Timeout);

/// <summary>
/// The Xiaomi cloud as the Mi Home app speaks to it — for the one thing the LAN
/// cannot do: fetch the vacuum's map, list its backups and hand it a backup to
/// restore. Everything else about the vacuum stays on miIO.
/// <para>
/// Login is by QR code only: the owner scans it with the Mi Home app, and the
/// password never passes through the hub. The session (user id, ssecurity,
/// service token) is kept in the secret store beside the other keys.
/// </para>
/// <para>
/// Requests go to the China region's <c>api.io.mi.com/app</c>, RC4-encrypted
/// and signed as the app does: a nonce, a SHA-256 "signed nonce" over the
/// ssecurity, SHA-1 signatures before and after encryption. The server is slow
/// (the map RPC waits on the robot), so the client allows a minute.
/// </para>
/// </summary>
public sealed class XiaomiCloudClient : IDisposable
{
    private const string Api = "https://api.io.mi.com/app";
    private const string UserAgent = "abcdefghijklmnopqr-ABCDEABCDEABC APP/com.xiaomi.mihome APPV/10.5.201";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The name the session is kept under in the secret store.</summary>
    public const string SessionSecret = "xiaomi-cloud";

    private readonly IHomeSecretStore _secrets;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly Lock _gate = new();

    public XiaomiCloudClient(IHomeSecretStore secrets, TimeProvider clock)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        // No cookie jar: the cookies are the session and are set per request.
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    public bool SignedIn => Session is not null;

    /// <summary>The stored session, or null. Read fresh each time, like the other keys.</summary>
    public XiaomiCloudSession? Session
    {
        get
        {
            lock (_gate)
            {
                if (_secrets.Read(SessionSecret) is not { } json)
                {
                    return null;
                }

                try
                {
                    return JsonSerializer.Deserialize<XiaomiCloudSession>(json, Json);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }
    }

    public void SignOut()
    {
        lock (_gate)
        {
            _secrets.Delete(SessionSecret);
        }
    }

    /// <summary>Asks the account server for a QR code to scan with the Mi Home app.</summary>
    public async Task<HomeReply<XiaomiQrLogin>> StartQrLoginAsync(CancellationToken cancellationToken)
    {
        var query = string.Join('&',
            "_qrsize=480",
            "qs=%253Fsid%253Dxiaomiio%2526_json%253Dtrue",
            "callback=https%3A%2F%2Fsts.api.io.mi.com%2Fsts",
            "_hasLogo=false",
            "sid=xiaomiio",
            "serviceParam=",
            "_locale=en_GB",
            "_dc=" + _clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

        try
        {
            var text = await _http.GetStringAsync(new Uri("https://account.xiaomi.com/longPolling/loginUrl?" + query), cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(Unwrap(text));
            var root = document.RootElement;

            var qr = new Uri(root.GetProperty("qr").GetString()!);
            var lp = new Uri(root.GetProperty("lp").GetString()!);
            var timeout = TimeSpan.FromSeconds(root.TryGetProperty("timeout", out var t) && t.TryGetInt32(out var s) ? s : 300);
            var image = await _http.GetByteArrayAsync(qr, cancellationToken).ConfigureAwait(false);

            return HomeReply.Success(new XiaomiQrLogin(image, lp, timeout));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return HomeReply.Failure<XiaomiQrLogin>(HomeReplyCodes.Unreachable, "Não consegui pedir o QR à Xiaomi: " + e.Message);
        }
    }

    /// <summary>
    /// Waits for the scan, then trades the ticket for a service token and keeps
    /// the session. The long poll answers only when the code is scanned or
    /// expires; each attempt is short and retried until the QR's own timeout.
    /// </summary>
    public async Task<HomeReply<XiaomiCloudSession>> CompleteQrLoginAsync(XiaomiQrLogin login, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(login);

        var deadline = _clock.GetUtcNow() + login.Timeout;
        string? body = null;

        while (body is null)
        {
            if (_clock.GetUtcNow() > deadline)
            {
                return HomeReply.Failure<XiaomiCloudSession>(HomeReplyCodes.Unauthorized, "O QR expirou sem ser lido.");
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                using var response = await _http.GetAsync(login.LongPoll, attempt.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), _clock, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The long poll timed out with nothing scanned yet. Ask again.
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _clock, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            using var document = JsonDocument.Parse(Unwrap(body));
            var root = document.RootElement;
            var userId = root.GetProperty("userId").ToString();
            var ssecurity = root.GetProperty("ssecurity").GetString()!;
            var location = new Uri(root.GetProperty("location").GetString()!);

            // The service token arrives as a cookie somewhere along the
            // location's redirects, so this hop keeps a jar of its own.
            var jar = new CookieContainer();
            using var handler = new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var _ = await client.GetAsync(location, cancellationToken).ConfigureAwait(false);

            var token = jar.GetAllCookies().FirstOrDefault(c => c.Name == "serviceToken")?.Value;

            if (string.IsNullOrEmpty(token))
            {
                return HomeReply.Failure<XiaomiCloudSession>(HomeReplyCodes.Unauthorized, "A Xiaomi não devolveu o token de serviço.");
            }

            var session = new XiaomiCloudSession(userId, ssecurity, token);
            Keep(session);

            return HomeReply.Success(session);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return HomeReply.Failure<XiaomiCloudSession>(HomeReplyCodes.Unreachable, "O login não terminou: " + e.Message);
        }
    }

    /// <summary>A signed, encrypted call to <c>/app/{path}</c>; the reply's <c>result</c> on code 0.</summary>
    public async Task<HomeReply<JsonElement>> CallAsync(string path, object data, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Session is not { } session)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.NotConfigured, "Entre na conta Xiaomi pelo QR primeiro.");
        }

        var minutes = (int)(_clock.GetUtcNow().ToUnixTimeMilliseconds() / 60000);
        var nonceBytes = new byte[12];
        RandomNumberGenerator.Fill(nonceBytes.AsSpan(0, 8));
        nonceBytes[8] = (byte)(minutes >> 24);
        nonceBytes[9] = (byte)(minutes >> 16);
        nonceBytes[10] = (byte)(minutes >> 8);
        nonceBytes[11] = (byte)minutes;
        var nonce = Convert.ToBase64String(nonceBytes);
        var signedNonce = SignedNonce(session.Ssecurity, nonce);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("data", JsonSerializer.Serialize(data)),
        };
        fields.Add(new("rc4_hash__", Signature("/" + path, signedNonce, fields)));

        for (var i = 0; i < fields.Count; i++)
        {
            fields[i] = new(fields[i].Key, Convert.ToBase64String(Rc4(signedNonce, Encoding.UTF8.GetBytes(fields[i].Value))));
        }

        fields.Add(new("signature", Signature("/" + path, signedNonce, fields)));
        fields.Add(new("ssecurity", session.Ssecurity));
        fields.Add(new("_nonce", nonce));

        var query = string.Join('&', fields.Select(f => f.Key + "=" + Uri.EscapeDataString(f.Value)));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Api + "/" + path + "?" + query));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.TryAddWithoutValidation("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2");
        request.Headers.TryAddWithoutValidation("MIOT-ENCRYPT-ALGORITHM", "ENCRYPT-RC4");
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ",
            "userId=" + session.UserId,
            "serviceToken=" + session.ServiceToken,
            "yetAnotherServiceToken=" + session.ServiceToken,
            "locale=en_GB",
            "channel=MI_APP_STORE"));
        request.Content = new FormUrlEncodedContent([]);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unauthorized, "A sessão Xiaomi expirou. Entre de novo pelo QR.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable,
                    string.Create(CultureInfo.InvariantCulture, $"Nuvem Xiaomi respondeu HTTP {(int)response.StatusCode}."));
            }

            var plain = Encoding.UTF8.GetString(Rc4(signedNonce, Convert.FromBase64String(text)));
            using var document = JsonDocument.Parse(plain);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : -1;

            if (code != 0)
            {
                var message = root.TryGetProperty("message", out var m) ? m.ToString() : plain;
                return HomeReply.Failure<JsonElement>(code == 3 ? HomeReplyCodes.Unauthorized : HomeReplyCodes.Rejected,
                    string.Create(CultureInfo.InvariantCulture, $"Nuvem Xiaomi: {message} (código {code})."));
            }

            return HomeReply.Success(root.TryGetProperty("result", out var result) ? result.Clone() : default);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "Nuvem Xiaomi fora de alcance: " + e.Message);
        }
    }

    /// <summary>A short-lived download URL for a file the robot uploaded, then its bytes.</summary>
    public async Task<HomeReply<byte[]>> DownloadObjectAsync(string objectName, CancellationToken cancellationToken)
    {
        var url = await CallAsync("v2/home/get_interim_file_url_pro", new { obj_name = objectName }, cancellationToken).ConfigureAwait(false);

        if (!url.Ok)
        {
            return HomeReply.Failure<byte[]>(url.Code, url.Message);
        }

        if (!url.Value.TryGetProperty("url", out var u) || u.GetString() is not { Length: > 0 } link)
        {
            return HomeReply.Failure<byte[]>(HomeReplyCodes.Rejected, "A nuvem não deu endereço para " + objectName + ".");
        }

        return await DownloadAsync(new Uri(link), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The signed URL itself, for handing to the robot on a restore.</summary>
    public async Task<HomeReply<string>> ObjectUrlAsync(string objectName, CancellationToken cancellationToken)
    {
        var url = await CallAsync("v2/home/get_interim_file_url_pro", new { obj_name = objectName }, cancellationToken).ConfigureAwait(false);

        return url.Ok && url.Value.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } link
            ? HomeReply.Success(link)
            : HomeReply.Failure<string>(url.Ok ? HomeReplyCodes.Rejected : url.Code, url.Ok ? "Sem endereço para " + objectName + "." : url.Message);
    }

    public async Task<HomeReply<byte[]>> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            // The storage answers 404 with a plain-text "Object Not Found" once a
            // file is gone, so the status is what tells.
            return response.IsSuccessStatusCode
                ? HomeReply.Success(bytes)
                : HomeReply.Failure<byte[]>(HomeReplyCodes.Rejected,
                    string.Create(CultureInfo.InvariantCulture, $"O arquivo não está mais na nuvem (HTTP {(int)response.StatusCode})."));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return HomeReply.Failure<byte[]>(HomeReplyCodes.Unreachable, "Download falhou: " + e.Message);
        }
    }

    public void Dispose() => _http.Dispose();

    private void Keep(XiaomiCloudSession session)
    {
        lock (_gate)
        {
            _secrets.Write(SessionSecret, JsonSerializer.Serialize(session, Json));
        }
    }

    /// <summary>The account server prefixes its JSON with <c>&amp;&amp;&amp;START&amp;&amp;&amp;</c>.</summary>
    private static string Unwrap(string text) => text.Replace("&&&START&&&", string.Empty, StringComparison.Ordinal);

    internal static string SignedNonce(string ssecurity, string nonce) =>
        Convert.ToBase64String(SHA256.HashData([.. Convert.FromBase64String(ssecurity), .. Convert.FromBase64String(nonce)]));

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "The Mi Home API fixes SHA-1 for its request signature; this code only speaks it.")]
    internal static string Signature(string path, string signedNonce, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var text = string.Join('&', new[] { "POST", path }
            .Concat(fields.Select(f => f.Key + "=" + f.Value))
            .Append(signedNonce));

        return Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>RC4 keyed by the signed nonce, with the first 1024 bytes of keystream dropped, as the app does.</summary>
    internal static byte[] Rc4(string key, byte[] data)
    {
        var k = Convert.FromBase64String(key);
        var s = new byte[256];

        for (var i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + k[i % k.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        int x = 0, y = 0;

        for (var n = -1024; n < data.Length; n++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);

            if (n >= 0)
            {
                output[n] = (byte)(data[n] ^ s[(s[x] + s[y]) & 0xFF]);
            }
        }

        return output;
    }
}
