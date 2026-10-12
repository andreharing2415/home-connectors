using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HomeConnectors;
using HomeConnectors.Adapters;
using Microsoft.Extensions.Time.Testing;

namespace HomeConnectors.Tests;

/// <summary>
/// The Blue Riiot adapter: the SigV4 signer against AWS's own published test
/// vectors, and the client and gateway against a fake HTTP handler that plays
/// the cloud's replies back and records what went on the wire.
/// <para>
/// If the signer vectors ever fail, API Gateway answers 403 to every read and
/// nothing else in this suite matters.
/// </para>
/// </summary>
[TestClass]
public sealed class BlueRiiotCloudTests
{
    private const string Serial = "00012345";
    private const string Pool = "11111111-2222-3333-4444-555555555555";

    // -----------------------------------------------------------------------
    // Signature (aws-sig-v4-test-suite)
    // -----------------------------------------------------------------------

    private static readonly DateTimeOffset VectorTime = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    [TestMethod]
    public void The_signature_matches_the_aws_get_vanilla_vector()
    {
        var authorization = AwsSigV4.Authorization(
            "GET",
            new Uri("https://example.amazonaws.com/"),
            new Dictionary<string, string> { ["Host"] = "example.amazonaws.com", ["X-Amz-Date"] = "20150830T123600Z" },
            AwsSigV4.EmptyPayloadHash,
            "AKIDEXAMPLE",
            "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
            "us-east-1",
            "service",
            VectorTime);

        Assert.AreEqual(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            authorization);
    }

    [TestMethod]
    public void The_signature_sorts_the_query_like_the_aws_vector()
    {
        var authorization = AwsSigV4.Authorization(
            "GET",
            new Uri("https://example.amazonaws.com/?Param2=value2&Param1=value1"),
            new Dictionary<string, string> { ["host"] = "example.amazonaws.com", ["x-amz-date"] = "20150830T123600Z" },
            AwsSigV4.EmptyPayloadHash,
            "AKIDEXAMPLE",
            "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
            "us-east-1",
            "service",
            VectorTime);

        StringAssert.EndsWith(authorization, "Signature=b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500");
    }

    [TestMethod]
    public void Only_aws_region_names_are_accepted()
    {
        Assert.IsTrue(BlueRiiotCloudClient.IsRegion("eu-west-1"));
        Assert.IsTrue(BlueRiiotCloudClient.IsRegion("us-gov-west-1"));
        Assert.IsFalse(BlueRiiotCloudClient.IsRegion("eu"));
        Assert.IsFalse(BlueRiiotCloudClient.IsRegion("https://evil.example/"));
        Assert.IsFalse(BlueRiiotCloudClient.IsRegion(null));
    }

    // -----------------------------------------------------------------------
    // Client
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Without_an_account_nothing_goes_on_the_wire()
    {
        var world = new World(credentials: null);

        var reply = await world.Gateway.ReadStateAsync(Analyzer(), TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(reply.Ok);
        Assert.AreEqual(HomeReplyCodes.NotConfigured, reply.Code);
        Assert.IsFalse(world.Gateway.IsConfigured(HomeProvider.BlueRiiot));
        Assert.IsEmpty(world.Handler.Requests);
    }

    [TestMethod]
    public async Task The_login_posts_the_account_and_the_read_is_signed_with_the_session_keys()
    {
        var world = new World();
        world.Handler.Enqueue(Login());
        world.Handler.Enqueue(Json(Measurements));

        var reply = await world.Gateway.ReadStateAsync(Analyzer(), TestContext.CancellationTokenSource.Token);

        Assert.IsTrue(reply.Ok, reply.Message);
        Assert.HasCount(2, world.Handler.Requests);

        var login = world.Handler.Requests[0];
        Assert.AreEqual(HttpMethod.Post, login.Method);
        Assert.AreEqual("https://api.riiotlabs.com/prod/user/login", login.Url);
        using (var body = JsonDocument.Parse(login.Body!))
        {
            Assert.AreEqual("owner@example.com", body.RootElement.GetProperty("email").GetString());
            Assert.AreEqual("s3nha", body.RootElement.GetProperty("password").GetString());
        }

        Assert.IsFalse(login.Headers.ContainsKey("Authorization"), "o login não é assinado");

        var read = world.Handler.Requests[1];
        Assert.AreEqual(
            $"https://api.riiotlabs.com/prod/swimming_pool/{Pool}/blue/{Serial}/lastMeasurements?mode=blue_and_strip",
            read.Url);
        StringAssert.StartsWith(read.Headers["Authorization"], "AWS4-HMAC-SHA256 Credential=ASIAKEY/20260927/eu-west-1/execute-api/aws4_request, SignedHeaders=host;x-amz-date;x-amz-security-token, Signature=");
        Assert.AreEqual("token-1", read.Headers["X-Amz-Security-Token"]);
        Assert.AreEqual("20260927T120000Z", read.Headers["X-Amz-Date"]);
        Assert.AreEqual("BlueConnect/3.2.1", read.Headers["User-Agent"]);
    }

    [TestMethod]
    public async Task The_session_is_reused_until_it_nears_expiry()
    {
        var world = new World();
        world.Handler.Enqueue(Login());
        world.Handler.Enqueue(Json(Measurements));
        world.Handler.Enqueue(Json(Measurements));
        world.Handler.Enqueue(Login());
        world.Handler.Enqueue(Json(Measurements));

        await world.Gateway.ReadStateAsync(Analyzer(), TestContext.CancellationTokenSource.Token);

        // Past the reading's own life, inside the session's: a read, no login.
        world.Clock.Advance(TimeSpan.FromMinutes(6));
        await world.Gateway.ReadStateAsync(Analyzer(), TestContext.CancellationTokenSource.Token);

        // The keys expire at 13:00; five minutes before that, a new login.
        world.Clock.Advance(TimeSpan.FromMinutes(50));
        await world.Gateway.ReadStateAsync(Analyzer(), TestContext.CancellationTokenSource.Token);

        CollectionAssert.AreEqual(
            new[] { "login", "read", "read", "login", "read" },
            world.Handler.Requests.Select(r => r.Url.EndsWith("/user/login", StringComparison.Ordinal) ? "login" : "read").ToArray());
    }

    [TestMethod]
    public async Task A_read_inside_five_minutes_is_answered_from_the_last_reading()
    {
        var world = new World();
        world.Handler.Enqueue(Login());
        world.Handler.Enqueue(Json(Measurements));

        await world.Gateway.ReadStateAsync(Analyzer(), TestContext.CancellationTokenSource.Token);
        world.Clock.Advance(TimeSpan.FromMinutes(2));
        var again = await world.Gateway.ReadStateAsync(Analyzer(), TestContext.CancellationTokenSource.Token);

        Assert.IsTrue(again.Ok);
        Assert.HasCount(2, world.Handler.Requests);
    }

    [TestMethod]
    public async Task Keys_the_gateway_refuses_are_renewed_once()
    {
        var world = new World();
        world.Handler.Enqueue(Login());
        world.Handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden));
        world.Handler.Enqueue(Login());
        world.Handler.Enqueue(Json(Measurements));

        var reply = await world.Gateway.ReadStateAsync(Analyzer(), TestContext.CancellationTokenSource.Token);

        Assert.IsTrue(reply.Ok, reply.Message);
        Assert.HasCount(4, world.Handler.Requests);
    }

    [TestMethod]
    public async Task A_refused_login_is_unauthorized()
    {
        var world = new World();
        world.Handler.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest));

        var reply = await world.Gateway.ReadStateAsync(Analyzer(), TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(reply.Ok);
        Assert.AreEqual(HomeReplyCodes.Unauthorized, reply.Code);
    }

    [TestMethod]
    public async Task An_error_in_a_200_body_is_not_success()
    {
        var world = new World();
        world.Handler.Enqueue(Login());
        world.Handler.Enqueue(Json("""{"errorMessage":"Internal server error"}"""));

        var reply = await world.Gateway.ReadStateAsync(Analyzer(), TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(reply.Ok);
        Assert.AreEqual(HomeReplyCodes.Unreachable, reply.Code);
    }

    // -----------------------------------------------------------------------
    // Gateway
    // -----------------------------------------------------------------------

    [TestMethod]
    public void The_water_temperature_is_a_sensor_and_the_chemistry_carries_its_unit_and_range()
    {
        using var document = JsonDocument.Parse(Measurements);

        var state = BlueRiiotHomeGateway.Parse(document.RootElement, new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));

        Assert.IsTrue(state.Online);
        Assert.IsNull(state.Power);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 27, 11, 32, 46, TimeSpan.Zero), state.ObservedAt);
        Assert.IsNull(state.Status, "leitura de meia hora atrás não é antiga");

        Assert.HasCount(1, state.Readings);
        Assert.AreEqual(new HomeSensor("água", 27.4), state.Readings[0]);

        var labels = state.Quantities.Select(m => m.Label).ToArray();
        CollectionAssert.AreEqual(new[] { "pH", "ORP", "sal", "cloro livre" }, labels);

        var ph = state.Quantities[0];
        Assert.AreEqual(7.42, ph.Value);
        Assert.AreEqual(string.Empty, ph.Unit);
        Assert.AreEqual(0, ph.Verdict);

        var orp = state.Quantities[1];
        Assert.AreEqual("mV", orp.Unit);
        Assert.AreEqual(-1, orp.Verdict, "580 mV está abaixo de 650");
    }

    [TestMethod]
    public void The_probe_wins_over_a_strip_and_an_expired_strip_is_left_out()
    {
        using var document = JsonDocument.Parse(Measurements);

        var state = BlueRiiotHomeGateway.Parse(document.RootElement, new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));

        Assert.AreEqual(7.42, state.Quantities.Single(m => m.Label == "pH").Value, "o pH da sonda, não o da fita");
        Assert.IsFalse(state.Quantities.Any(m => m.Label == "alcalinidade"), "fita vencida fica de fora");
    }

    [TestMethod]
    public void An_old_reading_says_how_old_it_is()
    {
        using var document = JsonDocument.Parse(Measurements);

        var state = BlueRiiotHomeGateway.Parse(document.RootElement, new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.AreEqual("última medição há 2 dias", state.Status);
    }

    [TestMethod]
    public async Task Without_a_pool_id_the_pool_is_found_once_and_remembered()
    {
        var world = new World();
        world.Handler.Enqueue(Login());
        world.Handler.Enqueue(Json($$"""{"data":[{"swimming_pool_id":"{{Pool}}","name":"Casa"}]}"""));
        world.Handler.Enqueue(Json($$$"""{"data":[{"blue_device_serial":"{{{Serial}}}","blue_device":{"hw_type":"blue_connect_plus"}}]}"""));
        world.Handler.Enqueue(Json(Measurements));
        world.Handler.Enqueue(Json(Measurements));

        var device = Analyzer() with { Slot = null };

        var first = await world.Gateway.ReadStateAsync(device, TestContext.CancellationTokenSource.Token);
        world.Clock.Advance(TimeSpan.FromMinutes(6));
        var second = await world.Gateway.ReadStateAsync(device, TestContext.CancellationTokenSource.Token);

        Assert.IsTrue(first.Ok, first.Message);
        Assert.IsTrue(second.Ok, second.Message);
        Assert.AreEqual("https://api.riiotlabs.com/prod/swimming_pool?deleted=false", world.Handler.Requests[1].Url);
        Assert.HasCount(5, world.Handler.Requests, "login, piscinas, sondas, leitura, leitura");
    }

    [TestMethod]
    public async Task Discovery_lists_each_probe_with_its_pool()
    {
        var world = new World();
        world.Handler.Enqueue(Login());
        world.Handler.Enqueue(Json($$"""{"data":[{"swimming_pool_id":"{{Pool}}","name":"Casa"}]}"""));
        world.Handler.Enqueue(Json($$$"""{"data":[{"blue_device_serial":"{{{Serial}}}","blue_device":{"hw_type":"blue_connect_plus"}}]}"""));

        var reply = await world.Gateway.DiscoverAsync(HomeProvider.BlueRiiot, TestContext.CancellationTokenSource.Token);

        Assert.IsTrue(reply.Ok, reply.Message);
        var found = reply.Value.Single();
        Assert.AreEqual(Serial, found.Id);
        Assert.AreEqual("Casa", found.Name);
        Assert.AreEqual("blue_connect_plus", found.ProductName);
        Assert.AreEqual($"pool {Pool}", found.Codes.Single());
    }

    [TestMethod]
    public async Task A_command_is_refused_without_touching_the_cloud()
    {
        var world = new World();

        var reply = await world.Gateway.SendAsync(Analyzer(), HomeCommand.TurnOn, TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(reply.Ok);
        Assert.AreEqual(HomeReplyCodes.Rejected, reply.Code);
        Assert.IsEmpty(world.Handler.Requests);
    }

    public TestContext TestContext { get; set; } = null!;

    private const string Measurements = """
        {
          "status": "OK",
          "last_blue_measure_timestamp": "2026-09-27T11:32:46.000Z",
          "blue_device_serial": "00012345",
          "data": [
            { "name": "orp", "priority": 30, "value": 580, "ok_min": 650, "ok_max": 750, "issuer": "sigfox", "expired": false },
            { "name": "temperature", "priority": 10, "value": 27.43, "ok_min": 20, "ok_max": 30, "issuer": "sigfox", "expired": false },
            { "name": "ph", "priority": 20, "value": 7.1, "ok_min": 7.2, "ok_max": 7.6, "issuer": "strip", "expired": false },
            { "name": "ph", "priority": 20, "value": 7.4189, "ok_min": 7.2, "ok_max": 7.6, "issuer": "sigfox", "expired": false },
            { "name": "salinity", "priority": 40, "value": 3.2, "ok_min": 2.7, "ok_max": 4.5, "issuer": "sigfox", "expired": false },
            { "name": "fcl", "priority": 50, "value": 1.5, "ok_min": 1, "ok_max": 3, "issuer": "strip", "expired": false },
            { "name": "ta", "priority": 60, "value": 90, "ok_min": 80, "ok_max": 120, "issuer": "strip", "expired": true }
          ]
        }
        """;

    private static HomeDevice Analyzer() => new(
        Serial, "Analisador da piscina", "Piscina", HomeDeviceClass.Sensor, HomeProvider.BlueRiiot, [], Slot: Pool);

    private static HttpResponseMessage Login() => Json("""
        {"identity_id":"id","token":"t","credentials":{"access_key":"ASIAKEY","secret_key":"secret","session_token":"token-1","expiration":"2026-09-27T13:00:00.000Z"}}
        """);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class World
    {
        private static readonly HomeCredentials Default = new(HomeProvider.BlueRiiot, "owner@example.com", "s3nha", "eu-west-1");

        public World()
            : this(Default)
        {
        }

        public World(HomeCredentials? credentials)
        {
            Handler = new RecordingHandler();
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
            Client = new BlueRiiotCloudClient(new HttpClient(Handler), new MemoryCredentialStore(credentials), Clock);
            Gateway = new BlueRiiotHomeGateway(Client, Clock);
        }

        public RecordingHandler Handler { get; }

        public FakeTimeProvider Clock { get; }

        public BlueRiiotCloudClient Client { get; }

        public BlueRiiotHomeGateway Gateway { get; }
    }

    private sealed class MemoryCredentialStore(HomeCredentials? initial) : IHomeCredentialStore
    {
        private HomeCredentials? _current = initial;

        public HomeCredentials? Read(HomeProvider provider) => _current?.Provider == provider ? _current : null;

        public void Save(HomeCredentials credentials) => _current = credentials;

        public void Clear(HomeProvider provider) => _current = null;
    }

    public sealed record Recorded(HttpMethod Method, string Url, Dictionary<string, string> Headers, string? Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<Recorded> Requests { get; } = [];

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.Ordinal);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new Recorded(request.Method, request.RequestUri!.ToString(), headers, body));

            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}
