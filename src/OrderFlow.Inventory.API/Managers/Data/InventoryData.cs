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

/// <summary>
/// The outcome of one line's hold, plus the CATALOGUE PRICE of the SKU.
/// </summary>
/// <remarks>
/// The price rides back on the hold because this is the moment we have the StockItem row in hand. It
/// is the authoritative number the saga will charge — the customer never sends one (ADR-006).
/// </remarks>
public sealed record HoldResult(HoldOutcome Outcome, decimal UnitPrice)
{
    public static HoldResult Held(decimal unitPrice) => new(HoldOutcome.Held, unitPrice);

    public static HoldResult Rejected(HoldOutcome outcome) => new(outcome, 0m);
}

public interface IInventoryData
{
    /// <summary>
    /// Take one line's hold: increment Reserved if Available covers it, and write the Reservation
    /// row that records the hold — in a single transaction. Returns the catalogue price on success.
    /// </summary>
    Task<HoldResult> TryHoldAsync(Guid orderId, string sku, int quantity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Give back every hold this order is carrying. Returns how many reservations were released;
    /// zero is a valid, expected answer.
    /// </summary>
    Task<int> ReleaseAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The goods shipped: turn this order's holds into a permanent decrement. Returns how many
    /// reservations were consumed; zero is a valid, expected answer.
    /// </summary>
    Task<int> CommitAsync(Guid orderId, CancellationToken cancellationToken = default);

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

    public async Task<HoldResult> TryHoldAsync(
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
                return HoldResult.Rejected(HoldOutcome.UnknownSku);
            }

            // The check happens on values we loaded, so by the time we write they may be stale —
            // which is precisely what the row version is for. This is not a TOCTOU bug; it is a
            // TOCTOU bug that the database is about to catch.
            if (stockItem.Available < quantity)
            {
                return HoldResult.Rejected(HoldOutcome.InsufficientStock);
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

                return HoldResult.Held(stockItem.UnitPrice);
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

        return HoldResult.Rejected(HoldOutcome.ConcurrencyExhausted);
    }

    public Task<int> ReleaseAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        SettleAsync(orderId, ReservationState.Released, cancellationToken);

    public Task<int> CommitAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        SettleAsync(orderId, ReservationState.Consumed, cancellationToken);

    /// <summary>
    /// Both endings of a hold, which are the same operation with one difference.
    /// </summary>
    /// <remarks>
    /// <b>Release</b> gives the stock back: Reserved falls, OnHand does not — the goods are still on
    /// the shelf. <b>Commit</b> takes the stock away for good: Reserved AND OnHand both fall, because
    /// the goods have left the building. <c>Available</c> is unchanged by a commit, which is exactly
    /// right: the stock was already spoken for, and now it is simply gone.
    /// <para>
    /// They share this method because they share the invariant that matters — the stock level and the
    /// reservation's fate move in ONE transaction. Write them separately and a crash in between
    /// either gives stock back twice or loses it entirely.
    /// </para>
    /// </remarks>
    private async Task<int> SettleAsync(Guid orderId, ReservationState target, CancellationToken cancellationToken)
    {
        var held = await context.Reservations
            .Where(item => item.OrderId == orderId && item.State == ReservationState.Held)
            .ToListAsync(cancellationToken);

        if (held.Count == 0)
        {
            // The redelivered-compensation case, the redelivered-commit case, and the never-reserved
            // case. All no-ops, which is what makes these commands safe to send more than once.
            return 0;
        }

        var settled = 0;

        // One SKU at a time: the stock move and the tombstones for that SKU commit together, so a
        // crash mid-settle can never leave stock given back with the reservations still marked
        // Held — which would let the next redelivery give the same stock back a second time.
        foreach (var group in held.GroupBy(item => item.Sku))
        {
            if (await SettleSkuAsync(orderId, group.Key, [.. group], target, cancellationToken))
            {
                settled += group.Count();
            }
        }

        return settled;
    }

    private async Task<bool> SettleSkuAsync(
        Guid orderId,
        string sku,
        IReadOnlyList<Reservation> reservations,
        ReservationState target,
        CancellationToken cancellationToken)
    {
        var quantity = reservations.Sum(item => item.Quantity);
        var consuming = target == ReservationState.Consumed;

        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            var stockItem = await context.StockItems
                .FirstOrDefaultAsync(item => item.Sku == sku, cancellationToken);

            if (stockItem is null)
            {
                // A reservation against a SKU that no longer exists. Nothing to move, but the hold
                // must still be tombstoned or it stays "Held" forever and the ops view reads it as
                // stranded stock.
                logger.LogError(
                    "Order {OrderId} holds {Quantity} of unknown SKU {Sku}. Tombstoning as {Target}; stock cannot be adjusted.",
                    orderId, quantity, sku, target);

                MarkAs(reservations, target);

                await context.SaveChangesAsync(cancellationToken);

                return true;
            }

            if (stockItem.Reserved < quantity)
            {
                // Invariant broken: we are about to settle more than the SKU thinks is held. Clamp
                // rather than drive Reserved negative ([R]3 — Available must never exceed OnHand),
                // and shout, because this means a hold was settled twice somewhere.
                logger.LogError(
                    "Order {OrderId} is settling {Quantity} of {Sku} but only {Reserved} is held. Clamping to zero.",
                    orderId, quantity, sku, stockItem.Reserved);
            }

            stockItem.Reserved = Math.Max(0, stockItem.Reserved - quantity);

            if (consuming)
            {
                // The goods shipped. They are no longer on the shelf.
                stockItem.OnHand = Math.Max(0, stockItem.OnHand - quantity);
            }

            stockItem.UpdatedUtc = DateTime.UtcNow;

            MarkAs(reservations, target);

            try
            {
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation(
                    "{Target} {Quantity} of {Sku} for order {OrderId}.", target, quantity, sku, orderId);

                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogWarning(
                    "Concurrency conflict settling {Quantity} of {Sku} for order {OrderId} (attempt {Attempt} of {MaxAttempts}). Reloading.",
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
            "FAILED to settle {Quantity} of {Sku} for order {OrderId} as {Target} after {MaxAttempts} concurrency conflicts.",
            quantity, sku, orderId, target, MaxConcurrencyAttempts);

        throw new InvalidOperationException(
            $"Could not settle {quantity} of '{sku}' for order {orderId} as {target} after {MaxConcurrencyAttempts} concurrency conflicts.");
    }

    private static void MarkAs(IReadOnlyList<Reservation> reservations, ReservationState state)
    {
        foreach (var reservation in reservations)
        {
            reservation.State = state;
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
