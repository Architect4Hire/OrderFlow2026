using OrderFlow.Orders.API.Managers.Data;
using OrderFlow.Orders.API.Managers.DataContext;
using OrderFlow.Orders.API.Managers.Extensions;
using OrderFlow.Orders.API.Managers.Saga;
using OrderFlow.Orders.API.Managers.ServiceModels;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Orders.API.Managers.Business;

/// <summary>
/// The operator's view of the system: what happened to an order, what is stuck, what the broker gave
/// up on, and how to put the projection back.
/// </summary>
public interface IOrderOpsManager
{
    /// <summary>One order's full history, oldest first.</summary>
    Task<IReadOnlyList<OrderEventServiceModel>> GetTimelineAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);

    /// <summary>Every dead-letter queue in the system, newest first.</summary>
    Task<IReadOnlyList<DeadLetterServiceModel>> ListDeadLettersAsync(
        int maxPerSource,
        CancellationToken cancellationToken = default);

    /// <summary>Rebuilds the Redis projection from the Cosmos event log.</summary>
    Task<ProjectionRebuildServiceModel> RebuildProjectionAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IOrderOpsManager"/>
public sealed class OrderOpsManager(
    IOrderEventStore eventStore,
    IOrderReadModel readModel,
    IDeadLetterBrowser deadLetterBrowser,
    ILogger<OrderOpsManager> logger) : IOrderOpsManager
{
    public async Task<IReadOnlyList<OrderEventServiceModel>> GetTimelineAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var stream = await eventStore.ReadStreamAsync(orderId, cancellationToken).ConfigureAwait(false);

        return stream.ToServiceModels();
    }

    public async Task<IReadOnlyList<DeadLetterServiceModel>> ListDeadLettersAsync(
        int maxPerSource,
        CancellationToken cancellationToken = default)
    {
        var messages = await deadLetterBrowser.PeekAllAsync(maxPerSource, cancellationToken).ConfigureAwait(false);

        return messages.ToServiceModels();
    }

    /// <summary>
    /// Replays every stream and re-projects it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-003 claims the Redis read model is a projection that can be rebuilt from the event log.
    /// Until this method existed, that claim was false</b> — nothing could rebuild it, so a flushed
    /// Redis meant the ops list was permanently empty while orders were genuinely in flight. The
    /// architecture document said one thing and the code did another, which is the worst kind of
    /// documentation.
    /// </para>
    /// <para>
    /// It replays through <see cref="OrderRehydrator"/> — the SAME fold the saga uses to make its
    /// decisions. A second, rebuild-specific interpretation of the event log would eventually
    /// disagree with the saga's, and the ops view would confidently report a state the saga does not
    /// believe in.
    /// </para>
    /// <para>
    /// This is the one operation in the system that fans out across Cosmos partitions. It is an
    /// administrative action, not a request path — which is why it is a POST an operator triggers,
    /// not something that runs on a timer.
    /// </para>
    /// </remarks>
    public async Task<ProjectionRebuildServiceModel> RebuildProjectionAsync(CancellationToken cancellationToken = default)
    {
        var orderIds = await eventStore.ListOrderIdsAsync(cancellationToken).ConfigureAwait(false);

        logger.LogWarning("Rebuilding the order projection from {Count} event stream(s).", orderIds.Count);

        var projected = 0;

        foreach (var orderId in orderIds)
        {
            var stream = await eventStore.ReadStreamAsync(orderId, cancellationToken).ConfigureAwait(false);

            var order = OrderRehydrator.Rehydrate(orderId, stream);

            if (order is null)
            {
                // A stream with no OrderPlaced. Should be unreachable, and worth saying out loud
                // rather than silently skipping — it would mean the event log itself is malformed.
                logger.LogError("Stream {OrderId} has no OrderPlaced event. Skipping.", orderId);

                continue;
            }

            await readModel.SetStatusAsync(order.ToServiceModel(), cancellationToken).ConfigureAwait(false);

            projected++;
        }

        logger.LogWarning("Projection rebuild complete: {Projected} of {Total} stream(s) projected.", projected, orderIds.Count);

        return new ProjectionRebuildServiceModel
        {
            StreamsReplayed = orderIds.Count,
            OrdersProjected = projected,
            CompletedUtc = DateTime.UtcNow
        };
    }
}

public static class OrderOpsExtensions
{
    public static IHostApplicationBuilder AddOrderOps(this IHostApplicationBuilder builder)
    {
        builder.AddDeadLetterBrowser();

        builder.Services.AddScoped<IOrderOpsManager, OrderOpsManager>();

        return builder;
    }
}
