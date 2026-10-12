using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// The local HTTP server of a Daikin Smart AC (Brazil) Wi-Fi module on port 15914.
/// Every body is base64 of <c>IV(16) · [one byte] · AES-128-CFB(JSON) · CRC-16</c>;
/// a request carries the JSON followed by <c>BZ</c> and no extra byte. Knows the
/// envelope and the two paths, <c>/acstatus</c> and <c>/status</c>, and nothing
/// about what an air conditioner is: <see cref="DaikinHomeGateway"/> does.
/// <para>
/// The module writes an HTTP reply no stock client accepts — every header line
/// indented with tabs — so this speaks HTTP/1.1 over a bare socket and reads
/// until the module closes, which it does after each reply.
/// </para>
/// </summary>
public sealed class DaikinLocalClient : IDisposable
{
    /// <summary>The one port the module's local server listens on.</summary>
    public const int DefaultPort = 15914;

    // A healthy module answers in well under a second; this only keeps a module
    // off the Wi-Fi from holding the panel.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    // The module is a small board that serves one connection at a time; a read
    // from the panel and a write from a voice command go out in single file.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <summary>Decodes the base64 key the app hands out; null unless it is exactly 16 bytes.</summary>
    public static byte[]? ParseKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var buffer = new byte[32];

        return Convert.TryFromBase64String(key.Trim(), buffer, out var written) && written == 16 ? buffer[..16] : null;
    }

    /// <summary>Reads the air conditioner's full state.</summary>
    public Task<HomeReply<JsonElement>> ReadAsync(string host, byte[] key, CancellationToken cancellationToken) =>
        ExchangeAsync(host, "GET", "/acstatus", key, body: null, cancellationToken);

    /// <summary>
    /// Writes the keys of <c>port1</c> that change — <c>power</c>, <c>temperature</c>
    /// — and returns the state the module reports after taking them.
    /// </summary>
    public Task<HomeReply<JsonElement>> WriteAsync(string host, byte[] key, object port1, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(port1);

        var json = JsonSerializer.SerializeToUtf8Bytes(new { port1 });

        return ExchangeAsync(host, "POST", "/acstatus", key, Encode(key, json, RandomNumberGenerator.GetBytes(16)), cancellationToken);
    }

    private async Task<HomeReply<JsonElement>> ExchangeAsync(
        string host, string method, string path, byte[] key, string? body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(key);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        string raw;

        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Offline, "O ar-condicionado Daikin não respondeu na rede.");
        }

        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(host, DefaultPort, timeout.Token).ConfigureAwait(false);
            await using var stream = socket.GetStream();

            // Head and body in one write: the module reads a request from the first
            // segment only, and a body arriving in a second one is never seen — the
            // POST then hangs or is reset.
            var payload = body ?? string.Empty;
            var request = Encoding.ASCII.GetBytes(
                $"{method} {path} HTTP/1.1\r\nHost: {host}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}");

            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);

            using var reader = new StreamReader(stream, Encoding.ASCII);
            raw = await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Offline, "O ar-condicionado Daikin não respondeu na rede.");
        }
        catch (Exception failure) when (failure is SocketException or IOException)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Offline, "O ar-condicionado Daikin não respondeu na rede.");
        }
        finally
        {
            _gate.Release();
        }

        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);

        if (!raw.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) || split < 0)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Rejected, "O módulo Daikin recusou o pedido.");
        }

        if (Decode(key, raw[(split + 4)..].Trim()) is not { } json)
        {
            // The envelope arrived intact but the key opened it to noise: it is
            // not this module's key.
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unauthorized, "A chave do Daikin não abre a resposta do aparelho.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return HomeReply.Success(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unauthorized, "A chave do Daikin não abre a resposta do aparelho.");
        }
    }

    /// <summary>Base64 of <c>IV · CFB(json + "BZ") · CRC-16</c>, the shape the module accepts.</summary>
    internal static string Encode(byte[] key, ReadOnlySpan<byte> json, byte[] iv)
    {
        var plain = new byte[json.Length + 2];
        json.CopyTo(plain);
        plain[^2] = (byte)'B';
        plain[^1] = (byte)'Z';

        var cipher = Cfb128(key, iv, plain, encrypt: true);

        var frame = new byte[16 + cipher.Length + 2];
        iv.CopyTo(frame, 0);
        cipher.CopyTo(frame, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(^2), Crc16(frame.AsSpan(0, frame.Length - 2)));

        return Convert.ToBase64String(frame);
    }

    /// <summary>
    /// The JSON inside a reply, or null when the frame is short, its CRC is wrong or
    /// the key does not open it. A reply carries one byte between the IV and the
    /// cipher text that a request does not.
    /// </summary>
    internal static byte[]? Decode(byte[] key, string body)
    {
        byte[] frame;

        try
        {
            frame = Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            return null;
        }

        if (frame.Length < 16 + 1 + 2
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(^2)) != Crc16(frame.AsSpan(0, frame.Length - 2)))
        {
            return null;
        }

        var plain = Cfb128(key, frame.AsSpan(0, 16), frame.AsSpan(17, frame.Length - 17 - 2), encrypt: false);

        // JSON from the right key starts with a brace; from any other, with noise.
        return plain.Length > 0 && plain[0] == (byte)'{' ? plain : null;
    }

    /// <summary>
    /// AES-128 in CFB with a full-block segment, over a length that need not be a
    /// multiple of 16 — which .NET's own CFB refuses without padding. Each keystream
    /// block is the AES of the previous cipher block, starting from the IV.
    /// </summary>
    private static byte[] Cfb128(byte[] key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;

        var output = new byte[input.Length];
        var feedback = iv.ToArray();
        var stream = new byte[16];

        for (var offset = 0; offset < input.Length; offset += 16)
        {
            aes.EncryptEcb(feedback, stream, PaddingMode.None);
            var count = Math.Min(16, input.Length - offset);

            for (var i = 0; i < count; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ stream[i]);
            }

            var cipher = encrypt ? output.AsSpan(offset, count) : input.Slice(offset, count);
            cipher.CopyTo(feedback);
        }

        return output;
    }

    /// <summary>CRC-16 with polynomial 0x1021, initial 0xFFFF and the result inverted (CRC-16/GENIBUS).</summary>
    internal static ushort Crc16(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;

        foreach (var b in data)
        {
            crc ^= b << 8;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = ((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1) & 0xFFFF;
            }
        }

        return (ushort)(~crc & 0xFFFF);
    }
}
