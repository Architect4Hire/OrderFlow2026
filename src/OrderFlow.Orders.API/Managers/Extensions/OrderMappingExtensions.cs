using OrderFlow.Orders.API.Managers.DataContext;
using OrderFlow.Orders.API.Managers.Domain;
using OrderFlow.Orders.API.Managers.ServiceModels;
using OrderFlow.Orders.API.Managers.ViewModels;
using OrderFlow.ServiceDefaults.Messaging;

// Domain.OrderLine and Contracts.OrderLine share a name by design — one is persisted, one is on
// the wire. Aliasing keeps both visible in this file, which is the only place that knows both.
using ContractLine = OrderFlow.Contracts.Messages.OrderLine;

namespace OrderFlow.Orders.API.Managers.Extensions;

/// <summary>
/// Hand-rolled mapping between the wire shapes and the aggregate. No AutoMapper, no Mapster, no
/// reflection: a convention-based mapper would silently start copying any property someone later
/// adds to the ViewModel — including the server-controlled ones B3 [R]1 deliberately keeps off it.
/// Here, every assignment is a line you can see in a diff.
/// </summary>
public static class OrderMappingExtensions
{
    /// <summary>
    /// Builds a new <see cref="Order"/> from a customer's request. The server assigns every
    /// identity, price, and timestamp — the ViewModel contributes only CustomerRef, Sku and Quantity.
    /// </summary>
    /// <remarks>
    /// <b>A new order is unpriced.</b> Subtotal, Total and every UnitPrice are zero and stay zero
    /// until Inventory answers with the catalogue prices on InventoryReserved (ADR-006). This looks
    /// odd for a moment — an order that exists and costs nothing — and it is the correct shape: the
    /// customer has told us what they want, and nobody has yet told us what it costs. The alternative
    /// is trusting a number the customer sent, which is how you sell a laptop for a penny.
    /// </remarks>
    public static Order ToDomain(this PlaceOrderViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        // One reading of the clock for the whole aggregate, so CreatedUtc and UpdatedUtc are
        // identical on a freshly placed order rather than microseconds apart.
        var nowUtc = DateTime.UtcNow;

        var orderId = Guid.NewGuid();

        var lines = viewModel.Lines
            .Select(line => new OrderLine
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                Sku = line.Sku.Trim(),
                Quantity = line.Quantity,
                UnitPrice = 0m
            })
            .ToList();

        return new Order
        {
            Id = orderId,
            CustomerRef = viewModel.CustomerRef.Trim(),

            // The saga owns every transition after this one. Placed is the only state a mapper
            // is ever allowed to author.
            State = OrderState.Placed,

            // Unpriced. Inventory fills these in.
            // TODO: pricing engine. Total == Subtotal — no tax, no shipping, no discounts. The
            // saga's compensation paths are what this demo proves, not pricing.
            Subtotal = 0m,
            Total = 0m,

            FailureReason = string.Empty,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            Lines = lines
        };
    }

    /// <summary>Projects the aggregate to what the status view polls.</summary>
    public static OrderServiceModel ToServiceModel(this Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new OrderServiceModel
        {
            Id = order.Id,
            CustomerRef = order.CustomerRef,

            // The name, not the ordinal — the client must not care how OrderState is numbered.
            State = order.State.ToString(),

            Subtotal = order.Subtotal,
            Total = order.Total,
            FailureReason = order.FailureReason,
            CreatedUtc = order.CreatedUtc,
            UpdatedUtc = order.UpdatedUtc,
            Lines = order.Lines
                .Select(line => new OrderLineServiceModel
                {
                    Id = line.Id,
                    Sku = line.Sku,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    LineTotal = decimal.Round(line.Quantity * line.UnitPrice, 2)
                })
                .ToList()
        };
    }

    public static List<OrderServiceModel> ToServiceModels(this IEnumerable<Order> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);

        return orders.Select(order => order.ToServiceModel()).ToList();
    }

    /// <summary>
    /// Projects persisted lines onto the wire shape carried by ReserveInventory and
    /// DispatchFulfillment. Beyond B4's [S], but the alternative was a private copy of it in the
    /// Business layer — and mapping belongs here, where both shapes are already in scope.
    /// </summary>
    public static IReadOnlyList<ContractLine> ToMessageLines(this IEnumerable<OrderLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines
            .Select(line => new ContractLine
            {
                Sku = line.Sku,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice
            })
            .ToList();
    }

    /// <summary>
    /// The same projection, from the read model rather than the aggregate. The recovery sweeper
    /// re-sends a stuck order's command using only what the projection holds — it deliberately does
    /// not rehydrate the event stream, because re-driving an order must stay cheap enough to do on a
    /// timer for every stuck order at once.
    /// </summary>
    public static IReadOnlyList<ContractLine> ToMessageLines(this IEnumerable<OrderLineServiceModel> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines
            .Select(line => new ContractLine
            {
                Sku = line.Sku,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice
            })
            .ToList();
    }

    /// <summary>The raw event log, as the timeline endpoint serves it.</summary>
    public static IReadOnlyList<OrderEventServiceModel> ToServiceModels(this IEnumerable<OrderEventEnvelope> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return
        [
            .. stream.Select(envelope => new OrderEventServiceModel
            {
                Sequence = envelope.Sequence,
                Type = envelope.Type,
                OccurredUtc = envelope.OccurredUtc,

                // The payload goes out whole. Summarising it here would mean deciding, now, which
                // fields a future investigation will care about.
                Payload = envelope.Payload
            })
        ];
    }

    /// <summary>The system's dead-letter queues, as the ops view serves them.</summary>
    public static IReadOnlyList<DeadLetterServiceModel> ToServiceModels(this IEnumerable<DeadLetteredMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return
        [
            .. messages.Select(message => new DeadLetterServiceModel
            {
                Source = message.Source,
                OrderId = message.OrderId,
                MessageId = message.MessageId,
                MessageType = message.MessageType,
                Reason = message.Reason,
                ErrorDescription = message.ErrorDescription,
                DeliveryCount = message.DeliveryCount,
                EnqueuedUtc = message.EnqueuedUtc
            })
        ];
    }
}
