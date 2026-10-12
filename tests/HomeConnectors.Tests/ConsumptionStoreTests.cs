using System.IO;
using HomeConnectors;
using HomeConnectors.Adapters;
using Microsoft.Extensions.Time.Testing;

namespace HomeConnectors.Tests;

/// <summary>
/// The consumption store accumulates increments into hourly buckets and rolls
/// them up into the day/week/month the chart asks for. The riskiest part is the
/// roll-up, so these check that totals survive it and that the hour grain keeps
/// each hour apart.
/// </summary>
[TestClass]
public sealed class ConsumptionStoreTests
{
    private static SqliteConsumptionStore NewStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "jarvis-consumo-tests", Guid.NewGuid().ToString("N") + ".db");
        return new SqliteConsumptionStore(new ConsumptionStoreOptions { DatabasePath = path }, new FakeTimeProvider());
    }

    [TestMethod]
    public async Task Increments_to_the_same_hour_add_up()
    {
        var store = NewStore(out _);
        var at = new DateTimeOffset(2026, 10, 12, 8, 15, 0, TimeSpan.Zero);

        await store.AccumulateAsync(
            [
                new ConsumptionDelta("chuveiro", ConsumptionMetric.WaterLitres, 3.0, at),
                new ConsumptionDelta("chuveiro", ConsumptionMetric.WaterLitres, 4.5, at.AddMinutes(20)),
            ],
            CancellationToken.None);

        var series = await store.QueryAsync(
            ["chuveiro"], [ConsumptionMetric.WaterLitres],
            at.AddHours(-1), at.AddHours(2), ConsumptionGranularity.Hour, CancellationToken.None);

        Assert.AreEqual(1, series.Length);
        Assert.AreEqual(7.5, series[0].Total, 1e-9);
    }

    [TestMethod]
    public async Task Negative_and_nan_increments_are_ignored()
    {
        var store = NewStore(out _);
        var at = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

        await store.AccumulateAsync(
            [
                new ConsumptionDelta("x", ConsumptionMetric.EnergyKwh, -5, at),
                new ConsumptionDelta("x", ConsumptionMetric.EnergyKwh, double.NaN, at),
                new ConsumptionDelta("x", ConsumptionMetric.EnergyKwh, 2, at),
            ],
            CancellationToken.None);

        var series = await store.QueryAsync(
            ["x"], [ConsumptionMetric.EnergyKwh],
            at.AddHours(-1), at.AddHours(1), ConsumptionGranularity.Hour, CancellationToken.None);

        Assert.AreEqual(2, series[0].Total, 1e-9);
    }

    [TestMethod]
    public async Task Hours_land_in_separate_hour_buckets_but_one_day_bucket()
    {
        var store = NewStore(out _);
        var baseTime = new DateTimeOffset(2026, 10, 12, 6, 30, 0, TimeSpan.Zero);

        await store.AccumulateAsync(
            [
                new ConsumptionDelta("heater", ConsumptionMetric.OnSeconds, 60, baseTime),
                new ConsumptionDelta("heater", ConsumptionMetric.OnSeconds, 120, baseTime.AddHours(1)),
                new ConsumptionDelta("heater", ConsumptionMetric.OnSeconds, 180, baseTime.AddHours(2)),
            ],
            CancellationToken.None);

        var byHour = await store.QueryAsync(
            ["heater"], [ConsumptionMetric.OnSeconds],
            baseTime.AddHours(-1), baseTime.AddHours(4), ConsumptionGranularity.Hour, CancellationToken.None);

        var nonEmptyHours = byHour[0].Points.Count(p => p.Value > 0);
        Assert.AreEqual(3, nonEmptyHours, "cada hora é sua própria barra");
        Assert.AreEqual(360, byHour[0].Total, 1e-9);

        // The three readings are the same local day, so the day grain is one bar.
        var startLocal = baseTime.ToLocalTime().Date;
        var byDay = await store.QueryAsync(
            ["heater"], [ConsumptionMetric.OnSeconds],
            new DateTimeOffset(startLocal, baseTime.ToLocalTime().Offset),
            new DateTimeOffset(startLocal.AddDays(1), baseTime.ToLocalTime().Offset),
            ConsumptionGranularity.Day, CancellationToken.None);

        Assert.AreEqual(1, byDay[0].Points.Length);
        Assert.AreEqual(360, byDay[0].Total, 1e-9);
    }

    [TestMethod]
    public async Task A_device_with_nothing_in_range_comes_back_empty_totalled()
    {
        var store = NewStore(out _);
        var at = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

        var series = await store.QueryAsync(
            ["silent"], [ConsumptionMetric.WaterLitres],
            at, at.AddHours(3), ConsumptionGranularity.Hour, CancellationToken.None);

        Assert.AreEqual(1, series.Length);
        Assert.AreEqual(0, series[0].Total, 1e-9);
    }
}
