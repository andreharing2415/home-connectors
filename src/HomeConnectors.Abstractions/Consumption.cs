using System.Collections.Immutable;

namespace HomeConnectors;

/// <summary>
/// What a device consumed, by kind. The house meters almost nothing itself, so
/// each metric is honest about where it comes from: the shower measures its own
/// water and power, while a heater only tells the hub it is on — the kWh for one
/// of those is an estimate from a nameplate figure, never a reading.
/// </summary>
public enum ConsumptionMetric
{
    /// <summary>Litres of water, integrated from the shower's flow. Measured.</summary>
    WaterLitres = 0,

    /// <summary>kWh integrated from the shower's own power reading. Measured.</summary>
    EnergyKwh,

    /// <summary>Seconds a device was running. Measured from its on/off state.</summary>
    OnSeconds,

    /// <summary>kWh = on-time × the device's rated power. An estimate, not a meter.</summary>
    EnergyKwhEstimated,
}

/// <summary>One increment to accumulate: how much of a metric a device added since the last sample.</summary>
/// <param name="DeviceId">The catalog id the amount belongs to.</param>
/// <param name="Metric">What was consumed.</param>
/// <param name="Amount">The increment, in the metric's unit (litres, kWh or seconds). Never negative.</param>
/// <param name="At">When the increment ended — the sample time it falls into.</param>
public sealed record ConsumptionDelta(string DeviceId, ConsumptionMetric Metric, double Amount, DateTimeOffset At);

/// <summary>The grain a series is bucketed into for the chart.</summary>
public enum ConsumptionGranularity
{
    Hour = 0,
    Day,
    Week,
    Month,
}

/// <summary>One bar: the total of a metric in the window that starts at <see cref="Start"/>.</summary>
public sealed record ConsumptionPoint(DateTimeOffset Start, double Value);

/// <summary>A device's metric over a range, already bucketed, with its total.</summary>
public sealed record ConsumptionSeries(string DeviceId, ConsumptionMetric Metric, ImmutableArray<ConsumptionPoint> Points)
{
    public double Total => Points.Sum(p => p.Value);
}

/// <summary>
/// Where the house's consumption history lives between runs. Writes are
/// increments, never absolutes, because the sampler only ever knows what was
/// added since it last looked; reads come back bucketed by day, week or month
/// for the chart.
/// </summary>
public interface IConsumptionStore
{
    /// <summary>Adds each increment to its device, metric and hour. Zero and negative amounts are skipped.</summary>
    Task AccumulateAsync(IReadOnlyCollection<ConsumptionDelta> deltas, CancellationToken cancellationToken);

    /// <summary>
    /// Every series asked for, over <paramref name="since"/> (inclusive) to
    /// <paramref name="until"/> (exclusive), bucketed at <paramref name="granularity"/>.
    /// A device-and-metric with nothing in range comes back with no points.
    /// </summary>
    Task<ImmutableArray<ConsumptionSeries>> QueryAsync(
        IReadOnlyCollection<string> deviceIds,
        IReadOnlyCollection<ConsumptionMetric> metrics,
        DateTimeOffset since,
        DateTimeOffset until,
        ConsumptionGranularity granularity,
        CancellationToken cancellationToken);
}
