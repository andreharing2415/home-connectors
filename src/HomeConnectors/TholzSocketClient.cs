using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// The local TCP socket a Tholz Smart Connect module serves on port 4000. Speaks
/// the documented JSON exchange — a <c>getDevice</c> or <c>setDevice</c> string in,
/// one serialized JSON object back — and knows nothing about heatings or outputs:
/// <see cref="TholzHomeGateway"/> does.
/// <para>
/// No credentials: the socket is reachable to anyone on the LAN, and Tholz gates
/// it on the network having internet, not on a key. One connection per module is
/// kept open and reused, with the requests to it in single file. The module
/// answers any number of commands on a connection it already holds in well under
/// a second, but a fresh connection opened right after another was closed waits
/// seconds or never answers — the pool module (secondary firmware P843) does
/// exactly that, and four slots read back to back used to lose three. A
/// connection that breaks is dropped and the request goes once more on a new one.
/// </para>
/// </summary>
public sealed class TholzSocketClient(TimeProvider clock) : IDisposable
{
    /// <summary>The one port the Smart Connect server listens on, per Tholz's socket doc.</summary>
    public const int DefaultPort = 4000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A read that never closes its object would otherwise hang on the module.
    // The doc promises an immediate reply, and the house module gives one in
    // ~200 ms — but the pool module sits at the edge of the Wi-Fi (pings of
    // 0.7–1.6 s) and takes 0.7 to 6 s per answer even on a kept connection.
    // Four seconds called that unreachable and blanked the pool's circuits.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ConcurrentDictionary<string, Link> _links = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the whole device state with the <c>getDevice</c> command.</summary>
    public Task<HomeReply<JsonElement>> GetDeviceAsync(string host, int port, CancellationToken cancellationToken) =>
        ExchangeAsync(host, port, """{"command":"getDevice"}""", cancellationToken);

    /// <summary>
    /// Writes the given argument object with <c>setDevice</c>. The argument is the
    /// serialized body of the doc's <c>argument</c> key — only the slots and keys
    /// that change.
    /// </summary>
    public Task<HomeReply<JsonElement>> SetDeviceAsync(string host, int port, object argument, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument);

        var body = JsonSerializer.Serialize(new { command = "setDevice", argument }, Json);

        return ExchangeAsync(host, port, body, cancellationToken);
    }

    /// <summary>
    /// One request, one framed JSON object back, on the module's kept connection.
    /// The returned element is the whole top-level object — <c>command</c> plus
    /// <c>response</c> — cloned out of the document it was parsed from, so it
    /// outlives this call.
    /// </summary>
    public async Task<HomeReply<JsonElement>> ExchangeAsync(string host, int port, string requestJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.NotConfigured, "Sem endereço do módulo Tholz.");
        }

        var link = _links.GetOrAdd(string.Create(CultureInfo.InvariantCulture, $"{host}:{port}"), _ => new Link());

        await link.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var reused = link.IsOpen;
            var reply = await AttemptAsync(link, host, port, requestJson, cancellationToken).ConfigureAwait(false);

            if (!reply.Ok && reused && !cancellationToken.IsCancellationRequested)
            {
                // The kept connection had gone stale on the module's side. Once
                // more, on a fresh one, before giving up.
                link.Drop();
                reply = await AttemptAsync(link, host, port, requestJson, cancellationToken).ConfigureAwait(false);
            }

            if (!reply.Ok)
            {
                link.Drop();
            }

            return reply;
        }
        finally
        {
            link.Gate.Release();
        }
    }

    private async Task<HomeReply<JsonElement>> AttemptAsync(Link link, string host, int port, string requestJson, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(Timeout, _clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            var stream = await link.OpenAsync(host, port, timeout.Token).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetBytes(requestJson);

            await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

            return await ReadObjectAsync(stream, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "O módulo Tholz não respondeu a tempo.");
        }
        catch (SocketException failure)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable,
                string.Create(CultureInfo.InvariantCulture, $"Não alcancei o módulo Tholz ({failure.SocketErrorCode})."));
        }
        catch (IOException failure)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, failure.Message);
        }
        catch (ObjectDisposedException)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "A conexão com o módulo Tholz foi encerrada.");
        }
    }

    public void Dispose()
    {
        foreach (var link in _links.Values)
        {
            link.Dispose();
        }

        _links.Clear();
    }

    /// <summary>The kept connection to one module, and the gate that keeps its requests in single file.</summary>
    private sealed class Link : IDisposable
    {
        private TcpClient? _socket;
        private NetworkStream? _stream;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool IsOpen => _socket is { Connected: true } && _stream is not null;

        public async Task<NetworkStream> OpenAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (IsOpen && DrainStale())
            {
                return _stream!;
            }

            Drop();

            var socket = new TcpClient();

            try
            {
                await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            _socket = socket;
            _stream = socket.GetStream();

            return _stream;
        }

        /// <summary>
        /// Bytes left over from an exchange nobody waited for would frame as the
        /// next reply. They are discarded first; a stream that cannot even be
        /// polled is dropped, and the caller opens a new one.
        /// </summary>
        private bool DrainStale()
        {
            try
            {
                var stream = _stream!;
                var buffer = new byte[1024];

                while (stream.DataAvailable)
                {
                    if (stream.Read(buffer) <= 0)
                    {
                        break;
                    }
                }

                return true;
            }
            catch (IOException)
            {
                Drop();
                return false;
            }
            catch (ObjectDisposedException)
            {
                Drop();
                return false;
            }
        }

        public void Drop()
        {
            try { _stream?.Dispose(); } catch (IOException) { /* already gone */ }
            try { _socket?.Dispose(); } catch (SocketException) { /* already gone */ }
            _stream = null;
            _socket = null;
        }

        public void Dispose()
        {
            Drop();
            Gate.Dispose();
        }
    }

    /// <summary>
    /// Reads until the first complete JSON object has arrived. The module holds the
    /// connection open, so a close is not the frame — a return to brace depth zero
    /// is. Braces and quotes are ASCII, and a UTF-8 continuation byte never carries
    /// an ASCII value, so scanning raw bytes for them is safe.
    /// </summary>
    private static async Task<HomeReply<JsonElement>> ReadObjectAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var accumulated = new MemoryStream();

        var depth = 0;
        var started = false;
        var inString = false;
        var escaped = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                // The module closed before an object completed. Whatever arrived is
                // not a frame we can trust.
                return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "O módulo Tholz fechou a conexão sem responder.");
            }

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                accumulated.WriteByte(b);

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (b == (byte)'\\')
                    {
                        escaped = true;
                    }
                    else if (b == (byte)'"')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (b)
                {
                    case (byte)'"':
                        inString = true;
                        break;
                    case (byte)'{':
                        depth++;
                        started = true;
                        break;
                    case (byte)'}':
                        depth--;

                        if (started && depth == 0)
                        {
                            return Parse(accumulated);
                        }

                        break;
                    default:
                        break;
                }
            }
        }
    }

    private static HomeReply<JsonElement> Parse(MemoryStream accumulated)
    {
        try
        {
            using var document = JsonDocument.Parse(accumulated.ToArray());

            return HomeReply.Success(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "O módulo Tholz respondeu algo que não é JSON.");
        }
    }
}
