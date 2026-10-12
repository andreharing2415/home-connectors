using System.Collections.Immutable;
using System.Globalization;

namespace HomeConnectors;

/// <summary>Which cloud answers for a device.</summary>
public enum HomeProvider
{
    /// <summary>Tuya IoT Platform — the cloud behind Smart Life and most white-label apps.</summary>
    Tuya = 0,

    /// <summary>
    /// Google's Smart Device Management API. Declared so a configuration can
    /// name it, refused by the gateway until an adapter exists: Google publishes
    /// no server API for third-party devices, only for Nest hardware.
    /// </summary>
    GoogleNest,

    /// <summary>
    /// The Tholz Casa Inteligente cloud (Smart Connect module — pool heater,
    /// filtration, lighting). Declared so a configuration can name it, refused
    /// by the gateway until an adapter exists: Tholz publishes no documented
    /// public API, only its own app and Alexa/Google Assistant skills.
    /// </summary>
    Tholz,

    /// <summary>
    /// A Hikvision NVR on the local network, reached directly — no cloud, no
    /// account. <see cref="HomeCredentials.AccessId"/> is the NVR username,
    /// <see cref="HomeCredentials.Secret"/> its password, and
    /// <see cref="HomeCredentials.Region"/> its host (and, after a colon, its
    /// ISAPI port when it is not 80).
    /// </summary>
    Hikvision,

    /// <summary>
    /// A Xiaomi (Mi Home) device on the local network, reached directly over the
    /// miIO UDP protocol on port 54321 — no cloud at run time. The device
    /// encrypts every exchange with its own 128-bit token, which Mi Home never
    /// shows and the device no longer hands out on the LAN; it is copied once
    /// out of the Xiaomi account. <see cref="HomeCredentials.AccessId"/> is the
    /// device id (did), <see cref="HomeCredentials.Secret"/> the token in hex,
    /// and <see cref="HomeCredentials.Region"/> the device's host.
    /// </summary>
    Xiaomi,

    /// <summary>
    /// A Blue Riiot Blue Connect pool analyzer, read through the Blue Riiot
    /// cloud — the only place its readings land. The probe in the water talks
    /// Sigfox and Bluetooth to its Blue Extender, and the extender only ever
    /// connects out: nothing on the LAN answers. <see cref="HomeCredentials.AccessId"/>
    /// is the Blue Riiot account e-mail, <see cref="HomeCredentials.Secret"/> its
    /// password, and <see cref="HomeCredentials.Region"/> the AWS region of the
    /// API (<c>eu-west-1</c>). Read-only: the probe measures, it switches nothing.
    /// </summary>
    BlueRiiot,

    /// <summary>
    /// A Daikin split with the Brazilian Wi-Fi module (app Daikin Smart AC),
    /// reached directly on the LAN at port 15914 — no cloud at run time. Every
    /// body is AES-128-CFB under the device's own key, which the app hands out
    /// in Menu → Integrações → Home Assistant. <see cref="HomeCredentials.AccessId"/>
    /// is the device id on the list, <see cref="HomeCredentials.Secret"/> the key
    /// in base64, and <see cref="HomeCredentials.Region"/> the module's IP.
    /// </summary>
    Daikin,

    /// <summary>
    /// The GoodWe solar inverter, read on the LAN over Modbus TCP (port 502 of
    /// its Wi-Fi dongle) — no cloud, no keys. <see cref="HomeDevice.Host"/> is
    /// the dongle's IP. Read-only: it reports what the roof generates and, with a
    /// GoodWe smart meter wired in, what goes to the grid and what the house uses.
    /// </summary>
    GoodWe,

    /// <summary>
    /// A Zagonel Smart Banho digital shower, reached on the vendor's own MQTT
    /// broker — open on the internet with no login, addressed only by the shower's
    /// cloud id. <see cref="HomeDevice.Id"/> is that id (the Mongo id the app's
    /// API hands out, not the MAC the display shows). A <see cref="HomeDeviceClass.Climate"/>
    /// device: it carries its setpoint in the active preset, with no separate code,
    /// and the hub only sets that target — no cloud command opens the valve.
    /// </summary>
    Zagonel,
}

/// <summary>
/// What a device is, in the terms that decide how the hub treats it. Everything
/// but <see cref="Access"/> and <see cref="Lock"/> is a reversible switch; those
/// two open the house, so a client should confirm before sending them.
/// </summary>
public enum HomeDeviceClass
{
    Light = 0,
    Switch,

    /// <summary>A pump: the waterfall, the pool filter.</summary>
    Pump,

    /// <summary>Air conditioning. Power plus a setpoint.</summary>
    Climate,

    /// <summary>A camera or a doorbell with one. Observed, never switched.</summary>
    Camera,

    /// <summary>
    /// A curtain or blind motor. Power still means the one thing an On/Off
    /// command can mean — <see cref="HomePower.On"/> opens, <see cref="HomePower.Off"/>
    /// closes — but the vendor code that carries it is a string command
    /// (<c>open</c>/<c>close</c>), never a boolean, and what is read back is a
    /// position, not a switch.
    /// </summary>
    Curtain,

    /// <summary>Gate, garage door, a relay that opens the house. Sensitive by catalog.</summary>
    Access,

    /// <summary>
    /// A smart lock with its own unlock protocol — not a relay. On unlocks (the
    /// vendor's remote, password-free unlock); Off is refused, a lock re-locks
    /// itself. As sensitive as <see cref="Access"/>: every unlock is confirmed.
    /// </summary>
    Lock,

    /// <summary>
    /// A robot vacuum. On starts a whole-house clean; Off sends it back to its
    /// base — never "stop where you are", which would leave it in the middle of
    /// a room. What is read back is a working state, the battery and how much
    /// life the brushes, filter and mop have left.
    /// </summary>
    Vacuum,

    /// <summary>
    /// A probe that only measures — the pool's water analyzer. Read, never
    /// switched: there is no power to turn on or off, and a request to do so
    /// is refused before it reaches any gateway.
    /// </summary>
    Sensor,
}

/// <summary>
/// One device the owner allowed the hub to touch. Configuration, not discovery: a
/// device the cloud reports but this list does not carry is refused.
/// </summary>
/// <param name="Id">The vendor cloud's device id. Case-sensitive, opaque.</param>
/// <param name="DisplayName">What the owner is told and what the audit names.</param>
/// <param name="Room">Where it is, for the panel and for phrases like "luz da piscina".</param>
/// <param name="Class">What it is, which decides what may be done to it.</param>
/// <param name="Provider">Which cloud answers for it.</param>
/// <param name="Aliases">Other things the owner calls it. Matched whole, after folding.</param>
/// <param name="PowerCode">The vendor's data point for on/off — <c>switch_1</c>, <c>switch_led</c>, <c>switch</c>.</param>
/// <param name="TemperatureCode">The vendor's data point for a setpoint, when the device has one.</param>
/// <param name="Host">The module's address on the LAN, for a provider spoken to locally (Tholz). Null for a cloud device.</param>
/// <param name="Slot">The slot key inside the local module — <c>heat0</c>, <c>out1</c>. Tholz addresses one physical output by this, not by <see cref="Id"/>.</param>
/// <param name="MinTemperature">The lowest setpoint this device accepts, when it is not the air-conditioner default. A pool heater goes far past 30 °C.</param>
/// <param name="MaxTemperature">The highest setpoint this device accepts, when it is not the air-conditioner default.</param>
/// <param name="RatedWatts">The device's nameplate electrical power, when it has one worth metering. Lets the sampler estimate kWh from run time for a device the hardware does not meter (a heat pump, an electric backup). Null for a device the hub does not estimate, or one that measures its own power (the shower).</param>
public sealed record HomeDevice(
    string Id,
    string DisplayName,
    string Room,
    HomeDeviceClass Class,
    HomeProvider Provider,
    ImmutableArray<string> Aliases,
    string PowerCode = "switch_1",
    string? TemperatureCode = null,
    string? Host = null,
    string? Slot = null,
    int? MinTemperature = null,
    int? MaxTemperature = null,
    int? RatedWatts = null)
{
    /// <summary>A device that opens the house — a gate, a garage, a lock. Clients confirm before acting on it.</summary>
    public bool IsSensitive => Class is HomeDeviceClass.Access or HomeDeviceClass.Lock;

    public bool IsCamera => Class == HomeDeviceClass.Camera;

    /// <summary>A measuring probe: it answers questions and takes no command.</summary>
    public bool IsSensor => Class == HomeDeviceClass.Sensor;

    /// <summary>
    /// A cloud device names its setpoint data point; a Tholz climate device carries
    /// its setpoint in the heating slot, with no separate code. Either way, a
    /// climate device has one and a lamp does not.
    /// </summary>
    public bool HasSetpoint => !string.IsNullOrWhiteSpace(TemperatureCode)
        || (Provider is (HomeProvider.Tholz or HomeProvider.Daikin or HomeProvider.Zagonel) && Class == HomeDeviceClass.Climate);

    /// <summary>The lowest setpoint the executor will send, per device or the air-conditioner default.</summary>
    public int MinSetpoint => MinTemperature ?? HomeCommand.MinimumTemperature;

    /// <summary>The highest setpoint the executor will send, per device or the air-conditioner default.</summary>
    public int MaxSetpoint => MaxTemperature ?? HomeCommand.MaximumTemperature;
}

/// <summary>The devices the hub may touch, and nothing else.</summary>
public sealed class HomeCatalog
{
    public static HomeCatalog Empty { get; } = new([]);

    private readonly ImmutableDictionary<string, HomeDevice> _byId;

    public HomeCatalog(ImmutableArray<HomeDevice> devices)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, HomeDevice>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id) || string.IsNullOrWhiteSpace(device.DisplayName))
            {
                throw new ArgumentException("Every device needs an id and a display name.", nameof(devices));
            }

            // Two entries for one id would make "which one did the owner mean" a
            // question with no answer, so it is refused at construction.
            builder.Add(device.Id.Trim(), device);
        }

        _byId = builder.ToImmutable();
        All =
        [
            .. _byId.Values
                .OrderBy(d => d.Room, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    public ImmutableArray<HomeDevice> All { get; }

    public bool IsEmpty => All.IsEmpty;

    /// <summary>Exact id or nothing. Never a substring, never a guess.</summary>
    public HomeDevice? Find(string? id) =>
        id is not null && _byId.TryGetValue(id.Trim(), out var device) ? device : null;
}

/// <summary>On or off. There is no third state a spoken command produces.</summary>
public enum HomePower
{
    Off = 0,
    On,
}

/// <summary>
/// What a robot vacuum does on its way round: "aspira" only vacuums, "passa
/// pano" only mops, "limpa" does both. The values are the robot's own.
/// </summary>
public enum VacuumCleanMode
{
    Sweep = 0,
    Mop = 1,
    SweepAndMop = 2,
}

/// <summary>
/// What is asked of a device. Power is always present; a setpoint only for
/// climate devices, and only inside the range the hub is willing to set; a
/// clean mode only for a vacuum being sent out — none means both.
/// </summary>
public sealed record HomeCommand(HomePower Power, int? Temperature = null, VacuumCleanMode? Clean = null)
{
    /// <summary>16 °C to 30 °C: what a bedroom air conditioner actually accepts.</summary>
    public const int MinimumTemperature = 16;

    public const int MaximumTemperature = 30;

    public static HomeCommand TurnOn { get; } = new(HomePower.On);

    public static HomeCommand TurnOff { get; } = new(HomePower.Off);

    public static HomeCommand SetTemperature(int celsius) => new(HomePower.On, celsius);

    public static HomeCommand CleanWith(VacuumCleanMode mode) => new(HomePower.On, Clean: mode);

    /// <summary>A whole-house clean in the owner's words: "aspirar a casa", "passar pano na casa", "limpar a casa".</summary>
    private static string CleanPhrase(VacuumCleanMode? mode) => mode switch
    {
        VacuumCleanMode.Sweep => "aspirar a casa",
        VacuumCleanMode.Mop => "passar pano na casa",
        _ => "limpar a casa",
    };

    public string Describe(HomeDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Class == HomeDeviceClass.Vacuum)
        {
            return Power == HomePower.On
                ? string.Create(CultureInfo.CurrentCulture, $"{device.DisplayName} {CleanPhrase(Clean)}")
                : string.Create(CultureInfo.CurrentCulture, $"{device.DisplayName} voltar para a base");
        }

        var verb = device.Class switch
        {
            HomeDeviceClass.Curtain => Power == HomePower.On ? "Abrir" : "Fechar",
            HomeDeviceClass.Lock => Power == HomePower.On ? "Destrancar" : "Trancar",
            _ => Power == HomePower.On ? "Ligar" : "Desligar",
        };

        return Temperature is { } t
            ? string.Create(CultureInfo.CurrentCulture, $"{verb} {device.DisplayName} em {t} °C")
            : string.Create(CultureInfo.CurrentCulture, $"{verb} {device.DisplayName}");
    }
}

/// <summary>One named temperature a device measures. A collector, a reservoir, the consumption line.</summary>
/// <param name="Label">What the sensor reads, in the owner's words: "coletor", "consumo".</param>
/// <param name="Celsius">The reading in °C, with tenths when the device reports them, or null when the sensor itself reports a fault.</param>
public sealed record HomeSensor(string Label, double? Celsius);

/// <summary>One named percentage a device reports. The battery, what is left of a filter.</summary>
/// <param name="Label">What it measures, in the owner's words: "bateria", "filtro".</param>
/// <param name="Percent">0 to 100.</param>
public sealed record HomeGauge(string Label, int Percent);

/// <summary>
/// One named reading that is neither a temperature nor a percentage — the pH of
/// the pool, its ORP, its salt — with the unit it is spoken in and the range the
/// vendor calls ideal.
/// </summary>
/// <param name="Label">What it measures, in the owner's words: "pH", "ORP", "sal".</param>
/// <param name="Value">The reading, or null when the probe has none right now.</param>
/// <param name="Unit">"mV", "g/L", "ppm" — empty for pH, which has none.</param>
/// <param name="IdealMin">The low end of the range the vendor calls ideal, when it says.</param>
/// <param name="IdealMax">The high end of the same range.</param>
public sealed record HomeMeasure(string Label, double? Value, string Unit, double? IdealMin = null, double? IdealMax = null)
{
    /// <summary>Below, inside or above the ideal range; null when either the value or the range is missing.</summary>
    public int? Verdict => Value is not { } v || IdealMin is not { } lo || IdealMax is not { } hi
        ? null
        : v < lo ? -1 : v > hi ? 1 : 0;
}

/// <summary>What the cloud says a device is doing right now.</summary>
/// <param name="Online">Whether the cloud can reach it at all.</param>
/// <param name="Power">The switch data point, when the device reports one.</param>
/// <param name="Temperature">The setpoint, when the device reports one — in °C, with tenths when the device keeps them.</param>
/// <param name="ObservedAt">When the reading was taken.</param>
/// <param name="Sensors">The temperatures the device measures, named. Empty for a device that reports none.</param>
/// <param name="Status">What the device says it is doing, in the owner's words, when on/off does not say enough — "carregando na base", "passando pano". Null for a device that has only a switch.</param>
/// <param name="Gauges">The percentages the device reports, named. Empty for a device that reports none.</param>
/// <param name="Measures">The readings with a unit of their own — pH, ORP, salt. Empty for a device that reports none.</param>
/// <param name="Running">
/// Whether the device is working this minute, when that differs from being on:
/// a heating in automatic is on all day and runs its pump only when the collector
/// is hotter than the tank. Null for a device that does not say.
/// </param>
public sealed record HomeDeviceState(
    bool Online,
    HomePower? Power,
    double? Temperature,
    DateTimeOffset ObservedAt,
    ImmutableArray<HomeSensor> Sensors = default,
    string? Status = null,
    ImmutableArray<HomeGauge> Gauges = default,
    ImmutableArray<HomeMeasure> Measures = default,
    bool? Running = null)
{
    /// <summary>The readings with a unit, never the uninitialized array.</summary>
    public ImmutableArray<HomeMeasure> Quantities => Measures.IsDefault ? [] : Measures;

    /// <summary>The named readings, never the uninitialized array.</summary>
    public ImmutableArray<HomeSensor> Readings => Sensors.IsDefault ? [] : Sensors;

    /// <summary>The named percentages, never the uninitialized array.</summary>
    public ImmutableArray<HomeGauge> Levels => Gauges.IsDefault ? [] : Gauges;
}

/// <summary>A live stream the cloud handed out, and how long it is good for.</summary>
public sealed record CameraFeed(Uri Url, string Protocol, DateTimeOffset ExpiresAt);

/// <summary>
/// One still frame off a camera, as JPEG bytes.
/// <para>
/// It exists to be looked at once — by the local vision model that answers
/// "quem está na porta" — and then dropped. It never reaches a receipt, an
/// audit line or the disk: a frame of the front door is the most private thing
/// the house produces, and the receipt is read in the clear.
/// </para>
/// </summary>
public sealed record CameraFrame(ReadOnlyMemory<byte> Jpeg, DateTimeOffset TakenAt);

/// <summary>A device the cloud reports on the owner's account. For filling in the allowlist, never for acting.</summary>
public sealed record DiscoveredDevice(
    string Id,
    string Name,
    string Category,
    string ProductName,
    bool Online,
    ImmutableArray<string> Codes);

/// <summary>
/// One answer from a vendor cloud. Failure is data here: an HTTP fault, a
/// refused signature and an offline device are all things the verifier has to
/// reason about, not exceptions to be caught somewhere upstream.
/// </summary>
public sealed record HomeReply<T>(bool Ok, T? Value, string Code, string Message);

/// <summary>Factories for <see cref="HomeReply{T}"/>, kept off the generic type so inference does the typing.</summary>
public static class HomeReply
{
    public static HomeReply<T> Success<T>(T value) => new(true, value, string.Empty, string.Empty);

    public static HomeReply<T> Failure<T>(string code, string message) => new(false, default, code, message);
}

/// <summary>Failure codes every gateway shares, so the executor can speak about them without knowing the vendor.</summary>
public static class HomeReplyCodes
{
    public const string NotConfigured = "home.not-configured";
    public const string Unauthorized = "home.unauthorized";
    public const string Unreachable = "home.cloud-unreachable";
    public const string Offline = "home.device-offline";
    public const string Rejected = "home.command-rejected";
    public const string NoStream = "home.no-stream";
    public const string NoFrame = "home.no-frame";
    public const string ProviderUnsupported = "home.provider-unsupported";
}

/// <summary>
/// The narrow door to a vendor cloud. It takes a <see cref="HomeDevice"/> from
/// the catalog, never an id off a request, so nothing that is not on the
/// allowlist has a way of reaching the network.
/// </summary>
public interface IHomeDeviceGateway
{
    /// <summary>True when credentials exist for this provider. Says nothing about whether they work.</summary>
    bool IsConfigured(HomeProvider provider);

    Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken);

    Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken);

    Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken);

    /// <summary>
    /// One frame of what the camera is seeing right now. Separate from
    /// <see cref="AllocateStreamAsync"/> because it answers a different
    /// question: the stream is for watching, the frame is for reading — it is
    /// what the vision model is given when the owner asks who is at the door.
    /// </summary>
    Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken);

    /// <summary>Everything the account can see. A discovery aid for configuration, not a path to an action.</summary>
    Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken);
}

/// <summary>The keys to a vendor cloud. Stored protected, read only by the gateway.</summary>
/// <param name="Provider">Which cloud — or, for <see cref="HomeProvider.Hikvision"/>, which NVR.</param>
/// <param name="AccessId">Tuya calls it Access ID / client_id; for Hikvision it is the NVR username.</param>
/// <param name="Secret">Tuya calls it Access Secret; for Hikvision it is the NVR password.</param>
/// <param name="Region">
/// For Tuya, the data-center key: <c>us</c>, <c>us-e</c>, <c>eu</c>, <c>eu-w</c>, <c>cn</c>, <c>in</c>, <c>sg</c>.
/// For Hikvision, the NVR's host, and after a colon its ISAPI port when it is not 80 (e.g. <c>192.168.1.10</c> or <c>192.168.1.10:8080</c>).
/// </param>
public sealed record HomeCredentials(HomeProvider Provider, string AccessId, string Secret, string Region)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(AccessId)
        && !string.IsNullOrWhiteSpace(Secret)
        && !string.IsNullOrWhiteSpace(Region);
}

/// <summary>Where the keys live between runs. Never logs, never returns them anywhere but to the gateway.</summary>
public interface IHomeCredentialStore
{
    HomeCredentials? Read(HomeProvider provider);

    void Save(HomeCredentials credentials);

    void Clear(HomeProvider provider);
}

/// <summary>
/// Named secrets that outlive a run — a cloud session, a provider's keys — kept
/// somewhere the hub trusts: a vault, a protected file. Values are opaque text;
/// a store never logs them and never hands them to anything but the adapter
/// that asked.
/// </summary>
public interface IHomeSecretStore
{
    /// <summary>The value under <paramref name="name"/>, or null when there is none or it cannot be read.</summary>
    string? Read(string name);

    void Write(string name, string value);

    void Delete(string name);
}
