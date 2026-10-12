using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Xml;
using System.Xml.Linq;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>The NVR's address, split out of <see cref="HomeCredentials.Region"/> once and reused by every call.</summary>
public readonly record struct HikvisionHost(string Address, int IsapiPort)
{
    /// <summary>
    /// <c>host</c> or <c>host:port</c>. Not IPv6-aware — Hikvision NVRs on
    /// The owner's network are IPv4 — and there is no scheme: a URL here is
    /// rejected, never resolved.
    /// </summary>
    public static HikvisionHost? Parse(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return null;
        }

        var trimmed = region.Trim();

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var colon = trimmed.IndexOf(':');

        if (colon < 0)
        {
            return new HikvisionHost(trimmed, 80);
        }

        var host = trimmed[..colon];
        var portText = trimmed[(colon + 1)..];

        return host.Length > 0
            && int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is > 0 and <= 65535
                ? new HikvisionHost(host, port)
                : null;
    }
}

/// <summary>
/// A Hikvision NVR on the local network, seen through <see cref="IHomeDeviceGateway"/>.
/// <para>
/// No cloud, no token: the NVR is reached directly with the username and
/// password the owner entered — ISAPI, over HTTP with Digest authentication, for
/// discovery; RTSP, handed to <see cref="HikvisionStreamServer"/>, for video.
/// A camera has no switch to send and no power state to read; the one real
/// thing this gateway does is <see cref="AllocateStreamAsync"/>.
/// </para>
/// </summary>
public sealed class HikvisionHomeGateway(
    IHomeCredentialStore credentials,
    HikvisionStreamServer streams,
    TimeProvider clock) : IHomeDeviceGateway
{
    private const int RtspPort = 554;

    private readonly IHomeCredentialStore _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly HikvisionStreamServer _streams = streams ?? throw new ArgumentNullException(nameof(streams));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public bool IsConfigured(HomeProvider provider) => provider == HomeProvider.Hikvision && Resolve() is not null;

    public Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(device.Provider != HomeProvider.Hikvision
            ? HomeReply.Failure<HomeDeviceState>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString())
            : HomeReply.Success(new HomeDeviceState(true, null, null, _clock.GetUtcNow())));
    }

    public Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(device.Provider != HomeProvider.Hikvision
            ? HomeReply.Failure<bool>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString())
            : HomeReply.Failure<bool>(HomeReplyCodes.Rejected, "Uma câmera Hikvision se vê, não se liga."));
    }

    public async Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Provider != HomeProvider.Hikvision)
        {
            return HomeReply.Failure<CameraFeed>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        if (Resolve() is not { } resolved)
        {
            return HomeReply.Failure<CameraFeed>(HomeReplyCodes.NotConfigured, "Falta o host, o usuário e a senha do NVR.");
        }

        return await _streams
            .AllocateAsync(device, resolved.Host.Address, RtspPort, resolved.Username, resolved.Password, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One JPEG straight off the NVR, over ISAPI.
    /// <para>
    /// Not a frame plucked out of the HLS transcode: that path costs an ffmpeg
    /// process, a segment's worth of latency and a stream that has to already
    /// be running. <c>/ISAPI/Streaming/channels/&lt;canal&gt;01/picture</c>
    /// answers with the picture itself, on the main stream, in one request —
    /// which is what "quem está na porta" needs, because the owner is waiting for
    /// the answer out loud.
    /// </para>
    /// </summary>
    public async Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Provider != HomeProvider.Hikvision)
        {
            return HomeReply.Failure<CameraFrame>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        if (!device.IsCamera)
        {
            return HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame, "Esse dispositivo não é uma câmera.");
        }

        // The channel becomes part of a URL. Same rule as the RTSP path in
        // HikvisionStreamServer: digits only, never anything else.
        if (device.Id.Length == 0 || !device.Id.All(char.IsAsciiDigit))
        {
            return HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame, "O id do canal tem que ser só dígitos.");
        }

        if (Resolve() is not { } resolved)
        {
            return HomeReply.Failure<CameraFrame>(HomeReplyCodes.NotConfigured, "Falta o host, o usuário e a senha do NVR.");
        }

        var baseUri = new Uri(string.Create(CultureInfo.InvariantCulture,
            $"http://{resolved.Host.Address}:{resolved.Host.IsapiPort.ToString(CultureInfo.InvariantCulture)}"));

        var cache = new CredentialCache
        {
            { baseUri, "Digest", new NetworkCredential(resolved.Username, resolved.Password) },
            { baseUri, "Basic", new NetworkCredential(resolved.Username, resolved.Password) },
        };

        using var handler = new HttpClientHandler { Credentials = cache, PreAuthenticate = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

        var picture = new Uri(baseUri, string.Create(CultureInfo.InvariantCulture, $"/ISAPI/Streaming/channels/{device.Id}01/picture"));

        try
        {
            using var response = await http.GetAsync(picture, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return HomeReply.Failure<CameraFrame>(HomeReplyCodes.Unauthorized, "O NVR recusou o usuário ou a senha.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame,
                    string.Create(CultureInfo.InvariantCulture, $"O NVR respondeu {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} para a foto do canal."));
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            // An NVR with the channel off answers 200 with an XML error body.
            // Two bytes of JPEG magic separate a picture from an apology.
            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            {
                return HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame,
                    "O NVR respondeu, mas não com uma foto. O canal pode estar desligado.");
            }

            return HomeReply.Success(new CameraFrame(bytes, _clock.GetUtcNow()));
        }
        catch (HttpRequestException failure)
        {
            return HomeReply.Failure<CameraFrame>(HomeReplyCodes.Unreachable, failure.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HomeReply.Failure<CameraFrame>(HomeReplyCodes.Unreachable, "O NVR não respondeu a tempo.");
        }
    }

    public async Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken)
    {
        if (provider != HomeProvider.Hikvision)
        {
            return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.ProviderUnsupported, provider.ToString());
        }

        if (Resolve() is not { } resolved)
        {
            return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.NotConfigured, "Falta o host, o usuário e a senha do NVR.");
        }

        return await DiscoverChannelsAsync(resolved.Host, resolved.Username, resolved.Password, cancellationToken).ConfigureAwait(false);
    }

    private ResolvedCredentials? Resolve()
    {
        var stored = _credentials.Read(HomeProvider.Hikvision);

        return stored is { IsComplete: true } creds && HikvisionHost.Parse(creds.Region) is { } host
            ? new ResolvedCredentials(host, creds.AccessId, creds.Secret)
            : null;
    }

    private static async Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverChannelsAsync(
        HikvisionHost host, string username, string password, CancellationToken cancellationToken)
    {
        var baseUri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host.Address}:{host.IsapiPort.ToString(CultureInfo.InvariantCulture)}"));

        // Scoped to this one NVR's base address, not a blanket credential: the
        // username and password never travel to any other host this process
        // might ever talk to.
        var cache = new CredentialCache
        {
            { baseUri, "Digest", new NetworkCredential(username, password) },
            { baseUri, "Basic", new NetworkCredential(username, password) },
        };

        using var handler = new HttpClientHandler { Credentials = cache, PreAuthenticate = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

        // IP cameras behind the NVR are "input proxy" channels; analog or coax
        // cameras are plain video inputs. An NVR with none of the first still
        // has the second, so both are asked before "no channels" is the answer.
        var proxied = await FetchChannelsAsync(http, new Uri(baseUri, "/ISAPI/ContentMgmt/InputProxy/channels"), cancellationToken).ConfigureAwait(false);

        if (!proxied.Ok || proxied.Value.Length > 0)
        {
            return proxied;
        }

        return await FetchChannelsAsync(http, new Uri(baseUri, "/ISAPI/System/Video/inputs/channels"), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HomeReply<ImmutableArray<DiscoveredDevice>>> FetchChannelsAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException failure)
        {
            return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.Unreachable, failure.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.Unreachable,
                "O NVR não respondeu a tempo. O ISAPI fala HTTP na porta 80; a 8000 é a porta do SDK e não responde aqui.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.Unauthorized, "O NVR recusou o usuário ou a senha.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.Rejected,
                    string.Create(CultureInfo.InvariantCulture, $"O NVR respondeu {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}."));
            }

            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return HomeReply.Success(ParseChannels(xml));
        }
    }

    /// <summary>
    /// Namespace-agnostic on purpose: Hikvision's ISAPI schema version drifts
    /// across firmware, and every channel this cares about — id and name — is
    /// unambiguous by local name alone.
    /// </summary>
    public static ImmutableArray<DiscoveredDevice> ParseChannels(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return [];
        }

        if (document.Root is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<DiscoveredDevice>();

        foreach (var channel in document.Root.Elements().Where(e => e.Name.LocalName is "InputProxyChannel" or "VideoInputChannel"))
        {
            var id = Child(channel, "id");

            if (id.Length == 0)
            {
                continue;
            }

            var name = Child(channel, "name");

            builder.Add(new DiscoveredDevice(id, name.Length == 0 ? id : name, "Hikvision", "Canal NVR", true, []));
        }

        return builder.ToImmutable();

        static string Child(XElement parent, string localName) =>
            parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() ?? string.Empty;
    }

    private sealed record ResolvedCredentials(HikvisionHost Host, string Username, string Password);
}
