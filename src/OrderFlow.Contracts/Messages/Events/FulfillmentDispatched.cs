namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Fulfillment → saga: the carrier accepted the shipment. The saga confirms the order.
/// </summary>
public record FulfillmentDispatched : MessageBase
{
    /// <summary>Simulated carrier tracking reference.</summary>
    public string TrackingRef { get; init; } = string.Empty;
}
