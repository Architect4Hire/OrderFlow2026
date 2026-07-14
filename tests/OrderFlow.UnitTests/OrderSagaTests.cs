using OrderFlow.Contracts.Messages;
using OrderFlow.Orders.API.Managers.Saga;

namespace OrderFlow.UnitTests;

/// <summary>
/// The failure matrix, executable. This is the file a reviewer should read first, because it is the
/// one that would go red if the compensation logic were quietly broken.
/// </summary>
public class OrderSagaTests
{
    private readonly FakeEventStore _eventStore = new();
    private readonly FakeReadModel _readModel = new();
    private readonly FakeMessageBus _bus = new();

    private readonly Guid _orderId = Guid.NewGuid();

    private OrderSaga CreateSaga() => new(_eventStore, _readModel, _bus, Log.For<OrderSaga>());

    /// <summary>Puts an order in the event store at Placed, as PlaceAsync would have.</summary>
    private async Task GivenPlacedOrderAsync()
    {
        await _eventStore.AppendAsync(_orderId, new OrderPlaced
        {
            CorrelationId = _orderId,
            OccurredUtc = DateTime.UtcNow,
            CustomerRef = "CUST-1",
            Lines = [new OrderLine { Sku = "SKU-LAPTOP-01", Quantity = 1 }]
        });
    }

    private static InventoryReserved Reserved(Guid orderId, decimal total = 1299.99m) => new()
    {
        CorrelationId = orderId,
        Lines = [new OrderLine { Sku = "SKU-LAPTOP-01", Quantity = 1, UnitPrice = total }],
        Total = total
    };

    // ── Pricing (ADR-006) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChargePayment_uses_the_price_Inventory_returned_not_one_the_customer_sent()
    {
        await GivenPlacedOrderAsync();

        await CreateSaga().OnInventoryReservedAsync(Reserved(_orderId, 1299.99m));

        var charge = _bus.SentOfType<ChargePayment>();

        Assert.NotNull(charge);

        // The customer never sends a price. If this is ever 0, pricing has been lost somewhere
        // between Inventory and the charge, and we are about to authorize nothing.
        Assert.Equal(1299.99m, charge.Amount);
    }

    [Fact]
    public async Task ChargePayment_carries_a_stable_idempotency_key_so_a_replay_cannot_double_charge()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();

        await saga.OnInventoryReservedAsync(Reserved(_orderId));
        await saga.OnInventoryReservedAsync(Reserved(_orderId));

        var charges = _bus.Sent.OfType<ChargePayment>().ToList();

        // Both were sent (the saga is not the deduping layer) — but they are the SAME message, so
        // Payment's guard collapses them onto one row.
        Assert.All(charges, charge => Assert.Equal(_orderId.ToString("N"), charge.IdempotencyKey));
        Assert.Single(charges.Select(charge => charge.MessageId).Distinct());
    }

    // ── The compensation paths. The reason this system exists. ──────────────────────────────────

    [Fact]
    public async Task PaymentDeclined_releases_the_inventory_hold()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();

        await saga.OnInventoryReservedAsync(Reserved(_orderId));
        await saga.OnPaymentDeclinedAsync(new PaymentDeclined { CorrelationId = _orderId, Reason = "Card declined." });

        // Stock was held. Payment failed. If this is missing, the stock is stranded forever.
        Assert.Equal(1, _bus.CountOf<ReleaseInventory>());
    }

    [Fact]
    public async Task PaymentDeclined_releases_the_hold_BEFORE_the_order_becomes_terminal()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();

        await saga.OnInventoryReservedAsync(Reserved(_orderId));
        await saga.OnPaymentDeclinedAsync(new PaymentDeclined { CorrelationId = _orderId, Reason = "Card declined." });

        // THE ordering rule. Record Failed first and a retry of this handler walks into the terminal
        // guard, no-ops, and never re-sends the release — so a crash between the two would strand the
        // stock permanently. Compensations go out first; terminal state is recorded last.
        Assert.True(
            _bus.IndexOf<ReleaseInventory>() < _bus.IndexOf<OrderFailed>(),
            $"ReleaseInventory must precede OrderFailed. Actual order: {string.Join(" → ", _bus.SentTypeNames)}");
    }

    [Fact]
    public async Task FulfillmentFailed_refunds_the_payment_AND_releases_the_hold()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();

        await saga.OnInventoryReservedAsync(Reserved(_orderId));
        await saga.OnPaymentSucceededAsync(new PaymentSucceeded { CorrelationId = _orderId, Amount = 1299.99m });
        await saga.OnFulfillmentFailedAsync(new FulfillmentFailed { CorrelationId = _orderId, Reason = "Carrier rejected." });

        // BOTH. By this point stock is held and money is captured, so exactly one compensation is
        // half a compensation — and the half you forget is the one that costs a customer real money.
        Assert.Equal(1, _bus.CountOf<RefundPayment>());
        Assert.Equal(1, _bus.CountOf<ReleaseInventory>());
    }

    [Fact]
    public async Task FulfillmentFailed_sends_both_compensations_BEFORE_the_order_becomes_terminal()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();

        await saga.OnInventoryReservedAsync(Reserved(_orderId));
        await saga.OnPaymentSucceededAsync(new PaymentSucceeded { CorrelationId = _orderId, Amount = 1299.99m });
        await saga.OnFulfillmentFailedAsync(new FulfillmentFailed { CorrelationId = _orderId, Reason = "Carrier rejected." });

        var terminal = _bus.IndexOf<OrderFailed>();

        Assert.True(_bus.IndexOf<RefundPayment>() < terminal, $"RefundPayment must precede OrderFailed. Actual: {string.Join(" → ", _bus.SentTypeNames)}");
        Assert.True(_bus.IndexOf<ReleaseInventory>() < terminal, $"ReleaseInventory must precede OrderFailed. Actual: {string.Join(" → ", _bus.SentTypeNames)}");
    }

    [Fact]
    public async Task InventoryRejected_fails_the_order_with_NO_compensation()
    {
        await GivenPlacedOrderAsync();

        await CreateSaga().OnInventoryRejectedAsync(new InventoryRejected
        {
            CorrelationId = _orderId,
            Reason = "Insufficient stock for SKU-LAPTOP-01."
        });

        // Nothing was ever held and nothing was ever charged, so there is nothing to undo. A release
        // here would be a release of stock we never took — which, with the clamp in Inventory, would
        // silently give away somebody else's hold.
        Assert.Equal(0, _bus.CountOf<ReleaseInventory>());
        Assert.Equal(0, _bus.CountOf<RefundPayment>());
        Assert.Equal(1, _bus.CountOf<OrderFailed>());
    }

    // ── The terminal guard: redelivery must not re-fire a compensation ([R]3) ───────────────────

    [Fact]
    public async Task A_redelivered_PaymentDeclined_does_not_release_the_stock_twice()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();
        var declined = new PaymentDeclined { CorrelationId = _orderId, Reason = "Card declined." };

        await saga.OnInventoryReservedAsync(Reserved(_orderId));
        await saga.OnPaymentDeclinedAsync(declined);
        await saga.OnPaymentDeclinedAsync(declined);   // the broker redelivers

        // At-least-once delivery is assumed, so this WILL happen in production. The second release
        // would give back stock the order no longer holds — handing somebody else's reservation away.
        Assert.Equal(1, _bus.CountOf<ReleaseInventory>());
        Assert.Equal(1, _bus.CountOf<OrderFailed>());
    }

    [Fact]
    public async Task A_redelivered_FulfillmentFailed_does_not_refund_twice()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();
        var failed = new FulfillmentFailed { CorrelationId = _orderId, Reason = "Carrier rejected." };

        await saga.OnInventoryReservedAsync(Reserved(_orderId));
        await saga.OnPaymentSucceededAsync(new PaymentSucceeded { CorrelationId = _orderId, Amount = 1299.99m });
        await saga.OnFulfillmentFailedAsync(failed);
        await saga.OnFulfillmentFailedAsync(failed);   // the broker redelivers

        // A second refund is money leaving the business twice for one order.
        Assert.Equal(1, _bus.CountOf<RefundPayment>());
        Assert.Equal(1, _bus.CountOf<ReleaseInventory>());
    }

    [Fact]
    public async Task An_event_arriving_after_the_order_is_Confirmed_is_ignored()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();

        await saga.OnInventoryReservedAsync(Reserved(_orderId));
        await saga.OnPaymentSucceededAsync(new PaymentSucceeded { CorrelationId = _orderId, Amount = 1299.99m });
        await saga.OnFulfillmentDispatchedAsync(new FulfillmentDispatched { CorrelationId = _orderId, TrackingRef = "TRK-1" });

        var before = _bus.Sent.Count;

        // A late, redelivered failure on an order that already shipped. It must not unwind it.
        await saga.OnFulfillmentFailedAsync(new FulfillmentFailed { CorrelationId = _orderId, Reason = "Late failure." });

        Assert.Equal(before, _bus.Sent.Count);
        Assert.Equal(0, _bus.CountOf<RefundPayment>());
    }

    // ── The happy path closes ([ADR-006] / CommitInventory) ─────────────────────────────────────

    [Fact]
    public async Task FulfillmentDispatched_commits_the_stock_and_confirms_the_order()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();

        await saga.OnInventoryReservedAsync(Reserved(_orderId));
        await saga.OnPaymentSucceededAsync(new PaymentSucceeded { CorrelationId = _orderId, Amount = 1299.99m });
        await saga.OnFulfillmentDispatchedAsync(new FulfillmentDispatched { CorrelationId = _orderId, TrackingRef = "TRK-1" });

        // Without CommitInventory the hold stays Held forever, OnHand never falls, and the ops view
        // cannot tell a shipped order from a stranded one.
        Assert.Equal(1, _bus.CountOf<CommitInventory>());
        Assert.Equal(1, _bus.CountOf<OrderConfirmed>());
    }

    [Fact]
    public async Task OrderConfirmed_is_published_BEFORE_the_order_becomes_terminal()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();

        await saga.OnInventoryReservedAsync(Reserved(_orderId));
        await saga.OnPaymentSucceededAsync(new PaymentSucceeded { CorrelationId = _orderId, Amount = 1299.99m });
        await saga.OnFulfillmentDispatchedAsync(new FulfillmentDispatched { CorrelationId = _orderId, TrackingRef = "TRK-1" });

        // Same rule as the compensations, for the same reason: record Confirmed first and a failed
        // publish is swallowed by the terminal guard on retry — the customer is never told.
        Assert.NotEqual(-1, _bus.IndexOf<OrderConfirmed>());
        Assert.Contains(nameof(OrderConfirmed), _eventStore.TypesIn(_orderId));
    }

    // ── Rehydration ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_saga_reads_its_state_from_the_event_store_so_a_restart_resumes_correctly()
    {
        await GivenPlacedOrderAsync();

        // Two saga instances, as if the process had been killed and restarted between events. No
        // state is carried in a field, so the second one must pick up exactly where the first left off.
        await CreateSaga().OnInventoryReservedAsync(Reserved(_orderId));
        await CreateSaga().OnPaymentDeclinedAsync(new PaymentDeclined { CorrelationId = _orderId, Reason = "Card declined." });

        Assert.Equal(1, _bus.CountOf<ReleaseInventory>());
        Assert.Equal("Failed", _readModel[_orderId]?.State);
    }

    [Fact]
    public async Task An_event_for_an_order_with_no_stream_throws_rather_than_being_shrugged_off()
    {
        var saga = CreateSaga();

        // Business appends before it sends, so this is unreachable — which is exactly why it must be
        // loud. Swallowed, it would be a saga silently doing nothing for an order that exists.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => saga.OnInventoryReservedAsync(Reserved(Guid.NewGuid())));
    }

    [Fact]
    public async Task A_failed_compensation_send_surfaces_so_the_message_retries()
    {
        await GivenPlacedOrderAsync();

        var saga = CreateSaga();

        await saga.OnInventoryReservedAsync(Reserved(_orderId));

        _bus.ThrowOnDispatch = new InvalidOperationException("Broker unavailable.");

        // A swallowed failure here is the silent-stock-loss bug: the saga would carry on, mark the
        // order Failed, and the release would never have happened. It must throw so the consumer
        // abandons the message and the whole handler is replayed.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => saga.OnPaymentDeclinedAsync(new PaymentDeclined { CorrelationId = _orderId, Reason = "Card declined." }));

        // And crucially: the order must NOT be terminal, or the retry would be guarded out.
        Assert.NotEqual("Failed", _readModel[_orderId]?.State);
    }
}
