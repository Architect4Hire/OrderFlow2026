namespace OrderFlow.Inventory.API.Managers.ServiceModels;

/// <summary>
/// Availability for one SKU, as the ops view sees it. The whole point of the screen is the gap
/// between <see cref="OnHand"/> and <see cref="Available"/>: that gap is stock the saga is holding,
/// and if it stops shrinking, a compensation has been lost somewhere.
/// </summary>
public class StockItemServiceModel
{
    public string Sku { get; set; } = string.Empty;

    /// <summary>Units in the warehouse.</summary>
    public int OnHand { get; set; }

    /// <summary>The catalogue price. This is the number the customer gets charged (ADR-006).</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Units held by in-flight orders.</summary>
    public int Reserved { get; set; }

    /// <summary>
    /// OnHand − Reserved. Sent as a plain value rather than recomputed in the browser: the client
    /// should never be the place that decides what "available" means.
    /// </summary>
    public int Available { get; set; }

    public DateTime UpdatedUtc { get; set; }

    // RowVersion is deliberately absent. It is a persistence detail with no meaning outside the
    // DbContext, and shipping a concurrency token to a read-only view invites someone to try to
    // send it back.
}
