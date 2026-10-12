using System.IO;
using HomeConnectors;
using HomeConnectors.Adapters;
using Microsoft.Extensions.Time.Testing;

namespace HomeConnectors.Tests;

/// <summary>
/// HOME-01 (Hikvision). No live NVR and no real ffmpeg here — those are
/// exercised by running the app — but everything that does not need either:
/// parsing the host out of <see cref="HomeCredentials.Region"/>, parsing the
/// ISAPI channel list, and every refusal path a gateway takes before it would
/// ever touch the network or a process.
/// </summary>
[TestClass]
public sealed class HikvisionHomeGatewayTests
{
    // -----------------------------------------------------------------------
    // HikvisionHost.Parse
    // -----------------------------------------------------------------------

    [TestMethod]
    public void A_bare_host_defaults_to_port_80_and_a_colon_names_another_one()
    {
        Assert.AreEqual((HikvisionHost?)new HikvisionHost("192.168.1.10", 80), HikvisionHost.Parse("192.168.1.10"));
        Assert.AreEqual((HikvisionHost?)new HikvisionHost("192.168.1.10", 8080), HikvisionHost.Parse(" 192.168.1.10:8080 "));
    }

    [TestMethod]
    public void A_url_a_bad_port_or_nothing_at_all_parses_to_nothing()
    {
        Assert.IsNull(HikvisionHost.Parse("https://evil.example"));
        Assert.IsNull(HikvisionHost.Parse("192.168.1.10:not-a-port"));
        Assert.IsNull(HikvisionHost.Parse("192.168.1.10:99999"));
        Assert.IsNull(HikvisionHost.Parse(":80"));
        Assert.IsNull(HikvisionHost.Parse(string.Empty));
        Assert.IsNull(HikvisionHost.Parse(null!));
    }

    // -----------------------------------------------------------------------
    // ISAPI channel parsing
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Channels_are_read_by_local_name_regardless_of_namespace()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <InputProxyChannelList xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <InputProxyChannel>
                <id>1</id>
                <name>Portão</name>
              </InputProxyChannel>
              <InputProxyChannel>
                <id>2</id>
                <name>Campainha</name>
              </InputProxyChannel>
            </InputProxyChannelList>
            """;

        var channels = HikvisionHomeGateway.ParseChannels(xml);

        Assert.AreEqual(2, channels.Length);
        Assert.AreEqual("1", channels[0].Id);
        Assert.AreEqual("Portão", channels[0].Name);
        Assert.AreEqual("Hikvision", channels[0].Category);
        Assert.IsTrue(channels[0].Online);
        Assert.AreEqual("2", channels[1].Id);
        Assert.AreEqual("Campainha", channels[1].Name);
    }

    [TestMethod]
    public void A_channel_with_no_id_is_skipped_and_a_missing_name_falls_back_to_the_id()
    {
        const string xml = """
            <InputProxyChannelList>
              <InputProxyChannel>
                <name>Sem id</name>
              </InputProxyChannel>
              <InputProxyChannel>
                <id>3</id>
              </InputProxyChannel>
            </InputProxyChannelList>
            """;

        var channels = HikvisionHomeGateway.ParseChannels(xml);

        Assert.AreEqual(1, channels.Length);
        Assert.AreEqual("3", channels[0].Id);
        Assert.AreEqual("3", channels[0].Name);
    }

    [TestMethod]
    public void Analog_video_inputs_are_channels_too()
    {
        const string xml = """
            <VideoInputChannelList xmlns="http://www.hikvision.com/ver20/XMLSchema" version="2.0">
              <VideoInputChannel><id>1</id><inputPort>1</inputPort><name>Garagem</name></VideoInputChannel>
            </VideoInputChannelList>
            """;

        var channels = HikvisionHomeGateway.ParseChannels(xml);

        Assert.AreEqual(1, channels.Length);
        Assert.AreEqual("1", channels[0].Id);
        Assert.AreEqual("Garagem", channels[0].Name);
    }

    [TestMethod]
    public void Malformed_xml_or_no_root_answers_an_empty_list_never_a_throw()
    {
        Assert.AreEqual(0, HikvisionHomeGateway.ParseChannels("not xml at all").Length);
        Assert.AreEqual(0, HikvisionHomeGateway.ParseChannels(string.Empty).Length);
    }

    // -----------------------------------------------------------------------
    // The gateway's refusal paths
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Without_keys_every_call_says_not_configured_and_touches_nothing()
    {
        await using var world = new World(credentials: null);

        Assert.IsFalse(world.Gateway.IsConfigured(HomeProvider.Hikvision));

        var stream = await world.Gateway.AllocateStreamAsync(Doorbell, CancellationToken.None);
        Assert.IsFalse(stream.Ok);
        Assert.AreEqual(HomeReplyCodes.NotConfigured, stream.Code);

        var discovery = await world.Gateway.DiscoverAsync(HomeProvider.Hikvision, CancellationToken.None);
        Assert.IsFalse(discovery.Ok);
        Assert.AreEqual(HomeReplyCodes.NotConfigured, discovery.Code);
    }

    [TestMethod]
    public async Task An_unparsable_host_counts_as_not_configured_even_with_a_username_and_password_on_file()
    {
        var bad = new HomeCredentials(HomeProvider.Hikvision, "admin", "secret", "https://not-a-host");
        await using var world = new World(bad);

        Assert.IsFalse(world.Gateway.IsConfigured(HomeProvider.Hikvision));
    }

    [TestMethod]
    public async Task A_device_from_another_provider_is_refused_before_anything_hikvision_specific_runs()
    {
        await using var world = new World(Configured());

        var tuyaDevice = Doorbell with { Provider = HomeProvider.Tuya };

        var state = await world.Gateway.ReadStateAsync(tuyaDevice, CancellationToken.None);
        Assert.AreEqual(HomeReplyCodes.ProviderUnsupported, state.Code);

        var sent = await world.Gateway.SendAsync(tuyaDevice, HomeCommand.TurnOn, CancellationToken.None);
        Assert.AreEqual(HomeReplyCodes.ProviderUnsupported, sent.Code);

        var stream = await world.Gateway.AllocateStreamAsync(tuyaDevice, CancellationToken.None);
        Assert.AreEqual(HomeReplyCodes.ProviderUnsupported, stream.Code);
    }

    [TestMethod]
    public async Task A_camera_is_watched_never_switched()
    {
        await using var world = new World(Configured());

        var reply = await world.Gateway.SendAsync(Doorbell, HomeCommand.TurnOn, CancellationToken.None);

        Assert.IsFalse(reply.Ok);
        Assert.AreEqual(HomeReplyCodes.Rejected, reply.Code);
    }

    [TestMethod]
    public async Task Reading_state_reports_online_with_no_power_or_temperature_to_speak_of()
    {
        await using var world = new World(Configured());

        var reply = await world.Gateway.ReadStateAsync(Doorbell, CancellationToken.None);

        Assert.IsTrue(reply.Ok);
        Assert.IsTrue(reply.Value!.Online);
        Assert.IsNull(reply.Value.Power);
        Assert.IsNull(reply.Value.Temperature);
    }

    [TestMethod]
    public async Task Missing_ffmpeg_returns_a_failure_instead_of_crashing_and_can_be_retried()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-hikvision-tests", Guid.NewGuid().ToString("N"));
        using var streams = new HikvisionStreamServer(directory, TimeProvider.System,
            Path.Combine(directory, "missing-ffmpeg.exe"));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reply = await streams.AllocateAsync(Doorbell, "127.0.0.1", 554,
                "admin", "private-password", CancellationToken.None);
            Assert.IsFalse(reply.Ok);
            Assert.AreEqual(HomeReplyCodes.NoStream, reply.Code);
            StringAssert.Contains(reply.Message, "FFmpeg");
            Assert.IsFalse(reply.Message.Contains("private-password", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task Served_playlists_carry_the_CORS_header_the_console_origin_needs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-hikvision-tests", Guid.NewGuid().ToString("N"));
        using var streams = new HikvisionStreamServer(directory, TimeProvider.System);
        Directory.CreateDirectory(Path.Combine(directory, "1"));
        await File.WriteAllTextAsync(Path.Combine(directory, "1", "index.m3u8"), "#EXTM3U");

        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(streams.BaseUri, "1/index.m3u8"));

        Assert.IsTrue(response.IsSuccessStatusCode);
        Assert.AreEqual("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.AreEqual("application/vnd.apple.mpegurl", response.Content.Headers.ContentType?.MediaType);
    }

    private static HomeDevice Doorbell { get; } = new(
        "1", "Campainha", "Porta", HomeDeviceClass.Camera, HomeProvider.Hikvision, ["campainha", "porta"]);

    private static HomeCredentials Configured() =>
        new(HomeProvider.Hikvision, "admin", "secret", "192.168.1.10");

    private sealed class World : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "jarvis-hikvision-tests", Guid.NewGuid().ToString("N"));

        public World(HomeCredentials? credentials)
        {
            Store = new MemoryCredentialStore(credentials);
            Clock = new FakeTimeProvider();
            Streams = new HikvisionStreamServer(_directory, Clock);
            Gateway = new HikvisionHomeGateway(Store, Streams, Clock);
        }

        public MemoryCredentialStore Store { get; }

        public FakeTimeProvider Clock { get; }

        public HikvisionStreamServer Streams { get; }

        public HikvisionHomeGateway Gateway { get; }

        public ValueTask DisposeAsync()
        {
            Streams.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryCredentialStore(HomeCredentials? initial) : IHomeCredentialStore
    {
        private HomeCredentials? _current = initial;

        public HomeCredentials? Read(HomeProvider provider) => _current?.Provider == provider ? _current : null;

        public void Save(HomeCredentials credentials) => _current = credentials;

        public void Clear(HomeProvider provider) => _current = null;
    }
}
