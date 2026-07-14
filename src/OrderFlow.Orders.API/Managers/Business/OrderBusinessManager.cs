using OrderFlow.Contracts.Messages;
using OrderFlow.Orders.API.Managers.Data;
using OrderFlow.Orders.API.Managers.DataContext;
using OrderFlow.Orders.API.Managers.Extensions;
using OrderFlow.Orders.API.Managers.ServiceModels;
using OrderFlow.Orders.API.Managers.ViewModels;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Orders.API.Managers.Business;

/// <summary>Where "place an order" is coordinated. Everything after that is the saga's.</summary>
public interface IOrderBusinessManager
{
    Task<OrderServiceModel> PlaceAsync(PlaceOrderViewModel viewModel, CancellationToken cancellationToken = default);

    Task<OrderServiceModel?> GetStatusAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrderServiceModel>> ListActiveAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IOrderBusinessManager"/>
/// <remarks>
/// Depends on the three abstractions and nothing else — no Cosmos, Redis, or Service Bus type
/// appears in this file. Swapping the read model for Postgres would not change a line of it.
/// </remarks>
public sealed class OrderBusinessManager(
    IOrderEventStore eventStore,
    IOrderReadModel readModel,
    IMessageBus messageBus,
    ILogger<OrderBusinessManager> logger) : IOrderBusinessManager
{
    public async Task<OrderServiceModel> PlaceAsync(PlaceOrderViewModel viewModel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var order = viewModel.ToDomain();

        // CorrelationId IS the OrderId, set once here and never regenerated. Every message this
        // order ever produces — across all five services, forward and compensating — carries it.
        var orderPlaced = new OrderPlaced
        {
            MessageId = Guid.NewGuid(),
            CorrelationId = order.Id,
            OccurredUtc = order.CreatedUtc,
            CustomerRef = order.CustomerRef,
            Lines = order.Lines.ToMessageLines(),
            Total = order.Total
        };

        // Append BEFORE sending, deliberately. If the append fails we have sent nothing and the
        // order simply does not exist. Were it the other way round, a failed append would leave
        // the saga running against an order with no record of it — far worse than not starting.
        await eventStore.AppendAsync(order.Id, orderPlaced, cancellationToken).ConfigureAwait(false);

        var serviceModel = order.ToServiceModel();

        await readModel.SetStatusAsync(serviceModel, cancellationToken).ConfigureAwait(false);

        // TODO: outbox. The append and the send are two writes with no transaction between them —
        // if the send throws, the order sits at Placed with no saga running: a stuck order, which
        // is exactly what the ops view exists to surface. A real system writes the command to an
        // outbox in the same transaction as the append and dispatches it separately.
        //
        // This is also the ONE piece of orchestration outside the saga: that the first step of a
        // placed order is to reserve inventory. See the note in [R]2 below.
        var reserveInventory = new ReserveInventory
        {
            CorrelationId = order.Id,
            Lines = order.Lines.ToMessageLines()
        };

        await messageBus.SendCommandAsync(reserveInventory, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Placed order {OrderId} for {CustomerRef} ({LineCount} lines, total {Total}). Saga started.",
            order.Id, order.CustomerRef, order.Lines.Count, order.Total);

        // [R]2: Business STARTS the saga and stops. It does not wait for InventoryReserved, and it
        // does not know what comes after — the consumers (B9) drive every subsequent step.
        return serviceModel;
    }

    public Task<OrderServiceModel?> GetStatusAsync(Guid orderId, CancellationToken cancellationToken = default)
        => readModel.GetStatusAsync(orderId, cancellationToken);

    public Task<IReadOnlyList<OrderServiceModel>> ListActiveAsync(CancellationToken cancellationToken = default)
        => readModel.ListActiveAsync(cancellationToken);
}
