using System.Collections.Immutable;
using System.Text.Json;
using HomeConnectors.Adapters.Secrets;
using HomeHub.Client;

namespace HomeConnectors.Tests;

/// <summary>The credential-to-secret bridge and the hub's wire contract.</summary>
[TestClass]
public sealed class SecretsAndWireTests
{
    [TestMethod]
    public void Credentials_round_trip_through_a_secret_store_one_entry_per_provider()
    {
        var secrets = new MemorySecrets();
        var store = new SecretStoreCredentialStore(secrets);

        store.Save(new HomeCredentials(HomeProvider.Daikin, " device-1 ", "a2V5", "192.0.2.22"));

        var back = store.Read(HomeProvider.Daikin);
        Assert.IsNotNull(back);
        Assert.AreEqual("device-1", back.AccessId, "trimmed on the way in");
        Assert.AreEqual("a2V5", back.Secret);
        Assert.AreEqual("192.0.2.22", back.Region);
        Assert.IsNull(store.Read(HomeProvider.Tuya), "another provider has its own entry");
        CollectionAssert.AreEquivalent(new[] { "credentials/Daikin" }, secrets.Values.Keys.ToArray());

        store.Clear(HomeProvider.Daikin);
        Assert.IsNull(store.Read(HomeProvider.Daikin));
    }

    [TestMethod]
    public void A_secret_that_does_not_parse_reads_as_no_keys()
    {
        var secrets = new MemorySecrets();
        secrets.Write("credentials/Tuya", "not json");

        Assert.IsNull(new SecretStoreCredentialStore(secrets).Read(HomeProvider.Tuya));
    }

    [TestMethod]
    public void The_vault_store_refuses_names_that_would_leave_its_folder()
    {
        using var store = new OpenBaoSecretStore(new Uri("http://127.0.0.1:1/"), () => "t", "home", "house");

        Assert.ThrowsExactly<ArgumentException>(() => store.Read("../other/secret"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Read("/root"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Read("a b"));
    }

    [TestMethod]
    public void A_vault_that_is_down_reads_as_no_secret()
    {
        // Port 1 on loopback refuses at once: the adapter must see "not
        // configured", never an exception from the store.
        using var store = new OpenBaoSecretStore(new Uri("http://127.0.0.1:1/"), () => "t", "home", "house");

        Assert.IsNull(store.Read("credentials/Tuya"));
    }

    [TestMethod]
    public void A_state_with_unset_arrays_crosses_the_wire_and_comes_back_empty()
    {
        var state = new HomeDeviceState(true, HomePower.On, 38.5, DateTimeOffset.UnixEpoch);
        var reply = HomeReply.Success(state);

        var json = JsonSerializer.Serialize(reply, HubWire.Json);
        var back = JsonSerializer.Deserialize<HomeReply<HomeDeviceState>>(json, HubWire.Json);

        StringAssert.Contains(json, "\"power\":\"On\"", "enums travel as names");
        Assert.IsNotNull(back?.Value);
        Assert.AreEqual(HomePower.On, back.Value.Power);
        Assert.AreEqual(38.5, back.Value.Temperature);
        Assert.IsFalse(back.Value.Sensors.IsDefault, "an absent array comes back empty, not default");
        Assert.IsEmpty(back.Value.Readings);
    }

    [TestMethod]
    public void A_full_state_survives_the_round_trip()
    {
        var state = new HomeDeviceState(
            true,
            HomePower.Off,
            null,
            DateTimeOffset.UnixEpoch,
            [new HomeSensor("coletor", 41.2)],
            "aquecendo",
            [new HomeGauge("bateria", 80)],
            [new HomeMeasure("pH", 7.4, string.Empty, 7.2, 7.6)],
            Running: true);

        var back = JsonSerializer.Deserialize<HomeReply<HomeDeviceState>>(
            JsonSerializer.Serialize(HomeReply.Success(state), HubWire.Json), HubWire.Json)!.Value!;

        Assert.AreEqual("coletor", back.Readings.Single().Label);
        Assert.AreEqual(80, back.Levels.Single().Percent);
        Assert.AreEqual(0, back.Quantities.Single().Verdict);
        Assert.IsTrue(back.Running);
        Assert.AreEqual("aquecendo", back.Status);
    }

    [TestMethod]
    public void A_command_and_a_failure_cross_the_wire()
    {
        var command = JsonSerializer.Deserialize<HomeCommand>(
            JsonSerializer.Serialize(new HomeCommand(HomePower.On, 40, VacuumCleanMode.Mop), HubWire.Json), HubWire.Json);
        var failure = JsonSerializer.Deserialize<HomeReply<bool>>(
            JsonSerializer.Serialize(HomeReply.Failure<bool>(HomeReplyCodes.Offline, "fora"), HubWire.Json), HubWire.Json);

        Assert.AreEqual(new HomeCommand(HomePower.On, 40, VacuumCleanMode.Mop), command);
        Assert.IsFalse(failure!.Ok);
        Assert.AreEqual(HomeReplyCodes.Offline, failure.Code);
    }

    [TestMethod]
    public void The_catalog_crosses_the_wire_with_aliases()
    {
        ImmutableArray<HomeDevice> devices =
        [
            new HomeDevice("dev-1", "Luz", "Sala", HomeDeviceClass.Light, HomeProvider.Tuya, ["luz da sala"]),
        ];

        var back = JsonSerializer.Deserialize<HomeReply<ImmutableArray<HomeDevice>>>(
            JsonSerializer.Serialize(HomeReply.Success(devices), HubWire.Json), HubWire.Json)!.Value;

        Assert.AreEqual("luz da sala", back.Single().Aliases.Single());
        Assert.AreEqual(HomeProvider.Tuya, back.Single().Provider);
    }

    private sealed class MemorySecrets : IHomeSecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public string? Read(string name) => Values.GetValueOrDefault(name);

        public void Write(string name, string value) => Values[name] = value;

        public void Delete(string name) => Values.Remove(name);
    }
}
