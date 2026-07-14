namespace OrderFlow.Inventory.API.Managers.Domain;

/// <summary>What a hold is doing right now.</summary>
public enum ReservationState
{
    /// <summary>Stock is spoken for. Counted in <see cref="StockItem.Reserved"/>.</summary>
    Held = 0,

    /// <summary>The saga compensated and gave the stock back. No longer counted.</summary>
    Released = 1
}

/// <summary>
/// One SKU's worth of stock held for one order — the record of what the saga is carrying.
/// </summary>
/// <remarks>
/// <para>
/// This exists so <c>ReleaseInventory</c> can be honoured. That command carries only the
/// CorrelationId (the order id) and a reason — no line items, no reservation ids — so the only way
/// Inventory can work out what to give back is to look up the rows it wrote under that
/// <see cref="OrderId"/>. A hold that never got a Reservation row is stock that can never be
/// released: it is leaked until someone edits the database by hand.
/// </para>
/// <para>
/// A row per (order, SKU), not per order: an order for three SKUs takes three holds, and a release
/// has to unwind each against its own <see cref="StockItem"/>.
/// </para>
/// <para>
/// The state transition itself — Held to Released, decrementing <see cref="StockItem.Reserved"/> in
/// the same transaction — belongs to the business manager, not here. Marking a reservation Released
/// without also moving the stock level is the silent-stock-loss bug in miniature.
/// </para>
/// </remarks>
public class Reservation
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The order this hold belongs to. Equal to the saga's CorrelationId, and the only
    /// handle <c>ReleaseInventory</c> gives us — so it is the column that gets the index.</summary>
    public Guid OrderId { get; set; }

    /// <summary>The SKU being held. Points at the <see cref="StockItem"/> to unwind.</summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>How many units are held.</summary>
    public int Quantity { get; set; }

    /// <summary>
    /// Held or Released. Released is a tombstone, not a delete: the row stays so the audit trail can
    /// answer "what did this order hold, and did it give it back", and so a redelivered
    /// <c>ReleaseInventory</c> can see the hold is already gone and no-op instead of double-releasing
    /// stock the system does not have.
    /// </summary>
    public ReservationState State { get; set; } = ReservationState.Held;
}
