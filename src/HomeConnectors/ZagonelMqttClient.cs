using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// The Zagonel Smart Banho cloud broker, spoken to directly over MQTT 3.1.1 on a
/// WebSocket — the very broker the app uses, open on the internet with no login
/// and addressed only by the shower's device id. No MQTT package: the exchange is
/// a handful of framed packets built here by hand, the way
/// <see cref="TholzSocketClient"/> hand-builds its JSON socket, so
/// Directory.Packages.props gains no dependency and its provenance rule stays put.
/// <para>
/// One command in — <c>{"command":"getStatus"}</c> or a field write such as
/// <c>{"command":"Preset_1","value":41000}</c> on <c>{id}_AS</c> — and the broker's
/// next publish on <c>{id}_SA</c> back. A fresh connection per exchange: the broker
/// is a cloud reached in a second or two, and holding a socket open across the
/// minutes between a read and the next would only buy a keep-alive to run. The
/// shower itself blocks <c>getChars</c> while water runs, but answers
/// <c>getStatus</c> and accepts a preset either way.
/// </para>
/// </summary>
public sealed class ZagonelMqttClient(TimeProvider clock)
{
    public const string BrokerHost = "smartbanho.zagonel.com.br";
    public const int BrokerPort = 58083;

    private static readonly Uri BrokerUri = new($"ws://{BrokerHost}:{BrokerPort}/mqtt");

    // The cloud answers getStatus in ~1 s; a write echoes about as fast. Ten
    // seconds is the "it is not coming" line, not the expected wait.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Reads the live status with <c>getStatus</c>.</summary>
    public Task<HomeReply<JsonElement>> GetStatusAsync(string deviceId, CancellationToken cancellationToken) =>
        ExchangeAsync(deviceId, """{"command":"getStatus"}""", cancellationToken);

    /// <summary>
    /// Writes one named field — a preset's temperature (<c>Preset_1</c>, °C ×1000),
    /// the RGB mode, the buzzer volume — with <c>{"command":field,"value":value}</c>,
    /// and returns the broker's next publish as confirmation it was taken.
    /// </summary>
    public Task<HomeReply<JsonElement>> SendFieldAsync(string deviceId, string field, int value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return Task.FromResult(HomeReply.Failure<JsonElement>(HomeReplyCodes.Rejected, "Sem campo para escrever no chuveiro Zagonel."));
        }

        var body = JsonSerializer.Serialize(new { command = field, value });

        return ExchangeAsync(deviceId, body, cancellationToken);
    }

    /// <summary>
    /// Connects, subscribes to <c>{id}_SA</c>, publishes the command on <c>{id}_AS</c>
    /// and returns the first publish the broker sends back on <c>{id}_SA</c>, parsed.
    /// The returned element is cloned out of the document it was parsed from, so it
    /// outlives this call.
    /// </summary>
    public async Task<HomeReply<JsonElement>> ExchangeAsync(string deviceId, string commandJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.NotConfigured, "Sem device_id do chuveiro Zagonel.");
        }

        using var deadline = new CancellationTokenSource(Timeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var ct = linked.Token;

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("mqtt");

        try
        {
            await socket.ConnectAsync(BrokerUri, ct).ConfigureAwait(false);

            await SendAsync(socket, Connect(), ct).ConfigureAwait(false);

            var reader = new PacketReader();
            var connack = await reader.NextAsync(socket, ct).ConfigureAwait(false);

            if (connack.Type != 0x20 || connack.Payload.Length < 2 || connack.Payload[1] != 0x00)
            {
                return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "O broker Zagonel recusou a conexão.");
            }

            var replies = deviceId + "_SA";
            await SendAsync(socket, Subscribe(replies, 1), ct).ConfigureAwait(false);

            var suback = await reader.NextAsync(socket, ct).ConfigureAwait(false);

            if (suback.Type != 0x90)
            {
                return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "O broker Zagonel não confirmou a inscrição.");
            }

            await SendAsync(socket, Publish(deviceId + "_AS", commandJson), ct).ConfigureAwait(false);

            while (true)
            {
                var packet = await reader.NextAsync(socket, ct).ConfigureAwait(false);

                // A PUBLISH is 0x3x; a subscription at QoS 0 only ever receives
                // QoS-0 publishes, so the topic is followed straight by the body.
                if ((packet.Type & 0xF0) == 0x30 && TryReadPublish(packet.Payload, out var topic, out var body)
                    && string.Equals(topic, replies, StringComparison.Ordinal))
                {
                    var parsed = Parse(body);
                    await DisconnectAsync(socket).ConfigureAwait(false);
                    return parsed;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "O chuveiro Zagonel não respondeu a tempo.");
        }
        catch (WebSocketException failure)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable,
                string.Create(CultureInfo.InvariantCulture, $"Não alcancei a nuvem Zagonel ({failure.WebSocketErrorCode})."));
        }
    }

    private static Task SendAsync(ClientWebSocket socket, byte[] packet, CancellationToken cancellationToken) =>
        socket.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);

    /// <summary>A best-effort DISCONNECT and close; the exchange already has its answer.</summary>
    private static async Task DisconnectAsync(ClientWebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(new byte[] { 0xE0, 0x00 }, WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // The answer is already in hand; a failed goodbye changes nothing.
        }
        catch (OperationCanceledException)
        {
        }
    }

    // --- MQTT 3.1.1 packet builders (clean session, QoS 0, no auth) ---

    private static byte[] Connect()
    {
        var id = Encoding.UTF8.GetBytes("jarvis-" + Guid.NewGuid().ToString("N")[..12]);

        var body = new List<byte>
        {
            0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', // protocol name
            0x04,                                                   // protocol level 3.1.1
            0x02,                                                   // connect flags: clean session
            0x00, 0x3C,                                             // keep-alive 60 s
        };

        AppendString(body, id);

        return Frame(0x10, body);
    }

    private static byte[] Subscribe(string topic, ushort packetId)
    {
        var filter = Encoding.UTF8.GetBytes(topic);

        var body = new List<byte> { (byte)(packetId >> 8), (byte)(packetId & 0xFF) };
        AppendString(body, filter);
        body.Add(0x00); // requested QoS 0

        return Frame(0x82, body); // SUBSCRIBE requires bit 1 set
    }

    private static byte[] Publish(string topic, string payload)
    {
        var name = Encoding.UTF8.GetBytes(topic);

        var body = new List<byte>();
        AppendString(body, name);
        body.AddRange(Encoding.UTF8.GetBytes(payload)); // QoS 0: no packet identifier

        return Frame(0x30, body);
    }

    private static void AppendString(List<byte> body, byte[] value)
    {
        body.Add((byte)(value.Length >> 8));
        body.Add((byte)(value.Length & 0xFF));
        body.AddRange(value);
    }

    private static byte[] Frame(byte header, List<byte> body)
    {
        var frame = new List<byte>(body.Count + 5) { header };

        var remaining = body.Count;

        do
        {
            var encoded = (byte)(remaining % 128);
            remaining /= 128;

            if (remaining > 0)
            {
                encoded |= 0x80;
            }

            frame.Add(encoded);
        }
        while (remaining > 0);

        frame.AddRange(body);

        return frame.ToArray();
    }

    private static bool TryReadPublish(byte[] payload, out string topic, out byte[] body)
    {
        topic = string.Empty;
        body = [];

        if (payload.Length < 2)
        {
            return false;
        }

        var length = (payload[0] << 8) | payload[1];

        if (payload.Length < 2 + length)
        {
            return false;
        }

        topic = Encoding.UTF8.GetString(payload, 2, length);
        body = payload[(2 + length)..];

        return true;
    }

    private static HomeReply<JsonElement> Parse(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return HomeReply.Success(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "O chuveiro Zagonel respondeu algo que não é JSON.");
        }
    }

    /// <summary>
    /// Pulls whole MQTT packets out of a WebSocket byte stream. A packet is a fixed
    /// header byte, a variable-length "remaining length", then that many bytes; one
    /// WebSocket frame may carry several packets or half of one, so bytes are kept
    /// until a full packet is in hand.
    /// </summary>
    private sealed class PacketReader
    {
        private readonly List<byte> _buffer = [];
        private readonly byte[] _chunk = new byte[4096];

        public async Task<(byte Type, byte[] Payload)> NextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (TryExtract(out var packet))
                {
                    return packet;
                }

                var result = await socket.ReceiveAsync(_chunk, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "O broker Zagonel fechou a conexão.");
                }

                for (var i = 0; i < result.Count; i++)
                {
                    _buffer.Add(_chunk[i]);
                }
            }
        }

        private bool TryExtract(out (byte Type, byte[] Payload) packet)
        {
            packet = default;

            if (_buffer.Count < 2)
            {
                return false;
            }

            var multiplier = 1;
            var value = 0;
            var index = 1;
            byte encoded;

            do
            {
                if (index >= _buffer.Count)
                {
                    return false; // the length field itself is not all here yet
                }

                encoded = _buffer[index];
                value += (encoded & 0x7F) * multiplier;
                multiplier *= 128;
                index++;

                if (multiplier > 128 * 128 * 128)
                {
                    throw new WebSocketException(WebSocketError.InvalidMessageType, "Comprimento MQTT malformado do broker Zagonel.");
                }
            }
            while ((encoded & 0x80) != 0);

            var total = index + value;

            if (_buffer.Count < total)
            {
                return false;
            }

            var type = _buffer[0];
            var body = new byte[value];
            _buffer.CopyTo(index, body, 0, value);
            _buffer.RemoveRange(0, total);

            packet = (type, body);

            return true;
        }
    }
}
