namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Inventory → saga: every line was held. The saga advances to ChargePayment.
/// A hold is now outstanding — any later failure path MUST release it.
/// </summary>
/// <remarks>
/// <b>This message is also where the order gets its price.</b> Inventory owns the catalogue, so
/// Inventory is the only service entitled to say what a SKU costs. The saga charges
/// <see cref="Total"/> — never a number that came from the customer. Before ADR-006 the client
/// supplied UnitPrice on the incoming ViewModel, which meant a customer could buy a laptop for a
/// penny by editing one field of the JSON they were already sending.
/// </remarks>
public record InventoryReserved : MessageBase
{
    /// <summary>The reserved lines, priced from the catalogue. UnitPrice here is authoritative.</summary>
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];

    /// <summary>The order total the saga will charge, rounded to 2 dp.</summary>
    public decimal Total { get; init; }
}
