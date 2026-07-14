namespace OrderFlow.Contracts.Messages;

/// <summary>
/// One line of an order as it travels on the bus: what SKU, how many, at what price.
/// Not the Order service's domain entity — a flat payload fragment shared by the
/// messages that need to talk about line items.
/// </summary>
public record OrderLine
{
    /// <summary>Stock-keeping unit being ordered.</summary>
    public string Sku { get; init; } = string.Empty;

    /// <summary>How many units of <see cref="Sku"/>.</summary>
    public int Quantity { get; init; }

    /// <summary>Price per unit, rounded to 2 dp by the producer.</summary>
    public decimal UnitPrice { get; init; }
}
