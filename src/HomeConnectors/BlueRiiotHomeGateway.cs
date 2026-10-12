using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// A Blue Connect pool analyzer, seen through <see cref="IHomeDeviceGateway"/>
/// and read through <see cref="BlueRiiotCloudClient"/>.
/// <para>
/// The device's <see cref="HomeDevice.Id"/> is the probe's serial; its
/// <see cref="HomeDevice.Slot"/> may carry the pool id, and when it does not,
/// the pool is found once by walking the account's pools. The water
/// temperature becomes a <see cref="HomeSensor"/> labelled "água", so the
/// opening briefing's pool verdict reads it like any other thermometer by the
/// pool; pH, ORP and salt become <see cref="HomeMeasure"/>s with the ideal range
/// the cloud sends alongside.
/// </para>
/// <para>
/// Read-only. The probe measures about once an hour and the panel reads far
/// more often than that, so one reading is kept for a few minutes rather than
/// asking the cloud for the same numbers again.
/// </para>
/// </summary>
public sealed class BlueRiiotHomeGateway(BlueRiiotCloudClient client, TimeProvider clock) : IHomeDeviceGateway
{
    private static readonly TimeSpan ReadingLife = TimeSpan.FromMinutes(5);

    // The API's measure names, in the owner's words, with their unit. Temperature is
    // not here: it is a HomeSensor, read as °C everywhere else in the house.
    private static readonly ImmutableDictionary<string, (string Label, string Unit)> Names =
        ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, new Dictionary<string, (string, string)>
        {
            ["ph"] = ("pH", string.Empty),
            ["orp"] = ("ORP", "mV"),
            ["salinity"] = ("sal", "g/L"),
            ["conductivity"] = ("condutividade", "µS/cm"),
            ["fcl"] = ("cloro livre", "ppm"),
            ["fbr"] = ("bromo livre", "ppm"),
            ["ta"] = ("alcalinidade", "ppm"),
            ["cya"] = ("estabilizante", "ppm"),
            ["th"] = ("dureza", "ppm"),
        });

    private readonly BlueRiiotCloudClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ConcurrentDictionary<string, string> _pools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (long Stamp, HomeDeviceState State)> _readings = new(StringComparer.Ordinal);

    public bool IsConfigured(HomeProvider provider) => provider == HomeProvider.BlueRiiot && _client.IsConfigured;

    public async Task<HomeReply<HomeDeviceState>> ReadStateAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Provider != HomeProvider.BlueRiiot)
        {
            return HomeReply.Failure<HomeDeviceState>(HomeReplyCodes.ProviderUnsupported, device.Provider.ToString());
        }

        var serial = device.Id.Trim();

        if (_readings.TryGetValue(serial, out var kept) && _clock.GetElapsedTime(kept.Stamp) < ReadingLife)
        {
            return HomeReply.Success(kept.State);
        }

        var pool = await PoolAsync(device, cancellationToken).ConfigureAwait(false);

        if (!pool.Ok)
        {
            return HomeReply.Failure<HomeDeviceState>(pool.Code, pool.Message);
        }

        var reply = await _client.GetAsync(
            string.Create(CultureInfo.InvariantCulture,
                $"swimming_pool/{Uri.EscapeDataString(pool.Value!)}/blue/{Uri.EscapeDataString(serial)}/lastMeasurements?mode=blue_and_strip"),
            cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            // A pool id that no longer answers is found again next time.
            if (reply.Code == HomeReplyCodes.Rejected)
            {
                _pools.TryRemove(serial, out _);
            }

            return HomeReply.Failure<HomeDeviceState>(reply.Code, reply.Message);
        }

        var state = Parse(reply.Value, _clock.GetUtcNow());
        _readings[serial] = (_clock.GetTimestamp(), state);

        return HomeReply.Success(state);
    }

    /// <summary>
    /// The readings in a <c>lastMeasurements</c> reply. The probe's own reading
    /// wins over a test strip's for the same quantity; a strip reading the
    /// cloud marks expired is weeks old and is left out.
    /// </summary>
    internal static HomeDeviceState Parse(JsonElement reply, DateTimeOffset now)
    {
        var chosen = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        if (reply.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var measure in data.EnumerateArray())
            {
                if (BlueRiiotCloudClient.Text(measure, "name") is not { Length: > 0 } name)
                {
                    continue;
                }

                var strip = string.Equals(BlueRiiotCloudClient.Text(measure, "issuer"), "strip", StringComparison.OrdinalIgnoreCase);

                if (strip && measure.TryGetProperty("expired", out var expired) && expired.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (!chosen.TryGetValue(name, out var already)
                    || (string.Equals(BlueRiiotCloudClient.Text(already, "issuer"), "strip", StringComparison.OrdinalIgnoreCase) && !strip))
                {
                    chosen[name] = measure;
                }
            }
        }

        var sensors = ImmutableArray.CreateBuilder<HomeSensor>();
        var measures = ImmutableArray.CreateBuilder<HomeMeasure>();

        // The API's own priority orders the list; the dictionary does not keep it.
        foreach (var (name, measure) in chosen.OrderBy(m => Number(m.Value, "priority") ?? double.MaxValue))
        {
            var value = Number(measure, "value");

            if (string.Equals(name, "temperature", StringComparison.OrdinalIgnoreCase))
            {
                sensors.Add(new HomeSensor("água", value is { } c ? Math.Round(c, 1) : null));
                continue;
            }

            if (!Names.TryGetValue(name, out var known))
            {
                continue;
            }

            measures.Add(new HomeMeasure(
                known.Label,
                value is { } v ? Math.Round(v, name.Equals("ph", StringComparison.OrdinalIgnoreCase) ? 2 : 1) : null,
                known.Unit,
                Number(measure, "ok_min"),
                Number(measure, "ok_max")));
        }

        var observed = BlueRiiotCloudClient.Text(reply, "last_blue_measure_timestamp") is { } stamp
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : now;

        // A probe that has not reported for hours is not "offline" — it sleeps
        // between readings — but its numbers are old, and that is said.
        var status = now - observed > TimeSpan.FromHours(3)
            ? string.Create(CultureInfo.CurrentCulture, $"última medição há {Age(now - observed)}")
            : null;

        return new HomeDeviceState(
            Online: sensors.Count + measures.Count > 0,
            Power: null,
            Temperature: null,
            observed,
            sensors.ToImmutable(),
            status,
            Measures: measures.ToImmutable());
    }

    public Task<HomeReply<bool>> SendAsync(HomeDevice device, HomeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Task.FromResult(HomeReply.Failure<bool>(HomeReplyCodes.Rejected,
            string.Create(CultureInfo.CurrentCulture, $"{device.DisplayName} só mede; não há o que ligar ou desligar.")));
    }

    public Task<HomeReply<CameraFeed>> AllocateStreamAsync(HomeDevice device, CancellationToken cancellationToken) =>
        Task.FromResult(HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream, "Um analisador de piscina não tem câmera."));

    public Task<HomeReply<CameraFrame>> CaptureFrameAsync(HomeDevice device, CancellationToken cancellationToken) =>
        Task.FromResult(HomeReply.Failure<CameraFrame>(HomeReplyCodes.NoFrame, "Um analisador de piscina não tem câmera."));

    /// <summary>Every probe on the account, one per pool, with the serial to copy into <c>home.json</c> and the pool id beside it.</summary>
    public async Task<HomeReply<ImmutableArray<DiscoveredDevice>>> DiscoverAsync(HomeProvider provider, CancellationToken cancellationToken)
    {
        if (provider != HomeProvider.BlueRiiot)
        {
            return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(HomeReplyCodes.ProviderUnsupported, provider.ToString());
        }

        var pools = await _client.GetAsync("swimming_pool?deleted=false", cancellationToken).ConfigureAwait(false);

        if (!pools.Ok)
        {
            return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(pools.Code, pools.Message);
        }

        var found = ImmutableArray.CreateBuilder<DiscoveredDevice>();

        foreach (var pool in Items(pools.Value))
        {
            if (BlueRiiotCloudClient.Text(pool, "swimming_pool_id") is not { Length: > 0 } poolId)
            {
                continue;
            }

            var poolName = BlueRiiotCloudClient.Text(pool, "name") ?? "Piscina";
            var blues = await _client.GetAsync(
                string.Create(CultureInfo.InvariantCulture, $"swimming_pool/{Uri.EscapeDataString(poolId)}/blue"),
                cancellationToken).ConfigureAwait(false);

            if (!blues.Ok)
            {
                return HomeReply.Failure<ImmutableArray<DiscoveredDevice>>(blues.Code, blues.Message);
            }

            foreach (var blue in Items(blues.Value))
            {
                if (BlueRiiotCloudClient.Text(blue, "blue_device_serial") is not { Length: > 0 } serial)
                {
                    continue;
                }

                var hardware = blue.TryGetProperty("blue_device", out var detail)
                    ? BlueRiiotCloudClient.Text(detail, "hw_type") ?? string.Empty
                    : string.Empty;

                _pools[serial] = poolId;
                found.Add(new DiscoveredDevice(serial, poolName, "Blue Connect", hardware, Online: true, [string.Create(CultureInfo.InvariantCulture, $"pool {poolId}")]));
            }
        }

        return HomeReply.Success(found.ToImmutable());
    }

    /// <summary>The pool the probe sits in: from the list when it says, else found once and remembered.</summary>
    private async Task<HomeReply<string>> PoolAsync(HomeDevice device, CancellationToken cancellationToken)
    {
        if (device.Slot is { Length: > 0 } configured)
        {
            return HomeReply.Success(configured.Trim());
        }

        var serial = device.Id.Trim();

        if (_pools.TryGetValue(serial, out var known))
        {
            return HomeReply.Success(known);
        }

        var discovered = await DiscoverAsync(HomeProvider.BlueRiiot, cancellationToken).ConfigureAwait(false);

        if (!discovered.Ok)
        {
            return HomeReply.Failure<string>(discovered.Code, discovered.Message);
        }

        return _pools.TryGetValue(serial, out var found)
            ? HomeReply.Success(found)
            : HomeReply.Failure<string>(HomeReplyCodes.Offline,
                string.Create(CultureInfo.CurrentCulture, $"A conta da Blue Riiot não tem o analisador {serial} em nenhuma piscina."));
    }

    private static JsonElement[] Items(JsonElement reply) =>
        reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? [.. data.EnumerateArray()]
            : reply.ValueKind == JsonValueKind.Array ? [.. reply.EnumerateArray()] : [];

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static string Age(TimeSpan age) => age.TotalHours < 48
        ? string.Create(CultureInfo.CurrentCulture, $"{(int)age.TotalHours} horas")
        : string.Create(CultureInfo.CurrentCulture, $"{(int)age.TotalDays} dias");
}
