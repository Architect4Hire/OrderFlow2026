namespace OrderFlow.Inventory.API.Managers.Domain;

/// <summary>
/// Stock levels for one SKU. <b>The contended row.</b>
/// </summary>
/// <remarks>
/// <para>
/// Every concurrent order for the same SKU converges on this single record, which makes it the one
/// place in the system where two requests can genuinely race: read Available = 1, both decide they
/// can satisfy the order, both write. <see cref="RowVersion"/> is what stops that — SQL Server
/// stamps a new value on every update, so the second writer's UPDATE matches zero rows and EF
/// throws <c>DbUpdateConcurrencyException</c> instead of quietly overselling.
/// </para>
/// <para>
/// Optimistic, not pessimistic: no lock is held across the reserve decision. Losing the race is
/// cheap here — reload and re-evaluate — whereas a lock would serialize every order for a popular
/// SKU behind one another.
/// </para>
/// </remarks>
public class StockItem
{
    /// <summary>Stock-keeping unit. The natural key — SKUs are what the bus talks about.</summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>Units physically in the warehouse, held or not.</summary>
    public int OnHand { get; set; }

    /// <summary>Units spoken for by orders currently in flight. Rises on hold, falls on release.</summary>
    public int Reserved { get; set; }

    /// <summary>
    /// What a new order can actually take. Derived, never stored ([R]1): a persisted copy is a
    /// second source of truth for the same fact, and the moment an update touches one field and not
    /// the other, the row starts lying about how much stock exists.
    /// </summary>
    /// <remarks>
    /// Get-only with no backing field, so EF ignores it by convention. The DbContext will still
    /// <c>Ignore</c> it explicitly — relying on a convention to keep a computed value out of the
    /// table is exactly the kind of silent coupling that breaks on an EF upgrade.
    /// </remarks>
    public int Available => OnHand - Reserved;

    /// <summary>
    /// The optimistic-concurrency token. Configured as the row version in <c>OnModelCreating</c>,
    /// not with <c>[Timestamp]</c> — persistence config lives in the context, so the domain stays
    /// free of any EF reference.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];

    /// <summary>When the levels last moved. For the ops view, not for concurrency — that is
    /// <see cref="RowVersion"/>'s job, and a timestamp is far too coarse to arbitrate a race.</summary>
    public DateTime UpdatedUtc { get; set; }
}
