using System.IO;
using System.Net.Http;
using HomeConnectors.Adapters;
using HomeConnectors;
using Microsoft.Extensions.Time.Testing;

namespace HomeConnectors.Tests;

/// <summary>
/// HOME-01/HOME-02. <see cref="CompositeHomeGateway"/> routes by the device's
/// own provider, never by which gateway happens to be configured — an
/// unconfigured Tuya device must still reach the Tuya adapter and answer
/// <see cref="HomeReplyCodes.NotConfigured"/> from there, not fall through to
/// Hikvision and answer <see cref="HomeReplyCodes.ProviderUnsupported"/>
/// instead.
/// </summary>
[TestClass]
public sealed class CompositeHomeGatewayTests
{
    private static readonly HomeDevice TuyaDevice = new(
        "bf-cascata", "Cascata", "Piscina", HomeDeviceClass.Pump, HomeProvider.Tuya, ["cascata"]);

    private static readonly HomeDevice HikvisionDevice = new(
        "1", "Campainha", "Porta", HomeDeviceClass.Camera, HomeProvider.Hikvision, ["campainha"]);

    // Tholz has its own adapter now, so it is no longer "unowned"; Google Nest
    // still has none and is what falls through to Tuya.
    private static readonly HomeDevice UnsupportedDevice = new(
        "any", "Termostato", "Casa", HomeDeviceClass.Climate, HomeProvider.GoogleNest, ["termostato"]);

    private static readonly HomeDevice TholzDevice = new(
        "agua", "Aquecimento da água", "Casa", HomeDeviceClass.Climate, HomeProvider.Tholz, ["agua"],
        Host: "10.0.0.9", Slot: "heat0", MinTemperature: 60, MaxTemperature: 90);

    [TestMethod]
    public async Task An_unconfigured_tuya_device_is_answered_by_tuya_not_hikvision()
    {
        await using var world = new World();

        var reply = await world.Gateway.SendAsync(TuyaDevice, HomeCommand.TurnOn, CancellationToken.None);

        Assert.AreEqual(HomeReplyCodes.NotConfigured, reply.Code);
    }

    [TestMethod]
    public async Task A_hikvision_device_is_routed_to_the_hikvision_gateway()
    {
        await using var world = new World();

        var reply = await world.Gateway.AllocateStreamAsync(HikvisionDevice, CancellationToken.None);

        // Unconfigured too, but the Hikvision adapter's own message — proof it
        // was the one asked, not Tuya's.
        Assert.AreEqual(HomeReplyCodes.NotConfigured, reply.Code);
        Assert.IsTrue(reply.Message.Contains("NVR", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task A_provider_neither_adapter_owns_falls_to_tuya_and_is_refused_as_unsupported()
    {
        await using var world = new World();

        var reply = await world.Gateway.SendAsync(UnsupportedDevice, HomeCommand.TurnOn, CancellationToken.None);

        Assert.AreEqual(HomeReplyCodes.ProviderUnsupported, reply.Code);
    }

    [TestMethod]
    public async Task A_xiaomi_vacuum_is_routed_to_the_xiaomi_gateway()
    {
        await using var world = new World();

        var vacuum = new HomeDevice("100000001", "Aspirador", "Casa", HomeDeviceClass.Vacuum, HomeProvider.Xiaomi, ["aspirador"],
            Host: "10.0.0.40");

        var reply = await world.Gateway.SendAsync(vacuum, HomeCommand.TurnOn, CancellationToken.None);

        Assert.AreEqual(HomeReplyCodes.NotConfigured, reply.Code);
        Assert.IsTrue(reply.Message.Contains("Xiaomi", StringComparison.Ordinal), reply.Message);
    }

    [TestMethod]
    public async Task A_zagonel_shower_is_routed_to_the_zagonel_gateway()
    {
        await using var world = new World();

        var shower = new HomeDevice("0123456789abcdef01234567", "Chuveiro", "Suíte 2", HomeDeviceClass.Climate,
            HomeProvider.Zagonel, ["chuveiro"], MinTemperature: 25, MaxTemperature: 50);

        // A power-only command the shower cannot honour is refused by the Zagonel
        // adapter's own words — proof the composite routed it there and never
        // touched the network.
        var reply = await world.Gateway.SendAsync(shower, HomeCommand.TurnOn, CancellationToken.None);

        Assert.AreEqual(HomeReplyCodes.Rejected, reply.Code);
        Assert.IsTrue(reply.Message.Contains("Zagonel", StringComparison.Ordinal), reply.Message);
    }

    [TestMethod]
    public async Task IsConfigured_asks_the_gateway_that_owns_the_provider()
    {
        await using var world = new World();

        // A Tholz that answers true where Tuya and Hikvision answer false is proof
        // the composite routed the provider to the gateway that owns it.
        Assert.IsFalse(world.Gateway.IsConfigured(HomeProvider.Tuya));
        Assert.IsFalse(world.Gateway.IsConfigured(HomeProvider.Hikvision));
        Assert.IsTrue(world.Gateway.IsConfigured(HomeProvider.Tholz), "o socket Tholz não pede chave");
        Assert.IsFalse(world.Gateway.IsConfigured(HomeProvider.BlueRiiot), "a Blue Riiot pede a conta");
        Assert.IsTrue(world.Gateway.IsConfigured(HomeProvider.GoodWe), "o Modbus do inversor não pede chave");
        Assert.IsTrue(world.Gateway.IsConfigured(HomeProvider.Zagonel), "o broker Zagonel não pede chave");
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "jarvis-composite-home-tests", Guid.NewGuid().ToString("N"));

        public World()
        {
            var clock = new FakeTimeProvider();
            var tuyaClient = new TuyaCloudClient(new HttpClient(), new EmptyCredentialStore(), clock);
            var tuya = new TuyaHomeGateway(tuyaClient, clock);

            Streams = new HikvisionStreamServer(_directory, clock);
            var hikvision = new HikvisionHomeGateway(new EmptyCredentialStore(), Streams, clock);

            var tholz = new TholzHomeGateway(new TholzSocketClient(clock), clock);

            Miio = new MiioClient(clock);
            var xiaomi = new XiaomiHomeGateway(Miio, new EmptyCredentialStore(), clock);

            var blueRiiot = new BlueRiiotHomeGateway(new BlueRiiotCloudClient(new HttpClient(), new EmptyCredentialStore(), clock), clock);

            var daikin = new DaikinHomeGateway(new DaikinLocalClient(), new EmptyCredentialStore(), clock);

            var goodWe = new GoodWeHomeGateway(new GoodWeModbusClient(), clock);

            var zagonel = new ZagonelHomeGateway(new ZagonelMqttClient(clock), clock);

            Gateway = new CompositeHomeGateway(tuya, tholz, hikvision, xiaomi, blueRiiot, daikin, goodWe, zagonel);
        }

        public CompositeHomeGateway Gateway { get; }

        private HikvisionStreamServer Streams { get; }

        private MiioClient Miio { get; }

        public ValueTask DisposeAsync()
        {
            Streams.Dispose();
            Miio.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyCredentialStore : IHomeCredentialStore
    {
        public HomeCredentials? Read(HomeProvider provider) => null;

        public void Save(HomeCredentials credentials)
        {
        }

        public void Clear(HomeProvider provider)
        {
        }
    }
}
