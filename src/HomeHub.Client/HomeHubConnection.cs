using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HomeConnectors;

namespace HomeHub.Client;

/// <summary>
/// One HTTP connection to a hub: its address and the bearer token it hands
/// out. Failures come back as <see cref="HomeReply{T}"/> like any adapter's —
/// a hub that is down is <see cref="HomeReplyCodes.Unreachable"/>, a refused
/// token <see cref="HomeReplyCodes.Unauthorized"/> — so a client reasons about
/// the hub exactly as it would about a vendor cloud.
/// </summary>
public sealed class HomeHubConnection : IDisposable
{
    private readonly HttpClient _http;

    public HomeHubConnection(Uri address, string token, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        _http = new HttpClient
        {
            BaseAddress = address,
            // The slowest thing behind the hub is the pool's Tholz module at the
            // edge of the Wi-Fi (12 s); a camera stream waits on ffmpeg. Thirty
            // seconds covers both without hanging a client forever.
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    public Uri Address => _http.BaseAddress!;

    public async Task<HomeReply<T>> GetAsync<T>(string path, CancellationToken cancellationToken) =>
        await SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken).ConfigureAwait(false);

    public async Task<HomeReply<T>> PostAsync<T>(string path, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: HubWire.Json);
        }

        return await SendAsync<T>(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HomeReply<T>> PutAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body, body.GetType(), options: HubWire.Json),
        };

        return await SendAsync<T>(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HomeReply<T>> DeleteAsync<T>(string path, CancellationToken cancellationToken) =>
        await SendAsync<T>(new HttpRequestMessage(HttpMethod.Delete, path), cancellationToken).ConfigureAwait(false);

    /// <summary>A raw body — the camera frame is JPEG, not JSON.</summary>
    public async Task<HomeReply<byte[]>> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                var reply = await response.Content.ReadFromJsonAsync<HomeReply<byte[]>>(HubWire.Json, cancellationToken).ConfigureAwait(false);
                return reply ?? HomeReply.Failure<byte[]>(HomeReplyCodes.Unreachable, "O hub respondeu vazio.");
            }

            return response.IsSuccessStatusCode
                ? HomeReply.Success(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false))
                : Failure<byte[]>(response);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return HomeReply.Failure<byte[]>(HomeReplyCodes.Unreachable, "Hub da casa fora do ar: " + e.Message);
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<HomeReply<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    // The hub answers every device call with a HomeReply, success
                    // or not, so the vendor's own code and message come through.
                    var reply = await response.Content.ReadFromJsonAsync<HomeReply<T>>(HubWire.Json, cancellationToken).ConfigureAwait(false);
                    return reply ?? HomeReply.Failure<T>(HomeReplyCodes.Unreachable, "O hub respondeu vazio.");
                }

                return Failure<T>(response);
            }
            catch (JsonException e)
            {
                return HomeReply.Failure<T>(HomeReplyCodes.Unreachable, "Resposta do hub ilegível: " + e.Message);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return HomeReply.Failure<T>(HomeReplyCodes.Unreachable, "Hub da casa fora do ar: " + e.Message);
            }
        }
    }

    private static HomeReply<T> Failure<T>(HttpResponseMessage response) => response.StatusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            HomeReply.Failure<T>(HomeReplyCodes.Unauthorized, "O hub recusou o token."),
        System.Net.HttpStatusCode.NotFound =>
            HomeReply.Failure<T>(HomeReplyCodes.Rejected, "O hub não conhece esse dispositivo."),
        _ => HomeReply.Failure<T>(HomeReplyCodes.Unreachable, $"O hub respondeu HTTP {(int)response.StatusCode}."),
    };
}
