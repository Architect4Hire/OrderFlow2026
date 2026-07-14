using Microsoft.EntityFrameworkCore;
using OrderFlow.Inventory.API.Managers.DataContext;
using OrderFlow.Inventory.API.Managers.Domain;

namespace OrderFlow.Inventory.API.Managers.Data;

/// <summary>Why a hold did or did not happen. Insufficient stock is an outcome, not an exception ([R]5).</summary>
public enum HoldOutcome
{
    /// <summary>Stock is now held and a Reservation row exists to prove it.</summary>
    Held = 0,

    /// <summary>Not enough Available. A normal business answer.</summary>
    InsufficientStock = 1,

    /// <summary>No StockItem for that SKU. Also a business answer — we simply do not sell it.</summary>
    UnknownSku = 2,

    /// <summary>
    /// Lost the optimistic-concurrency race too many times in a row. The line is rejected rather
    /// than retried forever ([R]4): under a load test this is the honest answer, and an unbounded
    /// loop would turn contention into a hang.
    /// </summary>
    ConcurrencyExhausted = 3
}

public interface IInventoryData
{
    /// <summary>
    /// Take one line's hold: increment Reserved if Available covers it, and write the Reservation
    /// row that records the hold — in a single transaction.
    /// </summary>
    Task<HoldOutcome> TryHoldAsync(Guid orderId, string sku, int quantity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Give back every hold this order is carrying. Returns how many reservations were released;
    /// zero is a valid, expected answer.
    /// </summary>
    Task<int> ReleaseAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockItem>> ListStockAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Reservation>> ListReservationsAsync(Guid orderId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every write to a <see cref="StockItem"/> in this service goes through here, and every one of them
/// is guarded by the row version.
/// </summary>
/// <remarks>
/// The shape to notice: read, decide, write-if-unchanged, and on a lost race <b>reload and decide
/// again</b> — never reload and blindly re-apply. The re-decision is the point. A retry that
/// re-applies the original decision against a row that has since moved is just the oversell race
/// with extra steps.
/// </remarks>
public class InventoryData(InventoryDbContext context, ILogger<InventoryData> logger) : IInventoryData
{
    /// <summary>Bounded ([R]4). Three losses in a row means real contention, not a blip.</summary>
    private const int MaxConcurrencyAttempts = 3;

    public async Task<HoldOutcome> TryHoldAsync(
        Guid orderId,
        string sku,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            var stockItem = await context.StockItems
                .FirstOrDefaultAsync(item => item.Sku == sku, cancellationToken);

            if (stockItem is null)
            {
                return HoldOutcome.UnknownSku;
            }

            // The check happens on values we loaded, so by the time we write they may be stale —
            // which is precisely what the row version is for. This is not a TOCTOU bug; it is a
            // TOCTOU bug that the database is about to catch.
            if (stockItem.Available < quantity)
            {
                return HoldOutcome.InsufficientStock;
            }

            stockItem.Reserved += quantity;
            stockItem.UpdatedUtc = DateTime.UtcNow;

            // Written in the SAME SaveChanges as the increment, so they land in one transaction.
            // Split them and a crash in between leaves stock held with no record of who holds it —
            // unreleasable, invisible, and gone until someone edits the table by hand.
            var reservation = new Reservation
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                Sku = sku,
                Quantity = quantity,
                State = ReservationState.Held
            };

            context.Reservations.Add(reservation);

            try
            {
                // UPDATE StockItems SET Reserved = @new, UpdatedUtc = @now
                //  WHERE Sku = @sku AND RowVersion = @loaded    <-- [R]1
                // INSERT INTO Reservations ...
                await context.SaveChangesAsync(cancellationToken);

                return HoldOutcome.Held;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone moved the row between our read and our write. Nothing committed.
                logger.LogWarning(
                    "Concurrency conflict holding {Quantity} of {Sku} for order {OrderId} (attempt {Attempt} of {MaxAttempts}). Reloading.",
                    quantity, sku, orderId, attempt, MaxConcurrencyAttempts);

                // Detach the insert we did not get to make. Leave it Added and the NEXT line's
                // SaveChanges would drag this abandoned reservation in with it — a hold recorded
                // against stock that was never actually reserved.
                context.Entry(reservation).State = EntityState.Detached;

                // Pull the winner's OnHand, Reserved and RowVersion, then go round and re-decide.
                await context.Entry(stockItem).ReloadAsync(cancellationToken);
            }
        }

        logger.LogWarning(
            "Gave up holding {Quantity} of {Sku} for order {OrderId} after {MaxAttempts} concurrency conflicts.",
            quantity, sku, orderId, MaxConcurrencyAttempts);

        return HoldOutcome.ConcurrencyExhausted;
    }

    public async Task<int> ReleaseAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var held = await context.Reservations
            .Where(item => item.OrderId == orderId && item.State == ReservationState.Held)
            .ToListAsync(cancellationToken);

        if (held.Count == 0)
        {
            // The redelivered-ReleaseInventory case, and the never-reserved case. Both are no-ops,
            // which is what makes this command safe to send more than once.
            return 0;
        }

        var released = 0;

        // One SKU at a time: the decrement and the tombstones for that SKU commit together, so a
        // crash mid-release can never leave stock given back with the reservations still marked
        // Held — which would let the next redelivery give the same stock back a second time.
        foreach (var group in held.GroupBy(item => item.Sku))
        {
            if (await ReleaseSkuAsync(orderId, group.Key, [.. group], cancellationToken))
            {
                released += group.Count();
            }
        }

        return released;
    }

    private async Task<bool> ReleaseSkuAsync(
        Guid orderId,
        string sku,
        IReadOnlyList<Reservation> reservations,
        CancellationToken cancellationToken)
    {
        var quantity = reservations.Sum(item => item.Quantity);

        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            var stockItem = await context.StockItems
                .FirstOrDefaultAsync(item => item.Sku == sku, cancellationToken);

            if (stockItem is null)
            {
                // A reservation against a SKU that no longer exists. Nothing to give back, but the
                // hold must still be tombstoned or it stays "Held" forever and the ops view reads it
                // as stranded stock.
                logger.LogError(
                    "Order {OrderId} holds {Quantity} of unknown SKU {Sku}. Tombstoning the reservations; stock cannot be restored.",
                    orderId, quantity, sku);

                MarkReleased(reservations);

                await context.SaveChangesAsync(cancellationToken);

                return true;
            }

            if (stockItem.Reserved < quantity)
            {
                // Invariant broken: we are about to give back more than the SKU thinks is held.
                // Clamp rather than drive Reserved negative ([R]3 — Available must never exceed
                // OnHand), and shout, because this means a hold was released twice somewhere.
                logger.LogError(
                    "Order {OrderId} is releasing {Quantity} of {Sku} but only {Reserved} is held. Clamping to zero.",
                    orderId, quantity, sku, stockItem.Reserved);
            }

            stockItem.Reserved = Math.Max(0, stockItem.Reserved - quantity);
            stockItem.UpdatedUtc = DateTime.UtcNow;

            MarkReleased(reservations);

            try
            {
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation(
                    "Released {Quantity} of {Sku} for order {OrderId}.", quantity, sku, orderId);

                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogWarning(
                    "Concurrency conflict releasing {Quantity} of {Sku} for order {OrderId} (attempt {Attempt} of {MaxAttempts}). Reloading.",
                    quantity, sku, orderId, attempt, MaxConcurrencyAttempts);

                // The reservations stay Modified and ride along on the retry; only the stock row is
                // stale, so only the stock row is reloaded.
                await context.Entry(stockItem).ReloadAsync(cancellationToken);
            }
        }

        // A failed release is the one failure in this service that costs real money: the stock stays
        // held for an order that is already dead. It is louder than a warning for that reason, and
        // the caller re-raises so the message is abandoned and retried rather than settled.
        logger.LogError(
            "FAILED to release {Quantity} of {Sku} for order {OrderId} after {MaxAttempts} concurrency conflicts. Stock remains held.",
            quantity, sku, orderId, MaxConcurrencyAttempts);

        throw new InvalidOperationException(
            $"Could not release {quantity} of '{sku}' for order {orderId} after {MaxConcurrencyAttempts} concurrency conflicts.");
    }

    private static void MarkReleased(IReadOnlyList<Reservation> reservations)
    {
        foreach (var reservation in reservations)
        {
            reservation.State = ReservationState.Released;
        }
    }

    public async Task<IReadOnlyList<StockItem>> ListStockAsync(CancellationToken cancellationToken = default) =>
        await context.StockItems
            .AsNoTracking()
            .OrderBy(item => item.Sku)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Reservation>> ListReservationsAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        await context.Reservations
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToListAsync(cancellationToken);
}

public static class InventoryDataExtensions
{
    public static IHostApplicationBuilder AddInventoryData(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<IInventoryData, InventoryData>();

        return builder;
    }
}
