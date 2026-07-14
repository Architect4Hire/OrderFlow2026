namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Saga → Fulfillment: hand the paid order to the carrier.
/// Answered with FulfillmentDispatched or FulfillmentFailed.
/// </summary>
public record DispatchFulfillment : MessageBase
{
    /// <summary>Opaque customer reference the carrier ships to.</summary>
    public string CustomerRef { get; init; } = string.Empty;

    /// <summary>What to ship.</summary>
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];
}
