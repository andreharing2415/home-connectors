using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// Modbus TCP on port 502 of a GoodWe inverter's Wi-Fi dongle — the only plain
/// door it opens. The dongle answers the discovery broadcast on UDP 48899 with
/// <c>dongle@sn,dtls_port:8899,…</c>: the old open UDP 8899 is DTLS on this
/// firmware, so it is not used. Knows the MBAP envelope and function 3 (read
/// holding registers) and nothing about what an inverter is:
/// <see cref="GoodWeHomeGateway"/> does.
/// </summary>
public sealed class GoodWeModbusClient : IDisposable
{
    public const int DefaultPort = 502;

    /// <summary>The unit GoodWe's own tools address; the dongle also answers 1 and 0 the same.</summary>
    public const byte Unit = 247;

    // The dongle answers in tens of milliseconds; this only keeps an inverter off
    // the Wi-Fi from holding the panel.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    // A small board: a panel refresh and a spoken question go out in single file.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ushort _transaction;

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Reads each block of holding registers over one connection. A block the
    /// inverter refuses with a Modbus exception comes back null; the rest still do.
    /// </summary>
    public async Task<HomeReply<ushort[]?[]>> ReadAsync(
        string host, IReadOnlyList<(ushort Start, ushort Count)> blocks, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(blocks);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Silent();
        }

        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(host, DefaultPort, timeout.Token).ConfigureAwait(false);
            await using var stream = socket.GetStream();

            var result = new ushort[]?[blocks.Count];

            for (var i = 0; i < blocks.Count; i++)
            {
                var transaction = ++_transaction;
                await stream.WriteAsync(Request(transaction, Unit, blocks[i].Start, blocks[i].Count), timeout.Token).ConfigureAwait(false);

                var head = new byte[7];
                await stream.ReadExactlyAsync(head, timeout.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));

                if (length is < 2 or > 260)
                {
                    return HomeReply.Failure<ushort[]?[]>(HomeReplyCodes.Rejected, "O inversor respondeu um quadro Modbus inválido.");
                }

                var frame = new byte[7 + length - 1];
                head.CopyTo(frame, 0);
                await stream.ReadExactlyAsync(frame.AsMemory(7), timeout.Token).ConfigureAwait(false);

                result[i] = Registers(frame, transaction, blocks[i].Count);
            }

            return HomeReply.Success(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Silent();
        }
        catch (Exception failure) when (failure is SocketException or IOException)
        {
            return Silent();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HomeReply<ushort[]?[]> Silent() =>
        HomeReply.Failure<ushort[]?[]>(HomeReplyCodes.Offline, "O inversor solar não respondeu na rede.");

    /// <summary>MBAP header and a function-3 PDU: read <paramref name="count"/> holding registers from <paramref name="start"/>.</summary>
    internal static byte[] Request(ushort transaction, byte unit, ushort start, ushort count)
    {
        var frame = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), transaction);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);
        frame[6] = unit;
        frame[7] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), start);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), count);
        return frame;
    }

    /// <summary>
    /// The registers in a function-3 reply, or null when it is an exception, answers
    /// another transaction or carries a byte count other than the one asked for.
    /// </summary>
    internal static ushort[]? Registers(ReadOnlySpan<byte> frame, ushort transaction, ushort count)
    {
        if (frame.Length < 9
            || BinaryPrimitives.ReadUInt16BigEndian(frame) != transaction
            || frame[7] != 3
            || frame[8] != count * 2
            || frame.Length < 9 + (count * 2))
        {
            return null;
        }

        var registers = new ushort[count];

        for (var i = 0; i < count; i++)
        {
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(frame[(9 + (i * 2))..]);
        }

        return registers;
    }
}
