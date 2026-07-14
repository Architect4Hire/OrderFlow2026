using System.Text.Json;
using OrderFlow.Contracts.Messages;
using OrderFlow.Orders.API.Managers.DataContext;
using OrderFlow.Orders.API.Managers.Domain;
using OrderFlow.ServiceDefaults.Messaging;

using DomainLine = OrderFlow.Orders.API.Managers.Domain.OrderLine;

namespace OrderFlow.Orders.API.Managers.Saga;

/// <summary>
/// Folds an order's event stream into the aggregate. <b>The single definition of what an order's
/// history means.</b>
/// </summary>
/// <remarks>
/// <para>
/// Lifted out of <see cref="OrderSaga"/> because it now has two callers: the saga, which rehydrates
/// before every decision, and the projection rebuild, which replays every stream to reconstruct
/// Redis from scratch. Two implementations of "what state is this order in" would be two
/// implementations that eventually disagree — and the disagreement would show up as an ops view
/// confidently reporting a state the saga does not believe in.
/// </para>
/// <para>
/// Idempotent by construction: applying the same event twice sets the same state twice.
/// </para>
/// <para>
/// Only OrderConfirmed and OrderFailed are terminal. InventoryRejected, PaymentDeclined and
/// FulfillmentFailed record the REASON but leave the state alone: they are the cause, not the
/// conclusion, and the handler still has compensations to send. Making them terminal here would
/// re-introduce the stranded-stock bug through the back door.
/// </para>
/// </remarks>
public static class OrderRehydrator
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The saga is done with an order in one of these states, and no event may move it again.</summary>
    public static bool IsTerminal(OrderState state) => state is OrderState.Confirmed or OrderState.Failed;

    /// <summary>Replays the stream. Null when the stream carries no OrderPlaced — i.e. no such order.</summary>
    public static Order? Rehydrate(Guid orderId, IReadOnlyList<OrderEventEnvelope> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Order? order = null;

        foreach (var envelope in stream)
        {
            switch (envelope.Type)
            {
                case nameof(OrderPlaced):
                    order = FromPlaced(orderId, envelope.Payload.Deserialize<OrderPlaced>(PayloadOptions)!);
                    break;

                case nameof(InventoryReserved):
                    Advance(order, OrderState.Reserved);
                    ApplyPricing(order, envelope.Payload.Deserialize<InventoryReserved>(PayloadOptions)!);
                    break;

                case nameof(PaymentSucceeded):
                    Advance(order, OrderState.Paid);
                    break;

                case nameof(FulfillmentDispatched):
                    Advance(order, OrderState.Dispatched);
                    break;

                case nameof(OrderConfirmed):
                    Advance(order, OrderState.Confirmed);
                    break;

                case nameof(OrderFailed):
                    Advance(order, OrderState.Failed);
                    RecordReason(order, envelope.Payload.Deserialize<OrderFailed>(PayloadOptions)!.Reason);
                    break;

                case nameof(InventoryRejected):
                    RecordReason(order, envelope.Payload.Deserialize<InventoryRejected>(PayloadOptions)!.Reason);
                    break;

                case nameof(PaymentDeclined):
                    RecordReason(order, envelope.Payload.Deserialize<PaymentDeclined>(PayloadOptions)!.Reason);
                    break;

                case nameof(FulfillmentFailed):
                    RecordReason(order, envelope.Payload.Deserialize<FulfillmentFailed>(PayloadOptions)!.Reason);
                    break;
            }

            if (order is not null)
            {
                order.UpdatedUtc = envelope.OccurredUtc;
            }
        }

        return order;
    }

    private static Order FromPlaced(Guid orderId, OrderPlaced placed) => new()
    {
        Id = orderId,
        CustomerRef = placed.CustomerRef,
        State = OrderState.Placed,

        // A placed order is NOT yet priced: the customer does not set prices, Inventory does, and it
        // has not answered yet. These stay zero until InventoryReserved arrives with the catalogue
        // prices. See ADR-006.
        Subtotal = 0m,
        Total = 0m,

        FailureReason = string.Empty,
        CreatedUtc = placed.OccurredUtc,
        UpdatedUtc = placed.OccurredUtc,
        Lines = placed.Lines
            .Select((line, index) => new DomainLine
            {
                // OrderPlaced does not carry line ids, so they cannot be restored — only derived.
                // Deterministic from (order, position) so at least they are STABLE across every
                // rehydration, rather than churning on each poll of the status view.
                Id = MessagingConventions.DeterministicMessageId(orderId, $"line:{index}:{line.Sku}"),
                OrderId = orderId,
                Sku = line.Sku,
                Quantity = line.Quantity,
                UnitPrice = 0m
            })
            .ToList()
    };

    /// <summary>
    /// Stamps the authoritative prices Inventory returned onto the order. This is the moment an order
    /// acquires a value, and it happens on the way back from the service that owns the catalogue —
    /// never from anything the customer sent.
    /// </summary>
    public static void ApplyPricing(Order? order, InventoryReserved reserved)
    {
        if (order is null || reserved.Lines.Count == 0)
        {
            return;
        }

        foreach (var line in order.Lines)
        {
            var priced = reserved.Lines.FirstOrDefault(item =>
                string.Equals(item.Sku, line.Sku, StringComparison.OrdinalIgnoreCase));

            if (priced is not null)
            {
                line.UnitPrice = priced.UnitPrice;
            }
        }

        order.Subtotal = reserved.Total;
        order.Total = reserved.Total;
    }

    private static void Advance(Order? order, OrderState state)
    {
        if (order is not null)
        {
            order.State = state;
        }
    }

    private static void RecordReason(Order? order, string reason)
    {
        if (order is not null)
        {
            order.FailureReason = reason;
        }
    }
}
