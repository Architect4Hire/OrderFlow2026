using OrderFlow.Inventory.API.Managers.Data;
using OrderFlow.Inventory.API.Managers.Extensions;
using OrderFlow.Inventory.API.Managers.ServiceModels;

namespace OrderFlow.Inventory.API.Managers.Business;

/// <summary>
/// The answer to a ReserveInventory command. A rejection is a <b>value</b>, not an exception ([R]5):
/// "we do not have the stock" is the system working correctly, and the saga has a first-class path
/// for it.
/// </summary>
public sealed record ReservationResult(bool Success, string Reason)
{
    public static ReservationResult Reserved() => new(true, string.Empty);

    public static ReservationResult Rejected(string reason) => new(false, reason);
}

public interface IInventoryBusinessManager
{
    Task<ReservationResult> ReserveAsync(
        Guid orderId,
        IReadOnlyList<(string Sku, int Quantity)> lines,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockItemServiceModel>> ListStockAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReservationServiceModel>> ListReservationsAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// All-or-nothing reservation. The failure-matrix row this whole service exists to demonstrate is
/// "concurrent purchase of the last unit": two orders, one unit, exactly one winner and one clean
/// rejection — never two winners, never a negative Available.
/// </summary>
/// <remarks>
/// <para>
/// The per-line race is settled one layer down, by the row version. What lives here is the other
/// half of correctness: <b>an order is not a set of independent lines.</b> Line 3 failing makes the
/// holds already taken for lines 1 and 2 wrong retroactively, and they have to go back ([R]2).
/// Partial holds are stock stranded against an order that will never be confirmed — the exact
/// silent-loss failure this architecture is built to make impossible.
/// </para>
/// <para>
/// The unwind deliberately reuses the same <c>ReleaseAsync</c> the saga's compensation calls. One
/// release path, exercised on every rejected order rather than only on the rare compensation, so it
/// cannot rot unnoticed.
/// </para>
/// </remarks>
public class InventoryBusinessManager(IInventoryData data, ILogger<InventoryBusinessManager> logger)
    : IInventoryBusinessManager
{
    public async Task<ReservationResult> ReserveAsync(
        Guid orderId,
        IReadOnlyList<(string Sku, int Quantity)> lines,
        CancellationToken cancellationToken = default)
    {
        if (lines.Count == 0)
        {
            return ReservationResult.Rejected("Order has no lines.");
        }

        // Idempotency by reset. The consumer's (ConsumerName, MessageId) guard already stops a
        // redelivered ReserveInventory in the normal case — but that store is per-process for the
        // POC, so a restart mid-order can lose it. Clearing this order's holds first means a
        // redelivery re-reserves from a known-empty state instead of holding the same stock twice.
        //
        // It also covers the nastier case the guard cannot: a crash *between* lines, which leaves
        // real holds behind that no idempotency record ever knew about.
        var abandoned = await data.ReleaseAsync(orderId, cancellationToken);

        if (abandoned > 0)
        {
            logger.LogWarning(
                "Order {OrderId} already held {Count} reservation(s) before this reserve. Released them and starting clean.",
                orderId, abandoned);
        }

        foreach (var (sku, quantity) in lines)
        {
            if (quantity <= 0)
            {
                return await RejectAsync(orderId, $"Line '{sku}' has a non-positive quantity of {quantity}.", cancellationToken);
            }

            var outcome = await data.TryHoldAsync(orderId, sku, quantity, cancellationToken);

            if (outcome == HoldOutcome.Held)
            {
                continue;
            }

            var reason = outcome switch
            {
                HoldOutcome.InsufficientStock => $"Insufficient stock for SKU '{sku}': {quantity} requested.",
                HoldOutcome.UnknownSku => $"Unknown SKU '{sku}'.",
                HoldOutcome.ConcurrencyExhausted => $"SKU '{sku}' is too contended to reserve right now.",
                _ => $"SKU '{sku}' could not be reserved."
            };

            return await RejectAsync(orderId, reason, cancellationToken);
        }

        logger.LogInformation("Reserved {LineCount} line(s) for order {OrderId}.", lines.Count, orderId);

        return ReservationResult.Reserved();
    }

    /// <summary>
    /// [R]2. Give back everything this call already took, then answer. If the release itself fails it
    /// throws rather than returning a tidy rejection — a rejection the saga believes while stock is
    /// still held is worse than no answer at all, because the message would be settled and the leak
    /// made permanent. Throwing abandons the message and it comes back.
    /// </summary>
    private async Task<ReservationResult> RejectAsync(Guid orderId, string reason, CancellationToken cancellationToken)
    {
        await data.ReleaseAsync(orderId, cancellationToken);

        logger.LogInformation("Rejected order {OrderId}: {Reason}", orderId, reason);

        return ReservationResult.Rejected(reason);
    }

    public async Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var released = await data.ReleaseAsync(orderId, cancellationToken);

        logger.LogInformation("Release for order {OrderId} gave back {Count} reservation(s).", orderId, released);
    }

    public async Task<IReadOnlyList<StockItemServiceModel>> ListStockAsync(CancellationToken cancellationToken = default) =>
        (await data.ListStockAsync(cancellationToken)).ToServiceModels();

    public async Task<IReadOnlyList<ReservationServiceModel>> ListReservationsAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        (await data.ListReservationsAsync(orderId, cancellationToken)).ToServiceModels();
}

public static class InventoryBusinessExtensions
{
    public static IHostApplicationBuilder AddInventoryBusiness(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<IInventoryBusinessManager, InventoryBusinessManager>();

        return builder;
    }
}
