using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using HomeConnectors;
using Microsoft.Data.Sqlite;

namespace HomeConnectors.Adapters;

/// <summary>Where the consumption history lives.</summary>
public sealed record ConsumptionStoreOptions
{
    public required string DatabasePath { get; init; }

    /// <summary>How far back the history is kept. Thirteen months covers a year of monthly bars plus the current one.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(400);
}

/// <summary>
/// The house's consumption history, as hourly buckets in SQLite. The sampler
/// only ever knows increments, so a write adds to the hour it falls in; a read
/// rolls those hours up into the day, week or month the chart asks for, in local
/// time, because the owner reads "terça" and "outubro", not UTC.
/// <para>
/// One row per device, metric and hour keeps the table small (a metered device
/// is at most 24 rows a day) and makes a write an idempotent upsert: a sample
/// counted twice would still land in its hour, never double a day.
/// </para>
/// </summary>
public sealed class SqliteConsumptionStore : IConsumptionStore, IAsyncDisposable
{
    private const int SchemaVersion = 1;

    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly ConsumptionStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly string _connectionString;
    private bool _initialized;

    public SqliteConsumptionStore(ConsumptionStoreOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options;
        _time = timeProvider;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public async Task AccumulateAsync(IReadOnlyCollection<ConsumptionDelta> deltas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deltas);

        // One hour can take several increments in a tick's worth of devices;
        // folding them first makes the write one upsert per (device, metric, hour).
        var folded = new Dictionary<(string Device, int Metric, long Hour), double>();

        foreach (var delta in deltas)
        {
            if (delta.Amount <= 0 || double.IsNaN(delta.Amount) || double.IsInfinity(delta.Amount) || string.IsNullOrEmpty(delta.DeviceId))
            {
                continue;
            }

            var key = (delta.DeviceId, (int)delta.Metric, HourOf(delta.At));
            folded[key] = folded.TryGetValue(key, out var kept) ? kept + delta.Amount : delta.Amount;
        }

        if (folded.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO consumo (device, metric, hour, value)
            VALUES ($device, $metric, $hour, $value)
            ON CONFLICT (device, metric, hour) DO UPDATE SET value = value + excluded.value;
            """;

        var pDevice = command.Parameters.Add("$device", SqliteType.Text);
        var pMetric = command.Parameters.Add("$metric", SqliteType.Integer);
        var pHour = command.Parameters.Add("$hour", SqliteType.Integer);
        var pValue = command.Parameters.Add("$value", SqliteType.Real);

        foreach (var ((device, metric, hour), amount) in folded)
        {
            pDevice.Value = device;
            pMetric.Value = metric;
            pHour.Value = hour;
            pValue.Value = amount;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<ConsumptionSeries>> QueryAsync(
        IReadOnlyCollection<string> deviceIds,
        IReadOnlyCollection<ConsumptionMetric> metrics,
        DateTimeOffset since,
        DateTimeOffset until,
        ConsumptionGranularity granularity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentNullException.ThrowIfNull(metrics);

        if (deviceIds.Count == 0 || metrics.Count == 0 || until <= since)
        {
            return [];
        }

        var buckets = Buckets(since, until, granularity);
        if (buckets.Length == 0)
        {
            return [];
        }

        // sum[(device, metric)][bucketIndex]
        var sums = new Dictionary<(string Device, int Metric), double[]>();
        foreach (var device in deviceIds)
        {
            foreach (var metric in metrics)
            {
                sums[(device, (int)metric)] = new double[buckets.Length];
            }
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var deviceParams = deviceIds.Select((_, i) => "$d" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var metricParams = metrics.Select((_, i) => "$m" + i.ToString(CultureInfo.InvariantCulture)).ToArray();

        command.CommandText = $"""
            SELECT device, metric, hour, value FROM consumo
            WHERE device IN ({string.Join(", ", deviceParams)})
              AND metric IN ({string.Join(", ", metricParams)})
              AND hour >= $fromHour AND hour < $toHour;
            """;

        var i2 = 0;
        foreach (var device in deviceIds)
        {
            command.Parameters.AddWithValue(deviceParams[i2++], device);
        }

        var j2 = 0;
        foreach (var metric in metrics)
        {
            command.Parameters.AddWithValue(metricParams[j2++], (int)metric);
        }

        command.Parameters.AddWithValue("$fromHour", HourOf(since));
        command.Parameters.AddWithValue("$toHour", HourCeil(until));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var device = reader.GetString(0);
            var metric = reader.GetInt32(1);
            var hour = reader.GetInt64(2);
            var value = reader.GetDouble(3);

            if (!sums.TryGetValue((device, metric), out var row))
            {
                continue;
            }

            var at = DateTimeOffset.FromUnixTimeSeconds(hour * 3600);
            var index = BucketIndex(buckets, at);
            if (index >= 0)
            {
                row[index] += value;
            }
        }

        var series = ImmutableArray.CreateBuilder<ConsumptionSeries>(sums.Count);

        foreach (var ((device, metric), row) in sums)
        {
            var points = ImmutableArray.CreateBuilder<ConsumptionPoint>(buckets.Length);
            for (var b = 0; b < buckets.Length; b++)
            {
                points.Add(new ConsumptionPoint(buckets[b], row[b]));
            }

            series.Add(new ConsumptionSeries(device, (ConsumptionMetric)metric, points.ToImmutable()));
        }

        return series.ToImmutable();
    }

    /// <summary>Drops hours older than the retention window. Cheap; the sampler calls it now and then.</summary>
    public async Task PurgeOldAsync(CancellationToken cancellationToken)
    {
        var cutoff = HourOf(_time.GetUtcNow() - _options.Retention);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM consumo WHERE hour < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _initialization.Dispose();
        SqliteConnection.ClearAllPools();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // The local-time bucket starts that cover [from, to) at the chosen grain.
    private static ImmutableArray<DateTimeOffset> Buckets(DateTimeOffset from, DateTimeOffset to, ConsumptionGranularity grain)
    {
        var start = FloorLocal(from.ToLocalTime(), grain);
        var end = to.ToLocalTime();

        var list = ImmutableArray.CreateBuilder<DateTimeOffset>();
        var cursor = start;
        var guard = 0;

        while (cursor < end && guard++ < 100_000)
        {
            list.Add(cursor);
            cursor = Step(cursor, grain);
        }

        return list.ToImmutable();
    }

    private static int BucketIndex(ImmutableArray<DateTimeOffset> buckets, DateTimeOffset at)
    {
        var local = at.ToLocalTime();

        // Buckets are ascending; the last whose start is <= local holds it.
        var lo = 0;
        var hi = buckets.Length - 1;
        var found = -1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (buckets[mid] <= local)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    private static DateTimeOffset FloorLocal(DateTimeOffset local, ConsumptionGranularity grain)
    {
        var offset = local.Offset;
        var d = local.DateTime;

        return grain switch
        {
            ConsumptionGranularity.Hour => new DateTimeOffset(d.Year, d.Month, d.Day, d.Hour, 0, 0, offset),
            ConsumptionGranularity.Day => new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, offset),
            ConsumptionGranularity.Week => WeekStart(d, offset),
            ConsumptionGranularity.Month => new DateTimeOffset(d.Year, d.Month, 1, 0, 0, 0, offset),
            _ => new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, offset),
        };
    }

    private static DateTimeOffset WeekStart(DateTime d, TimeSpan offset)
    {
        // Monday is the start of the week.
        var delta = ((int)d.DayOfWeek + 6) % 7;
        var monday = d.Date.AddDays(-delta);
        return new DateTimeOffset(monday.Year, monday.Month, monday.Day, 0, 0, 0, offset);
    }

    private static DateTimeOffset Step(DateTimeOffset cursor, ConsumptionGranularity grain) => grain switch
    {
        ConsumptionGranularity.Hour => cursor.AddHours(1),
        ConsumptionGranularity.Day => cursor.AddDays(1),
        ConsumptionGranularity.Week => cursor.AddDays(7),
        ConsumptionGranularity.Month => cursor.AddMonths(1),
        _ => cursor.AddDays(1),
    };

    private static long HourOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / 3600;

    private static long HourCeil(DateTimeOffset at)
    {
        var seconds = at.ToUnixTimeSeconds();
        return seconds % 3600 == 0 ? seconds / 3600 : seconds / 3600 + 1;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(_options.DatabasePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS consumo (
                    device TEXT    NOT NULL,
                    metric INTEGER NOT NULL,
                    hour   INTEGER NOT NULL,
                    value  REAL    NOT NULL,
                    PRIMARY KEY (device, metric, hour)
                );

                CREATE INDEX IF NOT EXISTS ix_consumo_hour ON consumo (hour);

                PRAGMA user_version = {SchemaVersion};
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            if (_initialization.CurrentCount == 0)
            {
                _initialization.Release();
            }
        }
    }
}
