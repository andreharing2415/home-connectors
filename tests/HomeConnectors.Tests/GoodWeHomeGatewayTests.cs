using HomeConnectors;
using HomeConnectors.Adapters;
using Microsoft.Extensions.Time.Testing;

namespace HomeConnectors.Tests;

/// <summary>
/// The GoodWe Modbus reader, checked against the reply the owner's inverter gave on
/// 2026-10-10 at 13:44, while it stood in fault with no grid voltage — strings
/// at 424/380/386 V, nothing generated that day, 6089.3 kWh in its life.
/// </summary>
[TestClass]
public sealed class GoodWeHomeGatewayTests
{
    // Transaction 1, unit 247, function 3, 0x92 bytes: registers 30100–30172.
    private const string RunningReply =
        "000100000095f703921a0a0a0d2c34109000000ed700000f14000000000000ffffffffffffffff"
        + "000000000000000000000000000000000000000000000000000000000002010202000000000000"
        + "0000000000000000000000000001230122012200000000eddd0000065200950000000000000000"
        + "00000000ffffffffffffffffffffffff0000108b00000000000001010000051b0000006a0034";

    [TestMethod]
    public void A_request_is_an_mbap_header_and_a_function_3_read()
    {
        var frame = GoodWeModbusClient.Request(1, GoodWeModbusClient.Unit, GoodWeHomeGateway.Running, GoodWeHomeGateway.RunningCount);

        Assert.AreEqual("000100000006f70375940049", Convert.ToHexStringLower(frame));
    }

    [TestMethod]
    public void A_reply_for_another_transaction_or_an_exception_is_refused()
    {
        var reply = Convert.FromHexString(RunningReply);

        Assert.IsNotNull(GoodWeModbusClient.Registers(reply, 1, GoodWeHomeGateway.RunningCount));
        Assert.IsNull(GoodWeModbusClient.Registers(reply, 2, GoodWeHomeGateway.RunningCount));
        Assert.IsNull(GoodWeModbusClient.Registers(Convert.FromHexString("000100000003f78302"), 1, 0x21));
    }

    [TestMethod]
    public void The_recorded_fault_reads_as_no_grid_and_nothing_generated()
    {
        var running = GoodWeModbusClient.Registers(Convert.FromHexString(RunningReply), 1, GoodWeHomeGateway.RunningCount)!;
        var gateway = new GoodWeHomeGateway(new GoodWeModbusClient(), new FakeTimeProvider());

        var state = gateway.State(running, meter: null);

        Assert.IsTrue(state.Online);
        Assert.IsNull(state.Power);
        Assert.IsFalse(state.Running);
        Assert.AreEqual("em falha: sem tensão da rede elétrica", state.Status);
        Assert.AreEqual(0d, Measure(state, "geração"));
        Assert.AreEqual(0d, Measure(state, "hoje"));
        Assert.AreEqual(6089.3, Measure(state, "total")!.Value, 1e-9);
        Assert.AreEqual(0d, Measure(state, "tensão da rede"));
        Assert.AreEqual(29.1, state.Readings.Single(s => s.Label == "inversor").Celsius!.Value, 1e-9);
        Assert.IsNull(Measure(state, "rede"), "sem medidor não há rede nem consumo");
        Assert.IsNull(Measure(state, "consumo"));
    }

    [TestMethod]
    public void With_a_meter_talking_the_house_uses_what_is_generated_minus_what_goes_out()
    {
        var running = GoodWeModbusClient.Registers(Convert.FromHexString(RunningReply), 1, GoodWeHomeGateway.RunningCount)!;
        running[30127 - GoodWeHomeGateway.Running] = 0;
        running[30128 - GoodWeHomeGateway.Running] = 4200;
        running[30129 - GoodWeHomeGateway.Running] = 1;

        var meter = new ushort[GoodWeHomeGateway.MeterCount];
        meter[1] = 1500;
        meter[30209 - GoodWeHomeGateway.Meter] = 1;

        var state = new GoodWeHomeGateway(new GoodWeModbusClient(), new FakeTimeProvider()).State(running, meter);

        Assert.AreEqual("gerando", state.Status);
        Assert.IsTrue(state.Running);
        Assert.AreEqual(4200d, Measure(state, "geração"));
        Assert.AreEqual(1500d, Measure(state, "rede"));
        Assert.AreEqual(2700d, Measure(state, "consumo"));
    }

    [TestMethod]
    public void A_house_importing_at_night_consumes_what_it_draws()
    {
        var running = GoodWeModbusClient.Registers(Convert.FromHexString(RunningReply), 1, GoodWeHomeGateway.RunningCount)!;
        running[30129 - GoodWeHomeGateway.Running] = 0;

        var meter = new ushort[GoodWeHomeGateway.MeterCount];
        unchecked
        {
            var draw = -800;
            meter[0] = (ushort)(draw >> 16);
            meter[1] = (ushort)draw;
        }

        meter[30209 - GoodWeHomeGateway.Meter] = 1;

        var state = new GoodWeHomeGateway(new GoodWeModbusClient(), new FakeTimeProvider()).State(running, meter);

        Assert.AreEqual("aguardando sol", state.Status);
        Assert.AreEqual(-800d, Measure(state, "rede"));
        Assert.AreEqual(800d, Measure(state, "consumo"));
    }

    [TestMethod]
    public async Task Nothing_is_ever_sent_to_the_inverter()
    {
        var gateway = new GoodWeHomeGateway(new GoodWeModbusClient(), new FakeTimeProvider());
        var device = new HomeDevice("inversor-solar", "Inversor solar", "Depósito", HomeDeviceClass.Sensor, HomeProvider.GoodWe, [], Host: "192.0.2.1");

        var reply = await gateway.SendAsync(device, HomeCommand.TurnOff, CancellationToken.None);

        Assert.IsFalse(reply.Ok);
        Assert.AreEqual(HomeReplyCodes.Rejected, reply.Code);
        Assert.IsTrue(gateway.IsConfigured(HomeProvider.GoodWe));
    }

    private static double? Measure(HomeDeviceState state, string label) =>
        state.Quantities.SingleOrDefault(m => m.Label == label)?.Value;
}
