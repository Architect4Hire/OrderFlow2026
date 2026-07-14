namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Order → everyone: a customer placed an order. The origin of the saga; its
/// CorrelationId is the OrderId every later message on this order carries.
/// </summary>
public record OrderPlaced : MessageBase
{
    /// <summary>Opaque customer reference. No PII in this POC.</summary>
    public string CustomerRef { get; init; } = string.Empty;

    /// <summary>What was ordered.</summary>
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];

    /// <summary>Server-priced order total, rounded to 2 dp.</summary>
    public decimal Total { get; init; }
}
