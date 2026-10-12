using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// The miIO protocol every Xiaomi Wi-Fi device speaks on UDP port 54321: a
/// 32-byte header, then a JSON-RPC body encrypted with AES-128-CBC under a key
/// derived from the device's token. Knows nothing about vacuums —
/// <see cref="XiaomiHomeGateway"/> does.
/// <para>
/// A session starts with a "hello" the device answers with its id and its
/// clock; every request after that carries both, and a checksum over the
/// header, the token and the ciphertext. A wrong token is not refused — the
/// device simply never answers — so a timeout after a good hello is reported
/// as the token being wrong, not the device being off.
/// </para>
/// <para>
/// MD5 here is the protocol's, not a choice: it derives the key and the IV and
/// signs the packet. Nothing the hub stores is hashed with it.
/// </para>
/// </summary>
public sealed class MiioClient(TimeProvider clock) : IDisposable
{
    public const int Port = 54321;

    private const int HeaderLength = 32;

    // The M40 answers in well under a second on the house Wi-Fi. UDP loses
    // packets rather than delaying them, so a short wait and one retry beats a
    // long wait.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    // The device's clock drifts from ours and it rejects a stamp too far off,
    // so the handshake is redone well before that can happen.
    private static readonly TimeSpan SessionLife = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One JSON-RPC call. <paramref name="token"/> is the 16-byte device token.
    /// The returned element is the reply's <c>result</c>, cloned so it outlives
    /// this call; a reply carrying <c>error</c> comes back as
    /// <see cref="HomeReplyCodes.Rejected"/>.
    /// </summary>
    public async Task<HomeReply<JsonElement>> CallAsync(
        string host,
        ReadOnlyMemory<byte> token,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);

        if (string.IsNullOrWhiteSpace(host) || !IPAddress.TryParse(host, out var address))
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.NotConfigured, "Sem um IP válido para o aparelho Xiaomi.");
        }

        if (token.Length != 16)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.NotConfigured, "O token do aparelho Xiaomi precisa ter 32 caracteres hexadecimais.");
        }

        var session = _sessions.GetOrAdd(host, _ => new Session(new IPEndPoint(address, Port)));

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Two tries: a lost datagram or a stale session is the usual reason
            // for silence, and a fresh hello fixes both.
            HomeReply<JsonElement>? last = null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0 || !session.IsFresh(_clock, SessionLife))
                {
                    var hello = await HelloAsync(session, cancellationToken).ConfigureAwait(false);

                    if (!hello.Ok)
                    {
                        return HomeReply.Failure<JsonElement>(hello.Code, hello.Message);
                    }
                }

                last = await ExchangeAsync(session, token.Span.ToArray(), method, parameters, cancellationToken).ConfigureAwait(false);

                if (last.Ok || last.Code != HomeReplyCodes.Unreachable)
                {
                    return last;
                }
            }

            // Hello answered, the call did not, twice: the device is there and
            // is ignoring what we sign. That is what a wrong token looks like.
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unauthorized,
                "O aspirador responde na rede mas ignora os comandos: o token não confere.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>The 16-byte token from its 32-character hex form, or null when it is not one.</summary>
    public static byte[]? ParseToken(string? hex)
    {
        if (hex is not { Length: 32 })
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private async Task<HomeReply<bool>> HelloAsync(Session session, CancellationToken cancellationToken)
    {
        var hello = new byte[HeaderLength];
        hello.AsSpan().Fill(0xFF);
        hello[0] = 0x21;
        hello[1] = 0x31;
        hello[2] = 0x00;
        hello[3] = 0x20;

        var reply = await SendReceiveAsync(session, hello, cancellationToken).ConfigureAwait(false);

        if (reply is not { Length: >= HeaderLength })
        {
            session.Forget();
            return HomeReply.Failure<bool>(HomeReplyCodes.Offline, "O aspirador não respondeu na rede.");
        }

        session.Adopt(
            BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(12, 4)),
            _clock.GetTimestamp());

        return HomeReply.Success(true);
    }

    private async Task<HomeReply<JsonElement>> ExchangeAsync(
        Session session,
        byte[] token,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = session.NextId();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        var stamp = session.Stamp(_clock);
        var packet = Seal(token, session.DeviceId, stamp, body);

        var reply = await SendReceiveAsync(session, packet, cancellationToken).ConfigureAwait(false);

        if (reply is not { Length: > HeaderLength })
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unreachable, "O aspirador não respondeu ao comando.");
        }

        if (Open(token, reply) is not { } plain)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Unauthorized, "A resposta do aspirador não abriu com o token guardado.");
        }

        try
        {
            using var document = JsonDocument.Parse(plain);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                return HomeReply.Failure<JsonElement>(HomeReplyCodes.Rejected,
                    error.TryGetProperty("message", out var message) ? message.GetString() ?? error.ToString() : error.ToString());
            }

            return root.TryGetProperty("result", out var result)
                ? HomeReply.Success(result.Clone())
                : HomeReply.Failure<JsonElement>(HomeReplyCodes.Rejected, "O aspirador respondeu sem resultado.");
        }
        catch (JsonException)
        {
            return HomeReply.Failure<JsonElement>(HomeReplyCodes.Rejected, "O aspirador respondeu algo que não é JSON.");
        }
    }

    private static async Task<byte[]?> SendReceiveAsync(Session session, byte[] packet, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await session.Socket.SendAsync(packet, session.EndPoint, timeout.Token).ConfigureAwait(false);

            // Anything that is not from the device — or is too short to be a
            // miIO packet — is skipped rather than parsed.
            while (true)
            {
                var received = await session.Socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);

                if (received.RemoteEndPoint.Address.Equals(session.EndPoint.Address) && received.Buffer.Length >= HeaderLength)
                {
                    return received.Buffer;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>Header, checksum, ciphertext: one request packet. <see cref="Open"/> is its inverse.</summary>
    internal static byte[] Seal(byte[] token, uint deviceId, uint stamp, byte[] body)
    {
        var cipher = Encrypt(token, body);
        var packet = new byte[HeaderLength + cipher.Length];
        var span = packet.AsSpan();

        span[0] = 0x21;
        span[1] = 0x31;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], deviceId);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], stamp);
        cipher.CopyTo(span[HeaderLength..]);

        // The checksum field holds the token while the digest is taken, then
        // the digest itself.
        token.CopyTo(span[16..]);
        Md5(packet).CopyTo(span[16..]);

        return packet;
    }

    /// <summary>The JSON body of a reply, or null when the checksum or the padding says the token is wrong.</summary>
    internal static byte[]? Open(byte[] token, byte[] packet)
    {
        if (packet.Length <= HeaderLength)
        {
            return null;
        }

        var check = packet.ToArray();
        token.CopyTo(check.AsSpan(16));

        if (!Md5(check).AsSpan().SequenceEqual(packet.AsSpan(16, 16)))
        {
            return null;
        }

        try
        {
            var plain = Decrypt(token, packet[HeaderLength..]);

            // Some firmwares pad the JSON with NULs before the PKCS#7 padding.
            var end = plain.Length;

            while (end > 0 && plain[end - 1] == 0)
            {
                end--;
            }

            return plain[..end];
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] Encrypt(byte[] token, byte[] plain)
    {
        using var aes = Cipher(token);
        return aes.EncryptCbc(plain, aes.IV, PaddingMode.PKCS7);
    }

    private static byte[] Decrypt(byte[] token, byte[] cipher)
    {
        using var aes = Cipher(token);
        return aes.DecryptCbc(cipher, aes.IV, PaddingMode.PKCS7);
    }

    private static Aes Cipher(byte[] token)
    {
        var key = Md5(token);
        var aes = Aes.Create();
        aes.Key = key;
        aes.IV = Md5([.. key, .. token]);
        return aes;
    }

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "The miIO protocol fixes MD5 for key derivation and the packet checksum; it protects nothing this code chose.")]
    private static byte[] Md5(byte[] data) => MD5.HashData(data);

    private sealed class Session(IPEndPoint endPoint) : IDisposable
    {
        private uint _stamp;
        private long _stampTakenAt;
        private int _id = Random.Shared.Next(1, 10_000);

        public IPEndPoint EndPoint { get; } = endPoint;

        public UdpClient Socket { get; } = new(AddressFamily.InterNetwork);

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public uint DeviceId { get; private set; }

        public bool IsFresh(TimeProvider clock, TimeSpan life) =>
            _stampTakenAt != 0 && clock.GetElapsedTime(_stampTakenAt) < life;

        public void Adopt(uint deviceId, uint stamp, long takenAt)
        {
            DeviceId = deviceId;
            _stamp = stamp;
            _stampTakenAt = takenAt;
        }

        public void Forget() => _stampTakenAt = 0;

        /// <summary>The device's clock now: what it said at hello, plus the seconds since.</summary>
        public uint Stamp(TimeProvider clock) => _stamp + (uint)clock.GetElapsedTime(_stampTakenAt).TotalSeconds + 1;

        public int NextId() => Interlocked.Increment(ref _id);

        public void Dispose()
        {
            Socket.Dispose();
            Gate.Dispose();
        }
    }
}
