using OrderFlow.Contracts.Messages;
using OrderFlow.Orders.API.Managers.Data;
using OrderFlow.Orders.API.Managers.DataContext;
using OrderFlow.Orders.API.Managers.Domain;
using OrderFlow.Orders.API.Managers.Extensions;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Orders.API.Managers.Saga;

/// <summary>
/// The order saga. Reacts to one event, issues one next action, returns.
/// </summary>
public interface IOrderSaga
{
    Task OnInventoryReservedAsync(InventoryReserved reserved, CancellationToken cancellationToken = default);

    Task OnInventoryRejectedAsync(InventoryRejected rejected, CancellationToken cancellationToken = default);

    Task OnPaymentSucceededAsync(PaymentSucceeded succeeded, CancellationToken cancellationToken = default);

    Task OnPaymentDeclinedAsync(PaymentDeclined declined, CancellationToken cancellationToken = default);

    Task OnFulfillmentDispatchedAsync(FulfillmentDispatched dispatched, CancellationToken cancellationToken = default);

    Task OnFulfillmentFailedAsync(FulfillmentFailed failed, CancellationToken cancellationToken = default);
}

/// <summary>
/// The one auditable place an order's state changes. No 2PC, no distributed transaction: every
/// failure is undone by a compensating command (ADR-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>State is rehydrated from the event store, never held in a field.</b> Every handler replays the
/// order's Cosmos stream to learn where it is. That is what lets this service be killed mid-saga
/// and resume correctly, and it is why Redis can be flushed without losing an order.
/// </para>
/// <para>
/// <b>The ordering rule, which is the whole game: every outbound message is sent BEFORE the
/// terminal state is recorded.</b> The terminal guard below makes a redelivered event a no-op —
/// that is what stops a compensation firing twice ([R]3). But it cuts both ways. Record
/// <c>Failed</c> first and then fail to send <c>ReleaseInventory</c>, and the retry walks into the
/// terminal guard, no-ops, and the reservation is stranded forever: silent stock loss, the exact
/// bug this system exists to prevent ([R]2, [R]5). So on the failure paths the compensations go out
/// first, and only once they are away does the order become terminal. Crash anywhere before that
/// and the whole handler is replayed from a non-terminal state, which re-sends the compensation.
/// A duplicate <c>ReleaseInventory</c> is harmless — Inventory is idempotent. A missing one is not.
/// </para>
/// <para>
/// <b>Every message this saga emits carries a deterministic MessageId</b> derived from
/// (OrderId, message type). A replayed handler therefore re-sends a message the receiver has
/// already seen, and the receiver's (ConsumerName, MessageId) guard drops it. Mint a fresh Guid
/// here instead and every retry becomes a second refund, a second release, a second email.
/// </para>
/// </remarks>
public sealed class OrderSaga(
    IOrderEventStore eventStore,
    IOrderReadModel readModel,
    IMessageBus messageBus,
    ILogger<OrderSaga> logger) : IOrderSaga
{
    // ── Forward path ────────────────────────────────────────────────────────────────────────

    /// <summary>Inventory is held. Charge the customer.</summary>
    public async Task OnInventoryReservedAsync(InventoryReserved reserved, CancellationToken cancellationToken = default)
    {
        var order = await LoadAsync(reserved.CorrelationId, nameof(InventoryReserved), cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return;
        }

        // Inventory owns the catalogue, so Inventory priced the order. Stamp those prices on the
        // aggregate BEFORE projecting it, or the read model shows a £0 order and — far worse — the
        // ChargePayment below authorizes £0 (ADR-006).
        OrderRehydrator.ApplyPricing(order, reserved);

        await ApplyAsync(order, reserved, OrderState.Reserved, cancellationToken).ConfigureAwait(false);

        var chargePayment = new ChargePayment
        {
            MessageId = DeterministicMessageId(order.Id, nameof(ChargePayment)),
            CorrelationId = order.Id,
            OccurredUtc = DateTime.UtcNow,

            // The number Inventory gave us. Never a number the customer sent.
            Amount = order.Total,

            // Stable across every retry of this handler, so a redelivered InventoryReserved can
            // never authorize a second charge — Payment resolves the same key to the same row.
            IdempotencyKey = order.Id.ToString("N")
        };

        await messageBus.SendCommandAsync(chargePayment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Payment cleared. Ship it.</summary>
    public async Task OnPaymentSucceededAsync(PaymentSucceeded succeeded, CancellationToken cancellationToken = default)
    {
        var order = await LoadAsync(succeeded.CorrelationId, nameof(PaymentSucceeded), cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return;
        }

        await ApplyAsync(order, succeeded, OrderState.Paid, cancellationToken).ConfigureAwait(false);

        var dispatchFulfillment = new DispatchFulfillment
        {
            MessageId = DeterministicMessageId(order.Id, nameof(DispatchFulfillment)),
            CorrelationId = order.Id,
            OccurredUtc = DateTime.UtcNow,
            CustomerRef = order.CustomerRef,
            Lines = order.Lines.ToMessageLines()
        };

        await messageBus.SendCommandAsync(dispatchFulfillment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shipped. The order is done.</summary>
    public async Task OnFulfillmentDispatchedAsync(FulfillmentDispatched dispatched, CancellationToken cancellationToken = default)
    {
        var order = await LoadAsync(dispatched.CorrelationId, nameof(FulfillmentDispatched), cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return;
        }

        await ApplyAsync(order, dispatched, OrderState.Dispatched, cancellationToken).ConfigureAwait(false);

        // The goods left the building, so the holds must stop being holds. Without this the
        // reservation stays Held forever and OnHand never falls: the warehouse row permanently
        // overstates the shelf, and the ops view cannot tell a hold that SHIPPED from a hold that is
        // STRANDED by a lost compensation. Telling those two apart is the whole point of the system.
        //
        // Sent alongside the confirm, not instead of a step — the same shape as OnFulfillmentFailed
        // issuing both a refund and a release. Still one event in, one decision out.
        var commitInventory = new CommitInventory
        {
            MessageId = DeterministicMessageId(order.Id, nameof(CommitInventory)),
            CorrelationId = order.Id,
            OccurredUtc = DateTime.UtcNow
        };

        await messageBus.SendCommandAsync(commitInventory, cancellationToken).ConfigureAwait(false);

        var confirmed = new OrderConfirmed
        {
            MessageId = DeterministicMessageId(order.Id, nameof(OrderConfirmed)),
            CorrelationId = order.Id,
            OccurredUtc = DateTime.UtcNow
        };

        // Published BEFORE the order becomes terminal. Afterwards, a failed publish would be
        // swallowed by the terminal guard on retry and the customer would never be told.
        await messageBus.PublishEventAsync(confirmed, cancellationToken).ConfigureAwait(false);

        await ApplyAsync(order, confirmed, OrderState.Confirmed, cancellationToken).ConfigureAwait(false);
    }

    // ── Failure paths ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stock could not be held. Nothing was reserved and nothing was charged, so there is nothing
    /// to compensate — this is the one failure path with no compensating command.
    /// </summary>
    public async Task OnInventoryRejectedAsync(InventoryRejected rejected, CancellationToken cancellationToken = default)
    {
        var order = await LoadAsync(rejected.CorrelationId, nameof(InventoryRejected), cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return;
        }

        // Record the cause, but hold the state at Placed. Going terminal here would slam the
        // guard shut on a retry before OrderFailed had been published.
        await ApplyAsync(order, rejected, order.State, cancellationToken, rejected.Reason).ConfigureAwait(false);

        await FailAsync(order, rejected.Reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The charge was declined. Inventory is still held on this order's behalf — release it, or
    /// that stock is invisible and unsellable until someone notices ([R]2).
    /// </summary>
    public async Task OnPaymentDeclinedAsync(PaymentDeclined declined, CancellationToken cancellationToken = default)
    {
        var order = await LoadAsync(declined.CorrelationId, nameof(PaymentDeclined), cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return;
        }

        await ApplyAsync(order, declined, order.State, cancellationToken, declined.Reason).ConfigureAwait(false);

        // COMPENSATE, then go terminal. Never the reverse — see the class remarks.
        await ReleaseInventoryAsync(order, declined.Reason, cancellationToken).ConfigureAwait(false);

        await FailAsync(order, declined.Reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The carrier would not take it. By now the customer has been CHARGED and the stock is HELD,
    /// so this path has to undo both. Refunding without releasing, or releasing without refunding,
    /// are each a half-compensated order — and a half-compensated order is worse than a failed one,
    /// because nothing downstream will ever notice.
    /// </summary>
    public async Task OnFulfillmentFailedAsync(FulfillmentFailed failed, CancellationToken cancellationToken = default)
    {
        var order = await LoadAsync(failed.CorrelationId, nameof(FulfillmentFailed), cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return;
        }

        await ApplyAsync(order, failed, order.State, cancellationToken, failed.Reason).ConfigureAwait(false);

        var refundPayment = new RefundPayment
        {
            MessageId = DeterministicMessageId(order.Id, nameof(RefundPayment)),
            CorrelationId = order.Id,
            OccurredUtc = DateTime.UtcNow,
            Reason = failed.Reason
        };

        // Both compensations, both before the terminal append. If SendCommandAsync throws on the
        // refund, we never reach the release — but we also never reach Failed, so the redelivered
        // FulfillmentFailed replays this whole handler and tries both again. Letting the exception
        // out is what preserves that ([R]5); catching it here is what would strand the stock.
        await messageBus.SendCommandAsync(refundPayment, cancellationToken).ConfigureAwait(false);

        await ReleaseInventoryAsync(order, failed.Reason, cancellationToken).ConfigureAwait(false);

        await FailAsync(order, failed.Reason, cancellationToken).ConfigureAwait(false);
    }

    // ── Shared steps ────────────────────────────────────────────────────────────────────────

    private Task ReleaseInventoryAsync(Order order, string reason, CancellationToken cancellationToken)
    {
        var releaseInventory = new ReleaseInventory
        {
            MessageId = DeterministicMessageId(order.Id, nameof(ReleaseInventory)),
            CorrelationId = order.Id,
            OccurredUtc = DateTime.UtcNow,
            Reason = reason
        };

        return messageBus.SendCommandAsync(releaseInventory, cancellationToken);
    }

    /// <summary>
    /// Announces the failure, then makes the order terminal. In that order: OrderFailed is what
    /// Notification subscribes to, and after the terminal append no retry can get back in here.
    /// </summary>
    private async Task FailAsync(Order order, string reason, CancellationToken cancellationToken)
    {
        var orderFailed = new OrderFailed
        {
            MessageId = DeterministicMessageId(order.Id, nameof(OrderFailed)),
            CorrelationId = order.Id,
            OccurredUtc = DateTime.UtcNow,
            Reason = reason
        };

        await messageBus.PublishEventAsync(orderFailed, cancellationToken).ConfigureAwait(false);

        await ApplyAsync(order, orderFailed, OrderState.Failed, cancellationToken, reason).ConfigureAwait(false);

        logger.LogWarning("Order {OrderId} failed: {Reason}", order.Id, reason);
    }

    /// <summary>Appends the event, moves the state, republishes the projection. Always all three.</summary>
    private async Task ApplyAsync(
        Order order,
        object domainEvent,
        OrderState newState,
        CancellationToken cancellationToken,
        string failureReason = "")
    {
        await eventStore.AppendAsync(order.Id, domainEvent, cancellationToken).ConfigureAwait(false);

        var previousState = order.State;

        order.State = newState;
        order.UpdatedUtc = DateTime.UtcNow;

        if (failureReason.Length > 0)
        {
            order.FailureReason = failureReason;
        }

        await readModel.SetStatusAsync(order.ToServiceModel(), cancellationToken).ConfigureAwait(false);

        if (previousState != newState)
        {
            logger.LogInformation(
                "Order {OrderId}: {PreviousState} → {NewState} on {EventType}",
                order.Id, previousState, newState, domainEvent.GetType().Name);
        }
    }

    /// <summary>
    /// Replays the order's stream. Returns null when the saga must not act — either the order is
    /// already terminal (a redelivered event: no-op, per [R]3) or its stream is empty.
    /// </summary>
    private async Task<Order?> LoadAsync(Guid orderId, string trigger, CancellationToken cancellationToken)
    {
        var stream = await eventStore.ReadStreamAsync(orderId, cancellationToken).ConfigureAwait(false);

        // Shared with the projection rebuild (OrderProjectionRebuilder). One definition of what an
        // order's history means — two would eventually disagree, and the ops view would confidently
        // report a state the saga does not believe in.
        var order = OrderRehydrator.Rehydrate(orderId, stream);

        if (order is null)
        {
            // No OrderPlaced in the stream. Business appends before it sends, so this should be
            // unreachable — which is exactly why it must throw rather than be shrugged off. The
            // message will retry and eventually dead-letter, where someone can see it.
            throw new InvalidOperationException(
                $"Received {trigger} for order {orderId}, which has no event stream.");
        }

        if (OrderRehydrator.IsTerminal(order.State))
        {
            // THE terminal guard. A redelivered event on a finished order does nothing — no second
            // refund, no second release, no second email.
            logger.LogInformation(
                "Ignoring {Trigger} for order {OrderId}: already {State}.", trigger, orderId, order.State);

            return null;
        }

        return order;
    }


    /// <summary>
    /// Same (order, message type) always yields the same MessageId, so a replayed handler re-sends
    /// a message the receiver has already processed and its idempotency guard discards it.
    /// </summary>
    /// <remarks>
    /// Delegates to the shared convention rather than re-implementing the hash. The recovery sweeper
    /// re-sends the very same commands this saga does, and if the two derived ids by even slightly
    /// different means, every re-drive would look like a brand-new message to the receiver and charge
    /// the customer twice.
    /// </remarks>
    private static Guid DeterministicMessageId(Guid orderId, string discriminator) =>
        MessagingConventions.DeterministicMessageId(orderId, discriminator);
}
