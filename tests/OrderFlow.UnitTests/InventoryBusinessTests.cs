using OrderFlow.Inventory.API.Managers.Business;
using OrderFlow.Inventory.API.Managers.Data;
using OrderFlow.Inventory.API.Managers.Domain;
using OrderFlow.Inventory.API.Managers.ServiceModels;

namespace OrderFlow.UnitTests;

/// <summary>
/// A fake stock ledger. Models availability and holds honestly, but NOT the row-version race — that
/// is the database's job, and only the integration suite can prove it.
/// </summary>
public sealed class FakeInventoryData : IInventoryData
{
    private readonly Dictionary<string, (int OnHand, decimal Price)> _stock = [];
    private readonly List<Reservation> _reservations = [];

    /// <summary>Calls to ReleaseAsync. The intra-call unwind is counted here.</summary>
    public int ReleaseCalls { get; private set; }

    public int CommitCalls { get; private set; }

    public void Stock(string sku, int onHand, decimal price) => _stock[sku] = (onHand, price);

    public int HeldFor(Guid orderId) =>
        _reservations.Where(r => r.OrderId == orderId && r.State == ReservationState.Held).Sum(r => r.Quantity);

    public int TotalHeld => _reservations.Where(r => r.State == ReservationState.Held).Sum(r => r.Quantity);

    public Task<HoldResult> TryHoldAsync(Guid orderId, string sku, int quantity, CancellationToken cancellationToken = default)
    {
        if (!_stock.TryGetValue(sku, out var item))
        {
            return Task.FromResult(HoldResult.Rejected(HoldOutcome.UnknownSku));
        }

        var held = _reservations
            .Where(r => r.Sku == sku && r.State == ReservationState.Held)
            .Sum(r => r.Quantity);

        if (item.OnHand - held < quantity)
        {
            return Task.FromResult(HoldResult.Rejected(HoldOutcome.InsufficientStock));
        }

        _reservations.Add(new Reservation
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Sku = sku,
            Quantity = quantity,
            State = ReservationState.Held
        });

        return Task.FromResult(HoldResult.Held(item.Price));
    }

    public Task<int> ReleaseAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        ReleaseCalls++;

        return Task.FromResult(Settle(orderId, ReservationState.Released));
    }

    public Task<int> CommitAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        CommitCalls++;

        return Task.FromResult(Settle(orderId, ReservationState.Consumed));
    }

    private int Settle(Guid orderId, ReservationState target)
    {
        var held = _reservations.Where(r => r.OrderId == orderId && r.State == ReservationState.Held).ToList();

        foreach (var reservation in held)
        {
            reservation.State = target;

            if (target == ReservationState.Consumed)
            {
                var item = _stock[reservation.Sku];
                _stock[reservation.Sku] = (item.OnHand - reservation.Quantity, item.Price);
            }
        }

        return held.Count;
    }

    public Task<IReadOnlyList<StockItem>> ListStockAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StockItem>>(
        [
            .. _stock.Select(entry => new StockItem
            {
                Sku = entry.Key,
                OnHand = entry.Value.OnHand,
                UnitPrice = entry.Value.Price,
                Reserved = _reservations.Where(r => r.Sku == entry.Key && r.State == ReservationState.Held).Sum(r => r.Quantity)
            })
        ]);

    public Task<IReadOnlyList<Reservation>> ListReservationsAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Reservation>>([.. _reservations.Where(r => r.OrderId == orderId)]);
}

public class InventoryBusinessTests
{
    private readonly FakeInventoryData _data = new();
    private readonly Guid _orderId = Guid.NewGuid();

    private InventoryBusinessManager CreateBusiness() => new(_data, Log.For<InventoryBusinessManager>());

    [Fact]
    public async Task A_reservation_returns_the_catalogue_price_not_one_the_caller_supplied()
    {
        _data.Stock("SKU-LAPTOP-01", 5, 1299.99m);

        var result = await CreateBusiness().ReserveAsync(_orderId, [("SKU-LAPTOP-01", 2)]);

        Assert.True(result.Success);

        // 2 × 1299.99. The caller asked for a quantity and got a bill; it never proposed a price.
        Assert.Equal(2599.98m, result.Total);
        Assert.Equal(1299.99m, Assert.Single(result.PricedLines).UnitPrice);
    }

    [Fact]
    public async Task If_a_LATER_line_cannot_be_held_the_EARLIER_lines_are_released()
    {
        _data.Stock("SKU-MOUSE-01", 10, 24.50m);
        _data.Stock("SKU-LAST-1", 1, 9.99m);

        // Line 1 succeeds, line 2 asks for two of a SKU with one in stock.
        var result = await CreateBusiness().ReserveAsync(_orderId, [("SKU-MOUSE-01", 1), ("SKU-LAST-1", 2)]);

        Assert.False(result.Success);

        // [R]2. The mouse was HELD before the failure was discovered. Leave it held and that stock is
        // stranded against an order that will never be confirmed — the silent-loss bug, in miniature,
        // inside a single call.
        Assert.Equal(0, _data.HeldFor(_orderId));
        Assert.Equal(0, _data.TotalHeld);
    }

    [Fact]
    public async Task Insufficient_stock_is_a_rejection_not_an_exception()
    {
        _data.Stock("SKU-LAST-1", 1, 9.99m);

        var result = await CreateBusiness().ReserveAsync(_orderId, [("SKU-LAST-1", 5)]);

        // [R]5. "We do not have it" is the system working, not failing. Throwing would dead-letter a
        // perfectly normal business outcome.
        Assert.False(result.Success);
        Assert.Contains("Insufficient stock", result.Reason);
    }

    [Fact]
    public async Task An_unknown_SKU_is_rejected_with_a_reason_that_says_so()
    {
        var result = await CreateBusiness().ReserveAsync(_orderId, [("SKU-NOPE", 1)]);

        Assert.False(result.Success);

        // The reason ends up in InventoryRejected.Reason and then on an operator's screen. "Could not
        // reserve" would be useless there.
        Assert.Contains("Unknown SKU", result.Reason);
    }

    [Fact]
    public async Task Reserving_twice_for_the_same_order_does_not_hold_the_stock_twice()
    {
        _data.Stock("SKU-LAST-1", 1, 9.99m);

        var business = CreateBusiness();

        await business.ReserveAsync(_orderId, [("SKU-LAST-1", 1)]);
        var second = await business.ReserveAsync(_orderId, [("SKU-LAST-1", 1)]);

        // Idempotency by reset. A redelivered ReserveInventory — or a crash between lines on the
        // first attempt — must not end with the order holding the last unit twice.
        Assert.True(second.Success);
        Assert.Equal(1, _data.HeldFor(_orderId));
    }

    [Fact]
    public async Task Releasing_an_order_that_holds_nothing_is_a_no_op()
    {
        // [R]3. The redelivered-compensation case. A throw here would dead-letter the compensation
        // the second time it was asked — which is exactly how stock gets stranded.
        await CreateBusiness().ReleaseAsync(Guid.NewGuid());

        Assert.Equal(0, _data.TotalHeld);
    }

    [Fact]
    public async Task Committing_consumes_the_hold_and_takes_the_stock_off_the_shelf()
    {
        _data.Stock("SKU-LAPTOP-01", 5, 1299.99m);

        var business = CreateBusiness();

        await business.ReserveAsync(_orderId, [("SKU-LAPTOP-01", 2)]);
        await business.CommitAsync(_orderId);

        var stock = await _data.ListStockAsync();
        var laptop = stock.Single(item => item.Sku == "SKU-LAPTOP-01");

        // OnHand falls with Reserved: the goods left the building. Available is unchanged, which is
        // right — that stock was already spoken for.
        Assert.Equal(3, laptop.OnHand);
        Assert.Equal(0, laptop.Reserved);
        Assert.Equal(0, _data.HeldFor(_orderId));
    }

    [Fact]
    public async Task Committing_twice_does_not_take_the_stock_off_the_shelf_twice()
    {
        _data.Stock("SKU-LAPTOP-01", 5, 1299.99m);

        var business = CreateBusiness();

        await business.ReserveAsync(_orderId, [("SKU-LAPTOP-01", 2)]);
        await business.CommitAsync(_orderId);
        await business.CommitAsync(_orderId);   // the broker redelivers

        var laptop = (await _data.ListStockAsync()).Single(item => item.Sku == "SKU-LAPTOP-01");

        Assert.Equal(3, laptop.OnHand);
    }

    [Fact]
    public async Task An_order_with_no_lines_is_rejected()
    {
        var result = await CreateBusiness().ReserveAsync(_orderId, []);

        Assert.False(result.Success);
    }
}
