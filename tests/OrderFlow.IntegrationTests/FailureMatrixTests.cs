namespace OrderFlow.IntegrationTests;

/// <summary>
/// The happy path, end to end, through five real services and four real backing stores.
/// </summary>
public class HappyPathTests : IClassFixture<OrderFlowFixture>
{
    private readonly OrderFlowFixture _flow;

    public HappyPathTests(OrderFlowFixture flow) => _flow = flow;

    [Fact]
    public async Task An_order_is_reserved_charged_dispatched_and_confirmed()
    {
        var before = await _flow.GetStockAsync("SKU-MOUSE-01");

        var orderId = await _flow.PlaceOrderAsync("SKU-MOUSE-01", 2);

        var order = await _flow.WaitForTerminalAsync(orderId);

        Assert.Equal("Confirmed", order.State);

        // Priced by the CATALOGUE, not the caller — the request carried no price at all (ADR-006).
        Assert.Equal(before.UnitPrice * 2, order.Total);

        var after = await _flow.GetStockAsync("SKU-MOUSE-01");

        // CommitInventory closed the happy path: OnHand fell, and nothing is left held. Before
        // CommitInventory existed, Reserved would still be sitting at 2 forever.
        Assert.Equal(before.OnHand - 2, after.OnHand);
        Assert.Equal(before.Reserved, after.Reserved);

        var payments = await _flow.GetPaymentsAsync(orderId);

        // ONE payment row. Two would mean the idempotency guard failed and a customer was charged twice.
        Assert.Single(payments);
        Assert.Equal("Captured", payments[0].Status);
    }
}

/// <summary>
/// <b>Concurrent purchase of the last unit.</b> The row this whole service exists to satisfy.
/// </summary>
/// <remarks>
/// This is the test the unit suite explicitly CANNOT write. The guarantee is made by SQL Server's
/// row-version predicate, not by any C# in the codebase, so proving it requires a real database and
/// two genuinely concurrent writers. A fake that "modelled" the race would be a fake asserting its own
/// behaviour.
/// </remarks>
public class ConcurrentLastUnitTests : IClassFixture<OrderFlowFixture>
{
    private readonly OrderFlowFixture _flow;

    public ConcurrentLastUnitTests(OrderFlowFixture flow) => _flow = flow;

    [Fact]
    public async Task Two_concurrent_orders_for_the_last_unit_produce_exactly_one_winner()
    {
        var before = await _flow.GetStockAsync("SKU-LAST-1");

        Assert.Equal(1, before.Available);

        // Fired together, into a consumer running at MaxConcurrentCalls = 8, so they genuinely
        // contend on the same StockItem row. Serialize the consumer and this test passes for the
        // wrong reason.
        var first = _flow.PlaceOrderAsync("SKU-LAST-1", 1, "CUST-A");
        var second = _flow.PlaceOrderAsync("SKU-LAST-1", 1, "CUST-B");

        var orderIds = await Task.WhenAll(first, second);

        var outcomes = await Task.WhenAll(orderIds.Select(id => _flow.WaitForTerminalAsync(id)));

        // Exactly one wins. Not two — that is the oversell. Not zero.
        Assert.Equal(1, outcomes.Count(order => order.State == "Confirmed"));
        Assert.Equal(1, outcomes.Count(order => order.State == "Failed"));

        var loser = outcomes.Single(order => order.State == "Failed");

        Assert.Contains("Insufficient stock", loser.FailureReason);

        var after = await _flow.GetStockAsync("SKU-LAST-1");

        // And the loser got a CLEAN rejection: no negative availability, no stranded hold.
        Assert.Equal(0, after.Available);
        Assert.Equal(0, after.Reserved);
        Assert.True(after.OnHand >= 0, "OnHand must never go negative.");
    }
}

/// <summary>
/// <b>Payment declined.</b> The compensation path: the hold must come back.
/// </summary>
public class PaymentDeclinedTests : IClassFixture<PaymentDeclinedTests.DecliningFlow>
{
    /// <summary>Every charge declines. The lever is the AppHost parameter an operator would use.</summary>
    public class DecliningFlow() : OrderFlowFixture(new() { ["payment-decline-all"] = "true" });

    private readonly DecliningFlow _flow;

    public PaymentDeclinedTests(DecliningFlow flow) => _flow = flow;

    [Fact]
    public async Task A_declined_payment_releases_the_inventory_hold_and_fails_the_order()
    {
        var before = await _flow.GetStockAsync("SKU-KEYBOARD-01");

        var orderId = await _flow.PlaceOrderAsync("SKU-KEYBOARD-01", 1);

        var order = await _flow.WaitForTerminalAsync(orderId);

        Assert.Equal("Failed", order.State);

        var after = await _flow.GetStockAsync("SKU-KEYBOARD-01");

        // THE assertion. Stock was held, payment failed, and the hold came back. Everything else in
        // this architecture is in service of this line being true.
        Assert.Equal(before.OnHand, after.OnHand);
        Assert.Equal(before.Reserved, after.Reserved);
        Assert.Equal(before.Available, after.Available);
    }
}

/// <summary>
/// <b>Carrier fails permanently.</b> The double compensation: refund AND release.
/// </summary>
public class FulfillmentFailedTests : IClassFixture<FulfillmentFailedTests.FailingCarrierFlow>
{
    public class FailingCarrierFlow() : OrderFlowFixture(new() { ["carrier-failure-mode"] = "Permanent" });

    private readonly FailingCarrierFlow _flow;

    public FulfillmentFailedTests(FailingCarrierFlow flow) => _flow = flow;

    [Fact]
    public async Task A_permanent_carrier_failure_refunds_the_payment_AND_releases_the_hold()
    {
        var before = await _flow.GetStockAsync("SKU-MONITOR-01");

        var orderId = await _flow.PlaceOrderAsync("SKU-MONITOR-01", 1);

        var order = await _flow.WaitForTerminalAsync(orderId);

        Assert.Equal("Failed", order.State);

        // By the time fulfillment failed, stock was held AND money was captured. One compensation is
        // half a compensation, and the half you forget is the half that costs a customer real money.
        var after = await _flow.GetStockAsync("SKU-MONITOR-01");

        Assert.Equal(before.Available, after.Available);
        Assert.Equal(before.Reserved, after.Reserved);

        var payments = await _flow.GetPaymentsAsync(orderId);

        Assert.Equal("Refunded", Assert.Single(payments).Status);
    }
}

/// <summary>
/// <b>Notification provider down.</b> The boundary: the order must not notice.
/// </summary>
public class NotificationDownTests : IClassFixture<NotificationDownTests.SilentFlow>
{
    public class SilentFlow() : OrderFlowFixture(new() { ["notification-provider-down"] = "true" });

    private readonly SilentFlow _flow;

    public NotificationDownTests(SilentFlow flow) => _flow = flow;

    [Fact]
    public async Task An_order_completes_perfectly_even_though_the_customer_is_never_told()
    {
        var orderId = await _flow.PlaceOrderAsync("SKU-MOUSE-01", 1);

        var order = await _flow.WaitForTerminalAsync(orderId);

        // The entire point of the Notification service is that this assertion holds. A best-effort
        // dependency that can fail an order is not best-effort; it is a dependency.
        Assert.Equal("Confirmed", order.State);
    }
}
