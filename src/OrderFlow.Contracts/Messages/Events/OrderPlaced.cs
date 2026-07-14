namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Order → everyone: a customer placed an order. The origin of the saga; its
/// CorrelationId is the OrderId every later message on this order carries.
/// </summary>
/// <remarks>
/// <b>A placed order carries no total, and that is the point.</b> The customer says WHAT they want,
/// never what it costs. Prices come back from Inventory — the service that owns the catalogue — on
/// <see cref="InventoryReserved"/>, and only then does the order have a value the saga will charge.
/// See ADR-006.
/// </remarks>
public record OrderPlaced : MessageBase
{
    /// <summary>Opaque customer reference. No PII in this POC.</summary>
    public string CustomerRef { get; init; } = string.Empty;

    /// <summary>What was ordered. SKU and quantity only — any UnitPrice on these lines is ignored.</summary>
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];
}
