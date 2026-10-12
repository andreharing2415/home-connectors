using System.Collections.Immutable;
using HomeConnectors;

namespace HomeHub.Client;

/// <summary>
/// The consumption history as the hub keeps it. Read-only from here: the hub's
/// own sampler is the one writer, so two processes never count the same minute.
/// </summary>
public sealed class HubConsumptionStore(HomeHubConnection hub) : IConsumptionStore
{
    private readonly HomeHubConnection _hub = hub ?? throw new ArgumentNullException(nameof(hub));

    public Task AccumulateAsync(IReadOnlyCollection<ConsumptionDelta> deltas, CancellationToken cancellationToken) =>
        throw new NotSupportedException("O consumo é gravado pelo amostrador do hub, não pelo cliente.");

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

        var query = new ConsumptionQuery([.. deviceIds], [.. metrics], since, until, granularity);
        var reply = await _hub.PostAsync<ImmutableArray<ConsumptionSeries>>(HubWire.Consumption, query, cancellationToken).ConfigureAwait(false);

        // The chart reads an empty history as "nothing measured yet"; an
        // unreachable hub is surfaced the same way the local store surfaced an
        // unreadable file — as an exception the caller already handles.
        return reply.Ok
            ? reply.Value
            : throw new InvalidOperationException(reply.Message);
    }
}
