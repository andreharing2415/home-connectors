using System.Collections.Immutable;
using System.Globalization;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// The house's GoodWe solar inverter, read through <see cref="GoodWeModbusClient"/>
/// with the register map of GoodWe's DT family (the one the inverter answers on
/// 2026-10-10; the ET map at 35000 is refused). Read-only: the hub tells what
/// the roof makes, what goes to the street and what the house uses, and never
/// starts or stops the inverter.
/// <para>
/// Without a GoodWe smart meter on the grid connection the inverter knows only
/// what it generates; "rede" and "consumo" appear once the meter's communication
/// status (register 30209) says normal.
/// </para>
/// </summary>
public sealed class GoodWeHomeGateway(GoodWeModbusClient client, TimeProvider clock) : IHomeDeviceGateway
{
    internal const ushort Running = 30100;
    internal const ushort RunningCount = 0x49;
    internal const ushort Meter = 30195;
    internal const ushort MeterCount = 16;

    private readonly GoodWeModbusClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>A LAN socket with no keys, like the Tholz: always configured.</summary>
    public bool IsConfigured(HomeProvider provider) => provider == HomeProvider.GoodWe;

    public async Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Provider != HomeProvider.GoodWe)
        {
            return HomeReply.Failure<HomeDeviceState>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        if (string.IsNullOrWhiteSpace(device.Host))
        {
            return HomeReply.Failure<HomeDeviceState>(HomeReplyCodes.NotConfigured, "Falta o IP do inversor solar (Host) na lista da casa.");
        }

        var read = await _client.ReadAsync(device.Host, [(Running, RunningCount), (Meter, MeterCount)], cancellationToken).ConfigureAwait(false);

        if (!read.Ok)
        {
            return HomeReply.Failure<HomeDeviceState>(read.Code, read.Message);
        }

        return read.Value![0] is { } running
            ? HomeReply.Success(State(running, read.Value[1]))
            : HomeReply.Failure<HomeDeviceState>(HomeReplyCodes.Rejected, "O inversor recusou a leitura dos dados de operação.");
    }

    public Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(HomeReply.Failure<bool>(HomeReplyCodes.Rejected, "O inversor solar só é lido; o hub não liga nem desliga a geração."));
    }

    /// <summary>An inverter has no camera.</summary>
    public Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream, "Um inversor não tem câmera."));
    }

    /// <inheritdoc cref="AllocateStreamAsync"/>
    public Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame, "Um inversor não tem câmera."));
    }

    /// <summary>The inverter is the device on the list; there is nothing else behind its dongle.</summary>
    public Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken) =>
        Task.FromResult(provider == HomeProvider.GoodWe
            ? HomeReply.Success(ImmutableArray<DiscoveredDevice>.Empty)
            : HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.ProviderUnsupported, provider.ToString()));

    /// <summary>
    /// The running block (30100 on) and the meter block (30195 on) in the house's
    /// terms. Measures are named for the model: <c>geração</c>, <c>hoje</c>,
    /// <c>total</c>, <c>tensão da rede</c>, and with a meter <c>rede</c> (positive
    /// when the house sends power to the street) and <c>consumo</c>.
    /// </summary>
    internal HomeDeviceState State(ushort[] running, ushort[]? meter)
    {
        ArgumentNullException.ThrowIfNull(running);

        if (running.Length < RunningCount)
        {
            return new HomeDeviceState(Online: false, Power: null, Temperature: null, _clock.GetUtcNow());
        }

        ushort R(int register) => running[register - Running];
        int Long(int register) => (int)(((uint)R(register) << 16) | R(register + 1));

        var generation = Long(30127);
        var mode = R(30129);
        var errors = (uint)Long(30130);

        var measures = ImmutableArray.CreateBuilder<HomeMeasure>();
        measures.Add(new HomeMeasure("geração", generation, "W"));
        measures.Add(new HomeMeasure("hoje", R(30144) / 10.0, "kWh"));
        measures.Add(new HomeMeasure("total", (uint)Long(30145) / 10.0, "kWh"));
        measures.Add(new HomeMeasure("tensão da rede", R(30118) / 10.0, "V"));

        // 0 no meter, 1 normal, 2 disconnected: only a meter that talks is believed.
        if (meter is { Length: >= MeterCount } && meter[30209 - Meter] == 1)
        {
            var grid = (int)(((uint)meter[0] << 16) | meter[1]);
            measures.Add(new HomeMeasure("rede", grid, "W"));
            measures.Add(new HomeMeasure("consumo", Math.Max(0, generation - grid), "W"));
        }

        return new HomeDeviceState(
            Online: true,
            Power: null,
            Temperature: null,
            _clock.GetUtcNow(),
            [new HomeSensor("inversor", (short)R(30141) / 10.0)],
            Status(mode, errors, generation),
            Measures: measures.ToImmutable(),
            Running: generation > 0);
    }

    /// <summary>The work mode in the owner's words; a fault names the first cause it reports.</summary>
    internal static string Status(int mode, uint errors, int generation) => mode switch
    {
        0 => "aguardando sol",
        1 => generation > 0 ? "gerando" : "ligado, sem geração",
        2 => "em falha: " + Fault(errors),
        4 => "verificando a rede",
        _ => string.Create(CultureInfo.InvariantCulture, $"modo {mode}"),
    };

    // Most telling first: a missing grid also trips the voltage check, and the
    // missing grid is what someone has to go and fix.
    private static readonly (int Bit, string Words)[] Faults =
    [
        (9, "sem tensão da rede elétrica"),
        (17, "tensão da rede fora da faixa"),
        (29, "frequência da rede fora da faixa"),
        (14, "falta uma fase da rede"),
        (18, "falha de isolamento nos painéis"),
        (10, "fuga de corrente para o terra"),
        (15, "tensão dos painéis alta demais"),
        (13, "superaquecido"),
        (11, "tensão interna alta demais"),
        (19, "injeção de corrente contínua na rede"),
    ];

    internal static string Fault(uint errors)
    {
        foreach (var (bit, words) in Faults)
        {
            if ((errors & (1u << bit)) != 0)
            {
                return words;
            }
        }

        return errors == 0 ? "sem código" : string.Create(CultureInfo.InvariantCulture, $"código 0x{errors:X8}");
    }
}
