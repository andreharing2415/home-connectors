using System.Text;
using System.Text.Json;
using HomeConnectors;
using HomeConnectors.Adapters;
using Microsoft.Extensions.Time.Testing;

namespace HomeConnectors.Tests;

/// <summary>
/// The Daikin Smart AC envelope, checked against frames built by an independent
/// Python implementation (pycryptodome) — the same one that first read the owner's
/// split on 2026-09-28. Key 00…0f and IV 10…1f; the key on the real module is
/// in the protected store, never here.
/// </summary>
[TestClass]
public sealed class DaikinLocalClientTests
{
    private const string Request = """{"port1":{"power":1,"temperature":22.0}}""";

    private const string RequestReference = "EBESExQVFhcYGRobHB0eH3zcnxuToTJMqnXMYeHj9+GrpA+jCS1lSE4Fk63Ll1oYGrMaXSbUIy8wxMyE";

    // A reply carries one byte (here 0x2a) between the IV and the cipher text.
    private const string ReplyReference =
        "EBESExQVFhcYGRobHB0eHyp83J8bk6EyTKp1zGHh4/fhq6QPowk0b0FbQtv/k8BcGDUTP7FLqZwhQhdtmC46Jmh/HEL8I06PCTPJdIk6HumRICbHm2BCd6xeBzuR1J/PwwADM3tS6bQ1WHZpqvrH2lIZ2yutzZ4Jb0o=";

    private static readonly byte[] Key = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    private static readonly byte[] Iv = [.. Enumerable.Range(16, 16).Select(i => (byte)i)];

    [TestMethod]
    public void The_crc_is_crc16_genibus()
    {
        Assert.AreEqual((ushort)0xD64E, DaikinLocalClient.Crc16("123456789"u8));
    }

    [TestMethod]
    public void A_request_matches_the_reference_byte_for_byte()
    {
        Assert.AreEqual(RequestReference, DaikinLocalClient.Encode(Key, Encoding.UTF8.GetBytes(Request), Iv));
    }

    [TestMethod]
    public void The_reference_reply_opens_to_its_json()
    {
        var plain = DaikinLocalClient.Decode(Key, ReplyReference);

        Assert.IsNotNull(plain);
        StringAssert.StartsWith(Encoding.UTF8.GetString(plain), """{"port1":{"power":1,"mode":3""");
    }

    [TestMethod]
    public void A_reply_with_a_broken_crc_or_the_wrong_key_does_not_open()
    {
        var frame = Convert.FromBase64String(ReplyReference);
        frame[20] ^= 0xFF;

        Assert.IsNull(DaikinLocalClient.Decode(Key, Convert.ToBase64String(frame)));
        Assert.IsNull(DaikinLocalClient.Decode([.. Key.Reverse()], ReplyReference));
    }

    [TestMethod]
    public void Only_a_sixteen_byte_base64_key_parses()
    {
        Assert.IsNotNull(DaikinLocalClient.ParseKey(Convert.ToBase64String(Key)));
        Assert.IsNull(DaikinLocalClient.ParseKey("AAAA"));
        Assert.IsNull(DaikinLocalClient.ParseKey("not base64!"));
        Assert.IsNull(DaikinLocalClient.ParseKey(" "));
    }

    [TestMethod]
    public void A_status_reads_power_setpoint_mode_and_room_temperature_but_not_a_missing_outdoor_probe()
    {
        var plain = DaikinLocalClient.Decode(Key, ReplyReference)!;
        using var document = JsonDocument.Parse(plain);
        var gateway = new DaikinHomeGateway(new DaikinLocalClient(), new NoKeys(), new FakeTimeProvider());

        var state = gateway.State(document.RootElement);

        Assert.IsTrue(state.Online);
        Assert.AreEqual(HomePower.On, state.Power);
        Assert.AreEqual(22.0, state.Temperature);
        Assert.AreEqual("resfriar", state.Status);
        Assert.AreEqual(1, state.Readings.Length);
        Assert.AreEqual("ambiente", state.Readings[0].Label);
        Assert.AreEqual(24.5, state.Readings[0].Celsius);
    }

    [TestMethod]
    public async Task Without_a_key_nothing_goes_out()
    {
        var gateway = new DaikinHomeGateway(new DaikinLocalClient(), new NoKeys(), new FakeTimeProvider());
        var device = new HomeDevice("ar-quarto", "Ar-condicionado", "Quarto", HomeDeviceClass.Climate, HomeProvider.Daikin, [], "power", null, "192.0.2.1", null);

        var reply = await gateway.SendAsync(device, new HomeCommand(HomePower.On, 22), CancellationToken.None);

        Assert.IsFalse(reply.Ok);
        Assert.AreEqual(HomeReplyCodes.NotConfigured, reply.Code);
        Assert.IsFalse(gateway.IsConfigured(HomeProvider.Daikin));
    }

    private sealed class NoKeys : IHomeCredentialStore
    {
        public HomeCredentials? Read(HomeProvider provider) => null;

        public void Save(HomeCredentials credentials) { }

        public void Clear(HomeProvider provider) { }
    }
}
