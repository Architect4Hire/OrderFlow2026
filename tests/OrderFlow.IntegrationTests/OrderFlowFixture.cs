using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace OrderFlow.IntegrationTests;

/// <summary>What the tests post to place an order. SKU and quantity — no price (ADR-006).</summary>
public sealed record PlaceOrderRequest(string CustomerRef, IReadOnlyList<OrderLineRequest> Lines);

public sealed record OrderLineRequest(string Sku, int Quantity);

/// <summary>What comes back. A subset — the tests only assert on what they care about.</summary>
public sealed record OrderStatus(Guid Id, string State, decimal Total, string FailureReason);

public sealed record StockStatus(string Sku, int OnHand, int Reserved, int Available, decimal UnitPrice);

public sealed record PaymentStatusView(Guid OrderId, string Status, decimal Amount, string DeclineReason);

/// <summary>
/// Boots the real distributed application: SQL, Cosmos, Redis and Service Bus in containers, and all
/// five services wired against them. One instance per test class, because starting the emulators is
/// slow (the Cosmos one especially) and the tests are read-mostly against independent orders.
/// </summary>
/// <remarks>
/// Failure injection is done through the AppHost's parameters — the same levers an operator uses in a
/// demo. A test that reached into a service to make it fail would be testing a thing no operator can
/// reproduce.
/// </remarks>
public class OrderFlowFixture(Dictionary<string, string?>? parameters = null) : IAsyncLifetime
{
    private DistributedApplication? _app;

    public HttpClient OrderApi { get; private set; } = null!;

    public HttpClient InventoryApi { get; private set; } = null!;

    public HttpClient PaymentApi { get; private set; } = null!;

    public HttpClient NotificationApi { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.OrderFlow_AppHost>()
            .ConfigureAwait(false);

        foreach (var (key, value) in parameters ?? [])
        {
            builder.Configuration[$"Parameters:{key}"] = value;
        }

        _app = await builder.BuildAsync().ConfigureAwait(false);

        await _app.StartAsync().ConfigureAwait(false);

        OrderApi = _app.CreateHttpClient("order-api");
        InventoryApi = _app.CreateHttpClient("inventory-api");
        PaymentApi = _app.CreateHttpClient("payment-api");
        NotificationApi = _app.CreateHttpClient("notification-api");

        // The emulators are slow to come up — the Cosmos one especially. Waiting on health rather
        // than sleeping means the tests are slow only when the containers are.
        var startup = TimeSpan.FromMinutes(5);

        foreach (var service in (string[])["order-api", "inventory-api", "payment-api", "fulfillment-api", "notification-api"])
        {
            using var cts = new CancellationTokenSource(startup);

            await _app.ResourceNotifications
                .WaitForResourceHealthyAsync(service, cts.Token)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    public async Task<Guid> PlaceOrderAsync(string sku, int quantity, string customerRef = "CUST-TEST")
    {
        var response = await OrderApi.PostAsJsonAsync(
            "/api/Orders",
            new PlaceOrderRequest(customerRef, [new OrderLineRequest(sku, quantity)]));

        response.EnsureSuccessStatusCode();

        var order = await response.Content.ReadFromJsonAsync<OrderStatus>();

        return order!.Id;
    }

    /// <summary>
    /// Polls until the order reaches a terminal state, or gives up.
    /// </summary>
    /// <remarks>
    /// Polling, not a fixed sleep. The whole architecture is asynchronous, so "how long does an order
    /// take" is not a number the test is allowed to assume — and a sleep long enough to be reliable is
    /// a sleep long enough to make the suite unbearable.
    /// </remarks>
    public async Task<OrderStatus> WaitForTerminalAsync(Guid orderId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));

        OrderStatus? last = null;

        while (DateTime.UtcNow < deadline)
        {
            last = await OrderApi.GetFromJsonAsync<OrderStatus>($"/api/Orders/{orderId}");

            if (last is { State: "Confirmed" or "Failed" })
            {
                return last;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Order {orderId} did not reach a terminal state. Last seen: {last?.State ?? "(not found)"}. " +
            $"Timeline: {await OrderApi.GetStringAsync($"/api/Orders/{orderId}/timeline")}");
    }

    public async Task<StockStatus> GetStockAsync(string sku)
    {
        var stock = await InventoryApi.GetFromJsonAsync<List<StockStatus>>("/api/Inventory");

        return stock!.Single(item => item.Sku == sku);
    }

    public async Task<IReadOnlyList<PaymentStatusView>> GetPaymentsAsync(Guid orderId) =>
        await PaymentApi.GetFromJsonAsync<List<PaymentStatusView>>($"/api/Payments/order/{orderId}") ?? [];

    public async Task<JsonElement> GetTimelineAsync(Guid orderId) =>
        await OrderApi.GetFromJsonAsync<JsonElement>($"/api/Orders/{orderId}/timeline");
}
