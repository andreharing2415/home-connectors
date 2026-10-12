using HomeConnectors;
using Microsoft.Extensions.Hosting;

namespace HomeConnectors.Adapters;

public sealed record ConsumptionSamplerOptions
{
    /// <summary>How often the metered devices are read. Short enough to integrate a 10-minute shower well.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Old hours are swept this often.</summary>
    public TimeSpan PurgeInterval { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Reads the house's metered devices on a fixed beat and accumulates what they
/// used since the last read. Nothing in the house keeps a running total, so this
/// is the only place consumption is recorded: the shower's own flow and power are
/// integrated into litres and kWh, and a heater that only reports on/off has its
/// run time recorded and — where a nameplate wattage is set — an estimated kWh.
/// <para>
/// A sample is taken as having held for the whole interval before it, which is
/// what sampling means; the beat is short so a burst (a shower that runs five
/// minutes) is caught in several reads, not one. A read that fails records
/// nothing for that device that tick rather than guessing it was on or off.
/// </para>
/// </summary>
public sealed class ConsumptionSampler : BackgroundService
{
    private readonly HomeCatalog _catalog;
    private readonly IHomeDeviceGateway _gateway;
    private readonly SqliteConsumptionStore _store;
    private readonly TimeProvider _time;
    private readonly ConsumptionSamplerOptions _options;

    private long _lastTick;
    private bool _hasLast;
    private DateTimeOffset _lastPurge;

    public ConsumptionSampler(
        HomeCatalog catalog,
        IHomeDeviceGateway gateway,
        SqliteConsumptionStore store,
        TimeProvider timeProvider,
        ConsumptionSamplerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _catalog = catalog;
        _gateway = gateway;
        _store = store;
        _time = timeProvider;
        _options = options ?? new ConsumptionSamplerOptions();
    }

    /// <summary>The devices worth metering: the shower (measured) and any heater/pump that runs on a switch.</summary>
    public static bool IsMetered(HomeDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.IsCamera || device.IsSensor)
        {
            return false;
        }

        return device.Provider == HomeProvider.Zagonel
            || device.Provider == HomeProvider.Tholz
            || (device.Provider == HomeProvider.Tuya && device.Class == HomeDeviceClass.Climate)
            || device.RatedWatts is > 0;
    }

    /// <summary>How many sample passes have finished, for a test or a health read.</summary>
    public int SampleCount { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var metered = _catalog.All.Where(IsMetered).ToArray();

        if (metered.Length == 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.Interval, _time);

        // The first tick only sets the baseline time; there is no interval to
        // attribute consumption to yet.
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SampleAsync(metered, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass: read each metered device and write the interval's increments. Exposed for tests.</summary>
    public async Task SampleAsync(IReadOnlyList<HomeDevice> metered, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metered);

        var now = _time.GetUtcNow();
        var nowStamp = _time.GetTimestamp();

        if (!_hasLast)
        {
            _hasLast = true;
            _lastTick = nowStamp;
            _lastPurge = now;
            return;
        }

        var elapsed = _time.GetElapsedTime(_lastTick, nowStamp);
        _lastTick = nowStamp;

        // After a sleep or a long GC pause the gap can be minutes; a device cannot
        // have run longer than we watched, so the interval is the ceiling.
        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, _options.Interval.TotalSeconds * 1.5);
        if (seconds <= 0)
        {
            return;
        }

        var hours = seconds / 3600.0;
        var minutes = seconds / 60.0;

        var deltas = new List<ConsumptionDelta>(metered.Count * 2);

        foreach (var device in metered)
        {
            HomeReply<HomeDeviceState> read;

            try
            {
                read = await _gateway.ReadStateAsync(device, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A read that throws tells us nothing about on or off; skip it.
                continue;
            }

            if (!read.Ok || read.Value is not { Online: true } state)
            {
                continue;
            }

            if (device.Provider == HomeProvider.Zagonel)
            {
                AddShower(deltas, device, state, now, minutes, hours);
                continue;
            }

            var running = state.Running ?? state.Power == HomePower.On;
            if (!running)
            {
                continue;
            }

            deltas.Add(new ConsumptionDelta(device.Id, ConsumptionMetric.OnSeconds, seconds, now));

            if (device.RatedWatts is > 0 and var watts)
            {
                deltas.Add(new ConsumptionDelta(device.Id, ConsumptionMetric.EnergyKwhEstimated, watts / 1000.0 * hours, now));
            }
        }

        if (deltas.Count > 0)
        {
            await _store.AccumulateAsync(deltas, cancellationToken).ConfigureAwait(false);
        }

        SampleCount++;

        if (now - _lastPurge >= _options.PurgeInterval)
        {
            _lastPurge = now;
            try
            {
                await _store.PurgeOldAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sweep is harmless; the next one tries again.
            }
        }
    }

    private static void AddShower(List<ConsumptionDelta> deltas, HomeDevice device, HomeDeviceState state, DateTimeOffset now, double minutes, double hours)
    {
        // The shower only reports flow and power while it runs; off, there are no
        // measures and nothing is added.
        if (state.Running != true)
        {
            return;
        }

        foreach (var measure in state.Quantities)
        {
            if (measure.Value is not { } value || value <= 0)
            {
                continue;
            }

            if (measure.Label == "vazão")
            {
                deltas.Add(new ConsumptionDelta(device.Id, ConsumptionMetric.WaterLitres, value * minutes, now));
            }
            else if (measure.Label == "potência")
            {
                deltas.Add(new ConsumptionDelta(device.Id, ConsumptionMetric.EnergyKwh, value / 1000.0 * hours, now));
            }
        }
    }
}
